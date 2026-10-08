using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace TimberbornAI
{
    /// <summary>
    /// Chooses what a gatherer flag collects. A flag does nothing until a good is selected (the game
    /// shows "No good selected"), and the selection lives in the same dropdown component the in-game
    /// panel edits. Call without a good to list the options and the current choice.
    ///
    /// That component (Timberborn.GatheringUI.GatherablePrioritizerDropdownProvider) is internal to the
    /// game, so it cannot be named in code. It is found by name at runtime and used by reflection.
    /// </summary>
    internal static class GathererCommands
    {
        private const string DropdownTypeName = "Timberborn.GatheringUI.GatherablePrioritizerDropdownProvider";
        private static Type _dropdownType;

        private static Type DropdownType()
        {
            if (_dropdownType != null) return _dropdownType;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "Timberborn.GatheringUI") continue;
                _dropdownType = asm.GetType(DropdownTypeName);
                break;
            }
            return _dropdownType;
        }

        /// <summary>The selection dropdown component on a gatherer flag entity, or null.</summary>
        internal static object DropdownOf(object entity)
        {
            var type = DropdownType();
            return type == null ? null : Components.Get(entity, type);
        }

        /// <summary>What the flag currently gathers; null or empty means "No good selected".</summary>
        internal static string CurrentChoice(object dropdown)
        {
            if (dropdown == null) return null;
            try { return GameAccess.Invoke(dropdown, "GetValue", out var value) ? value as string : null; }
            catch { return null; }
        }

        public static string Select(string body)
        {
            var core = AIGameServices.Instance;
            if (core == null) return Placer.Fail("no save loaded");
            if (DropdownType() == null) return Placer.Fail("the game's gatherer selection (" + DropdownTypeName + ") was not found; the game version may have changed");

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

                var dropdown = DropdownOf(entity);
                if (dropdown == null) continue;
                flags++;

                var options = new List<string>();
                foreach (var item in GameAccess.Enumerate(GameAccess.Member(dropdown, "Items")))
                {
                    if (item is string option) options.Add(option);
                }

                if (string.IsNullOrEmpty(wanted))
                {
                    listing.Add("{\"x\":" + at.x + ",\"y\":" + at.y + ",\"current\":" + Json.Str(CurrentChoice(dropdown))
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
                    if (!GameAccess.Invoke(dropdown, "SetValue", out _, match))
                        return Placer.Fail("the gatherer selection has no SetValue(string); the game version may have changed");
                }
                catch (Exception e)
                {
                    var root = Placer.Root(e);
                    return Placer.Fail("the game refused the selection: " + root.GetType().Name + ": " + root.Message);
                }

                changed.Add("{\"x\":" + at.x + ",\"y\":" + at.y + ",\"now_gathering\":" + Json.Str(CurrentChoice(dropdown)) + "}");
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
