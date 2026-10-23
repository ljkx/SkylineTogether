using System;
using Game.Prefabs;
using SkylineTogether.Core;
using SkylineTogether.Core.Diagnostics;
using SkylineTogether.Sync;
using Unity.Entities;

namespace SkylineTogether.Diagnostics
{
    /// <summary>
    /// Breadcrumbs for native crashes (ARCHITECTURE.md#logs): a crash in a Burst job leaves no managed stack, so the log's
    /// last lines must say what just happened. One <c>[forensics]</c> line per local tool apply on a client, one per clean-up
    /// the mod makes in the world (removal cascade, orphan sweep, update-group guard, reference audit), and a frame marker
    /// every few seconds while the player builds. At most <see cref="BreadcrumbLimiter.PerSecond"/> lines a second, the
    /// rest summarised. The game's logger writes and flushes every line synchronously
    /// (<c>Colossal.Logging.UnityLogger.Internal_WriteStream</c> ends with <c>Flush()</c>), so a line logged before a crash is
    /// in the file. Entities are named by kind, prefab name, net id or index: no paths, no player names. Main thread only.
    /// </summary>
    internal static class Forensics
    {
        private static readonly BreadcrumbLimiter Limiter = new BreadcrumbLimiter();
        private static PrefabSystem _prefabs;
        private static World _prefabWorld;

        /// <summary>Clean-ups written this session (diagnostics).</summary>
        public static long Cleanups { get; private set; }

        /// <summary>Clean-ups since the last frame marker.</summary>
        public static int CleanupsSinceMarker { get; set; }

        /// <summary>Writes one forensics line (rate-limited; the summary of left-out lines first).</summary>
        public static void Write(string line)
        {
            ILog log = Mod.Log;
            if (log == null || string.IsNullOrEmpty(line)) return;
            try
            {
                if (!Limiter.Allow(Perf.Now, out string summary)) return;
                if (summary != null) log.Info(LogCategory.Forensics, summary);
                log.Info(LogCategory.Forensics, line);
            }
            catch (Exception)
            {
                // a breadcrumb never breaks the caller
            }
        }

        /// <summary>Writes the summary of left-out lines without a line to follow (frame markers, session end).</summary>
        public static void FlushSummary()
        {
            ILog log = Mod.Log;
            if (log == null) return;
            string summary = Limiter.TakeSummary(Perf.Now);
            if (summary != null) log.Info(LogCategory.Forensics, summary);
        }

        /// <summary>
        /// A clean-up the mod made in the world: <c>cleanup: &lt;who&gt;: &lt;entity&gt; &lt;what&gt;</c>. Counted always,
        /// written within the rate limit.
        /// </summary>
        public static void Cleanup(string who, EntityManager em, Entity e, string what)
        {
            Cleanups++;
            CleanupsSinceMarker++;
            if (Mod.Log == null) return;
            Write("cleanup: " + who + ": " + Describe(em, e) + " " + what);
        }

        /// <summary>
        /// An entity as the log names it: kind, prefab name and net id (or index:version), for example
        /// <c>edge 'Small Road' #8123</c>. Never throws.
        /// </summary>
        public static string Describe(EntityManager em, Entity e)
        {
            if (e == Entity.Null) return "nothing";
            try
            {
                if (!em.Exists(e)) return "destroyed entity " + e.Index + ":" + e.Version;
                string kind = Kind(em, e);
                string prefab = PrefabName(em, e);
                uint id = 0;
                NetIdSystem ids = em.World.GetExistingSystemManaged<NetIdSystem>();
                if (ids != null) id = ids.GetNetId(e);
                return kind + (prefab != null ? " '" + prefab + "'" : "") + (id != 0 ? " #" + id : " " + e.Index + ":" + e.Version)
                       + (em.HasComponent<Game.Tools.Temp>(e) ? " (preview)" : "") + (em.HasComponent<Game.Common.Deleted>(e) ? " (deleted)" : "");
            }
            catch (Exception)
            {
                return "entity " + e.Index + ":" + e.Version;
            }
        }

        private static string Kind(EntityManager em, Entity e)
        {
            if (em.HasComponent<Game.Net.Edge>(e)) return "edge";
            if (em.HasComponent<Game.Net.Node>(e)) return "node";
            if (em.HasComponent<Game.Net.Lane>(e)) return "lane";
            if (em.HasComponent<Game.Net.Aggregate>(e)) return "street";
            if (em.HasComponent<Game.Buildings.Building>(e)) return "building";
            if (em.HasComponent<Game.Vehicles.Vehicle>(e)) return "vehicle";
            if (em.HasComponent<Game.Creatures.Creature>(e)) return "creature";
            if (em.HasComponent<Game.Citizens.Citizen>(e)) return "citizen";
            if (em.HasComponent<Game.Citizens.Household>(e)) return "household";
            if (em.HasComponent<Game.Areas.Area>(e)) return "area";
            if (em.HasComponent<Game.Zones.Block>(e)) return "zone block";
            if (em.HasComponent<Game.Objects.Object>(e)) return "object";
            return "entity";
        }

        /// <summary>The prefab's name (an asset name), or null.</summary>
        public static string PrefabName(EntityManager em, Entity e)
        {
            try
            {
                if (!em.HasComponent<PrefabRef>(e)) return null;
                Entity prefab = em.GetComponentData<PrefabRef>(e).m_Prefab;
                if (prefab == Entity.Null || !em.Exists(prefab)) return null;
                if (_prefabWorld != em.World)
                {
                    _prefabWorld = em.World;
                    _prefabs = em.World.GetExistingSystemManaged<PrefabSystem>();
                }
                return _prefabs?.GetPrefabName(prefab);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static void ResetSession()
        {
            FlushSummary();
            Cleanups = 0;
            CleanupsSinceMarker = 0;
        }
    }
}
