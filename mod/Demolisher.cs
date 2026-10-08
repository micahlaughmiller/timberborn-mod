using System;
using System.Collections.Generic;
using Timberborn.BlockSystem;
using UnityEngine;

namespace TimberbornAI
{
    /// <summary>
    /// Removes one of the player's own buildings or path tiles through the game's EntityService, so the
    /// agent can undo a mistake (a flag on the wrong level, an unwanted site). Deliberately narrow:
    /// only things named in the building list can be removed, never trees, beavers or terrain, and
    /// the district center is refused outright.
    /// </summary>
    internal static class Demolisher
    {
        public static string Demolish(string body)
        {
            int x = Json.Int(body, "x", int.MinValue), y = Json.Int(body, "y", int.MinValue);
            if (x == int.MinValue || y == int.MinValue)
                return Placer.Fail("demolish requires x and y: the building's x and y as listed in placed_buildings");

            var wanted = Json.Field(body, "prefab"); // optional safety check on the name
            var core = AIGameServices.Instance;
            var build = AIBuildServices.Instance;
            if (core == null || build == null) return Placer.Fail("no save loaded, or build services not bound");

            var removable = new HashSet<string>();
            foreach (var spec in GameAccess.Enumerate(build.Buildings.Buildings))
            {
                if (GameAccess.Invoke(build.Buildings, "GetTemplateName", out var n, spec) && n is string s) removable.Add(s);
            }

            BlockObject found = null;
            string foundName = null;
            int matches = 0;

            foreach (var entity in GameAccess.Enumerate(core.Entities.Entities))
            {
                var name = StateReader.EntityName(entity);
                if (!removable.Contains(name)) continue;
                if (!string.IsNullOrEmpty(wanted) && name != wanted) continue;

                var block = WorldReader.BlockOf(entity);
                if (block == null) continue;

                var at = block.Coordinates;
                if (at.x != x || at.y != y) continue;

                matches++;
                if (found == null) { found = block; foundName = name; }
            }

            if (found == null)
                return Placer.Fail("nothing to demolish at " + x + "," + y + (string.IsNullOrEmpty(wanted) ? "" : " named " + wanted)
                                   + ". Use the x and y from placed_buildings (or a path tile's cell).");

            if (foundName.StartsWith("DistrictCenter", StringComparison.Ordinal))
                return Placer.Fail("refusing to demolish the district center");

            try
            {
                build.EntityRemover.Delete(found);
            }
            catch (Exception e)
            {
                var root = Placer.Root(e);
                return Placer.Fail("the game refused to remove " + foundName + ": " + root.GetType().Name + ": " + root.Message);
            }

            return Placer.Ok("demolished " + foundName + " at " + x + "," + y
                             + (matches > 1 ? " (" + (matches - 1) + " other building(s) share that cell and were left alone)" : ""));
        }
    }
}
