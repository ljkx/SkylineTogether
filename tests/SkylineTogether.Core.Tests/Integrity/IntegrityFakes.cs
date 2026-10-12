using System;
using System.Collections.Generic;
using SkylineTogether.Core.Integrity;

namespace SkylineTogether.Core.Tests.Integrity
{
    /// <summary>A dirty view whose dirty buckets are listed explicitly; can be made to throw.</summary>
    internal sealed class FakeDirtyView : IPeerDirtyView
    {
        public readonly HashSet<int> DirtyBuckets = new HashSet<int>();
        public bool Throw;

        public bool AnyDirty => DirtyBuckets.Count > 0;

        public int NextDirtySlot(int fromSlotInclusive) => -1;

        public ulong GetMask(NetSlot slot) => DirtyBuckets.Contains(slot.Bucket) ? ulong.MaxValue : 0;

        public double DirtyAgeSeconds(NetSlot slot) => 0;

        public bool BucketHasDirty(int bucket)
        {
            if (Throw) throw new InvalidOperationException("dirty view failure (test)");
            return DirtyBuckets.Contains(bucket);
        }
    }

    /// <summary>Pending pairs listed explicitly; can be made to throw.</summary>
    internal sealed class FakePendingPairs : IPendingPairs
    {
        private readonly HashSet<ulong> _pending = new HashSet<ulong>();
        public bool Throw;

        public void Add(NetSlot slot, TypeRank type) => _pending.Add(((ulong)slot.Value << 16) | type.Value);

        public void Remove(NetSlot slot, TypeRank type) => _pending.Remove(((ulong)slot.Value << 16) | type.Value);

        public bool IsPending(NetSlot slot, TypeRank type)
        {
            if (Throw) throw new InvalidOperationException("pending view failure (test)");
            return _pending.Contains(((ulong)slot.Value << 16) | type.Value);
        }
    }

    /// <summary>A slot table that only knows which slots are bound.</summary>
    internal sealed class FakeSlotTable : ISlotTable
    {
        public readonly HashSet<uint> Bound = new HashSet<uint>();

        public int BoundCount => Bound.Count;

        public int HighWaterMark
        {
            get
            {
                uint max = 0;
                foreach (uint slot in Bound) max = Math.Max(max, slot);
                return (int)max + 1;
            }
        }

        public bool TryGetSlot(EntityKey entity, out NetSlot slot)
        {
            slot = NetSlot.None;
            return false;
        }

        public bool TryGetSlotByIndex(int entityIndex, out NetSlot slot)
        {
            slot = NetSlot.None;
            return false;
        }

        public bool TryGetEntity(NetSlot slot, out EntityKey entity)
        {
            entity = Bound.Contains(slot.Value) ? new EntityKey((int)slot.Value, 1) : default;
            return Bound.Contains(slot.Value);
        }

        public byte GenerationOf(NetSlot slot) => 1;

        public Family FamilyOf(NetSlot slot) => Family.Object;

        public PrefabRank PrefabOf(NetSlot slot) => PrefabRank.None;

        public TypeSetId TypeSetOf(NetSlot slot) => TypeSetId.None;

        public ushort RevisionOf(NetSlot slot) => 0;

        public bool IsSpawnEmitted(NetSlot slot) => true;
    }

    /// <summary>One engine call recorded by <see cref="FakeEngine"/>.</summary>
    internal readonly struct EngineCall
    {
        public readonly string Kind;
        public readonly PeerId Peer;
        public readonly NetSlot Slot;
        public readonly ulong Mask;
        public readonly Priority Priority;
        public readonly ReplicationClass Class;

        public EngineCall(string kind, PeerId peer, NetSlot slot, ulong mask, Priority priority, ReplicationClass cls)
        {
            Kind = kind;
            Peer = peer;
            Slot = slot;
            Mask = mask;
            Priority = priority;
            Class = cls;
        }

        public override string ToString() => Kind + "(" + Peer + ", " + Slot + ", " + Class + ")";
    }

