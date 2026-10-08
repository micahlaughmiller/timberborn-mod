using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Timberborn.BlockSystem;

namespace TimberbornAI
{
    /// <summary>
    /// Colony management controls: what a warehouse holds, how many workers a building asks for, building
    /// priority, and working hours. The game's classes behind these are not public or not yet confirmed, so
    /// every command finds its target component at runtime by member name, and when nothing fits it returns
    /// the components and members it did find, so the real API can be read off the reply.
    /// </summary>
    internal static class Management
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // ------------------------------------------------------------------ finding things

        internal static HashSet<string> BuildingNames(AIBuildServices build)
        {
            var names = new HashSet<string>();
            foreach (var spec in GameAccess.Enumerate(build.Buildings.Buildings))
            {
                if (GameAccess.Invoke(build.Buildings, "GetTemplateName", out var n, spec) && n is string s) names.Add(s);
            }
            return names;
        }

        /// <summary>The placed building whose anchor cell is (x, y).</summary>
        internal static bool PlacedAt(int x, int y, out object entity, out string name)
        {
            entity = null;
            name = null;

            var core = AIGameServices.Instance;
            var build = AIBuildServices.Instance;
            if (core == null || build == null) return false;

            var names = BuildingNames(build);
            foreach (var e in GameAccess.Enumerate(core.Entities.Entities))
            {
                var n = StateReader.EntityName(e);
                if (!names.Contains(n)) continue;

                var block = WorldReader.BlockOf(e);
                if (block == null) continue;

                var at = block.Coordinates;
                if (at.x != x || at.y != y) continue;

                entity = e;
                name = n;
                return true;
            }
            return false;
        }

        /// <summary>Every component object attached to an entity.</summary>
        internal static List<object> ComponentsOf(object entity)
        {
            var result = new List<object>();
            foreach (var member in new[] { "AllComponents", "RegisteredComponents" })
            {
                var sequence = GameAccess.Member(entity, member);
                if (sequence == null) continue;

                foreach (var c in GameAccess.Enumerate(sequence)) if (c != null) result.Add(c);
                if (result.Count > 0) break;
            }
            return result;
        }

        private static IEnumerable<MethodInfo> MethodsOf(Type t)
            => t.GetMethods(Any | BindingFlags.FlattenHierarchy).Where(m => !m.IsSpecialName);

        private static string Signature(MethodInfo m)
            => m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")";

        /// <summary>Components on the entity that have a readable or settable member with one of these names.</summary>
        private static object ComponentWithMember(object entity, params string[] names)
        {
            foreach (var c in ComponentsOf(entity))
            {
                var t = c.GetType();
                foreach (var name in names)
                {
                    if (t.GetProperty(name, Any) != null || t.GetField(name, Any) != null) return c;
                }
            }
            return null;
        }

        private static string Fail(string message) => Placer.Fail(message);

        private static string Where(int x, int y) => "at " + x + "," + y;

        // ------------------------------------------------------------------ discovery endpoints

        /// <summary>Every component on the building at ?x=&amp;y=, with readable values and method names.</summary>
        public static string ListComponents(NameValueCollection q)
        {
            int x = IntOf(q, "x"), y = IntOf(q, "y");
            if (!PlacedAt(x, y, out var entity, out var name))
                throw new ArgumentException("no placed building anchored at " + x + "," + y + " (use x and y from placed_buildings)");

            var items = new List<string>();
            foreach (var c in ComponentsOf(entity))
            {
                var t = c.GetType();
                var methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(m => !m.IsSpecialName).Select(Signature).Take(30);

                items.Add("{\"type\":" + Json.Str(t.FullName) + ",\"values\":" + Describer.Describe(c)
                          + ",\"methods\":[" + string.Join(",", methods.Select(Json.Str)) + "]}");
            }

            return "{\"name\":" + Json.Str(name) + ",\"components\":[" + string.Join(",", items) + "]}";
        }

        /// <summary>Constructors, properties, fields and methods of a game type, by simple name: ?type=Workplace.</summary>
        public static string Members(NameValueCollection q)
        {
            var typeName = q == null ? null : q["type"];
            if (string.IsNullOrEmpty(typeName)) throw new ArgumentException("pass ?type=<simple type name>");

            var type = GameAccess.FindType(typeName);
            if (type == null) throw new ArgumentException("no game type named " + typeName);

            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var props = type.GetProperties(all).Select(p => Json.Str(p.PropertyType.Name + " " + p.Name + (p.CanWrite ? " {set}" : "")));
            var fields = type.GetFields(all).Select(f => Json.Str(f.FieldType.Name + " " + f.Name));
            var methods = type.GetMethods(all).Where(m => !m.IsSpecialName).Select(m => Json.Str(m.ReturnType.Name + " " + Signature(m)));

            return "{\"type\":" + Json.Str(type.FullName) + ",\"properties\":[" + string.Join(",", props)
                 + "],\"fields\":[" + string.Join(",", fields) + "],\"methods\":[" + string.Join(",", methods) + "]}";
        }

