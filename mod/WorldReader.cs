using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Timberborn.ResourceCountingSystem;
using UnityEngine;

namespace TimberbornAI
{
    /// <summary>
    /// Finds a component on a game object without knowing how Timberborn exposes it.
    /// Tries an instance generic getter, then extension methods in the base-component
    /// assembly, then Unity's own lookup through the GameObject. Returns null on a miss
    /// rather than throwing, so callers can report a useful error.
    /// </summary>
    internal static class Components
    {
        private static List<MethodInfo> _extensions;

        public static object Get(object owner, Type componentType)
        {
            if (owner == null) return null;
            const BindingFlags inst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            foreach (var name in new[] { "GetComponentFast", "GetComponent" })
            {
                foreach (var m in owner.GetType().GetMethods(inst))
                {
                    if (m.Name != name || !m.IsGenericMethodDefinition || m.GetParameters().Length != 0) continue;
                    var found = TryInvoke(() => m.MakeGenericMethod(componentType), owner, null);
                    if (found != null) return found;
                }
            }

            foreach (var m in Extensions())
            {
                var ps = m.GetParameters();
                if (ps.Length != 1 || !ps[0].ParameterType.IsAssignableFrom(owner.GetType())) continue;
                var found = TryInvoke(() => m.MakeGenericMethod(componentType), null, new[] { owner });
                if (found != null) return found;
            }

            var go = GameAccess.MemberAny(owner, "GameObject", "gameObject") as GameObject;
            if (go != null)
            {
                var c = go.GetComponent(componentType);
                if (c != null) return c;
            }

            return null;
        }

        /// <summary>Method names mentioning "Component" on the type, for error messages when lookup fails.</summary>
        public static string Describe(object owner)
        {
            if (owner == null) return "null";
            var names = owner.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                .Where(m => m.Name.IndexOf("Component", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(m => m.Name).Distinct().Take(12);
            return owner.GetType().FullName + " [" + string.Join(", ", names) + "]";
        }

        private static object TryInvoke(Func<MethodInfo> make, object target, object[] args)
        {
            try
            {
                var result = make().Invoke(target, args);
                if (result is UnityEngine.Object u && !u) return null;
                return result;
            }
            catch { return null; }
        }

        private static List<MethodInfo> Extensions()
        {
            if (_extensions != null) return _extensions;

            var list = new List<MethodInfo>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name.IndexOf("BaseComponent", StringComparison.OrdinalIgnoreCase) < 0) continue;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }

                foreach (var t in types.Where(t => t.IsAbstract && t.IsSealed))
                    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                        if ((m.Name == "GetComponentFast" || m.Name == "GetComponent") && m.IsGenericMethodDefinition)
                            list.Add(m);
            }

