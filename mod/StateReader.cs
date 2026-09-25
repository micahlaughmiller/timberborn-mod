using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TimberbornAI
{
    /// <summary>
    /// Builds the JSON world snapshot the agent reasons over. Deliberately
    /// compact: an LLM reads this every tick, so it carries decision-relevant
    /// signal only, not a full entity dump.
    /// </summary>
    internal static class StateReader
    {
        public static string Snapshot()
        {
            var sb = new StringBuilder("{");
            var unresolved = new List<string>();

            // --- time / weather -------------------------------------------------
            var weather = GameAccess.FindOne("weather");
            if (weather == null) unresolved.Add("weather");
            sb.Append("\"cycle\":").Append(GameAccess.IntOf(
                GameAccess.MemberAny(weather, "Cycle", "CycleNumber", "CurrentCycle")));
            sb.Append(",\"cycle_day\":").Append(GameAccess.IntOf(
                GameAccess.MemberAny(weather, "CycleDay", "DayNumber", "Day")));
            sb.Append(",\"is_drought\":").Append(
                GameAccess.MemberAny(weather, "IsDrought", "DroughtStarted") is bool d && d ? "true" : "false");
            sb.Append(",\"days_until_drought\":").Append(GameAccess.IntOf(
                GameAccess.MemberAny(weather, "DaysUntilDrought", "TemperateWeatherDaysLeft"), -1));

            // --- population -----------------------------------------------------
            var beavers = GameAccess.FindAll("beaver");
            if (beavers.Length == 0) unresolved.Add("beaver");
            sb.Append(",\"beavers\":").Append(beavers.Length);

            int hungry = 0, thirsty = 0, homeless = 0;
            foreach (var b in beavers)
            {
                if (GameAccess.FloatOf(GameAccess.MemberAny(b, "Hunger", "FoodLevel"), 1f) < 0.3f) hungry++;
                if (GameAccess.FloatOf(GameAccess.MemberAny(b, "Thirst", "WaterLevel"), 1f) < 0.3f) thirsty++;
                if (GameAccess.MemberAny(b, "Dwelling", "Home") == null) homeless++;
            }
            sb.Append(",\"hungry\":").Append(hungry)
              .Append(",\"thirsty\":").Append(thirsty)
              .Append(",\"homeless\":").Append(homeless);

            // --- stored goods ---------------------------------------------------
            var totals = new Dictionary<string, int>();
            foreach (var inv in GameAccess.FindAll("inventory"))
            {
                var stock = GameAccess.MemberAny(inv, "Stock", "Goods", "Amounts");
                foreach (var entry in GameAccess.Enumerate(stock))
                {
                    var good = GameAccess.MemberAny(entry, "GoodId", "Id", "Key") as string;
                    if (good == null) continue;
                    int amount = GameAccess.IntOf(GameAccess.MemberAny(entry, "Amount", "Value", "Count"));
                    totals[good] = totals.TryGetValue(good, out var prior) ? prior + amount : amount;
                }
            }
            if (totals.Count == 0) unresolved.Add("inventory");

            sb.Append(",\"goods\":{").Append(string.Join(",", totals
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{Json.Str(kv.Key)}:{kv.Value}"))).Append('}');

            // --- buildings ------------------------------------------------------
            var byKind = new Dictionary<string, int>();
            foreach (var b in GameAccess.FindAll("building"))
            {
                var name = GameAccess.MemberAny(b, "PrefabName", "Name", "Id") as string ?? "unknown";
                byKind[name] = byKind.TryGetValue(name, out var prior) ? prior + 1 : 1;
            }
            if (byKind.Count == 0) unresolved.Add("building");

            sb.Append(",\"buildings\":{").Append(string.Join(",", byKind
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{Json.Str(kv.Key)}:{kv.Value}"))).Append('}');

            // Surfaced so the agent can report a broken binding instead of
            // silently reasoning over zeros after a game update.
            sb.Append(",\"unresolved_bindings\":[")
              .Append(string.Join(",", unresolved.Select(Json.Str)))
              .Append(']');

            return sb.Append('}').ToString();
        }
    }
}