        // ------------------------------------------------------------------ worker counts

        private static readonly string[] WorkerMembers = { "DesiredWorkers" };

        public static string SetWorkers(string body)
        {
            int x = Json.Int(body, "x", int.MinValue), y = Json.Int(body, "y", int.MinValue), count = Json.Int(body, "count", int.MinValue);
            if (x == int.MinValue || y == int.MinValue || count == int.MinValue)
                return Fail("set_workers requires x, y (from placed_buildings) and count");
            if (!PlacedAt(x, y, out var entity, out var name)) return Fail("no building " + Where(x, y));

            var workplace = ComponentWithMember(entity, WorkerMembers);
            if (workplace == null)
                return Fail(name + " has no worker setting. Components found: " + ComponentSummary(entity) + ". See inspect_components for their members.");

            int max = GameAccess.IntOf(GameAccess.MemberAny(workplace, "MaxWorkers", "MaxWorkerCount"), int.MaxValue);
            int before = GameAccess.IntOf(GameAccess.Member(workplace, "DesiredWorkers"), -1);
            int wanted = Math.Max(0, Math.Min(count, max));

            if (!TrySetInt(workplace, "DesiredWorkers", wanted, out var how))
                return Fail("could not set the worker count on " + name + ": " + how + ". Members: " + string.Join(", ", MethodsOf(workplace.GetType()).Select(Signature).Take(25)));

            int after = GameAccess.IntOf(GameAccess.Member(workplace, "DesiredWorkers"), -1);
            return Placer.Ok(name + " " + Where(x, y) + ": workers " + before + " -> " + after + (wanted != count ? " (limited to its maximum of " + max + ")" : ""));
        }

        private static bool TrySetInt(object target, string name, int value, out string how)
        {
            var type = target.GetType();

            var property = type.GetProperty(name, Any);
            if (property != null && property.CanWrite && property.PropertyType == typeof(int))
            {
                property.SetValue(target, value);
                how = "property " + name;
                return true;
            }

            foreach (var method in MethodsOf(type))
            {
                var ps = method.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == typeof(int) && method.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0
                    && method.Name.StartsWith("Set", StringComparison.OrdinalIgnoreCase))
                {
                    method.Invoke(target, new object[] { value });
                    how = "method " + method.Name;
                    return true;
                }
            }

