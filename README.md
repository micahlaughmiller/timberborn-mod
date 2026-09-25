# Timberborn AI Director

Give a model a goal, then watch it play Timberborn live — with its reasoning
captioned on screen for the recording.

Two halves:

- **`mod/`** — a BepInEx plugin loaded into the game. Exposes a local HTTP API
  (`127.0.0.1:8787`) for reading world state and issuing commands, plus an
  on-screen caption showing the AI's current goal and reasoning.
- **`agent/`** — a Python loop outside the game. Polls state, asks Claude what
  to do, applies the commands, pushes the reasoning back to the caption.

The agent never touches the mouse or reads pixels. It reads structured state and
issues structured commands, which is what makes the run legible on video.

## Build

Needs the .NET SDK and a Timberborn install.

```bash
dotnet build mod/TimberbornAI.csproj -c Release -p:GameManaged="D:\SteamLibrary\steamapps\common\Timberborn\Timberborn_Data\Managed"
```

Copy `TimberbornAI.dll` into `Timberborn/BepInEx/plugins/`.

## First run: resolve the game bindings

Timberborn's internal type names change between updates, so the mod resolves
them by name at runtime instead of hard-referencing them. The shipped guesses in
`GameAccess.CandidateNames` are **unverified** — confirm them against your own
install before expecting the agent to play well.

Start the game, load a save, then:

```bash
curl http://127.0.0.1:8787/dump
```

That writes every Timberborn type and the members of live components to a text
file (path is returned in the response). Find the real names for inventory,
beaver, building, weather and placement, and put them first in each list in
[GameAccess.cs](mod/GameAccess.cs).

Check your work:

```bash
curl http://127.0.0.1:8787/state
```

`unresolved_bindings` should come back empty. Anything still listed there is
data the agent will be missing — it is told to say so on screen rather than
reason over zeros.

## Run

```bash
export ANTHROPIC_API_KEY=...
python agent/agent.py --goal agent/goal.md --interval 20
```

Edit [goal.md](agent/goal.md) to change what it's trying to achieve. That file is
the whole brief — the objective, the priorities, and how chatty it should be.

## Recording

Capture the game window as usual. The caption panel is drawn top-left via IMGUI,
so it lands in the recording without any extra compositing. `--interval 20` keeps
the pace watchable; drop it for a faster-moving edit.

## Known limits

- Building placement goes through a reflected `Place` call. Until the placement
  binding is confirmed on your install, `build` will return `ok=false` with the
  reason, and the agent will report that instead of pretending it worked.
- The agent picks coordinates from the state snapshot, which does not yet carry
  a terrain map. Expect poor siting until a heightmap is added to
  [StateReader.cs](mod/StateReader.cs).
