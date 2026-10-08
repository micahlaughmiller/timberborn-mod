using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;

namespace TimberbornAI
{
    /// <summary>
    /// Lets the agent read the game's own data instead of relying on assumptions: the full component
    /// specs of any building (workers, inputs, outputs, capacities, costs) and every spec of a given
    /// type (for example NeedSpec, what beavers need). The specs are plain data objects, so they are
    /// dumped by walking their public properties to a bounded depth.
    /// </summary>
    internal static class SpecReader
    {
        private const int MaxChars = 18000;
        private const int MaxListItems = 16;

        private sealed class RefComparer : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => RuntimeHelpers.GetHashCode(o);
        }

        /// <summary>Every component spec of one building template, e.g. ?name=WaterPump.Folktails.</summary>
        public static string Inspect(NameValueCollection q)
        {
            var name = q == null ? null : q["name"];
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("pass ?name=<building name from /buildings>");

            var build = AIBuildServices.Instance;
            if (build == null) throw new InvalidOperationException("build services not bound");
            if (!Placer.TryGetBlueprint(build, name, out var blueprint, out var error)) throw new ArgumentException(error);

            var parts = new List<string>();
            foreach (var spec in blueprint.Specs)
            {
                if (spec == null) continue;
                parts.Add("{\"type\":" + Json.Str(spec.GetType().Name) + ",\"values\":" + Deep(spec, 3, new HashSet<object>(new RefComparer())) + "}");
            }

            return Cap("{\"name\":" + Json.Str(name) + ",\"specs\":[" + string.Join(",", parts) + "]}");
        }

        /// <summary>All specs of one type, e.g. ?type=NeedSpec or ?type=GoodSpec.</summary>
        public static string OfType(NameValueCollection q)
        {
            var typeName = q == null ? null : q["type"];
            if (string.IsNullOrEmpty(typeName)) throw new ArgumentException("pass ?type=<spec type name>, for example NeedSpec or GoodSpec");

            var build = AIBuildServices.Instance;
            if (build == null) throw new InvalidOperationException("build services not bound");

            var type = GameAccess.FindType(typeName);
            if (type == null) throw new ArgumentException("no game type named " + typeName);

            var method = build.Specs.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "GetSpecs" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            if (method == null) throw new InvalidOperationException("the spec service has no GetSpecs<T>()");

            int max = 40;
            if (int.TryParse(q["max"], out var asked)) max = Math.Max(1, Math.Min(200, asked));

            var items = new List<string>();
            int total = 0;
            foreach (var item in GameAccess.Enumerate(method.MakeGenericMethod(type).Invoke(build.Specs, null)))
            {
                total++;
                if (items.Count < max) items.Add(Deep(item, 3, new HashSet<object>(new RefComparer())));
            }

            return Cap("{\"type\":" + Json.Str(type.FullName) + ",\"total\":" + total + ",\"shown\":" + items.Count
                       + ",\"items\":[" + string.Join(",", items) + "]}");
        }

        private static string Cap(string json)
        {
            if (json.Length <= MaxChars) return json;
            return "{\"truncated\":true,\"note\":\"output was longer than " + MaxChars + " characters; ask for a narrower query (a single name, or a smaller max)\",\"start\":"
                   + Json.Str(json.Substring(0, MaxChars)) + "}";
        }

        private static bool IsLeaf(Type t)
            => t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(Guid) || t == typeof(decimal)
               || t == typeof(Vector3Int) || t == typeof(Vector2Int) || t == typeof(Vector3) || t == typeof(Vector2);

        private static readonly HashSet<string> Skipped = new HashSet<string>
        {
            "Blueprint", "GameObject", "Transform", "gameObject", "transform", "Prefab", "Sprite", "Texture", "Material", "Mesh"
        };

        /// <summary>Walks public readable properties of game data objects to a bounded depth.</summary>
        private static string Deep(object o, int depth, HashSet<object> seen)
        {
            if (o == null) return "null";

            if (o is Guid guid) return Json.Str(guid.ToString());

            var type = o.GetType();
            if (IsLeaf(type)) return Describer.Value(o);
            if (depth <= 0) return Json.Str("<" + type.Name + ">");

            if (!type.IsValueType && !seen.Add(o)) return Json.Str("<repeat>");

            if (o is IEnumerable sequence && !(o is string))
            {
                var items = new List<string>();
                int extra = 0;
                foreach (var item in sequence)
                {
                    if (items.Count < MaxListItems) items.Add(Deep(item, depth - 1, seen));
                    else extra++;
                }
                if (extra > 0) items.Add(Json.Str("(+" + extra + " more)"));
                return "[" + string.Join(",", items) + "]";
            }

            // Only walk the game's own data; Unity and system objects are named, not opened.
            var ns = type.Namespace ?? "";
            if (!ns.StartsWith("Timberborn", StringComparison.Ordinal) && !ns.StartsWith("Bindito", StringComparison.Ordinal)
                && !(type.IsGenericType && type.Name.StartsWith("KeyValuePair", StringComparison.Ordinal)))
                return Json.Str(SafeToString(o));

            var fields = new List<string>();
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0 || Skipped.Contains(p.Name)) continue;

                object value;
                try { value = p.GetValue(o); }
                catch { continue; }

                fields.Add(Json.Str(p.Name) + ":" + Deep(value, depth - 1, seen));
            }

            return "{" + string.Join(",", fields) + "}";
        }

        private static string SafeToString(object o)
        {
            try
            {
                var s = o.ToString() ?? o.GetType().Name;
                return s.Length > 80 ? s.Substring(0, 80) : s;
            }
            catch { return o.GetType().Name; }
        }
    }
}
