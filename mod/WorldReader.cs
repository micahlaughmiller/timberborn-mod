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

            return "{\"center\":{\"x\":" + cx + ",\"y\":" + cy + ",\"surface_z\":" + centerSurface + "}"
                 + ",\"origin\":{\"x\":" + x0 + ",\"y\":" + y0 + "},\"width\":" + w + ",\"height\":" + h
                 + ",\"map_size\":{\"x\":" + size.x + ",\"y\":" + size.y + ",\"z\":" + size.z + "}"
                 + ",\"height_element_type\":" + Json.Str(heightType)
                 + ",\"legend\":\"rows are y ascending, columns x ascending; heights base36, - none; water ~\""
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
