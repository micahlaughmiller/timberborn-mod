using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Bindito.Core;
using Timberborn.Beavers;
using Timberborn.BlockSystem;
using Timberborn.BlueprintSystem;
using Timberborn.ConstructionSites;
using Timberborn.BlockObjectTools;
using Timberborn.Buildings;
using Timberborn.EntitySystem;
using Timberborn.GameCycleSystem;
using Timberborn.GameDistricts;
using Timberborn.Goods;
using Timberborn.HazardousWeatherSystem;
using Timberborn.MapStateSystem;
using Timberborn.ScienceSystem;
using Timberborn.SingletonSystem;
using Timberborn.TerrainSystem;
using Timberborn.TimeSystem;
using Timberborn.WaterSystem;
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

            // Separate holders, each with its own off-switch: a wrong constructor
            // argument stops saves from loading, so isolate the risk per group.
            if (Flags.Has("no-world.flag")) Debug.Log("[TimberbornAI] no-world.flag present: world services not bound");
            else Bind<AIWorldServices>().AsSingleton();

            if (Flags.Has("no-build.flag")) Debug.Log("[TimberbornAI] no-build.flag present: build services not bound");
            else Bind<AIBuildServices>().AsSingleton();
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

    /// <summary>Districts, goods, terrain and water: what the map contains and what is stockpiled.</summary>
    public class AIWorldServices : ILoadableSingleton
    {
        public static AIWorldServices Instance { get; private set; }

        public readonly DistrictCenterRegistry Districts;
        public readonly IGoodService Goods;
        public readonly ITerrainService Terrain;
        public readonly IThreadSafeWaterMap Water;
        public readonly MapSize MapSize;

        public AIWorldServices(
            DistrictCenterRegistry districts,
            IGoodService goods,
            ITerrainService terrain,
            IThreadSafeWaterMap water,
            MapSize mapSize)
        {
            Districts = districts;
            Goods = goods;
            Terrain = terrain;
            Water = water;
            MapSize = mapSize;
        }

        public void Load()
        {
            Instance = this;
            Debug.Log("[TimberbornAI] world services bound");
        }
    }

    /// <summary>Which buildings exist and are unlocked, science points, and the placement service.</summary>
    public class AIBuildServices : ILoadableSingleton
    {
        public static AIBuildServices Instance { get; private set; }

        public readonly BuildingService Buildings;
        public readonly BuildingUnlockingService Unlocking;
        public readonly ScienceService Science;
        public readonly BlockObjectPlacerService Placers;
        public readonly ISpecService Specs;
        public readonly BlockValidator Validator;
        public readonly ConstructionFactory Construction;

        public AIBuildServices(
            BuildingService buildings,
            BuildingUnlockingService unlocking,
            ScienceService science,
            BlockObjectPlacerService placers,
            ISpecService specs,
            BlockValidator validator,
            ConstructionFactory construction)
        {
            Buildings = buildings;
            Unlocking = unlocking;
            Science = science;
            Placers = placers;
            Specs = specs;
            Validator = validator;
            Construction = construction;
        }

        public void Load()
        {
            Instance = this;
            Debug.Log("[TimberbornAI] build services bound");
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
