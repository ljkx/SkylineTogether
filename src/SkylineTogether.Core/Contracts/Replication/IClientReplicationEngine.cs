using System;
using System.Collections.Generic;

namespace SkylineTogether.Core
{
    /// <summary>
    /// The client replication engine (ARCH §4.6-4.7), implemented by core-replication's <c>ClientReplicationEngine</c>
    /// and driven by game-client-apply. Epoch intake runs on the network thread; everything else on the main thread.
    /// </summary>
    public interface IClientReplicationEngine
    {
        /// <summary>One received epoch message (or part); assembles parts, a TSET section loads the type-set registry.</summary>
        [ThreadAffinity(ThreadKind.Network)]
        void OnEpochMessage(byte[] buffer, int offset, int count);

        [ThreadAffinity(ThreadKind.Main)]
        EpochId LastAppliedEpoch { get; }

        /// <summary>This client's peer id: RESULT and BIND records of other origins are skipped.</summary>
        [ThreadAffinity(ThreadKind.Main)]
        PeerId LocalPeer { get; set; }

        /// <summary>The next merged plan within the budget; false when nothing complete is waiting.</summary>
        [ThreadAffinity(ThreadKind.Main)]
        bool TryTakePlan(in ApplyBudget budget, out ApplyPlan plan);

        /// <summary>Plans the program for a plan against the current world view (core-replication <c>ApplyPlanner</c>).</summary>
        [ThreadAffinity(ThreadKind.Main)]
        ApplyProgram Plan(ApplyPlan plan, IApplyWorldView view);

        /// <summary>Payload facts for the planner's path-hold rule (game-ecs-schema).</summary>
        [ThreadAffinity(ThreadKind.Main)]
        void SetPayloadFacts(IPayloadFacts facts);

        /// <summary>Reports the executor's result for the plan (ledger bookkeeping, acknowledgement, fault handling).</summary>
        [ThreadAffinity(ThreadKind.Main)]
        void CompletePlan(ApplyPlan plan, in ApplyResult result);

        [ThreadAffinity(ThreadKind.Main)]
        void BindSlot(NetSlot slot, byte generation, EntityKey entity, Family family, PrefabRank prefab);

        [ThreadAffinity(ThreadKind.Main)]
        void UnbindSlot(NetSlot slot);

        /// <summary>A value whose reference to <paramref name="missing"/> did not resolve; re-applied when it binds (ARCH §4.7.5).</summary>
        [ThreadAffinity(ThreadKind.Main)]
        void ReportPending(NetSlot slot, TypeRank type, byte[] canonical, int offset, int count, NetSlot missing);

        /// <summary>Parked, held, awaiting re-application or awaiting re-resolution (ARCH §8.3).</summary>
        [ThreadAffinity(ThreadKind.Main)]
        bool IsPending(NetSlot slot, TypeRank type);

        [ThreadAffinity(ThreadKind.Main)]
        void SetHoldFilter(IHoldFilter filter);

        /// <summary>Received-value cache (ARCH §3.7): the applied canonical bytes and the derived owners they referenced.</summary>
        [ThreadAffinity(ThreadKind.Main)]
        void RecordReceived(NetSlot slot, TypeRank type, byte[] canonical, int offset, int count, IReadOnlyList<NetSlot> derivedOwnersReferenced);

        /// <summary>Appends the cached bytes of a pair (route guard restores, ARCH §4.11.4); false when not cached.</summary>
        [ThreadAffinity(ThreadKind.Main)]
        bool TryCopyReceived(NetSlot slot, TypeRank type, Buffers.ByteWriter output);

        /// <summary>The derived children of <paramref name="owner"/> were regenerated locally; its dependents need re-resolution.</summary>
        [ThreadAffinity(ThreadKind.Main)]
        void OnDerivedOwnerRegenerated(NetSlot owner);

        /// <summary>Value ops from the cached bytes of the dependents waiting for re-resolution (run in ClientEndOfFrameSystem).</summary>
        [ThreadAffinity(ThreadKind.Main)]
        bool TryBuildReresolutionPlan(ApplyPlan plan);

        /// <summary>Catch-up from the snapshot epoch E after a join load.</summary>
        [ThreadAffinity(ThreadKind.Main)]
        void StartCatchUp(EpochId snapshotEpoch);

        [ThreadAffinity(ThreadKind.Main)]
        bool IsFaulted { get; }

        /// <summary>Unknown type set, oversized epoch, plan failure: the follower freezes and resyncs (ARCH §12.2).</summary>
        event Action<FaultCategory, string> Faulted;

        /// <summary>Before a snapshot resync: registry, backlog and pending state are cleared.</summary>
        [ThreadAffinity(ThreadKind.Main)]
        void Reset();

        [ThreadAffinity(ThreadKind.Main)]
        ISlotTable Slots { get; }

        [ThreadAffinity(ThreadKind.Main)]
        ClientEngineStats GetStats();
    }
}
