#!/usr/bin/env python3
"""Local MLX inference server for NPC Farm decisions and dialogue."""

from __future__ import annotations

import argparse
import json
import re
import threading
import traceback
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any

LAYA_MODEL_ID = "convaiinnovations/laya"
CHAT_MODEL_ID = "mlx-community/Qwen3-4B-Instruct-2507-4bit"

_gpu_lock = threading.Lock()
_laya = None
_chat_model = None
_chat_tokenizer = None
_ready = False
_load_error: str | None = None


def _load_models() -> None:
    global _laya, _chat_model, _chat_tokenizer, _ready, _load_error
    try:
        import laya_mlx as laya
        from mlx_lm import load

        print(f"Loading Laya: {LAYA_MODEL_ID}", flush=True)
        _laya = laya.load(LAYA_MODEL_ID)
        print(f"Loading chat model: {CHAT_MODEL_ID}", flush=True)
        _chat_model, _chat_tokenizer = load(CHAT_MODEL_ID)
        _ready = True
        print("Models ready.", flush=True)
    except Exception as exc:
        _load_error = f"{type(exc).__name__}: {exc}"
        traceback.print_exc()
        raise


def _chat(prompt: str, max_tokens: int = 120) -> str:
    from mlx_lm import generate

    assert _chat_model is not None and _chat_tokenizer is not None
    messages = [{"role": "user", "content": prompt}]
    if _chat_tokenizer.chat_template is not None:
        rendered = _chat_tokenizer.apply_chat_template(
            messages, add_generation_prompt=True, tokenize=False
        )
    else:
        rendered = prompt
    with _gpu_lock:
        text = generate(
            _chat_model,
            _chat_tokenizer,
            prompt=rendered,
            max_tokens=max_tokens,
            verbose=False,
        )
    return (text or "").strip()


def _first_sentence(text: str) -> str:
    cleaned = re.sub(r"\s+", " ", text).strip().strip('"').strip("'")
    if not cleaned:
        return "..."
    # Drop thinking-style prefixes if present.
    cleaned = re.sub(r"^<think>.*?</think>\s*", "", cleaned, flags=re.DOTALL).strip()
    for sep in (". ", "! ", "? "):
        idx = cleaned.find(sep)
        if 0 < idx < 180:
            return cleaned[: idx + 1].strip()
    if len(cleaned) > 180:
        return cleaned[:177].rstrip() + "..."
    return cleaned


def _get(payload: dict[str, Any], *keys: str, default: Any = None) -> Any:
    for key in keys:
        if key in payload:
            return payload[key]
        for existing, value in payload.items():
            if str(existing).lower() == key.lower():
                return value
    return default


def decide(payload: dict[str, Any]) -> dict[str, Any]:
    assert _laya is not None
    state = _get(payload, "state", default={}) or {}
    actions = _get(payload, "actions", default=[]) or []
    if not actions:
        raise ValueError(f"actions required; keys={list(payload.keys())}")

    criteria: dict[str, str] = {}
    for action in actions:
        if not isinstance(action, dict):
            continue
        key = str(_get(action, "id", default="") or "").strip()
        if not key:
            continue
        criteria[key] = str(_get(action, "description", default=key) or key)

    if not criteria:
        raise ValueError("no valid actions")

    questions = {
        "next_action": {
            "type": "choice",
            "instructions": "Given this NPC's current state, which action should they take next?",
            "criteria": criteria,
        }
    }

    with _gpu_lock:
        result = _laya.predict(state, questions)

    answers = result.get("answers", result)
    answer = answers.get("next_action", {})
    choice = answer.get("choice")
    probabilities = answer.get("probabilities", {})
    confidence = answer.get("confidence")

    # Normalize probability maps that may be list/dict shaped.
    if isinstance(probabilities, list):
        probabilities = {
            k: float(v) for k, v in zip(criteria.keys(), probabilities, strict=False)
        }
    else:
        probabilities = {str(k): float(v) for k, v in dict(probabilities).items()}

    if choice not in criteria:
        # Fall back to highest probability or first action.
        if probabilities:
            choice = max(probabilities.items(), key=lambda kv: kv[1])[0]
        else:
            choice = next(iter(criteria))

    return {
        "choice": choice,
        "probabilities": probabilities,
        "confidence": float(confidence) if confidence is not None else None,
    }


def speak(payload: dict[str, Any]) -> dict[str, Any]:
    speaker = _get(payload, "speaker", default={}) or {}
    listener = _get(payload, "listener", default={}) or {}
    affinity = _get(payload, "affinity", default=0) or 0
    topic = _get(payload, "topic", default="everyday life") or "everyday life"
    memories = (_get(payload, "memories", default=[]) or [])[:3]
    memory_text = "; ".join(str(m) for m in memories) if memories else "none"
    outcome = _get(payload, "outcome", default="") or ""
    outcome_text = (
        f"\nWhat just happened (already resolved, do not contradict it): {outcome}."
        if outcome
        else ""
    )

    prompt = f"""You write one short spoken line for a town NPC simulation.
Speaker: {speaker.get("name", "Someone")}, {speaker.get("occupation", "townsfolk")}.
Personality: greed={speaker.get("greed", 0.5)}, sociability={speaker.get("sociability", 0.5)}, empathy={speaker.get("empathy", 0.5)}, honesty={speaker.get("honesty", 0.5)}.
Interests: {", ".join(speaker.get("interests", [])[:3]) or "ordinary life"}.
Needs: hunger={speaker.get("hunger", 0.5)}, energy={speaker.get("energy", 0.5)}, social={speaker.get("social", 0.5)}.
Listener: {listener.get("name", "Someone")}, {listener.get("occupation", "townsfolk")}.
Relationship affinity toward listener: {affinity} (-1 hate to 1 love).
Recent memories: {memory_text}.
Topic hint: {topic}.{outcome_text}
Rules: Reply with ONLY the spoken line. One or two short sentences. No quotes. No stage directions. Stay in character. If an outcome is given, the line must match it."""

    raw = _chat(prompt, max_tokens=80)
    line = _first_sentence(raw)
    return {"line": line, "raw": raw}