    /// <summary>
    /// A host engine that records the repair calls (MarkDirty, ResendStructural, ReportNotPresent, StartRebaseline) and
    /// forwards them to optional handlers; every other member is unsupported.
    /// </summary>
    internal sealed class FakeEngine : IHostReplicationEngine
    {
        public readonly FakeSlotTable SlotTable = new FakeSlotTable();
        public readonly List<EngineCall> Calls = new List<EngineCall>();
        public Action<PeerId, NetSlot, ulong, Priority> OnMarkDirty;
        public Action<PeerId, NetSlot> OnResendStructural;
        public Action<PeerId, NetSlot> OnReportNotPresent;
        public Action<PeerId, ReplicationClass> OnStartRebaseline;
        public bool ThrowOnResend;
        public bool ThrowOnRebaseline;
        public EpochId Epoch = new EpochId(1);

        public EpochId CurrentEpoch => Epoch;

        public ISlotTable Slots => SlotTable;

        public bool InCaptureWindow => false;

        public bool AcceptingPeers => true;

        public int Count(string kind)
        {
            int n = 0;
            foreach (EngineCall call in Calls)
            {
                if (call.Kind == kind) n++;
            }
            return n;
        }

        public void MarkDirty(PeerId peer, NetSlot slot, ulong typeMask, Priority priority)
        {
            Calls.Add(new EngineCall("MarkDirty", peer, slot, typeMask, priority, ReplicationClass.Excluded));
            OnMarkDirty?.Invoke(peer, slot, typeMask, priority);
        }

        public void ResendStructural(PeerId peer, NetSlot slot)
        {
            if (ThrowOnResend) throw new InvalidOperationException("resend failure (test)");
            Calls.Add(new EngineCall("ResendStructural", peer, slot, 0, Priority.P0, ReplicationClass.Excluded));
            OnResendStructural?.Invoke(peer, slot);
        }

        public void ReportNotPresent(PeerId peer, NetSlot slot)
        {
            Calls.Add(new EngineCall("ReportNotPresent", peer, slot, 0, Priority.P0, ReplicationClass.Excluded));
            OnReportNotPresent?.Invoke(peer, slot);
        }

        public void StartRebaseline(PeerId peer, ReplicationClass cls)
        {
            if (ThrowOnRebaseline) throw new InvalidOperationException("rebaseline failure (test)");
            Calls.Add(new EngineCall("StartRebaseline", peer, NetSlot.None, 0, Priority.Bulk, cls));
            OnStartRebaseline?.Invoke(peer, cls);
        }

        public event Action<PeerId> PeerResumed
        {
            add { }
            remove { }
        }

        public void BeginCapture(uint hostTick, double nowSeconds) => throw new NotSupportedException();
        public void EndCapture() => throw new NotSupportedException();
        public NetSlot Bind(EntityKey entity, Family family, PrefabRank prefab) => throw new NotSupportedException();
        public void BindExisting(NetSlot slot, byte generation, EntityKey entity, Family family, PrefabRank prefab) => throw new NotSupportedException();
        public void BeginSessionStart() => throw new NotSupportedException();
        public void Prime(NetSlot slot, in SpawnObservation spawn, IComponentPayloadSource payloads) => throw new NotSupportedException();
        public void CompleteSessionStart() => throw new NotSupportedException();
        public int CopyPendingSpawns(List<NetSlot> output) => throw new NotSupportedException();
        public void ObserveSpawn(NetSlot slot, in SpawnObservation spawn, IComponentPayloadSource payloads) => throw new NotSupportedException();
        public void ObserveDespawn(NetSlot slot) => throw new NotSupportedException();
        public bool IsRawUnchanged(NetSlot slot, TypeRank type, uint rawHash) => throw new NotSupportedException();
        public ObserveResult ObserveValue(NetSlot slot, TypeRank type, byte[] canonical, int offset, int count, uint rawHash, ObserveSource source) => throw new NotSupportedException();
        public void ObserveAuthored(NetSlot slot, TypeRank type, uint projectionHash) => throw new NotSupportedException();
        public void ObserveEnabled(NetSlot slot, TypeRank type, bool enabled) => throw new NotSupportedException();
        public void ObserveSignature(NetSlot slot, TypeSetId newSet, IComponentPayloadSource addedPayloads) => throw new NotSupportedException();
        public void ObserveTouch(NetSlot slot, TouchFlags flags) => throw new NotSupportedException();
        public void ObserveKeyed(in DerivedKey key, TypeRank type, byte[] canonical, int offset, int count, bool playerEdit) => throw new NotSupportedException();
        public void ObserveKeyedDelete(in DerivedKey key) => throw new NotSupportedException();
        public void BumpRevision(NetSlot slot) => throw new NotSupportedException();
        public void EmitChannelRecord(SectionType section, ChannelScope scope, PeerId peerOrHost, byte[] payload, int offset, int count) => throw new NotSupportedException();
        public void EmitResult(in ResultRecord result, IReadOnlyList<BindRecord> binds) => throw new NotSupportedException();
        public void SetInspectionSample(PeerId peer, uint sampleTick, IReadOnlyList<NetSlot> closure) => throw new NotSupportedException();
        public void AddPeer(PeerId peer, IPeerOutput output) => throw new NotSupportedException();
        public void RemovePeer(PeerId peer, bool keepGrace) => throw new NotSupportedException();
        public void OnAck(PeerId peer, EpochId epoch) => throw new NotSupportedException();
        public void MarkSnapshotFrame(uint hostBuildOrderNext) => throw new NotSupportedException();
        public JoinTicket BeginJoiner(PeerId peer) => throw new NotSupportedException();
        public bool TryResume(PeerId peer, EpochId lastApplied) => throw new NotSupportedException();
        public HostEngineStats GetStats() => default;
    }

