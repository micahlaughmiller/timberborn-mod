using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace TimberbornAI
{
    /// <summary>
    /// Builds the JSON world snapshot the agent reasons over, from the live game
    /// services injected into AIGameServices. Compact on purpose: an LLM reads
    /// this every turn, so it carries decision-relevant signal only.
    ///
    /// Each section is isolated, so one failing read becomes an entry in "errors"
    /// instead of taking down the whole snapshot.
    /// </summary>
    internal static class StateReader
    {
        private const int MaxEntities = 20000;
        private const int MaxEntityKinds = 40;

        public static string Snapshot()
        {
            var svc = AIGameServices.Instance;
            if (svc == null)
                return "{\"in_game\":false,\"note\":\"no save loaded, or game services not bound yet\"}";

            var sb = new StringBuilder("{\"in_game\":true");
            var errors = new List<string>();

            Section(sb, errors, "time", () =>
            {
                sb.Append(",\"cycle\":").Append(svc.Cycle.Cycle)
                  .Append(",\"cycle_day\":").Append(svc.Cycle.CycleDay)
                  .Append(",\"cycle_progress\":").Append(svc.Cycle.PartialCycleDay.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            });

            Section(sb, errors, "hazard", () =>
            {
                var current = svc.Hazard.CurrentCycleHazardousWeather;
                sb.Append(",\"hazard_type\":").Append(Json.Str(current == null ? "none" : current.GetType().Name))
                  .Append(",\"hazard_duration_days\":").Append(svc.Hazard.HazardousWeatherDuration);

                // Countdown to the next hazard. cycle_progress is the day number
                // within the cycle including the fraction, same scale as the start day.
                var startDay = GameAccess.IntOf(GameAccess.Member(svc.Weather, "HazardousWeatherStartCycleDay"));
                var cycleLength = GameAccess.IntOf(GameAccess.Member(svc.Weather, "CycleLengthInDays"));
                var activeNow = GameAccess.Member(svc.Weather, "IsHazardousWeather") is bool b && b;
                var progress = svc.Cycle.PartialCycleDay;
                var duration = svc.Hazard.HazardousWeatherDuration;

                var untilStart = Math.Max(0f, startDay - progress);
                var untilEnd = Math.Max(0f, startDay + duration - progress);

                sb.Append(",\"hazard_active\":").Append(activeNow ? "true" : "false")
                  .Append(",\"days_until_hazard\":").Append(Num(activeNow ? 0f : untilStart))
                  .Append(",\"hazard_days_left\":").Append(Num(activeNow ? untilEnd : 0f))
                  .Append(",\"cycle_length_days\":").Append(cycleLength);
            });

            Section(sb, errors, "beavers", () =>
            {
                sb.Append(",\"beavers\":").Append(svc.Beavers.NumberOfBeavers)
                  .Append(",\"adults\":").Append(svc.Beavers.NumberOfAdults)
                  .Append(",\"children\":").Append(svc.Beavers.NumberOfChildren);
            });

            Section(sb, errors, "entities", () =>
            {
                var counts = new Dictionary<string, int>();
                int seen = 0;
                foreach (var entity in GameAccess.Enumerate(svc.Entities.Entities))
                {
                    if (++seen > MaxEntities) break;
                    var name = Collapse(EntityName(entity));
                    counts[name] = counts.TryGetValue(name, out var prior) ? prior + 1 : 1;
                }

                sb.Append(",\"entity_total\":").Append(Math.Min(seen, MaxEntities))
                  .Append(",\"entities\":{")
                  .Append(string.Join(",", counts.OrderByDescending(kv => kv.Value)
                      .Take(MaxEntityKinds)
                      .Select(kv => Json.Str(kv.Key) + ":" + kv.Value)))
                  .Append('}');
            });

            Section(sb, errors, "districts", () => sb.Append(",\"districts\":").Append(WorldReader.Districts()));
            Section(sb, errors, "stock", () => sb.Append(",\"stock\":").Append(WorldReader.Stock()));
            Section(sb, errors, "science", () =>
            {
                var build = AIBuildServices.Instance;
                if (build != null) sb.Append(",\"science_points\":").Append(build.Science.SciencePoints);
            });

            // Members not yet confirmed: dump their readable properties so the real
            // names show up in /state without another lookup round.
            Section(sb, errors, "raw", () =>
            {
                sb.Append(",\"raw\":{")
                  .Append("\"weather_service\":").Append(Describer.Describe(svc.Weather)).Append(',')
                  .Append("\"speed_manager\":").Append(Describer.Describe(svc.Speed)).Append(',')
                  .Append("\"hazard_service\":").Append(Describer.Describe(svc.Hazard))
                  .Append('}');
            });

            sb.Append(",\"errors\":[").Append(string.Join(",", errors.Select(Json.Str))).Append(']');
            return sb.Append('}').ToString();
        }

        private static void Section(StringBuilder sb, List<string> errors, string name, Action body)
        {
            var mark = sb.Length;
            try { body(); }
            catch (Exception e)
            {
                sb.Length = mark; // drop any half-written fragment from this section
                errors.Add(name + ": " + e.GetType().Name + ": " + e.Message);
                Debug.Log("[TimberbornAI] state section '" + name + "' failed: " + e);
            }
        }

        private static string Num(float v) => v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>"BeaverAdult Alzim" and "BeaverChild Azibo" count as one kind each, not one entry per beaver.</summary>
        private static string Collapse(string name)
        {
            var space = name.IndexOf(' ');
            return space > 0 && name.StartsWith("Beaver", StringComparison.Ordinal) ? name.Substring(0, space) : name;
        }

        /// <summary>Best-effort readable name for an entity, whatever its component type exposes.</summary>
        private static string EntityName(object entity)
        {
            if (entity == null) return "null";

            var name = GameAccess.MemberAny(entity, "TemplateName", "PrefabName", "Name") as string;

            if (string.IsNullOrEmpty(name))
            {
                var go = GameAccess.MemberAny(entity, "GameObject", "gameObject") as GameObject;
                if (go != null) name = go.name;
            }

            if (string.IsNullOrEmpty(name)) name = entity.GetType().Name;

            const string clone = "(Clone)";
            return name.EndsWith(clone, StringComparison.Ordinal) ? name.Substring(0, name.Length - clone.Length) : name;
        }
    }
}