def interests(payload: dict[str, Any]) -> dict[str, Any]:
    people = _get(payload, "people", default=[]) or []
    if not people:
        return {"people": []}

    roster = []
    for person in people:
        if not isinstance(person, dict):
            continue
        roster.append(
            {
                "name": str(_get(person, "name", default="Someone") or "Someone"),
                "occupation": str(
                    _get(person, "occupation", default="townsfolk") or "townsfolk"
                ),
                "personality": _get(person, "personality", default={}) or {},
            }
        )

    # Generate in small batches so the JSON response is not truncated.
    merged: dict[str, list[str]] = {}
    batch_size = 4
    for i in range(0, len(roster), batch_size):
        batch = roster[i : i + batch_size]
        prompt = f"""Invent personal interests for a town simulation.
For EACH person below, invent exactly 3 short noun-phrase interests.
People JSON:
{json.dumps(batch, indent=2)}
Rules: Reply with ONLY a JSON array of objects:
[{{"name":"...","interests":["...","...","..."]}}, ...]
Same order and names as input. No other text."""
        raw = _chat(prompt, max_tokens=350)
        for item in _parse_interest_people(raw, batch):
            merged[item["name"]] = item["interests"]

    results = []
    for person in roster:
        name = person["name"]
        interests = merged.get(name) or _fallback_interests(person["occupation"])
        if len(interests) < 3:
            interests = (interests + _fallback_interests(person["occupation"]))[:3]
        results.append({"name": name, "interests": interests[:3]})
    return {"people": results}


def _parse_interest_people(
    raw: str, roster: list[dict[str, Any]]
) -> list[dict[str, Any]]:
    text = re.sub(r"^<think>.*?</think>\s*", "", raw, flags=re.DOTALL).strip()
    # Models sometimes truncate the closing bracket.
    if "[" in text and "]" not in text:
        text = text.rstrip().rstrip(",") + "]"
    match = re.search(r"\[[\s\S]*\]", text)
    by_name: dict[str, list[str]] = {}
    if match:
        blob = match.group(0)
        try:
            data = json.loads(blob)
        except json.JSONDecodeError:
            # Try closing any truncated final object.
            repaired = blob.rstrip()
            if not repaired.endswith("]"):
                repaired += "]"
            try:
                data = json.loads(repaired)
            except json.JSONDecodeError:
                data = None
        if isinstance(data, list):
            for item in data:
                if not isinstance(item, dict):
                    continue
                name = str(item.get("name", "")).strip()
                interests = item.get("interests", [])
                if name and isinstance(interests, list):
                    by_name[name] = [
                        str(x).strip() for x in interests if str(x).strip()
                    ][:3]

    results = []
    for person in roster:
        name = person["name"]
        occupation = person["occupation"]
        interests = by_name.get(name) or []
        if len(interests) < 3:
            interests = (interests + _fallback_interests(occupation))[:3]
        results.append({"name": name, "interests": interests[:3]})
    return results


def _fallback_interests(occupation: str) -> list[str]:
    table = {
        "farmer": ["crop rotation", "morning markets", "weather lore"],
        "baker": ["sourdough", "village festivals", "herb gardens"],
        "miller": ["river mills", "grain trade", "wood carving"],
        "merchant": ["rare goods", "travel stories", "coin counting"],
        "innkeeper": ["local gossip", "stew recipes", "card games"],
        "blacksmith": ["tool forging", "metal polish", "folk songs"],
        "guard": ["night patrols", "sparring", "town history"],
        "herbalist": ["wild herbs", "healing teas", "forest walks"],
    }
    return table.get(occupation.lower(), ["folk music", "market days", "river walks"])


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt: str, *args: Any) -> None:
        print(f"{self.address_string()} - {fmt % args}", flush=True)

    def _json(self, code: int, payload: dict[str, Any]) -> None:
        body = json.dumps(payload).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _read_json(self) -> dict[str, Any]:
        length = int(self.headers.get("Content-Length", "0"))
        raw = self.rfile.read(length) if length else b"{}"
        return json.loads(raw.decode("utf-8") or "{}")

    def do_GET(self) -> None:
        if self.path.split("?")[0] == "/health":
            if _ready:
                self._json(
                    200, {"ok": True, "laya": LAYA_MODEL_ID, "chat": CHAT_MODEL_ID}
                )
            else:
                self._json(
                    503,
                    {"ok": False, "error": _load_error or "models still loading"},
                )
            return
        self._json(404, {"error": "not found"})

    def do_POST(self) -> None:
        path = self.path.split("?")[0]
        if not _ready:
            self._json(503, {"error": _load_error or "models still loading"})
            return
        try:
            payload = self._read_json()
            if path == "/decide":
                self._json(200, decide(payload))
            elif path == "/speak":
                self._json(200, speak(payload))
            elif path == "/interests":
                self._json(200, interests(payload))
            else:
                self._json(404, {"error": "not found"})
        except Exception as exc:  # noqa: BLE001
            traceback.print_exc()
            self._json(500, {"error": str(exc)})


def main() -> None:
    parser = argparse.ArgumentParser(description="NPC Farm local AI server")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--preload", action="store_true", default=True)
    args = parser.parse_args()

    if args.preload:
        _load_models()

    server = ThreadingHTTPServer((args.host, args.port), Handler)
    print(f"Listening on http://{args.host}:{args.port}", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("Shutting down.", flush=True)
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
