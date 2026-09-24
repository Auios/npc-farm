#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

PYTHON_BIN="${PYTHON_BIN:-}"
if [[ -z "$PYTHON_BIN" ]]; then
  if command -v python3.13 >/dev/null 2>&1; then
    PYTHON_BIN="$(command -v python3.13)"
  elif command -v python3.12 >/dev/null 2>&1; then
    PYTHON_BIN="$(command -v python3.12)"
  else
    echo "Need python3.13 or python3.12. Installing python@3.12 via Homebrew..."
    brew install python@3.12
    PYTHON_BIN="$(command -v python3.12)"
  fi
fi

echo "Using $PYTHON_BIN"
"$PYTHON_BIN" -m venv .venv
# shellcheck disable=SC1091
source .venv/bin/activate
python -m pip install --upgrade pip
pip install -r ai/requirements.txt

echo "Downloading and smoke-testing models..."
python - <<'PY'
import json
import laya_mlx as laya
from mlx_lm import load, generate

LAYA = "convaiinnovations/laya"
CHAT = "mlx-community/Qwen3-4B-Instruct-2507-4bit"

print(f"Loading {LAYA}...")
agent = laya.load(LAYA)
state = {
    "name": "Mara",
    "occupation": "baker",
    "hour": 10,
    "hunger": 0.8,
    "energy": 0.6,
    "social": 0.4,
    "gold": 12,
    "greed": 0.3,
    "sociability": 0.7,
}
questions = {
    "next_action": {
        "type": "choice",
        "instructions": "Which action should this NPC take next?",
        "criteria": {
            "work": "daytime job is available and energy is okay",
            "eat": "hunger is high and food is available",
            "sleep": "energy is low or it is night",
            "talk": "someone is nearby and social need is up",
            "go_home": "safe idle return home",
        },
    }
}
result = agent.predict(state, questions)
answers = result.get("answers", result)
print("Laya decision:")
print(json.dumps(answers.get("next_action", answers), indent=2, default=str))

print(f"Loading {CHAT}...")
model, tokenizer = load(CHAT)
messages = [{"role": "user", "content": "Say one friendly sentence as a village baker named Mara."}]
prompt = tokenizer.apply_chat_template(messages, add_generation_prompt=True, tokenize=False)
text = generate(model, tokenizer, prompt=prompt, max_tokens=40, verbose=False)
print("Qwen line:")
print(text.strip())
print("Smoke test OK.")
PY

echo "Setup complete. Start the AI server with:"
echo "  source .venv/bin/activate && python ai/server.py"
