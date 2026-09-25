"""
Timberborn AI Director — external agent loop.

Reads the world snapshot from the in-game mod, asks Claude what to do next,
applies the commands, and pushes the reasoning back on screen so a viewer can
follow the decision, not just the outcome.

    python agent/agent.py --goal agent/goal.md

Requires ANTHROPIC_API_KEY in the environment and the game running with the
mod loaded.
"""

import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.request

from anthropic import Anthropic

MOD_URL = "http://127.0.0.1:8787"
MODEL = "claude-opus-5"

TOOLS = [
    {
        "name": "build",
        "description": (
            "Place a building at a map coordinate. Costs resources and enters "
            "the normal build queue, exactly as a human click would."
        ),
        "input_schema": {
            "type": "object",
            "properties": {
                "prefab": {"type": "string", "description": "Prefab name, e.g. 'Lumberjack'"},
                "x": {"type": "integer"},
                "y": {"type": "integer"},
                "z": {"type": "integer", "description": "Height level; 0 is ground"},
            },
            "required": ["prefab", "x", "y"],
        },
    },
    {
        "name": "set_speed",
        "description": "Set game speed. 0 pauses, 1 is normal, 3 is fast-forward.",
        "input_schema": {
            "type": "object",
            "properties": {"speed": {"type": "integer"}},
            "required": ["speed"],
        },
    },
    {
        "name": "note",
        "description": (
            "Update the on-screen caption. Call this every turn so viewers can "
            "follow your reasoning. Keep it short and plain-spoken."
        ),
        "input_schema": {
            "type": "object",
            "properties": {
                "goal": {"type": "string", "description": "Current objective, a few words"},
                "thought": {"type": "string", "description": "Why, in one sentence"},
                "action_summary": {"type": "string", "description": "What you are doing now"},
            },
            "required": ["goal", "thought"],
        },
    },
]

SYSTEM = """You are playing a full game of Timberborn, solo, in front of a live audience.

You receive a world snapshot each turn and act through tools. Rules:

- Call `note` every turn. The audience sees only that caption, so narrate the
  actual reason for the move, not filler.
- Prefer one clear decision per turn over scattering buildings.
- Droughts are the real clock. Water and food storage ahead of a drought beat
  any expansion.
- If `unresolved_bindings` is non-empty, that data is missing rather than zero.
  Say so in your note instead of reasoning over it.
- Commands can fail. Read the result and adapt; do not reissue a command that
  just returned ok=false for the same reason.
"""


def call_mod(path, payload=None, timeout=15):
    """POST/GET against the in-game HTTP API. Returns a dict, never raises."""
    url = f"{MOD_URL}{path}"
    data = json.dumps(payload).encode() if payload is not None else None
    req = urllib.request.Request(url, data=data, method="POST" if data else "GET")
    req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return json.loads(resp.read().decode())
    except urllib.error.URLError as exc:
        return {"ok": False, "error": f"cannot reach mod at {url}: {exc}"}
    except json.JSONDecodeError as exc:
        return {"ok": False, "error": f"bad JSON from mod: {exc}"}


def run(goal_text, interval, max_turns):
    client = Anthropic()
    messages = [{"role": "user", "content": f"Your objective for this playthrough:\n\n{goal_text}"}]
    turn = 0

    while max_turns == 0 or turn < max_turns:
        turn += 1

        state = call_mod("/state")
        if not state.get("error"):
            print(f"[turn {turn}] cycle {state.get('cycle')} "
                  f"beavers {state.get('beavers')} goods {len(state.get('goods', {}))}")
        else:
            print(f"[turn {turn}] {state['error']}", file=sys.stderr)

        messages.append({
            "role": "user",
            "content": f"World snapshot:\n{json.dumps(state, indent=2)}\n\nWhat do you do next?",
        })

        response = client.messages.create(
            model=MODEL,
            max_tokens=2000,
            system=SYSTEM,
            tools=TOOLS,
            messages=messages,
        )
        messages.append({"role": "assistant", "content": response.content})

        results = []
        for block in response.content:
            if block.type == "text" and block.text.strip():
                print(f"    {block.text.strip()}")
            elif block.type == "tool_use":
                payload = dict(block.input)
                payload["action"] = block.name
                outcome = call_mod("/command", payload)
                print(f"    -> {block.name}: {outcome}")
                results.append({
                    "type": "tool_result",
                    "tool_use_id": block.id,
                    "content": json.dumps(outcome),
                })

        if results:
            messages.append({"role": "user", "content": results})

        # Keep the context from growing without bound over a long stream:
        # the objective plus a rolling window is enough to keep playing.
        if len(messages) > 40:
            messages = messages[:1] + messages[-30:]

        time.sleep(interval)


def main():
    parser = argparse.ArgumentParser(description="Drive a Timberborn playthrough with Claude.")
    parser.add_argument("--goal", default="agent/goal.md", help="File describing the playthrough objective")
    parser.add_argument("--interval", type=float, default=20.0, help="Seconds between turns")
    parser.add_argument("--max-turns", type=int, default=0, help="0 runs until interrupted")
    args = parser.parse_args()

    if not os.environ.get("ANTHROPIC_API_KEY"):
        sys.exit("ANTHROPIC_API_KEY is not set.")

    with open(args.goal, encoding="utf-8") as fh:
        goal_text = fh.read()

    probe = call_mod("/state", timeout=5)
    if probe.get("error"):
        sys.exit(f"Mod not reachable — is the game running with the mod loaded?\n{probe['error']}")

    try:
        run(goal_text, args.interval, args.max_turns)
    except KeyboardInterrupt:
        print("\nstopped.")


if __name__ == "__main__":
    main()
