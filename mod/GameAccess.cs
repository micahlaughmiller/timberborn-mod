using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace TimberbornAI
{
    /// <summary>
    /// All game-internal coupling lives here. Timberborn's internal type names
    /// change between updates, so nothing is hard-referenced: types are resolved
    /// by name at runtime and every lookup degrades to null instead of throwing.
    ///
    /// Fill CandidateNames from the output of GET /dump on your own install.
    /// </summary>
    internal static class GameAccess
    {
        private static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>();

        /// <summary>
        /// Candidate simple type names per logical concept, tried in order.
        /// Verified names for the install go first; the rest are fallbacks.
        /// </summary>
        public static readonly Dictionary<string, string[]> CandidateNames = new Dictionary<string, string[]>
        {
            ["inventory"]  = new[] { "Inventory", "GoodStack", "StorageInventory" },
            ["beaver"]     = new[] { "Beaver", "Citizen", "BeaverCharacter" },
            ["building"]   = new[] { "Building", "BuildingSpec", "Prefab" },
            ["weather"]    = new[] { "WeatherService", "DroughtService", "HazardousWeatherService" },
            ["water"]      = new[] { "WaterService", "WaterSimulator", "WaterMap" },
            ["population"] = new[] { "DistrictPopulation", "Population", "PopulationService" },
            ["daynight"]   = new[] { "DayNightCycle", "GameTimeService", "DayNightService" },
            ["placer"]     = new[] { "BlockObjectPlacer", "BuildingPlacer", "PlacementService" },
        };

        /// <summary>Resolves the first candidate type that exists in any loaded assembly.</summary>
        public static Type Resolve(string concept)
        {
            if (TypeCache.TryGetValue(concept, out var cached)) return cached;

            Type found = null;
            if (CandidateNames.TryGetValue(concept, out var names))
            {
                var all = AppDomain.CurrentDomain.GetAssemblies();
                foreach (var name in names)
                {
                    foreach (var asm in all)
                    {
                        Type[] types;
                        try { types = asm.GetTypes(); }
                        catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }

                        found = types.FirstOrDefault(t => t.Name == name);
                        if (found != null) break;
                    }
                    if (found != null) break;
                }
            }

            TypeCache[concept] = found;
            return found;
        }

        /// <summary>All live scene components of the resolved type for a concept.</summary>
        public static UnityEngine.Object[] FindAll(string concept)
        {
            var t = Resolve(concept);
            if (t == null || !typeof(UnityEngine.Object).IsAssignableFrom(t))
                return Array.Empty<UnityEngine.Object>();
            return UnityEngine.Object.FindObjectsOfType(t);
        }

        public static UnityEngine.Object FindOne(string concept) => FindAll(concept).FirstOrDefault();

        /// <summary>Reads a property or field by name. Returns null rather than throwing on any miss.</summary>
        public static object Member(object target, string name)
        {
            if (target == null) return null;
            var t = target.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            try
            {
                var p = t.GetProperty(name, flags);
                if (p != null && p.CanRead) return p.GetValue(target);
                var f = t.GetField(name, flags);
                if (f != null) return f.GetValue(target);
            }
            catch { /* property getters can throw when the game is mid-load */ }

            return null;
        }

        /// <summary>Tries several member names and returns the first non-null result.</summary>
        public static object MemberAny(object target, params string[] names)
        {
            foreach (var n in names)
            {
                var v = Member(target, n);
                if (v != null) return v;
            }
            return null;
        }

        public static int IntOf(object v, int fallback = 0)
        {
            try { return v == null ? fallback : Convert.ToInt32(v); }
            catch { return fallback; }
        }

        public static float FloatOf(object v, float fallback = 0f)
        {
            try { return v == null ? fallback : Convert.ToSingle(v); }
            catch { return fallback; }
        }

        public static IEnumerable Enumerate(object v) => v as IEnumerable ?? Array.Empty<object>();

        /// <summary>Invokes a zero-or-more-arg method by name if it exists. Returns false if not found.</summary>
        public static bool Invoke(object target, string method, out object result, params object[] args)
        {
            result = null;
            if (target == null) return false;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            var m = target.GetType()
                .GetMethods(flags)
                .FirstOrDefault(x => x.Name == method && x.GetParameters().Length == args.Length);
            if (m == null) return false;

            result = m.Invoke(target, args);
            return true;
        }
    }
}
