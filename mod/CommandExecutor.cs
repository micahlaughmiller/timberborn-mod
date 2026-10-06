using System;
using UnityEngine;

namespace TimberbornAI
{
    /// <summary>
    /// Applies agent commands to the world. Runs on the Unity main thread.
    ///
    /// Every handler returns a JSON result describing what actually happened —
    /// the agent needs honest failure text, not a silent no-op, or it will keep
    /// re-issuing a command that cannot work.
    /// </summary>
    internal static class CommandExecutor
    {
        public static string Execute(string body)
        {
            var action = Json.Field(body, "action");
            if (string.IsNullOrEmpty(action))
                return Fail("missing \"action\"");

            switch (action)
            {
                case "build":       return Build(body);
                case "set_speed":   return SetSpeed(body);
                case "pause":       return SetSpeed("{\"speed\":0}");
                case "note":        return OverlayPanel.SetNarration(body);
                default:            return Fail($"unknown action \"{action}\"");
            }
        }

        private static string Build(string body)
        {
            var prefab = Json.Field(body, "prefab");
            if (string.IsNullOrEmpty(prefab)) return Fail("build requires \"prefab\"");

            int x = Json.Int(body, "x", int.MinValue);
            int y = Json.Int(body, "y", int.MinValue);
            int z = Json.Int(body, "z", 0);
            if (x == int.MinValue || y == int.MinValue) return Fail("build requires \"x\" and \"y\"");

            // Placement goes through the game's own placer so validation,
            // cost and the build queue all behave exactly as for a human click.
            var placer = GameAccess.FindOne("placer")
                         ?? GameAccess.FindOne("building");
            if (placer == null)
                return Fail("no placement service resolved — run GET /dump and update GameAccess.CandidateNames");

            if (!GameAccess.Invoke(placer, "Place", out var result, prefab, new Vector3Int(x, y, z)))
                return Fail("placement method not found on resolved service");

            return Ok($"queued {prefab} at {x},{y},{z}");
        }

        private static string SetSpeed(string body)
        {
            int speed = Mathf.Clamp(Json.Int(body, "speed", 1), 0, 3);

            var services = AIGameServices.Instance;
            if (services == null)
                return Fail("no save loaded, cannot change speed");

            // SpeedManager's member names aren't confirmed; /state -> raw.speed_manager
            // lists what it exposes. Try the likely names and report what happened.
            foreach (var method in new[] { "ChangeSpeed", "SetSpeed" })
            {
                try
                {
                    if (GameAccess.Invoke(services.Speed, method, out _, (float)speed))
                        return Ok("speed " + speed + " via " + method);
                }
                catch (Exception e)
                {
                    return Fail(method + " threw: " + e.GetType().Name + ": " + e.Message);
                }
            }

            return Fail("SpeedManager has no ChangeSpeed/SetSpeed(float); see /state raw.speed_manager");
        }

        private static string Ok(string msg)   => $"{{\"ok\":true,\"detail\":{Json.Str(msg)}}}";
        private static string Fail(string msg) => $"{{\"ok\":false,\"error\":{Json.Str(msg)}}}";
    }
}
