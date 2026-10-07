using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.BlockSystem;
using Timberborn.BlueprintSystem;
using Timberborn.Coordinates;
using Timberborn.EntitySystem;
using UnityEngine;

namespace TimberbornAI
{
    /// <summary>
    /// Places a building the way the game's own tool does, minus the mouse:
    /// template lookup, unlock check, physical validity check, then a normal
    /// construction site. Beavers deliver the materials, so cost and build time
    /// behave exactly as for a human click. Every refusal says why, so the agent
    /// can adapt instead of repeating a command that cannot work.
    /// </summary>
    internal static class Placer
    {
        /// <summary>Returns the same {"ok":..} JSON shape as the other commands.</summary>
        public static string Place(string body)
        {
            var prefab = Json.Field(body, "prefab");
            if (string.IsNullOrEmpty(prefab)) return Fail("build requires \"prefab\" (see /buildings)");

            int x = Json.Int(body, "x", int.MinValue);
            int y = Json.Int(body, "y", int.MinValue);
            if (x == int.MinValue || y == int.MinValue) return Fail("build requires \"x\" and \"y\"");

            var build = AIBuildServices.Instance;
            var world = AIWorldServices.Instance;
            if (build == null || world == null)
                return Fail("no save loaded, or build/world services not bound (check no-build.flag / no-world.flag)");

            // 1. Does the template exist, and is it unlocked?
            object spec;
            try
            {
                if (!GameAccess.Invoke(build.Buildings, "GetBuildingTemplate", out spec, prefab) || spec == null)
                    return Fail("unknown building \"" + prefab + "\" (see /buildings)");
            }
            catch (Exception e)
            {
                return Fail("unknown building \"" + prefab + "\": " + Root(e).Message + " (see /buildings)");
            }

            GameAccess.Invoke(build.Unlocking, "Unlocked", out var unlocked, spec);
            if (!(unlocked is bool isUnlocked && isUnlocked))
                return Fail("\"" + prefab + "\" is not unlocked yet (needs science; see /buildings)");

            // 2. Find its blueprint and the block layout inside it.
            if (!TryGetBlueprint(build, prefab, out var blueprint, out var blueprintError))
                return Fail(blueprintError);

            var blockSpec = blueprint.GetSpec(typeof(BlockObjectSpec)) as BlockObjectSpec;
            if (blockSpec == null)
                return Fail("\"" + prefab + "\" has no BlockObjectSpec, so it cannot be placed on the map");

            // 3. Where, and facing which way.
            int z = Json.Int(body, "z", int.MinValue);
            if (z == int.MinValue) z = SurfaceZ(world, x, y);
            if (z < 0) return Fail("no terrain at " + x + "," + y);

            Orientation orientation;
            try { orientation = (Orientation)Enum.Parse(typeof(Orientation), Json.Field(body, "orientation") ?? "Cw0", true); }
            catch { return Fail("orientation must be Cw0, Cw90, Cw180 or Cw270"); }

            var flipped = (Json.Field(body, "flip") ?? "false").ToLowerInvariant() == "true";
            var placement = new Placement(new Vector3Int(x, y, z), orientation, flipped ? FlipMode.Flipped : FlipMode.Unflipped);

            // 4. Physical validity: free cells, support below, terrain.
            if (!build.Validator.BlocksValid(blockSpec, placement))
            {
                // If a neighbouring height is valid, the height convention is the problem,
                // not an obstruction. Report it so the difference is visible.
                var valid = new List<string>();
                foreach (var dz in new[] { -2, -1, 1, 2 })
                {
                    try
                    {
                        var shifted = new Placement(new Vector3Int(x, y, z + dz), orientation, flipped ? FlipMode.Flipped : FlipMode.Unflipped);
                        if (build.Validator.BlocksValid(blockSpec, shifted)) valid.Add((z + dz).ToString());
                    }
                    catch { }
                }

                return Fail("cannot place " + prefab + " at " + x + "," + y + "," + z
                            + ": cells are occupied, unsupported or blocked (check /map; trees and other buildings block)"
                            + (valid.Count > 0 ? ". It WOULD be valid at z=" + string.Join(",", valid) + ", so the surface height used here is off" : ". No nearby height is valid either, so something is on or under this spot"));
            }

            if ((Json.Field(body, "dry_run") ?? "false").ToLowerInvariant() == "true")
                return Ok("valid: " + prefab + " can be placed at " + x + "," + y + "," + z + " facing " + orientation + " (dry run, nothing created)");

            // 5. Create it. PlaceFinished buildings (for example paths) appear complete.
            try
            {
                var builder = new EntitySetup.Builder(blueprint);
                bool finished = GameAccess.Member(spec, "PlaceFinished") is bool f && f;
                var created = finished
                    ? build.Construction.CreateAsFinished(builder, placement)
                    : build.Construction.CreateAsUnfinished(builder, placement);

                return Ok((finished ? "placed " : "queued ") + prefab + " at " + x + "," + y + "," + z
                          + " facing " + orientation + (created == null ? "" : " (entity created)"));
            }
            catch (Exception e)
            {
                var root = Root(e);
                return Fail("game refused to create " + prefab + ": " + root.GetType().Name + ": " + root.Message);
            }
        }

        private const int MaxPathTiles = 150;

