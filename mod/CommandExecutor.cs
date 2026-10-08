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
                case "build":       return Placer.Place(body);
                case "build_path":  return Placer.PlacePath(body);
                case "find_sites":  return SiteFinder.Find(body);
                case "connect":     return Connector.Connect(body);
                case "mark_trees":  return ForestryCommands.Mark(body, true);
                case "unmark_trees": return ForestryCommands.Mark(body, false);
                case "set_speed":   return SetSpeed(body);
                case "pause":       return SetSpeed("{\"speed\":0}");
                case "note":        return OverlayPanel.SetNarration(body);
                default:            return Fail($"unknown action \"{action}\"");
            }
        }

        private static string SetSpeed(string body)
        {
            int speed = Mathf.Clamp(Json.Int(body, "speed", 1), 0, 7);

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