            how = "no settable int " + name + " and no Set" + name + "(int) method";
            return false;
        }

        // ------------------------------------------------------------------ priority

        private static readonly string[] PriorityRanks = { "VeryLow", "Low", "Normal", "High", "VeryHigh" };

        public static string SetPriority(string body)
        {
            int x = Json.Int(body, "x", int.MinValue), y = Json.Int(body, "y", int.MinValue);
            var wanted = Json.Field(body, "priority");
            if (x == int.MinValue || y == int.MinValue || string.IsNullOrEmpty(wanted))
                return Fail("set_priority requires x, y (from placed_buildings) and priority (VeryLow, Low, Normal, High, VeryHigh)");
            if (!PlacedAt(x, y, out var entity, out var name)) return Fail("no building " + Where(x, y));

            if (!TryApplyPriority(entity, wanted, out var message)) return Fail(name + " " + Where(x, y) + ": " + message);
            return Placer.Ok(name + " " + Where(x, y) + ": " + message);
        }

        private static bool TryApplyPriority(object entity, string wanted, out string message)
        {
            foreach (var c in ComponentsOf(entity))
            {
                var type = c.GetType();

                // A settable enum property called Priority, or a SetPriority(enum) method.
                var property = type.GetProperty("Priority", Any);
                if (property != null && property.PropertyType.IsEnum)
                {
                    if (!TryEnum(property.PropertyType, wanted, out var value))
                    {
                        message = "unknown priority \"" + wanted + "\"; options: " + string.Join(", ", Enum.GetNames(property.PropertyType));
                        return false;
                    }

                    if (property.CanWrite) { property.SetValue(c, value); message = "priority set to " + value; return true; }
                }

                foreach (var method in MethodsOf(type))
                {
                    var ps = method.GetParameters();
                    if (ps.Length != 1 || !ps[0].ParameterType.IsEnum || method.Name.IndexOf("Priority", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (!method.Name.StartsWith("Set", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!TryEnum(ps[0].ParameterType, wanted, out var value)) continue;

                    method.Invoke(c, new[] { value });
                    message = "priority set to " + value;
                    return true;
                }
            }

            message = "no priority setting found. Components: " + ComponentSummary(entity);
            return false;
        }

        private static bool TryEnum(Type enumType, string text, out object value)
        {
            var flat = Normalize(text);
            foreach (var name in Enum.GetNames(enumType))
            {
                if (Normalize(name) == flat) { value = Enum.Parse(enumType, name); return true; }
            }
            value = null;
            return false;
        }

        private static string Normalize(string s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        // ------------------------------------------------------------------ policy: priorities and crews

        /// <summary>What a building is for, by its template name. Power that uses beavers counts with planks.</summary>
        internal static string Category(string name)
        {
            string n = name.ToLowerInvariant();
            if (n.Contains("gatherer") || n.Contains("farm") || n.Contains("grill") || n.Contains("bakery") || n.Contains("gristmill")
                || n.Contains("aquaticfarm") || n.Contains("mudpit")) return "food";
            if (n.Contains("waterpump") || n.Contains("aquiferdrill")) return "water";
            if (n.Contains("lumberjack") || n.Contains("forester") || n.Contains("tapper")) return "logs";
            if (n.Contains("lumbermill") || n.Contains("woodworkshop") || n.Contains("powerwheel")) return "planks";
            if (n.Contains("inventor") || n.Contains("observatory")) return "science";
            if (n.Contains("gearworkshop")) return "gears";
            if (n.Contains("scavenger")) return "scrap";
            if (n.Contains("badwater")) return "badwater";
            return "other";
        }

        private static string TierFor(string category)
        {
            switch (category)
            {
                case "food":
                case "water": return "VeryHigh";
                case "logs": return "High";
                case "planks": return "Normal";
                case "science":
                case "gears":
                case "scrap":
                case "badwater": return "Low";
                default: return "VeryLow";
            }
        }

        private static int Rank(string category)
        {
            switch (category)
            {
                case "food": return 0;
                case "water": return 1;
                case "logs": return 2;
                case "planks": return 3;
                case "science": return 4;
                case "gears": return 5;
                case "scrap": return 6;
                case "badwater": return 7;
                default: return 8;
            }
        }

        /// <summary>Applies the priority tiers to every placed building: food and water, logs, planks, then the rest.</summary>
        public static string ApplyPriorities()
        {
            var core = AIGameServices.Instance;
            var build = AIBuildServices.Instance;
            if (core == null || build == null) return Fail("no save loaded");

            var names = BuildingNames(build);
            int changed = 0, unsupported = 0;
            string lastFailure = null;
            var summary = new Dictionary<string, int>();

            foreach (var entity in GameAccess.Enumerate(core.Entities.Entities))
            {
                var name = StateReader.EntityName(entity);
                if (!names.Contains(name) || name == "Path") continue;

                var tier = TierFor(Category(name));
                if (TryApplyPriority(entity, tier, out var message))
                {
                    changed++;
                    summary[name + " -> " + tier] = summary.TryGetValue(name + " -> " + tier, out var n) ? n + 1 : 1;
                }
                else
                {
                    unsupported++;
                    lastFailure = name + ": " + message;
                }
            }

            return "{\"ok\":" + (changed > 0 || unsupported == 0 ? "true" : "false")
                 + ",\"detail\":" + Json.Str("set priority on " + changed + " buildings; " + unsupported + " have no priority setting")
                 + ",\"applied\":{" + string.Join(",", summary.Select(kv => Json.Str(kv.Key) + ":" + kv.Value)) + "}"
                 + (lastFailure != null ? ",\"example_unsupported\":" + Json.Str(lastFailure) : "") + "}";
        }

        private sealed class Job
        {
            public object Workplace;
            public string Name, Category;
            public int Max, X, Y;
        }

        /// <summary>
        /// Divides the adult beavers over the buildings. The district center gets 4 workers unless food and water
        /// buildings need them, never fewer than 2; food and water crews come first, then logs, planks, science,
        /// gears, scrap, badwater and the rest in that order.
        /// </summary>
        public static string ManageWorkers()
        {
            var core = AIGameServices.Instance;
            var build = AIBuildServices.Instance;
            if (core == null || build == null) return Fail("no save loaded");

            int adults = core.Beavers.NumberOfAdults;
            var names = BuildingNames(build);
            var jobs = new List<Job>();
            Job center = null;

            foreach (var entity in GameAccess.Enumerate(core.Entities.Entities))
            {
                var name = StateReader.EntityName(entity);
                if (!names.Contains(name) || name == "Path") continue;

                var block = WorldReader.BlockOf(entity);
                if (block == null || !block.IsFinished) continue;

                var workplace = ComponentWithMember(entity, WorkerMembers);
                if (workplace == null) continue;

                int max = GameAccess.IntOf(GameAccess.MemberAny(workplace, "MaxWorkers", "MaxWorkerCount"), 0);
                if (max <= 0) continue;

                var at = block.Coordinates;
                var job = new Job { Workplace = workplace, Name = name, Category = Category(name), Max = max, X = at.x, Y = at.y };
                if (name.StartsWith("DistrictCenter", StringComparison.Ordinal)) center = job; else jobs.Add(job);
            }

            int foodWater = jobs.Where(j => j.Category == "food" || j.Category == "water").Sum(j => j.Max);
            int centerWorkers = 0;
            if (center != null) centerWorkers = Math.Min(center.Max, Math.Max(2, Math.Min(4, adults - foodWater)));

            int remaining = Math.Max(0, adults - centerWorkers);
            var plan = new List<string>();

            if (center != null)
            {
                SetDesired(center, centerWorkers);
                plan.Add("{\"name\":" + Json.Str(center.Name) + ",\"workers\":" + centerWorkers + "}");
            }

            foreach (var job in jobs.OrderBy(j => Rank(j.Category)).ThenBy(j => j.Name))
            {
                int give = Math.Min(job.Max, remaining);
                remaining -= give;
                SetDesired(job, give);
                plan.Add("{\"name\":" + Json.Str(job.Name) + ",\"x\":" + job.X + ",\"y\":" + job.Y + ",\"category\":" + Json.Str(job.Category) + ",\"workers\":" + give + "}");
            }

            return "{\"ok\":true,\"detail\":" + Json.Str("divided " + adults + " adult beavers over " + (jobs.Count + (center != null ? 1 : 0)) + " workplaces; " + remaining + " left unassigned")
                 + ",\"plan\":[" + string.Join(",", plan) + "]}";
        }

        private static void SetDesired(Job job, int count)
        {
            try { TrySetInt(job.Workplace, "DesiredWorkers", count, out _); }
            catch { /* a building that rejects the change keeps its current crew */ }
        }

        // ------------------------------------------------------------------ storage contents

        public static string SetStorage(string body)
        {
            int x = Json.Int(body, "x", int.MinValue), y = Json.Int(body, "y", int.MinValue);
            var wanted = Json.Field(body, "good");
            if (x == int.MinValue || y == int.MinValue || string.IsNullOrEmpty(wanted))
                return Fail("set_storage requires x, y (from placed_buildings) and good (for example Berries, Water, Log)");
            if (!PlacedAt(x, y, out var entity, out var name)) return Fail("no building " + Where(x, y));

            var world = AIWorldServices.Instance;
            if (world == null) return Fail("world services not bound");

            var ids = new List<string>();
            foreach (var g in GameAccess.Enumerate(world.Goods.Goods)) if (g is string s) ids.Add(s);
            var id = ids.FirstOrDefault(i => string.Equals(i, wanted, StringComparison.OrdinalIgnoreCase))
                     ?? ids.FirstOrDefault(i => i.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0);
            if (id == null) return Fail("no good matching \"" + wanted + "\". Goods: " + string.Join(", ", ids));

            var tried = new List<string>();
            foreach (var c in ComponentsOf(entity))
            {
                var type = c.GetType();
                bool storageLike = type.Name.IndexOf("Stockpile", StringComparison.OrdinalIgnoreCase) >= 0
                                   || type.Name.IndexOf("Whitelist", StringComparison.OrdinalIgnoreCase) >= 0
                                   || type.Name.IndexOf("GoodSelect", StringComparison.OrdinalIgnoreCase) >= 0
                                   || type.Name.IndexOf("Allowed", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!storageLike) continue;

                // Best name first: a whitelist or "set good" call taking the good as an id or a GoodSpec.
                var candidates = MethodsOf(type)
                    .Where(m => m.GetParameters().Length == 1 && (m.Name.StartsWith("Set", StringComparison.OrdinalIgnoreCase)
                                                                   || m.Name.StartsWith("Select", StringComparison.OrdinalIgnoreCase)
                                                                   || m.Name.StartsWith("Allow", StringComparison.OrdinalIgnoreCase)
                                                                   || m.Name.StartsWith("Whitelist", StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(m => m.Name.IndexOf("Whitelist", StringComparison.OrdinalIgnoreCase) >= 0 ? 0
                                  : m.Name.IndexOf("Good", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 2);

                foreach (var method in candidates)
                {
                    var parameter = method.GetParameters()[0];
                    object argument = null;

                    if (parameter.ParameterType == typeof(string)) argument = id;
                    else if (parameter.ParameterType.Name == "GoodSpec") GameAccess.Invoke(world.Goods, "GetGood", out argument, id);
                    else continue;

                    tried.Add(type.Name + "." + Signature(method));
                    try
                    {
                        method.Invoke(c, new[] { argument });
                        return Placer.Ok(name + " " + Where(x, y) + " now holds " + id + " (via " + type.Name + "." + method.Name + "). Component values: " + Describer.Describe(c));
                    }
                    catch (Exception e)
                    {
                        tried.Add("  failed: " + Placer.Root(e).Message);
                    }
                }
            }

            return Fail("could not set what " + name + " holds. Tried: " + (tried.Count == 0 ? "nothing matched" : string.Join("; ", tried))
                        + ". Components found: " + ComponentSummary(entity) + ". Use inspect_components to see their members.");
        }

        // ------------------------------------------------------------------ working hours

        public static string SetWorkHours(string body)
        {
            int hours = Json.Int(body, "hours", int.MinValue);
            if (hours == int.MinValue) return Fail("set_work_hours requires hours (the game default is 16)");

            var build = AIBuildServices.Instance;
            if (build == null) return Fail("no save loaded");

            var matches = new List<Type>();
            foreach (var name in new[] { "WorkingHoursManager", "WorkingHoursService", "WorkingHours", "WorkingHoursSettings", "WorkHoursManager" })
            {
                var t = GameAccess.FindType(name);
                if (t != null) matches.Add(t);
            }
            if (matches.Count == 0)
                return Fail("no working-hours service found by the usual names. Search for it with /members?type=<name> once you know it, or tell me its name.");

            var attempts = new List<string>();
            foreach (var type in matches)
            {
                var instance = Resolve(build, type, out var resolveError);
                if (instance == null) { attempts.Add(type.Name + ": " + resolveError); continue; }

                // A settable int property or a Set...(int) method mentioning hours.
                foreach (var property in type.GetProperties(Any))
                {
                    if (property.PropertyType == typeof(int) && property.CanWrite && property.Name.IndexOf("Hours", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        property.SetValue(instance, hours);
                        return Placer.Ok("working hours set to " + hours + " via " + type.Name + "." + property.Name);
                    }
                }

                foreach (var method in MethodsOf(type))
                {
                    var ps = method.GetParameters();
                    if (ps.Length == 1 && (ps[0].ParameterType == typeof(int) || ps[0].ParameterType == typeof(float))
                        && method.Name.IndexOf("Hours", StringComparison.OrdinalIgnoreCase) >= 0
                        && method.Name.StartsWith("Set", StringComparison.OrdinalIgnoreCase))
                    {
                        method.Invoke(instance, new[] { ps[0].ParameterType == typeof(int) ? (object)hours : (float)hours });
                        return Placer.Ok("working hours set to " + hours + " via " + type.Name + "." + method.Name);
                    }
                }

                attempts.Add(type.Name + " found but nothing to set; members: " + string.Join(", ", MethodsOf(type).Select(Signature).Take(25)));
            }

            return Fail("could not change the working hours. " + string.Join(" | ", attempts));
        }

        /// <summary>Asks the game's own container for an instance of a service, by type.</summary>
        private static object Resolve(AIBuildServices build, Type type, out string error)
        {
            error = null;
            var container = build.Container;
            if (container == null) { error = "no container"; return null; }

            foreach (var method in container.GetType().GetMethods(Any))
            {
                if (method.Name != "GetInstance" && method.Name != "Resolve" && method.Name != "Get") continue;
                var ps = method.GetParameters();
                try
                {
                    if (method.IsGenericMethodDefinition && ps.Length == 0) return method.MakeGenericMethod(type).Invoke(container, null);
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(Type)) return method.Invoke(container, new object[] { type });
                }
                catch (Exception e) { error = Placer.Root(e).Message; }
            }

            error = error ?? "the container has no GetInstance/Resolve: " + string.Join(", ", container.GetType().GetMethods(Any).Select(m => m.Name).Distinct().Take(20));
            return null;
        }

        // ------------------------------------------------------------------ helpers

        private static string ComponentSummary(object entity)
            => string.Join(", ", ComponentsOf(entity).Select(c => c.GetType().Name).Distinct().Take(40));

        private static int IntOf(NameValueCollection q, string key)
        {
            if (q != null && int.TryParse(q[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
            throw new ArgumentException("pass ?" + key + "=<number>");
        }
    }
}
