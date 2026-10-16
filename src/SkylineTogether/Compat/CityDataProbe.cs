using System;
using System.Globalization;
using System.Reflection;
using Game;
using Game.SceneFlow;
using SkylineTogether.Core;
using SkylineTogether.Core.Modding;
using SkylineTogether.Core.Session;
using Unity.Entities;

namespace SkylineTogether.Compat
{
    /// <summary>
    /// Fills the local city-data fields of one loaded mod's handshake entry (<see cref="ModEntry.StoresCityData"/>,
    /// <see cref="ModEntry.CityDataInCity"/>, <see cref="ModEntry.CityDataDetail"/>; docs/design/MOD-COMPATIBILITY.md
    /// "Mods that store data in the city"). The evidence is the mod's analysis (<see cref="ModCompat.ReportFor"/>: saved
    /// component types and saved system state, a failed scan counts) or the built-in list
    /// (<see cref="CityDataRule.IsKnownCityData"/>). In a loaded city it also counts the entities that carry each saved
    /// component type (one entity query per type, prefabs excluded, disabled entities included) and the mod's saved
    /// systems that exist in the world, so the refusal can say "this city contains its data". The count only changes the
    /// wording: a mod that can store data is required either way (<see cref="CityDataRule"/>). Main thread.
    /// </summary>
    internal static class CityDataProbe
    {
        public static void Fill(ModEntry entry, Assembly assembly)
        {
            if (entry == null) return;
            CityDataEvidence evidence;
            try
            {
                evidence = ModCompat.ReportFor(assembly, null)?.CityData ?? new CityDataEvidence { Unknown = true };
            }
            catch (Exception)
            {
                evidence = new CityDataEvidence { Unknown = true };
            }
            bool known = CityDataRule.IsKnownCityData(entry.Name, entry.PlatformId);
            entry.StoresCityData = evidence.StoresData || known;
            if (!entry.StoresCityData) return;
            long entities = 0;
            int systems = 0;
            World world = World.DefaultGameObjectInjectionWorld;
            if (world != null && world.IsCreated && CityLoaded() && assembly != null)
            {
                foreach (string name in evidence.Components) entities += Count(world, assembly, name);
                foreach (string name in evidence.Systems)
                {
                    try
                    {
                        Type t = assembly.GetType(name, false);
                        if (t != null && world.GetExistingSystemManaged(t) != null) systems++;
                    }
                    catch (Exception)
                    {
                        // a type that cannot be resolved is no system in this world
                    }
                }
            }
            entry.CityDataInCity = entities > 0 || systems > 0;
            string what = evidence.StoresData ? evidence.Describe() : "saves data with the city (built-in list)";
            entry.CityDataDetail = what + "; in this city: "
                                   + (entry.CityDataInCity
                                       ? entities.ToString(CultureInfo.InvariantCulture) + " entities with its data" + (systems > 0 ? ", " + systems.ToString(CultureInfo.InvariantCulture) + " saved system states" : "")
                                       : "none found");
        }

        private static bool CityLoaded()
        {
            GameManager gm = GameManager.instance;
            return gm != null && !gm.isGameLoading && gm.gameMode == GameMode.Game;
        }

        // entities (prefabs excluded) carrying the saved component type; 0 when the type cannot be queried
        private static long Count(World world, Assembly assembly, string typeName)
        {
            try
            {
                Type t = assembly.GetType(typeName, false);
                if (t == null) return 0;
                using (EntityQuery q = world.EntityManager.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new[] { ComponentType.ReadOnly(t) },
                    Options = EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IgnoreComponentEnabledState,
                }))
                {
                    return q.CalculateEntityCount();
                }
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
