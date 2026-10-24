using System;
using Game;
using SkylineTogether.Diagnostics;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// The derived net links' guard (<see cref="DerivedLinkGuard"/>, ARCHITECTURE.md#derived-net-links) at its points of
    /// the frame (the start of the frame is <c>SyncController.ApplyIncoming</c>). Each returns at once outside a session, before a client is live, and when replication is off.
    /// </summary>
    internal static class DerivedLinkRun
    {
        public static void Run(string where, Action<DerivedLinkGuard, EntityManager> body, EntityManager em)
        {
            SyncController sync = Mod.Sync;
            if (sync == null || !SyncController.SessionActive || !(sync.IsHost || sync.IsLiveClient) || !Features.Replication.IsAvailable) return;
            long start = Perf.Start();
            try
            {
                body(sync.Links, em);
            }
            catch (Exception ex)
            {
                Guard.Report(Features.Replication, "derived net links (" + where + ")", ex);
            }
            finally
            {
                Perf.Stop(Perf.ReferenceRepair, start);
            }
        }
    }

    /// <summary>Modification2B, right before <c>Game.Net.ReferencesSystem</c>: the node and edge lists it is about to index.</summary>
    public sealed class NetReferenceGuardSystem : GameSystemBase
    {
        private static readonly Action<DerivedLinkGuard, EntityManager> Body = (g, em) => g.BeforeNetReferences(em);

        protected override void OnUpdate() => DerivedLinkRun.Run("node and edge lists", Body, EntityManager);
    }

    /// <summary>Modification2B, right before <c>Game.Net.AggregateSystem</c>: the streets it is about to index.</summary>
    public sealed class AggregateLinkGuardSystem : GameSystemBase
    {
        private static readonly Action<DerivedLinkGuard, EntityManager> Body = (g, em) => g.BeforeAggregates(em);

        protected override void OnUpdate() => DerivedLinkRun.Run("streets", Body, EntityManager);
    }

    /// <summary>Modification4B, right before <c>Game.Buildings.RoadConnectionSystem</c>: the road connections it is about to index.</summary>
    public sealed class RoadConnectionGuardSystem : GameSystemBase
    {
        private static readonly Action<DerivedLinkGuard, EntityManager> Body = (g, em) => g.BeforeRoadConnections(em);

        protected override void OnUpdate() => DerivedLinkRun.Run("road connections", Body, EntityManager);
    }

    /// <summary>PreCulling, right before <c>Game.Rendering.AggregateMeshSystem</c>: the street lists whose labels it rebuilds and their nodes' edge lists.</summary>
    public sealed class AggregateMeshGuardSystem : GameSystemBase
    {
        private static readonly Action<DerivedLinkGuard, EntityManager> Body = (g, em) => g.BeforeAggregateMeshes(em);

        protected override void OnUpdate() => DerivedLinkRun.Run("street labels", Body, EntityManager);
    }

    /// <summary>
    /// ModificationEnd, after <c>ModificationEndBarrier</c> (every deletion of the frame is tagged): nothing derived keeps
    /// naming an edge, node, street or building the game destroys at the end of this frame; once per session a full pass.
    /// </summary>
    public sealed class DerivedLinkCleanupSystem : GameSystemBase
    {
        private static readonly Action<DerivedLinkGuard, EntityManager> Body = (g, em) => g.EndOfModification(em);

        protected override void OnUpdate() => DerivedLinkRun.Run("end of modification", Body, EntityManager);
    }
}
