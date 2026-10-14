using System;
using System.Collections.Generic;

namespace SkylineTogether.Core
{
    /// <summary>
    /// The host replication engine (docs/history/ARCHITECTURE-original.md §4.4-4.5), implemented by core-replication's <c>HostReplicationEngine</c> and
    /// driven by game-host-capture.
    /// <para>
    /// Threading contract (docs/history/ARCHITECTURE-original.md §4.5.5): <see cref="CaptureWindowAttribute"/> members may run only between
    /// <see cref="BeginCapture"/> and <see cref="EndCapture"/> (debug builds assert). Every other mutating member may be
    /// called at any time on the main thread; outside the window it is appended to the engine command queue and executed
    /// at the next <see cref="BeginCapture"/>, after the previous frame's workers were joined. Read-only queries
    /// (statistics) are always allowed.
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public interface IHostReplicationEngine
    {
        EpochId CurrentEpoch { get; }

        ISlotTable Slots { get; }

        bool InCaptureWindow { get; }

        /// <summary>False during host session start (joins are refused with <c>HostPreparing</c>).</summary>
        bool AcceptingPeers { get; }

        /// <summary>Joins the workers, drains the command queue and opens the capture window.</summary>
        void BeginCapture(uint hostTick, double nowSeconds);

        /// <summary>Closes the epoch (splits it into parts when needed) and hands the frame token to the workers.</summary>
        void EndCapture();

        /// <summary>Binds a tracked entity to a new slot (lowest free first); its SPAWN is emitted by the next flush.</summary>
        [CaptureWindow]
        NetSlot Bind(EntityKey entity, Family family, PrefabRank prefab);

        /// <summary>Restores a binding loaded from the <c>NetIdSystem</c> save block.</summary>
        [CaptureWindow]
        void BindExisting(NetSlot slot, byte generation, EntityKey entity, Family family, PrefabRank prefab);

        /// <summary>Host session start begins (docs/history/ARCHITECTURE-original.md §4.4.8); <see cref="AcceptingPeers"/> becomes false.</summary>
        void BeginSessionStart();

        /// <summary>Session start: records baselines only (no dirty bits, no records); marks the spawn as emitted.</summary>
        [CaptureWindow]
        void Prime(NetSlot slot, in SpawnObservation spawn, IComponentPayloadSource payloads);

        /// <summary>Host session start completed; <see cref="AcceptingPeers"/> becomes true.</summary>
        void CompleteSessionStart();

        /// <summary>Copies the bound slots whose SPAWN was not emitted yet; returns their number.</summary>
        [CaptureWindow]
        int CopyPendingSpawns(List<NetSlot> output);

        /// <summary>Emits the SPAWN of a bound slot and marks it emitted.</summary>
        [CaptureWindow]
        void ObserveSpawn(NetSlot slot, in SpawnObservation spawn, IComponentPayloadSource payloads);

        [CaptureWindow]
        void ObserveDespawn(NetSlot slot);

        /// <summary>True when the raw hash equals the shadow's (the prefilter of change detection).</summary>
        bool IsRawUnchanged(NetSlot slot, TypeRank type, uint rawHash);

        [CaptureWindow]
        ObserveResult ObserveValue(NetSlot slot, TypeRank type, byte[] canonical, int offset, int count, uint rawHash, ObserveSource source);

        /// <summary>EDC or commit of a type with an authorable mask: the revision follows the projection hash (docs/history/ARCHITECTURE-original.md §3.8).</summary>
        [CaptureWindow]
        void ObserveAuthored(NetSlot slot, TypeRank type, uint projectionHash);

        [CaptureWindow]
        void ObserveEnabled(NetSlot slot, TypeRank type, bool enabled);

        [CaptureWindow]
        void ObserveSignature(NetSlot slot, TypeSetId newSet, IComponentPayloadSource addedPayloads);

        [CaptureWindow]
        void ObserveTouch(NetSlot slot, TouchFlags flags);

        [CaptureWindow]
        void ObserveKeyed(in DerivedKey key, TypeRank type, byte[] canonical, int offset, int count, bool playerEdit);

        [CaptureWindow]
        void ObserveKeyedDelete(in DerivedKey key);

        /// <summary>Commits of proposals bump the authored revision.</summary>
        [CaptureWindow]
        void BumpRevision(NetSlot slot);

        void EmitChannelRecord(SectionType section, ChannelScope scope, PeerId peerOrHost, byte[] payload, int offset, int count);

        /// <summary>A proposal result and its binds (origin inside the records), in the shared TX section.</summary>
        void EmitResult(in ResultRecord result, IReadOnlyList<BindRecord> binds);

        /// <summary>The inspection closure of a peer to sample at <paramref name="sampleTick"/> (docs/history/ARCHITECTURE-original.md §4.7.9).</summary>
        void SetInspectionSample(PeerId peer, uint sampleTick, IReadOnlyList<NetSlot> closure);

        void AddPeer(PeerId peer, IPeerOutput output);

        /// <summary><paramref name="keepGrace"/>: keep the peer's dirty set for a short-drop resume (docs/history/ARCHITECTURE-original.md §7.8).</summary>
        void RemovePeer(PeerId peer, bool keepGrace);

        void OnAck(PeerId peer, EpochId epoch);

        /// <summary>The current frame writes a join snapshot.</summary>
        void MarkSnapshotFrame(uint hostBuildOrderNext);

        /// <summary>Starts tracking a joiner at the snapshot epoch; its first message carries the TSET keyframe.</summary>
        JoinTicket BeginJoiner(PeerId peer);

        /// <summary>Short-drop resume (docs/history/ARCHITECTURE-original.md §7.8); false when the epoch log no longer reaches back to <paramref name="lastApplied"/>.</summary>
        bool TryResume(PeerId peer, EpochId lastApplied);

        /// <summary>Raised after a successful resume; game-motion and the channels reset their per-peer state.</summary>
        event Action<PeerId> PeerResumed;

        /// <summary>Repair level 2: marks the pairs of <paramref name="typeMask"/> (type-set ordinals) dirty for the peer.</summary>
        void MarkDirty(PeerId peer, NetSlot slot, ulong typeMask, Priority priority);

        void ResendStructural(PeerId peer, NetSlot slot);

        /// <summary>The host does not have the entity; the client adds <c>Deleted</c>.</summary>
        void ReportNotPresent(PeerId peer, NetSlot slot);

        /// <summary>Repair level 3 (bulk), decided by the host responder.</summary>
        void StartRebaseline(PeerId peer, ReplicationClass cls);

        HostEngineStats GetStats();
    }
}