    /// <summary>
    /// A host world reduced to what integrity sees: per slot an existence (generation, type set, family) and member hashes
    /// per type, with the class of every pair from a resolver. It maintains <see cref="BucketSums"/> exactly as the host's
    /// hash shadow must (members from <see cref="MemberHasher"/>) and serves bucket details (<see cref="IHashShadowView"/>).
    /// </summary>
    internal sealed class HostModel : IHashShadowView
    {
        public sealed class Entity
        {
            public byte Generation;
            public TypeSetId TypeSet;
            public Family Family;
            public readonly SortedDictionary<ushort, uint> Hashes = new SortedDictionary<ushort, uint>();
        }

        public readonly BucketSums Sums;
        public readonly SortedDictionary<uint, Entity> Entities = new SortedDictionary<uint, Entity>();
        private readonly Func<NetSlot, ReplicationClass> _slotClass;
        private readonly Func<NetSlot, TypeRank, ReplicationClass> _pairClass;
        public bool ThrowOnCollect;
        public bool ReturnMismatchedLists;
        public readonly List<NetSlot> ForeignSlots = new List<NetSlot>();

        public HostModel(BucketSums sums, Func<NetSlot, ReplicationClass> slotClass, Func<NetSlot, TypeRank, ReplicationClass> pairClass)
        {
            Sums = sums;
            _slotClass = slotClass;
            _pairClass = pairClass;
        }

        public ReplicationClass ExistenceClass(NetSlot slot) => _slotClass(slot);

        public ReplicationClass PairClass(NetSlot slot, TypeRank type) => _pairClass(slot, type);

        public void Spawn(NetSlot slot, byte generation, TypeSetId typeSet, Family family)
        {
            Despawn(slot);
            var entity = new Entity { Generation = generation, TypeSet = typeSet, Family = family };
            Entities.Add(slot.Value, entity);
            Sums.OnMemberChanged(ExistenceClass(slot), slot, 0, MemberHasher.ExistenceMember(slot, generation, typeSet, family));
        }

        public void SetValue(NetSlot slot, TypeRank type, uint hash)
        {
            Entity entity = Entities[slot.Value];
            entity.Hashes.TryGetValue(type.Value, out uint old);
            entity.Hashes[type.Value] = hash;
            Sums.OnMemberChanged(PairClass(slot, type), slot, MemberHasher.Member(slot, type, old), MemberHasher.Member(slot, type, hash));
        }

        public void RemoveValue(NetSlot slot, TypeRank type)
        {
            Entity entity = Entities[slot.Value];
            if (!entity.Hashes.TryGetValue(type.Value, out uint old)) return;
            entity.Hashes.Remove(type.Value);
            Sums.OnMemberChanged(PairClass(slot, type), slot, MemberHasher.Member(slot, type, old), 0);
        }

