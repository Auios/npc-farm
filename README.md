# NPC Farm

Observer-style town simulation: 16 NPCs live a day/night cycle with hunger, energy, social needs, jobs, money, and conversation. The C# / Raylib game owns the world. Local models on Apple Silicon choose actions (Laya) and write dialogue / interests (Qwen3-4B).

## Requirements

- macOS Apple Silicon
- .NET 10
- Python 3.13 (or 3.12)

This machine profile used for development: M4 Max, 48 GB unified memory.

## Setup (models)

```bash
./scripts/setup.sh
```

Creates `.venv`, installs `laya-mlx` + `mlx-lm`, downloads:

- `convaiinnovations/laya` — decision engine
- `mlx-community/Qwen3-4B-Instruct-2507-4bit` — dialogue / interests

## Run

```bash
# Interactive window (starts the Python AI server automatically)
dotnet run

# Headless smoke sim with models
dotnet run -- --headless --hours 2

# Deterministic heuristics only (no models)
dotnet run -- --offline --headless --hours 2
```

Controls: WASD pan, mouse wheel zoom, click an NPC to inspect, Space pause, `1`/`2`/`3` for 1x/3x/8x speed.

## Tests

```bash
dotnet test
```

## Architecture

- `src/` — simulation, pathfinding, Raylib view, HTTP client
- `ai/server.py` — localhost MLX service (`/decide`, `/speak`, `/interests`, `/health`)
- Engine filters valid actions; Laya only chooses among them
- Interests are generated once and cached in `data/interests.json`
