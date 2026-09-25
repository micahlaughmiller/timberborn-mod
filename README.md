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

Needs the .NET SDK and a Timberborn install. Update 6+ ships its own mod
loader, so this doesn't use BepInEx — the project only references the game's
own assemblies.

**Recommended: clone this repo directly into your local mods folder** so
`git pull` + `dotnet build` is the entire update loop, with no manual copy
step. This is your real Documents folder, **not necessarily
`%USERPROFILE%\Documents`** — if OneDrive has redirected Documents (check
`%USERPROFILE%\OneDrive\...\Documents`), the game reads from the redirected
location and silently ignores anything dropped in the unredirected one.
Confirmed working path on the game PC:

```
C:\Users\micah\OneDrive\Micah's Stuff\Documents\Timberborn\Mods\TimberbornAI\
```

```bash
git clone https://github.com/micahlaughmiller/timberborn-mod.git "C:\Users\micah\OneDrive\Micah's Stuff\Documents\Timberborn\Mods\TimberbornAI"
cd "C:\Users\micah\OneDrive\Micah's Stuff\Documents\Timberborn\Mods\TimberbornAI"
dotnet build mod/TimberbornAI.csproj -c Release -p:GameManaged="C:\Program Files (x86)\Steam\steamapps\common\Timberborn\Timberborn_Data\Managed"
```

`mod/TimberbornAI.csproj` has a post-build step (`CopyToModRoot`) that copies
`manifest.json` and the built DLL up into the repo root automatically —
since the repo root *is* the mod folder, the game sees the update the moment
the build finishes. No manual `copy` commands needed with this layout.

(If you'd rather build from a separate dev checkout and copy into the mods
folder by hand instead, that still works — just delete the `CopyToModRoot`
target in the csproj, or ignore it and copy manually as before.)

If the in-game Mods menu doesn't show the mod after a relaunch, check
`%USERPROFILE%\AppData\LocalLow\Mechanistry\Timberborn\Player.log` for what
folder it actually scanned before assuming the manifest is wrong.

`manifest.json`'s schema is confirmed against a real installed mod
(`Name`, `Version`, `Id`, `MinimumGameVersion`, `Description`, `RequiredMods`).

The DLL itself doesn't depend on the loader's entrypoint interface — it uses
Unity's own `RuntimeInitializeOnLoadMethod`, which fires automatically once
the assembly is loaded into the process, so it should work under the native
loader without further changes to `Plugin.cs`.

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
