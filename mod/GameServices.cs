using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Bindito.Core;
using Timberborn.Beavers;
using Timberborn.EntitySystem;
using Timberborn.GameCycleSystem;
using Timberborn.HazardousWeatherSystem;
using Timberborn.SingletonSystem;
using Timberborn.TimeSystem;
using Timberborn.WeatherSystem;
using UnityEngine;

namespace TimberbornAI
{
    /// <summary>
    /// Registers AIGameServices in the game's "Game" DI context. The game finds
    /// every Configurator marked with [Context("Game")] in enabled mods and runs
    /// it each time a save loads.
    ///
    /// If a bad constructor binding ever stops saves from loading, create an empty
    /// file named no-di.flag next to TimberbornAI.dll to skip this registration.
    /// </summary>
    [Context("Game")]
    public class AIConfigurator : Configurator
    {
        protected override void Configure()
        {
            if (Flags.Has("no-di.flag"))
            {
                Debug.Log("[TimberbornAI] no-di.flag present: game services not bound");
                return;
            }

            Bind<AIGameServices>().AsSingleton();
        }
    }

    /// <summary>
    /// Holds the real game services. The game builds this object and injects the
    /// constructor arguments, so these are the live instances, not lookups.
    /// Only services known to be singletons belong here: asking the container for
    /// something it has no binding for makes the whole save fail to load.
    /// </summary>
    public class AIGameServices : ILoadableSingleton
    {
        public static AIGameServices Instance { get; private set; }

        public readonly GameCycleService Cycle;
        public readonly HazardousWeatherService Hazard;
        public readonly BeaverPopulation Beavers;
        public readonly EntityRegistry Entities;
        public readonly SpeedManager Speed;
        public readonly WeatherService Weather;

        public AIGameServices(
            GameCycleService cycle,
            HazardousWeatherService hazard,
            BeaverPopulation beavers,
            EntityRegistry entities,
            SpeedManager speed,
            WeatherService weather)
        {
            Cycle = cycle;
            Hazard = hazard;
            Beavers = beavers;
            Entities = entities;
            Speed = speed;
            Weather = weather;
        }

        public void Load()
        {
            Instance = this;
            Debug.Log("[TimberbornAI] game services bound");
        }
    }

    /// <summary>
    /// Dumps every public primitive/enum/string property of an object as JSON.
    /// Lets /state show members whose names we haven't confirmed yet (WeatherService,
    /// SpeedManager) without needing another lookup round.
    /// </summary>
    internal static class Describer
    {
        public static string Describe(object target)
        {
            if (target == null) return "null";

            var parts = new List<string>();
            foreach (var p in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;

                var t = p.PropertyType;
                if (!(t.IsPrimitive || t.IsEnum || t == typeof(string))) continue;

                object value;
                try { value = p.GetValue(target); }
                catch { continue; }

                parts.Add(Json.Str(p.Name) + ":" + Value(value));
            }

            return "{" + string.Join(",", parts) + "}";
        }

        private static string Value(object v)
        {
            switch (v)
            {
                case null: return "null";
                case bool b: return b ? "true" : "false";
                case string s: return Json.Str(s);
                case float f: return float.IsNaN(f) || float.IsInfinity(f) ? "null" : f.ToString("R", CultureInfo.InvariantCulture);
                case double d: return double.IsNaN(d) || double.IsInfinity(d) ? "null" : d.ToString("R", CultureInfo.InvariantCulture);
                case Enum e: return Json.Str(e.ToString());
                default: return Convert.ToString(v, CultureInfo.InvariantCulture);
            }
        }
    }
}