        public void ChangeTypeSet(NetSlot slot, TypeSetId typeSet)
        {
            Entity entity = Entities[slot.Value];
            ulong old = MemberHasher.ExistenceMember(slot, entity.Generation, entity.TypeSet, entity.Family);
            entity.TypeSet = typeSet;
            Sums.OnMemberChanged(ExistenceClass(slot), slot, old, MemberHasher.ExistenceMember(slot, entity.Generation, typeSet, entity.Family));
        }

        public void Despawn(NetSlot slot)
        {
            if (!Entities.TryGetValue(slot.Value, out Entity entity)) return;
            foreach (KeyValuePair<ushort, uint> pair in entity.Hashes)
            {
                var type = new TypeRank(pair.Key);
                Sums.OnMemberChanged(PairClass(slot, type), slot, MemberHasher.Member(slot, type, pair.Value), 0);
            }
            Sums.OnMemberChanged(ExistenceClass(slot), slot, MemberHasher.ExistenceMember(slot, entity.Generation, entity.TypeSet, entity.Family), 0);
            Entities.Remove(slot.Value);
        }

        /// <summary>Sends the slot's complete state into a ledger, as a SPAWN flagged Repair would (an upsert).</summary>
        public void SendTo(HashLedger ledger, NetSlot slot)
        {
            ledger.RemoveSlot(slot);
            if (!Entities.TryGetValue(slot.Value, out Entity entity)) return;
            ledger.OnMemberChanged(ExistenceClass(slot), slot, 0, MemberHasher.ExistenceMember(slot, entity.Generation, entity.TypeSet, entity.Family));
            foreach (KeyValuePair<ushort, uint> pair in entity.Hashes) ledger.Record(slot, new TypeRank(pair.Key), pair.Value);
        }

        /// <summary>Re-sends the slot's values of one class into a ledger (MarkDirty at P1 of every type).</summary>
        public void SendValuesTo(HashLedger ledger, NetSlot slot, ReplicationClass cls)
        {
            var recorded = new List<(TypeRank type, uint hash, ReplicationClass cls)>();
            ledger.CollectPairs(slot, recorded);
            Entities.TryGetValue(slot.Value, out Entity entity);
            foreach (var (type, _, pairCls) in recorded)
            {
                if (pairCls == cls && (entity == null || !entity.Hashes.ContainsKey(type.Value))) ledger.RemovePair(slot, type);
            }
            if (entity == null) return;
            foreach (KeyValuePair<ushort, uint> pair in entity.Hashes)
            {
                var type = new TypeRank(pair.Key);
                if (PairClass(slot, type) == cls) ledger.Record(slot, type, pair.Value);
            }
        }

        public bool TryGetCanonical(NetSlot slot, TypeRank type, out uint hash)
        {
            hash = 0;
            return Entities.TryGetValue(slot.Value, out Entity entity) && entity.Hashes.TryGetValue(type.Value, out hash);
        }

        public void CollectBucket(ReplicationClass cls, int bucket, List<NetSlot> slots, List<uint> combinedHashes)
        {
            if (ThrowOnCollect) throw new InvalidOperationException("shadow failure (test)");
            foreach (KeyValuePair<uint, Entity> entry in Entities)
            {
                var slot = new NetSlot(entry.Key);
                if (slot.Bucket != bucket) continue;
                ulong sum = 0;
                bool any = false;
                unchecked
                {
                    if (ExistenceClass(slot) == cls)
                    {
                        sum += MemberHasher.Contribution(MemberHasher.ExistenceMember(slot, entry.Value.Generation, entry.Value.TypeSet, entry.Value.Family));
                        any = true;
                    }
                    foreach (KeyValuePair<ushort, uint> pair in entry.Value.Hashes)
                    {
                        var type = new TypeRank(pair.Key);
                        if (PairClass(slot, type) != cls) continue;
                        sum += MemberHasher.Contribution(MemberHasher.Member(slot, type, pair.Value));
                        any = true;
                    }
                }
                if (!any) continue;
                slots.Add(slot);
                combinedHashes.Add(MemberHasher.SlotDigest(sum));
            }
            foreach (NetSlot foreign in ForeignSlots)
            {
                slots.Add(foreign);
                combinedHashes.Add(0xDEADBEEF);
            }
            if (ReturnMismatchedLists) combinedHashes.Add(1);
        }

        public ReplicationClass ClassOf(NetSlot slot) => ExistenceClass(slot);
    }
}