        /// <summary>
        /// Lays Path tiles along an L-shaped route: horizontally from (x1,y1) to (x2,y1), then
        /// vertically to (x2,y2). Each tile goes through the normal placement checks. Tiles that
        /// cannot be placed (trees, buildings, water, or an existing path) are listed so the
        /// caller can route around them. dry_run=true only reports.
        /// </summary>
        public static string PlacePath(string body)
        {
            int x1 = Json.Int(body, "x1", int.MinValue), y1 = Json.Int(body, "y1", int.MinValue);
            int x2 = Json.Int(body, "x2", int.MinValue), y2 = Json.Int(body, "y2", int.MinValue);
            if (x1 == int.MinValue || y1 == int.MinValue || x2 == int.MinValue || y2 == int.MinValue)
                return Fail("build_path requires x1, y1, x2 and y2");

            var tiles = new List<int[]>();
            int stepX = x2 >= x1 ? 1 : -1;
            for (int x = x1; x != x2 + stepX; x += stepX) tiles.Add(new[] { x, y1 });

            int stepY = y2 >= y1 ? 1 : -1;
            for (int y = y1 + stepY; y != y2 + stepY && y1 != y2; y += stepY) tiles.Add(new[] { x2, y });

            if (tiles.Count > MaxPathTiles)
                return Fail("path is " + tiles.Count + " tiles long; the limit is " + MaxPathTiles + ". Build it in shorter pieces.");

            bool dry = (Json.Field(body, "dry_run") ?? "false").ToLowerInvariant() == "true";
            int placed = 0;
            var blocked = new List<string>();

            foreach (var tile in tiles)
            {
                var one = "{\"prefab\":\"Path\",\"x\":" + tile[0] + ",\"y\":" + tile[1] + (dry ? ",\"dry_run\":true" : "") + "}";
                if (Json.Field(Place(one), "ok") == "true") placed++;
                else blocked.Add("[" + tile[0] + "," + tile[1] + "]");
            }

            string verb = dry ? "could place" : "placed";
            return "{\"ok\":" + (placed > 0 ? "true" : "false")
                 + ",\"detail\":" + Json.Str(verb + " " + placed + " of " + tiles.Count + " path tiles from (" + x1 + "," + y1 + ") to (" + x2 + "," + y2 + ")")
                 + ",\"complete\":" + (blocked.Count == 0 ? "true" : "false")
                 + ",\"blocked_or_existing\":[" + string.Join(",", blocked.Take(30)) + "]}";
        }

        /// <summary>
        /// ISpecService.GetBlueprint wants a blueprint path whose exact form is not confirmed.
        /// Tries the template name, then common folder prefixes, and reports every failure
        /// so the real format can be read off the game's own error text.
        /// </summary>
        private static bool TryGetBlueprint(AIBuildServices build, string name, out Blueprint blueprint, out string error)
        {
            var errors = new List<string>();

            // Preferred: the game's own list of every template blueprint, matched by name.
            // No path format needed, and the names are the ones /buildings reports.
            try
            {
                Blueprint caseInsensitive = null;
                foreach (var item in GameAccess.Enumerate(build.Templates.AllTemplates))
                {
                    var candidate = item as Blueprint;
                    if (candidate == null) continue;

                    if (candidate.Name == name)
                    {
                        blueprint = candidate;
                        error = null;
                        return true;
                    }

                    if (caseInsensitive == null && string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                        caseInsensitive = candidate;
                }

                if (caseInsensitive != null)
                {
                    blueprint = caseInsensitive;
                    error = null;
                    return true;
                }

                errors.Add("not in TemplateCollectionService.AllTemplates");
            }
            catch (Exception e)
            {
                var root = Root(e);
                errors.Add("AllTemplates lookup failed: " + root.GetType().Name + ": " + root.Message);
            }

            // Fallback: ask ISpecService directly with a few path shapes.
            foreach (var candidate in new[] { name, "Buildings/" + name, "Blueprints/Buildings/" + name })
            {
                try
                {
                    var found = build.Specs.GetBlueprint(candidate);
                    if (found != null)
                    {
                        blueprint = found;
                        error = null;
                        return true;
                    }
                    errors.Add("[" + candidate + "] returned null");
                }
                catch (Exception e)
                {
                    var root = Root(e);
                    errors.Add("[" + candidate + "] " + root.GetType().Name + ": " + root.Message);
                }
            }

            blueprint = null;
            error = "no blueprint found for \"" + name + "\". ISpecService.GetBlueprint said: " + string.Join(" | ", errors);
            return false;
        }

        /// <summary>Surface level of a column: the highest cell z returned for it (already the placement level), or -1 if none.</summary>
        private static int SurfaceZ(AIWorldServices world, int x, int y)
        {
            int top = -1;
            foreach (var cell in GameAccess.Enumerate(world.Terrain.GetAllHeightsInCell(new Vector2Int(x, y))))
            {
                if (cell is Vector3Int v) top = Math.Max(top, v.z);
            }
            return top;
        }

        private static Exception Root(Exception e)
        {
            while (e.InnerException != null) e = e.InnerException;
            return e;
        }

        private static string Ok(string detail) => "{\"ok\":true,\"detail\":" + Json.Str(detail) + "}";
        private static string Fail(string error) => "{\"ok\":false,\"error\":" + Json.Str(error) + "}";
    }
}
