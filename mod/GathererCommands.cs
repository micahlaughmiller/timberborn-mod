using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.GatheringUI;
using UnityEngine;

namespace TimberbornAI
{
    /// <summary>
    /// Chooses what a gatherer flag collects. A flag does nothing until a good is selected (the game
    /// shows "No good selected"), and the selection lives in the same dropdown component the in-game
    /// panel edits. Call without a good to list the options and the current choice.
    /// </summary>
    internal static class GathererCommands
    {
        public static string Select(string body)
        {
            var core = AIGameServices.Instance;
            if (core == null) return Placer.Fail("no save loaded");

            int x = Json.Int(body, "x", int.MinValue), y = Json.Int(body, "y", int.MinValue);
            bool anyFlag = x == int.MinValue || y == int.MinValue; // no position means every gatherer flag
            var wanted = Json.Field(body, "good");

            var changed = new List<string>();
            var listing = new List<string>();
            int flags = 0;

            foreach (var entity in GameAccess.Enumerate(core.Entities.Entities))
            {
                var name = StateReader.EntityName(entity);
                if (!name.StartsWith("GathererFlag", StringComparison.Ordinal)) continue;

                var block = WorldReader.BlockOf(entity);
                if (block == null) continue;

                var at = block.Coordinates;
                if (!anyFlag && (at.x != x || at.y != y)) continue;

                var dropdown = Components.Get(entity, typeof(GatherablePrioritizerDropdownProvider)) as GatherablePrioritizerDropdownProvider;
                if (dropdown == null) continue;
                flags++;

                var options = new List<string>();
                foreach (var item in dropdown.Items) options.Add(item);

                if (string.IsNullOrEmpty(wanted))
                {
                    listing.Add("{\"x\":" + at.x + ",\"y\":" + at.y + ",\"current\":" + Json.Str(dropdown.GetValue())
                                + ",\"options\":[" + string.Join(",", options.Select(Json.Str)) + "]}");
                    continue;
                }

                var match = options.FirstOrDefault(o => string.Equals(o, wanted, StringComparison.OrdinalIgnoreCase))
                            ?? options.FirstOrDefault(o => o != null && o.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0);
                if (match == null)
                    return Placer.Fail("no gatherable matching \"" + wanted + "\" on the flag at " + at.x + "," + at.y
                                       + ". Options: " + string.Join(", ", options) + ".");

                try
                {
                    dropdown.SetValue(match);
                }
                catch (Exception e)
                {
                    var root = Placer.Root(e);
                    return Placer.Fail("the game refused the selection: " + root.GetType().Name + ": " + root.Message);
                }

                changed.Add("{\"x\":" + at.x + ",\"y\":" + at.y + ",\"now_gathering\":" + Json.Str(dropdown.GetValue()) + "}");
            }

            if (flags == 0)
                return Placer.Fail(anyFlag ? "there are no gatherer flags yet" : "no gatherer flag at " + x + "," + y + " (use x and y from placed_buildings)");

            if (string.IsNullOrEmpty(wanted))
                return "{\"ok\":true,\"detail\":\"options listed; call again with good set to pick one\",\"flags\":[" + string.Join(",", listing) + "]}";

            return "{\"ok\":true,\"detail\":" + Json.Str("set " + changed.Count + " gatherer flag(s) to " + wanted)
                 + ",\"flags\":[" + string.Join(",", changed) + "]}";
        }
    }
}
