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
    /// wording: a mod that can store data is required either way (<see cref="CityDataRule"/>), except for saved types the
    /// mod is known never to write (<see cref="CityDataRule.DormantSavedTypes"/>), which count only where entities have
    /// them. Main thread.
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
            World world = World.DefaultGameObjectInjectionWorld;
            bool inCity = world != null && world.IsCreated && CityLoaded() && assembly != null;
            // saved types the mod never writes are city data only where entities have them; outside a loaded city nothing
            // can be counted (fail closed)
            long dormant = 0;
            bool counted = inCity;
            if (evidence.Dormant.Count > 0 && inCity)
            {
                foreach (string name in evidence.Dormant)
                {
                    counted &= TryCount(world, assembly, name, out long n);
                    dormant += n;
                }
            }
            entry.StoresCityData = evidence.StoresData || known || (evidence.Dormant.Count > 0 && (!counted || dormant > 0));
            if (!entry.StoresCityData)
            {
                entry.CityDataDetail = evidence.Describe() + (evidence.Dormant.Count > 0 ? "; in this city: none found" : "");
                return;
            }
            long entities = dormant;
            int systems = 0;
            if (inCity)
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
            string what = evidence.StoresData || evidence.Dormant.Count > 0 ? evidence.Describe() : "saves data with the city (built-in list)";
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
            TryCount(world, assembly, typeName, out long n);
            return n;
        }

        // the same; false (and 0) when the type cannot be resolved or queried
        private static bool TryCount(World world, Assembly assembly, string typeName, out long count)
        {
            count = 0;
            try
            {
                Type t = assembly.GetType(typeName, false);
                if (t == null) return false;
                using (EntityQuery q = world.EntityManager.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new[] { ComponentType.ReadOnly(t) },
                    Options = EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IgnoreComponentEnabledState,
                }))
                {
                    count = q.CalculateEntityCount();
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