            _extensions = list;
            return list;
        }
    }

    /// <summary>Reads stockpiles, districts, the map and the building list from the injected services.</summary>
    internal static class WorldReader
    {
        private const int MaxMapSide = 48;
        private const string Base36 = "0123456789abcdefghijklmnopqrstuvwxyz";

        private static AIWorldServices World()
        {
            var w = AIWorldServices.Instance;
            if (w == null) throw new InvalidOperationException("world services not bound (no save loaded, or no-world.flag present)");
            return w;
        }

        private static AIBuildServices Build()
        {
            var b = AIBuildServices.Instance;
            if (b == null) throw new InvalidOperationException("build services not bound (no save loaded, or no-build.flag present)");
            return b;
        }

        /// <summary>Total stock per good, summed over finished districts. Only goods that exist or have capacity.</summary>
        public static string Stock()
        {
            var world = World();

            var goodIds = new List<string>();
            object goodSample = null;
            int goodCount = 0;
            foreach (var good in GameAccess.Enumerate(world.Goods.Goods))
            {
                goodSample = goodSample ?? good;
                goodCount++;
                var id = good as string ?? GameAccess.MemberAny(good, "Id", "GoodId", "GoodID", "Key", "Name") as string; // IGoodService.Goods is a list of id strings
                if (id != null) goodIds.Add(id);
            }

            var totals = new Dictionary<string, int[]>();
            int districtsCounted = 0;
            foreach (var center in GameAccess.Enumerate(world.Districts.FinishedDistrictCenters))
            {
                var counter = Components.Get(center, typeof(DistrictResourceCounter))
                              ?? Components.Get(GameAccess.Member(center, "District"), typeof(DistrictResourceCounter));
                if (counter == null)
                    throw new InvalidOperationException("no DistrictResourceCounter found on district center: " + Components.Describe(center));

                districtsCounted++;
                foreach (var id in goodIds)
                {
                    if (!GameAccess.Invoke(counter, "GetResourceCount", out var count, id) || count == null) continue;

                    var row = totals.TryGetValue(id, out var existing) ? existing : (totals[id] = new int[3]);
                    row[0] += GameAccess.IntOf(GameAccess.Member(count, "AvailableStock"));
                    row[1] += GameAccess.IntOf(GameAccess.Member(count, "AllStock"));
                    row[2] += GameAccess.IntOf(GameAccess.Member(count, "TotalCapacity"));
                }
            }

            var entries = totals
                .Where(kv => kv.Value[1] > 0 || kv.Value[2] > 0)
                .OrderBy(kv => kv.Key)
                .Select(kv => Json.Str(kv.Key) + ":{\"available\":" + kv.Value[0] + ",\"all\":" + kv.Value[1] + ",\"capacity\":" + kv.Value[2] + "}");

            // Underscore keys explain an empty result: no goods known, no district counted,
            // or counters that haven't ticked yet because the game is paused.
            var all = new List<string>
            {
                "\"_goods_checked\":" + goodIds.Count,
                "\"_districts_counted\":" + districtsCounted
            };
            if (goodIds.Count == 0)
            {
                all.Add("\"_good_count\":" + goodCount);
                all.Add("\"_good_sample_type\":" + Json.Str(goodSample == null ? null : goodSample.GetType().FullName));
                all.Add("\"_good_sample\":" + Describer.Describe(goodSample));
            }
            all.AddRange(entries);
            return "{" + string.Join(",", all) + "}";
        }

        /// <summary>Finished district centers with their coordinates (z is height).</summary>
        public static string Districts()
        {
            var world = World();
            var items = new List<string>();

            foreach (var center in GameAccess.Enumerate(world.Districts.FinishedDistrictCenters))
            {
                var name = GameAccess.Member(center, "DistrictName") as string ?? "";
                var at = GameAccess.Member(center, "CenterCoordinates");
                var v = at is Vector3Int c ? c : Vector3Int.zero;
                items.Add("{\"name\":" + Json.Str(name) + ",\"x\":" + v.x + ",\"y\":" + v.y + ",\"z\":" + v.z + "}");
            }

            return "[" + string.Join(",", items) + "]";
        }

        /// <summary>
        /// A rectangular window of the map as two grids. heights: base-36 digit per cell
        /// (top terrain level, '-' = none). water: '~' where any water column exists.
        /// Defaults to a window centred on the first district center.
        /// </summary>
        public static string Map(NameValueCollection q)
        {
            var world = World();
            var size = world.Terrain.Size;

            int w = Clamp(Int(q, "w", 32), 1, MaxMapSide);
            int h = Clamp(Int(q, "h", 32), 1, MaxMapSide);

            int cx = size.x / 2, cy = size.y / 2;
            foreach (var center in GameAccess.Enumerate(world.Districts.AllDistrictCenters))
            {
                if (GameAccess.Member(center, "CenterCoordinates") is Vector3Int c) { cx = c.x; cy = c.y; break; }
            }

            int x0 = Clamp(Int(q, "x", cx - w / 2), 0, Math.Max(0, size.x - 1));
            int y0 = Clamp(Int(q, "y", cy - h / 2), 0, Math.Max(0, size.y - 1));
            w = Math.Min(w, size.x - x0);
            h = Math.Min(h, size.y - y0);

            var heightRows = new List<string>();
            var waterRows = new List<string>();
            string heightType = null;

            for (int y = y0; y < y0 + h; y++)
            {
                var hr = new StringBuilder();
                var wr = new StringBuilder();

                for (int x = x0; x < x0 + w; x++)
                {
                    int top = -1;
                    foreach (var level in GameAccess.Enumerate(world.Terrain.GetAllHeightsInCell(new Vector2Int(x, y))))
                    {
                        if (heightType == null && level != null) heightType = level.GetType().FullName;
                        top = Math.Max(top, HeightOf(level));
                    }

                    hr.Append(top < 0 ? '-' : Base36[Math.Min(top, Base36.Length - 1)]);
                    wr.Append(world.Water.IsWaterOnAnyHeight(new Vector2Int(x, y)) ? '~' : '.');
                }

                heightRows.Add(Json.Str(hr.ToString()));
                waterRows.Add(Json.Str(wr.ToString()));
            }

            int centerSurface = -1;
            foreach (var level in GameAccess.Enumerate(world.Terrain.GetAllHeightsInCell(new Vector2Int(cx, cy))))
                centerSurface = Math.Max(centerSurface, HeightOf(level));

            string reachJson;
            try { reachJson = ReachLayer(world, x0, y0, w, h); }
            catch (Exception e) { reachJson = "{\"error\":" + Json.Str(e.GetType().Name + ": " + e.Message) + "}"; }

            string objectsJson, legendJson;
            try { ObjectLayer(x0, y0, w, h, out objectsJson, out legendJson); }
            catch (Exception e)
            {
                objectsJson = "[]";
                legendJson = "{\"error\":" + Json.Str(e.GetType().Name + ": " + e.Message) + "}";
            }

            return "{\"center\":{\"x\":" + cx + ",\"y\":" + cy + ",\"surface_z\":" + centerSurface + "}"
                 + ",\"objects\":" + objectsJson + ",\"object_legend\":" + legendJson
                 + ",\"reachable\":" + reachJson
                 + ",\"origin\":{\"x\":" + x0 + ",\"y\":" + y0 + "},\"width\":" + w + ",\"height\":" + h
                 + ",\"map_size\":{\"x\":" + size.x + ",\"y\":" + size.y + ",\"z\":" + size.z + "}"
                 + ",\"height_element_type\":" + Json.Str(heightType)
                 + ",\"legend\":\"rows are y ascending, columns x ascending; heights base36, - none; water ~; objects: a letter per kind (see object_legend), . = nothing on the cell; reachable: # = beavers can walk here from the district center on one level, . = not reachable (other level, water, trees)\""
                 + ",\"heights\":[" + string.Join(",", heightRows) + "]"
                 + ",\"water\":[" + string.Join(",", waterRows) + "]}";
        }

        /// <summary>
        /// GetAllHeightsInCell's element type isn't confirmed (a plain int cast failed).
        /// Accept an int-like value, or a struct exposing a top/ceiling/height member, or
        /// fall back to the largest int property. The first element type seen is reported
        /// as height_element_type so this can be pinned down.
        /// </summary>
        private static int HeightOf(object level)
        {
            if (level == null) return -1;
            if (level is Vector3Int cell) return cell.z; // the returned cell z is already the surface level (district center z=2 validates at z=2)
            if (level is int i) return i;
            if (level is IConvertible && !(level is string))
            {
                try { return Convert.ToInt32(level, CultureInfo.InvariantCulture); } catch { }
            }

            foreach (var name in new[] { "Ceiling", "Top", "Height", "Max", "Upper", "End" })
            {
                var v = GameAccess.Member(level, name);
                if (v is int n) return n;
            }

            int best = int.MinValue;
            foreach (var p in level.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.PropertyType != typeof(int) || p.GetIndexParameters().Length > 0) continue;
                try { best = Math.Max(best, (int)p.GetValue(level)); } catch { }
            }

            if (best != int.MinValue) return best;
            throw new InvalidOperationException("cannot read a height from " + level.GetType().FullName + " " + Describer.Describe(level));
        }

        private static readonly Dictionary<Type, MethodInfo> BlockObjectGetters = new Dictionary<Type, MethodInfo>();

        /// <summary>Ground cell an entity stands on, from its BlockObject. False for things without one.</summary>
        internal static bool EntityCell(object entity, out Vector3Int cell)
        {
            var blockObject = BlockOf(entity);
            cell = blockObject == null ? default(Vector3Int) : blockObject.Coordinates;
            return blockObject != null;
        }

        /// <summary>
        /// The (x, y) cells of every placed building's entrance and doorstep. New buildings must not
        /// be put on these, or they block a door that paths need to reach.
        /// </summary>
        internal static HashSet<long> EntranceCells()
        {
            var set = new HashSet<long>();
            var core = AIGameServices.Instance;
            if (core == null) return set;

            foreach (var entity in GameAccess.Enumerate(core.Entities.Entities))
            {
                var name = StateReader.EntityName(entity);
                if (name.IndexOf('.') < 0) continue; // buildings are named like "SmallWarehouse.Folktails"

                try
                {
                    var block = BlockOf(entity);
                    if (block == null || !block.HasEntrance) continue;

                    var entrance = block.PositionedEntrance;
                    foreach (var field in new[] { "Coordinates", "DoorstepCoordinates" })
                    {
                        if (GameAccess.Member(entrance, field) is Vector3Int v) set.Add((long)v.y * 100000L + v.x);
                    }
                }
                catch { }
            }

            return set;
        }

        /// <summary>The doorstep cell of the first finished district center, where a path to the settlement must end.</summary>
        internal static bool DistrictDoorstep(out int x, out int y)
        {
            x = y = 0;
            var core = AIGameServices.Instance;
            if (core == null) return false;

            // Read the district center the same way placed_buildings does: through its entity.
            foreach (var center in GameAccess.Enumerate(core.Entities.Entities))
            {
                if (!StateReader.EntityName(center).StartsWith("DistrictCenter", StringComparison.Ordinal)) continue;

                try
                {
                    var block = BlockOf(center);
                    if (block != null && block.HasEntrance
                        && GameAccess.Member(block.PositionedEntrance, "Coordinates") is Vector3Int door)
                    {
                        x = door.x;
                        y = door.y;
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        /// <summary>The BlockObject (position, orientation, entrance) of an entity, or null if it has none.</summary>
        internal static Timberborn.BlockSystem.BlockObject BlockOf(object entity)
        {
            if (entity == null) return null;

            object block = null;
            try
            {
                var type = entity.GetType();
                if (!BlockObjectGetters.TryGetValue(type, out var getter))
                {
                    getter = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        .Where(m => m.Name == "GetComponent" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
                        .Select(m => m.MakeGenericMethod(typeof(Timberborn.BlockSystem.BlockObject)))
                        .FirstOrDefault();
                    BlockObjectGetters[type] = getter;
                }

                block = getter != null ? getter.Invoke(entity, null) : null;
            }
            catch { block = null; } // GetComponent throws for entities with no BlockObject

            if (block == null) block = Components.Get(entity, typeof(Timberborn.BlockSystem.BlockObject));
            return block as Timberborn.BlockSystem.BlockObject;
        }

        /// <summary>
        /// Buildings you have placed: position, facing, whether finished, and the entrance. A path
        /// must end on the entrance cell for beavers to get in. The entrance is dumped with
        /// Describer, so its real field names show up here the first time it is read.
        /// </summary>
        public static string Placed()
        {
            var core = AIGameServices.Instance;
            var build = AIBuildServices.Instance;
            if (core == null || build == null) throw new InvalidOperationException("services not bound");

            var names = new HashSet<string>();
            foreach (var spec in GameAccess.Enumerate(build.Buildings.Buildings))
            {
                if (GameAccess.Invoke(build.Buildings, "GetTemplateName", out var n, spec) && n is string s) names.Add(s);
            }

            var items = new List<string>();
            foreach (var entity in GameAccess.Enumerate(core.Entities.Entities))
            {
                var name = StateReader.EntityName(entity);
                if (!names.Contains(name) || name == "Path") continue;

                var block = BlockOf(entity);
                if (block == null) continue;

                string entrance = "null", access = "null";
                try
                {
                    if (block.HasEntrance)
                    {
                        entrance = Describer.Describe(block.PositionedEntrance);
                        if (GameAccess.Member(block.PositionedEntrance, "Coordinates") is Vector3Int cell)
                            access = "{\"x\":" + cell.x + ",\"y\":" + cell.y + ",\"z\":" + cell.z + "}";
                    }
                }
                catch { entrance = "null"; access = "null"; }

                // A gatherer flag does nothing until a good is chosen; an empty value means "No good selected".
                string extra = "";
                if (name.StartsWith("GathererFlag", StringComparison.Ordinal))
                {
                    try
                    {
                        extra = ",\"gathering\":" + Json.Str(GathererCommands.CurrentChoice(GathererCommands.DropdownOf(entity)));
                    }
                    catch { extra = ""; }
                }

                var at = block.Coordinates;
                items.Add("{\"name\":" + Json.Str(name)
                        + ",\"x\":" + at.x + ",\"y\":" + at.y + ",\"z\":" + at.z
                        + ",\"orientation\":" + Json.Str(block.Orientation.ToString())
                        + ",\"finished\":" + (block.IsFinished ? "true" : "false")
                        + extra
                        + ",\"problems\":" + StatusReader.ProblemsJson(entity)
                        + ",\"access_cell\":" + access
                        + ",\"entrance\":" + entrance + "}");
                if (items.Count >= 80) break;
            }

            return "[" + string.Join(",", items) + "]";
        }

        /// <summary>
        /// What stands on each cell of the window: trees, bushes, ruins, buildings. One letter
        /// per kind, most common first, with a legend. Beavers are skipped (they move).
        /// Only the cell a multi-cell building is anchored on is marked.
        /// </summary>
        private static void ObjectLayer(int x0, int y0, int w, int h, out string rowsJson, out string legendJson)
        {
            var core = AIGameServices.Instance;
            if (core == null) throw new InvalidOperationException("game services not bound");

            var cells = new Dictionary<long, string>();
            var counts = new Dictionary<string, int>();

            foreach (var entity in GameAccess.Enumerate(core.Entities.Entities))
            {
                var kind = StateReader.Collapse(StateReader.EntityName(entity));
                if (kind.StartsWith("Beaver", StringComparison.Ordinal)) continue;
                if (!EntityCell(entity, out var c)) continue;
                if (c.x < x0 || c.x >= x0 + w || c.y < y0 || c.y >= y0 + h) continue;

                cells[(long)c.y * 100000L + c.x] = kind;
                counts[kind] = counts.TryGetValue(kind, out var prior) ? prior + 1 : 1;
            }

            const string letters = "abcdefghijklmnopqrstuvwxyz";
            var letterFor = new Dictionary<string, char>();
            var legend = new List<string>();
            foreach (var kv in counts.OrderByDescending(k => k.Value))
            {
                if (letterFor.Count >= letters.Length) break;
                var letter = letters[letterFor.Count];
                letterFor[kv.Key] = letter;
                legend.Add(Json.Str(letter.ToString()) + ":" + Json.Str(kv.Key + " x" + kv.Value));
            }

            var rows = new List<string>();
            for (int y = y0; y < y0 + h; y++)
            {
                var row = new StringBuilder();
                for (int x = x0; x < x0 + w; x++)
                {
                    row.Append(cells.TryGetValue((long)y * 100000L + x, out var kind)
                        ? (letterFor.TryGetValue(kind, out var ch) ? ch : '*')
                        : '.');
                }
                rows.Add(Json.Str(row.ToString()));
            }

            rowsJson = "[" + string.Join(",", rows) + "]";
            legendJson = "{" + string.Join(",", legend) + "}";
        }

        /// <summary>
        /// Rows of '#' for cells beavers can walk to from the district center on one level and '.' for
        /// the rest. This is the level analysis: ground that is higher, lower, across water or behind
        /// trees is '.', and nothing placed there can be reached by a path.
        /// </summary>
        private static string ReachLayer(AIWorldServices world, int x0, int y0, int w, int h)
        {
            var build = AIBuildServices.Instance;
            if (build == null || !DistrictDoorstep(out var tx, out var ty)) return "null";

            var size = world.Terrain.Size;
            int minX = Math.Max(0, Math.Min(x0, tx) - 4), maxX = Math.Min(size.x - 1, Math.Max(x0 + w, tx) + 4);
            int minY = Math.Max(0, Math.Min(y0, ty) - 4), maxY = Math.Min(size.y - 1, Math.Max(y0 + h, ty) + 4);
            if (maxX - minX + 1 > 110 || maxY - minY + 1 > 110) return "null";

            var field = Connector.WalkingDistances(build, world, tx, ty, minX, maxX, minY, maxY);
            if (field == null) return "null";

            var rows = new List<string>();
            for (int y = y0; y < y0 + h; y++)
            {
                var row = new StringBuilder();
                for (int x = x0; x < x0 + w; x++) row.Append(field.ContainsKey(Connector.Key(x, y)) ? '#' : '.');
                rows.Add(Json.Str(row.ToString()));
            }
            return "[" + string.Join(",", rows) + "]";
        }

        /// <summary>
        /// Where entities whose name contains the given text are: position, height, facing. For finding the
        /// natural Slope pieces that join levels, or checking where a building ended up.
        /// </summary>
        public static string Find(NameValueCollection q)
        {
            var core = AIGameServices.Instance;
            if (core == null) throw new InvalidOperationException("game services not bound");

            var needle = q == null ? null : q["name"];
            if (string.IsNullOrEmpty(needle)) throw new ArgumentException("pass ?name=<part of an entity name>, for example ?name=Slope");
            int max = Math.Max(1, Math.Min(200, Int(q, "max", 60)));

            var items = new List<string>();
            int total = 0;
            foreach (var entity in GameAccess.Enumerate(core.Entities.Entities))
            {
                var name = StateReader.EntityName(entity);
                if (name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;

                var block = BlockOf(entity);
                if (block == null) continue;

                total++;
                if (items.Count >= max) continue;

                var at = block.Coordinates;
                items.Add("{\"name\":" + Json.Str(name) + ",\"x\":" + at.x + ",\"y\":" + at.y + ",\"z\":" + at.z
                          + ",\"orientation\":" + Json.Str(block.Orientation.ToString())
                          + ",\"base_z\":" + block.BaseZ + "}");
            }

            return "{\"total\":" + total + ",\"shown\":" + items.Count + ",\"found\":[" + string.Join(",", items) + "]}";
        }

        /// <summary>Building templates with unlock state. Unlocked only unless all=1.</summary>
        public static string Buildings(NameValueCollection q)
        {
            var build = Build();
            bool all = Int(q, "all", 0) == 1;

            var items = new List<string>();
            foreach (var spec in GameAccess.Enumerate(build.Buildings.Buildings))
            {
                if (!GameAccess.Invoke(build.Buildings, "GetTemplateName", out var nameObj, spec)) continue;
                var name = nameObj as string ?? "?";

                GameAccess.Invoke(build.Unlocking, "Unlocked", out var unlockedObj, spec);
                GameAccess.Invoke(build.Unlocking, "Unlockable", out var unlockableObj, spec);
                bool unlocked = unlockedObj is bool u && u;
                bool unlockable = unlockableObj is bool k && k;

                if (!all && !unlocked) continue;
                items.Add("{\"name\":" + Json.Str(name) + ",\"unlocked\":" + (unlocked ? "true" : "false")
                        + ",\"unlockable\":" + (unlockable ? "true" : "false")
                        + ",\"science_cost\":" + GameAccess.IntOf(GameAccess.Member(spec, "ScienceCost"))
                        + ",\"cost\":" + Cost(spec) + "}");
            }

            return "{\"science_points\":" + build.Science.SciencePoints + ",\"count\":" + items.Count
                 + ",\"buildings\":[" + string.Join(",", items) + "]}";
        }

        /// <summary>BuildingSpec.BuildingCost as a JSON list of readable GoodAmountSpec values.</summary>
        private static string Cost(object spec)
        {
            try
            {
                var parts = new List<string>();
                foreach (var amount in GameAccess.Enumerate(GameAccess.Member(spec, "BuildingCost")))
                    parts.Add(Describer.Describe(amount));
                return "[" + string.Join(",", parts) + "]";
            }
            catch { return "[]"; }
        }

        private static int Int(NameValueCollection q, string key, int fallback)
            => q != null && int.TryParse(q[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
