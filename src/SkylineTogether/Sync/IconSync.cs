using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Game.Common;
using Game.Notifications;
using Game.Prefabs;
using SkylineTogether.Core;
using SkylineTogether.Core.Integrity;
using SkylineTogether.Core.Sync;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// Notification icons are host data (D11, ARCH §4.10.4, §4.11.5): building warnings, network connection warnings,
    /// line and waypoint "path not found" and "not enough vehicles" icons that a client sees are the host's.
    /// <para>
    /// <b>Host.</b> Every <see cref="HostScanInterval"/> s it reads the default-layer icons of every owner with an
    /// <c>IconElement</c> buffer (non-<c>Temp</c>, non-<c>Deleted</c> icons), names the owner and each target portably
    /// (net id, or the derived key of a lane or station stop) and broadcasts an <see cref="SyncRecordKind.IconSet"/> for
    /// every owner whose set changed (an empty set when its icons are gone). Every <see cref="DigestEveryScans"/> scans it
    /// adds an <see cref="IconControlMode.Digest"/> of everything sent; on request it sends a full sync to one peer.
    /// Owners and icons that cannot be named (citizens, vehicles, unbound sub-nets) are not replicated; marker icons
    /// (local infoview) and transaction icons (animations) are not either.
    /// </para>
    /// <para>
    /// <b>Client.</b> <see cref="BeforeIconCommandSystem"/> runs right before <c>IconCommandSystem</c> in ModificationEnd
    /// (<see cref="IconAuthorityFilterSystem"/>). It first filters the vanilla command queues
    /// (<c>IconCommandSystem.m_Queues</c>, read by reflection after completing <c>m_Dependencies</c>,
    /// <c>IconCommandSystem.cs:698-770</c>): local status decisions (<c>Add</c>/<c>Remove</c> on an owner the host
    /// controls) are dropped; tool previews (<c>Temp</c>), marker and transaction layers, <c>Update</c>, and the
    /// bookkeeping removals of deleted owners, targets or icons are kept. Then it reconciles every owner whose host set
    /// changed: missing or changed icons are added and extra ones removed through one replication-owned
    /// <c>IconCommandBuffer</c> (<c>CreateCommandBuffer</c>), so vanilla keeps creating the icon entities,
    /// <c>IconElement</c>, clusters and animations. After a full sync it sweeps every owner. The periodic digest is
    /// compared with the live icons at the end of a frame without pending work; two mismatches in a row request a full
    /// sync (at most once a minute; backs off after three that did not help).
    /// </para>
    /// <para>
    /// <b>Fallback</b> when the queues cannot be read: local icons may appear for a frame; owners of newly created icons
    /// are reconciled the next frame, and every owner is swept every <see cref="FallbackSweepInterval"/> s.
    /// </para>
    /// Main thread only.
    /// </summary>
    public sealed class IconSync
    {
        private const double HostScanInterval = 0.5;
        private const int DigestEveryScans = 20;
        private const int MaxOwnersPerFrame = 2048;
        private const int MaxAttemptsPerOwner = 3;
        private const int VerifyAfterFrames = 3;
        private const double UnresolvedTimeout = 30;
        private const double RevalidateInterval = 1.0;
        private const double FullSyncInterval = 60;
        private const double FullSyncBackoff = 600;
        private const double FallbackSweepInterval = 2.0;
        private const double FullSyncTimeout = 15;
        private const byte FlagIgnoreTarget = (byte)IconFlags.IgnoreTarget;
        private const byte FlagTargetLocation = (byte)IconFlags.TargetLocation;
        private const byte FlagSecondary = (byte)IconFlags.SecondaryLocation;
        private const byte FlagCustomLocation = (byte)IconFlags.CustomLocation;

        private readonly ILog _log;
        private readonly IClock _clock;
        private EntityCodec _codec;
        private NetIdSystem _ids;
        private EntityQuery _owners;
        private EntityQuery _createdIcons;

        // ---------------------------------------------------------------- host
        private readonly Dictionary<Entity, SentOwner> _sent = new Dictionary<Entity, SentOwner>();
        private readonly HashSet<Entity> _seen = new HashSet<Entity>();
        private readonly List<Entity> _gone = new List<Entity>();
        private double _lastScan = -1000;
        private int _scans;
        private uint _hostIcons;

        private sealed class SentOwner
        {
            public SyncRef Ref;
            public ulong Exact;
            public List<SyncIcon> Icons;
        }

        // ---------------------------------------------------------------- client
        private readonly Dictionary<SyncRef, HostSet> _hostSets = new Dictionary<SyncRef, HostSet>();
        private readonly Dictionary<Entity, SyncRef> _byEntity = new Dictionary<Entity, SyncRef>();
        private readonly HashSet<Entity> _dirty = new HashSet<Entity>();
        private readonly Dictionary<SyncRef, double> _unresolved = new Dictionary<SyncRef, double>();
        private readonly Dictionary<Entity, int> _attempts = new Dictionary<Entity, int>();
        private readonly List<(int Frame, Entity Owner)> _verify = new List<(int, Entity)>();
        private readonly List<LiveIcon> _live = new List<LiveIcon>();
        private readonly List<Pending> _pending = new List<Pending>();
        private readonly List<Entity> _work = new List<Entity>();
        private readonly List<SyncRef> _refScratch = new List<SyncRef>();
        private readonly List<IconCommandBuffer.Command> _keep = new List<IconCommandBuffer.Command>();
        private bool _clientActive;
        private bool _sweepAll;
        private bool _haveFullSync;
        private double _fullSyncSince = -1;
        private bool _requestFullSync;
        private double _lastFullSyncRequest = -1000;
        private int _fullSyncsWithoutMatch;
        private bool _hasDigest;
        private uint _digestOwners, _digestIcons;
        private ulong _digestSum;
        private double _digestAt;
        private int _digestMismatches;
        private int _iconState; // 0 unknown, 1 match, 2 differs
        private int _frame;
        private double _lastRevalidate;
        private double _lastFallbackSweep;
        private long _commandsIssued;

        // filter
        private IconCommandSystem _iconSystem;
        private FieldInfo _queuesField;
        private FieldInfo _depsField;
        private bool _filterReady;
        private bool _filterBroken;
        private long _dropped;
        private long _droppedLogged;
        private double _lastDropLog;

        private sealed class HostSet
        {
            public SyncRef Owner;
            public List<SyncIcon> Icons;
            public Entity Resolved;
        }

        private struct LiveIcon
        {
            public Entity Prefab;
            public Entity Target;
            public byte Priority;
            public byte Flags;
            public float3 Location;
            public bool Matched;
        }

        private struct Pending
        {
            public bool Remove;
            public Entity Prefab;
            public Entity Target;
            public byte Priority;
            public byte Flags;
            public bool Custom;
            public float3 Location;
        }

        public IconSync(ILog log, IClock clock)
        {
            _log = log;
            _clock = clock;
        }

        public void Attach(EntityCodec codec, NetIdSystem ids)
        {
            _codec = codec;
            _ids = ids;
            EntityManager em = codec.EntityManager;
            _owners = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<IconElement>() },
                None = new[] { ComponentType.ReadOnly<Game.Tools.Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            _createdIcons = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Icon>(), ComponentType.ReadOnly<Created>(), ComponentType.ReadOnly<Owner>() },
                None = new[] { ComponentType.ReadOnly<Game.Tools.Temp>() },
            });
            _iconSystem = codec.World.GetExistingSystemManaged<IconCommandSystem>();
        }

        public void Reset()
        {
            _sent.Clear();
            _lastScan = -1000;
            _scans = 0;
            _hostIcons = 0;
            _hostSets.Clear();
            _byEntity.Clear();
            _dirty.Clear();
            _unresolved.Clear();
            _attempts.Clear();
            _verify.Clear();
            _clientActive = false;
            _sweepAll = false;
            _haveFullSync = false;
            _fullSyncSince = -1;
            _requestFullSync = false;
            _lastFullSyncRequest = -1000;
            _fullSyncsWithoutMatch = 0;
            _hasDigest = false;
            _digestMismatches = 0;
            _iconState = 0;
        }

        /// <summary>Host: "N warnings on M objects"; client: whether the icons match the host's.</summary>
        public string Status
        {
            get
            {
                if (_codec == null) return "";
                if (!_clientActive)
                {
                    if (_scans == 0) return "";
                    return _hostIcons.ToString("N0", CultureInfo.InvariantCulture) + " icons on " + _sent.Count.ToString("N0", CultureInfo.InvariantCulture) + " objects";
                }
                string s = _iconState == 1 ? "icons match" : _iconState == 2 ? "icons differ (re-syncing)" : "icons: checking";
                if (_filterBroken) s += " (filter unavailable)";
                return s;
            }
        }

        // ================================================================== portable references

        private static SyncRef ToRef(uint netId, in DerivedKey key)
        {
            if (netId != 0) return SyncRef.ForNetId(netId);
            return new SyncRef
            {
                Kind = key.Kind == DerivedKey.LaneKind ? SyncRef.LaneKey : SyncRef.SubObjectKey,
                Id = key.OwnerNetId,
                Prefab = key.PrefabNetId,
                Index = key.Index,
                X = key.X,
                Y = key.Y,
                Z = key.Z,
            };
        }

        /// <summary>None for <c>Entity.Null</c>, the portable name when there is one, else Unnamed.</summary>
        private SyncRef Portable(Entity e)
        {
            if (e == Entity.Null) return default;
            return _codec.TryGetPortableRef(e, out uint id, out DerivedKey key) ? ToRef(id, key) : new SyncRef { Kind = SyncRef.Unnamed };
        }

        private Entity Resolve(in SyncRef r)
        {
            EntityManager em = _codec.EntityManager;
            Entity e = Entity.Null;
            switch (r.Kind)
            {
                case SyncRef.NetId:
                    e = _ids.GetEntity(r.Id);
                    break;
                case SyncRef.LaneKey:
                case SyncRef.SubObjectKey:
                    var key = new DerivedKey
                    {
                        Kind = r.Kind == SyncRef.LaneKey ? DerivedKey.LaneKind : DerivedKey.SubObjectKind,
                        OwnerNetId = r.Id,
                        PrefabNetId = r.Prefab,
                        Index = r.Index,
                        X = r.X,
                        Y = r.Y,
                        Z = r.Z,
                    };
                    e = _codec.ResolveDerivedKey(key);
                    break;
            }
            if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Deleted>(e)) return Entity.Null;
            return e;
        }

        // ================================================================== host

        /// <summary>Host, at the end of the frame's capture: changed icon sets (and periodically the digest) into the broadcast batch.</summary>
        public void HostCapture(double now, SyncBatch.Writer batch)
        {
            if (_codec == null || now - _lastScan < HostScanInterval) return;
            _lastScan = now;
            _scans++;
            EntityManager em = _codec.EntityManager;
            _seen.Clear();
            NativeArray<Entity> owners = _owners.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity owner in owners)
                {
                    try
                    {
                        if (!_codec.TryGetPortableRef(owner, out uint id, out DerivedKey key)) continue;
                        SyncRef r = ToRef(id, key);
                        List<SyncIcon> icons = HostIcons(em, owner, batch);
                        if (icons.Count == 0) continue;
                        _seen.Add(owner);
                        ulong exact = IconSetDigest.ExactSetHash(icons);
                        if (_sent.TryGetValue(owner, out SentOwner sent))
                        {
                            if (sent.Exact == exact && sent.Ref.Equals(r)) continue;
                            if (!sent.Ref.Equals(r)) batch.IconSet(sent.Ref, Array.Empty<SyncIcon>());
                        }
                        else
                        {
                            sent = new SentOwner();
                            _sent[owner] = sent;
                        }
                        sent.Ref = r;
                        sent.Exact = exact;
                        sent.Icons = icons;
                        batch.IconSet(r, icons);
                    }
                    catch (Exception ex)
                    {
                        _log.Debug(LogCategory.Integrity, "icons of " + owner + " not captured: " + ex.Message);
                    }
                }
            }
            finally
            {
                owners.Dispose();
            }
            _gone.Clear();
            foreach (KeyValuePair<Entity, SentOwner> pair in _sent)
            {
                if (!_seen.Contains(pair.Key)) _gone.Add(pair.Key);
            }
            foreach (Entity owner in _gone)
            {
                batch.IconSet(_sent[owner].Ref, Array.Empty<SyncIcon>());
                _sent.Remove(owner);
            }
            if (_scans % DigestEveryScans == 0)
            {
                IconSetDigest.Accumulator d = HostDigest();
                batch.IconControl(IconControlMode.Digest, d.Owners, d.Icons, d.Sum);
            }
        }

        private List<SyncIcon> HostIcons(EntityManager em, Entity owner, SyncBatch.Writer batch)
        {
            var list = new List<SyncIcon>();
            DynamicBuffer<IconElement> buffer = em.GetBuffer<IconElement>(owner, true);
            for (int i = 0; i < buffer.Length && list.Count < SyncBatch.MaxIconsPerOwner; i++)
            {
                Entity icon = buffer[i].m_Icon;
                if (!em.Exists(icon) || em.HasComponent<Deleted>(icon) || em.HasComponent<Game.Tools.Temp>(icon) || !em.HasComponent<Icon>(icon) || !em.HasComponent<PrefabRef>(icon)) continue;
                Icon data = em.GetComponentData<Icon>(icon);
                if (data.m_ClusterLayer != IconClusterLayer.Default) continue;
                uint prefab = _codec.EnsurePrefab(em.GetComponentData<PrefabRef>(icon).m_Prefab, batch);
                if (prefab == 0) continue;
                Entity target = em.HasComponent<Target>(icon) ? em.GetComponentData<Target>(icon).m_Target : Entity.Null;
                list.Add(new SyncIcon
                {
                    Prefab = prefab,
                    Target = Portable(target),
                    Priority = (byte)data.m_Priority,
                    Flags = (byte)data.m_Flags,
                    X = data.m_Location.x,
                    Y = data.m_Location.y,
                    Z = data.m_Location.z,
                });
            }
            return list;
        }

        private IconSetDigest.Accumulator HostDigest()
        {
            var d = new IconSetDigest.Accumulator();
            foreach (SentOwner sent in _sent.Values)
            {
                ulong owner = sent.Ref.NormalizedHash();
                d.AddOwner();
                foreach (SyncIcon icon in sent.Icons) d.AddIcon(IconSetDigest.Member(owner, icon.Prefab, icon.Target.NormalizedHash(), icon.Priority, icon.Flags));
            }
            _hostIcons = d.Icons;
            return d;
        }

        /// <summary>Host: every set sent so far, framed by full-sync begin and end (one peer, on its request).</summary>
        public void SendFullSync(Action<SyncBatch.Writer> send)
        {
            var w = new SyncBatch.Writer();
            w.IconControl(IconControlMode.FullBegin, 0, 0, 0);
            foreach (SentOwner sent in _sent.Values)
            {
                w.IconSet(sent.Ref, sent.Icons);
                if (w.RecordCount >= 256)
                {
                    send(w);
                    w = new SyncBatch.Writer();
                }
            }
            IconSetDigest.Accumulator d = HostDigest();
            w.IconControl(IconControlMode.FullEnd, d.Owners, d.Icons, d.Sum);
            send(w);
            _log.Info(LogCategory.Integrity, "icon full sync sent: " + d.Icons + " icons on " + d.Owners + " objects");
        }

        // ================================================================== client: records

        /// <summary>Client: the join snapshot is loaded; ask for the host's icon sets.</summary>
        public void OnClientLive()
        {
            _clientActive = true;
            _requestFullSync = true;
            _lastFullSyncRequest = _clock.Seconds;
        }

        /// <summary>True once when a full sync should be requested (consumed).</summary>
        public bool TakeFullSyncRequest()
        {
            bool r = _requestFullSync;
            _requestFullSync = false;
            return r;
        }

        /// <summary>Client: an icon record from the host (in batch order).</summary>
        public void OnRecord(SyncRecord r, double now)
        {
            if (_codec == null || !_clientActive) return;
            if (r.Kind == SyncRecordKind.IconSet)
            {
                ApplySet(r.Owner, r.Icons ?? new List<SyncIcon>(), now);
                return;
            }
            switch (r.IconMode)
            {
                case IconControlMode.Digest:
                    SetDigest(r, now);
                    break;
                case IconControlMode.FullBegin:
                    _hostSets.Clear();
                    _byEntity.Clear();
                    _unresolved.Clear();
                    _attempts.Clear();
                    _fullSyncSince = now;
                    break;
                case IconControlMode.FullEnd:
                    _fullSyncSince = -1;
                    _sweepAll = true;
                    _haveFullSync = true;
                    SetDigest(r, now);
                    break;
            }
        }

        private void SetDigest(SyncRecord r, double now)
        {
            _hasDigest = true;
            _digestOwners = r.Count;
            _digestIcons = r.Count2;
            _digestSum = r.Hash;
            _digestAt = now;
        }

        private void ApplySet(SyncRef owner, List<SyncIcon> icons, double now)
        {
            if (icons.Count == 0)
            {
                if (_hostSets.TryGetValue(owner, out HostSet old))
                {
                    _hostSets.Remove(owner);
                    Unmap(old);
                }
                _unresolved.Remove(owner);
                Entity e = Resolve(owner);
                if (e != Entity.Null)
                {
                    _dirty.Add(e);
                    _attempts.Remove(e);
                }
                return;
            }
            if (!_hostSets.TryGetValue(owner, out HostSet set))
            {
                set = new HostSet { Owner = owner };
                _hostSets.Add(owner, set);
            }
            set.Icons = icons;
            Map(set, now);
        }

        private void Map(HostSet set, double now)
        {
            Entity e = Resolve(set.Owner);
            if (set.Resolved != Entity.Null && set.Resolved != e) Unmap(set);
            if (e == Entity.Null)
            {
                if (!_unresolved.ContainsKey(set.Owner)) _unresolved.Add(set.Owner, now);
                return;
            }
            _unresolved.Remove(set.Owner);
            set.Resolved = e;
            _byEntity[e] = set.Owner;
            _dirty.Add(e);
            _attempts.Remove(e);
        }

        private void Unmap(HostSet set)
        {
            Entity e = set.Resolved;
            set.Resolved = Entity.Null;
            if (e == Entity.Null) return;
            if (_byEntity.TryGetValue(e, out SyncRef current) && current.Equals(set.Owner)) _byEntity.Remove(e);
            _dirty.Add(e);
        }

        /// <summary>Owners that disappeared (regenerated lanes or stops) are found again; unresolved ones are retried and given up after a while.</summary>
        private void Revalidate(double now)
        {
            if (now - _lastRevalidate < RevalidateInterval) return;
            _lastRevalidate = now;
            EntityManager em = _codec.EntityManager;
            foreach (HostSet set in _hostSets.Values)
            {
                if (set.Resolved == Entity.Null || em.Exists(set.Resolved) && !em.HasComponent<Deleted>(set.Resolved)) continue;
                Unmap(set);
                if (!_unresolved.ContainsKey(set.Owner)) _unresolved.Add(set.Owner, now);
            }
            if (_unresolved.Count == 0) return;
            _refScratch.Clear();
            _refScratch.AddRange(_unresolved.Keys);
            foreach (SyncRef r in _refScratch)
            {
                if (!_hostSets.TryGetValue(r, out HostSet set))
                {
                    _unresolved.Remove(r);
                    continue;
                }
                double since = _unresolved[r];
                Map(set, since);
                if (set.Resolved == Entity.Null && now - since > UnresolvedTimeout)
                {
                    _unresolved.Remove(r);
                    _log.Debug(LogCategory.Integrity, "icons of " + r + " cannot be shown: the owner is not found here");
                }
            }
        }

        // ================================================================== client: filter and reconciliation

        /// <summary>Client, right before <c>IconCommandSystem</c> (ModificationEnd): filter local producers, then reconcile with the host.</summary>
        public void BeforeIconCommandSystem()
        {
            if (_codec == null || !_clientActive || _iconSystem == null) return;
            _frame++;
            if (Diagnostics.Features.Icons.IsAvailable)
            {
                try
                {
                    Filter();
                }
                catch (Exception ex)
                {
                    // the queues are left as they were; one failure switches the filter off (local icons are then
                    // corrected after they appear, by the reconciliation below)
                    _filterBroken = true;
                    Diagnostics.Guard.Report(Diagnostics.Features.Icons, "icon filter", ex);
                    Diagnostics.Guard.Disable(Diagnostics.Features.Icons, "icon filter failed: " + ex.Message);
                }
            }
            double now = _clock.Seconds;
            if (_fullSyncSince >= 0)
            {
                if (now - _fullSyncSince < FullSyncTimeout) return;
                _fullSyncSince = -1;
                _sweepAll = true;
            }
            Revalidate(now);
            CollectWork();
            if (_work.Count == 0) return;
            EntityManager em = _codec.EntityManager;
            var buffer = default(IconCommandBuffer);
            bool created = false;
            int processed = 0;
            foreach (Entity owner in _work)
            {
                if (processed >= MaxOwnersPerFrame)
                {
                    _dirty.Add(owner);
                    continue;
                }
                processed++;
                try
                {
                    Reconcile(em, owner, ref buffer, ref created);
                }
                catch (Exception ex)
                {
                    _log.Debug(LogCategory.Integrity, "icons of " + owner + " not reconciled: " + ex.Message);
                }
            }
        }

        private void CollectWork()
        {
            _work.Clear();
            var set = new HashSet<Entity>();
            if (_sweepAll)
            {
                _sweepAll = false;
                NativeArray<Entity> owners = _owners.ToEntityArray(Allocator.Temp);
                try
                {
                    foreach (Entity e in owners) set.Add(e);
                }
                finally
                {
                    owners.Dispose();
                }
                foreach (Entity e in _byEntity.Keys) set.Add(e);
            }
            for (int i = _verify.Count - 1; i >= 0; i--)
            {
                if (_verify[i].Frame > _frame) continue;
                set.Add(_verify[i].Owner);
                _verify.RemoveAt(i);
            }
            foreach (Entity e in _dirty) set.Add(e);
            _dirty.Clear();
            _work.AddRange(set);
        }

        private void Reconcile(EntityManager em, Entity owner, ref IconCommandBuffer buffer, ref bool created)
        {
            if (!em.Exists(owner) || em.HasComponent<Deleted>(owner) || em.HasComponent<Game.Tools.Temp>(owner)) return;
            List<SyncIcon> desired = null;
            if (_byEntity.TryGetValue(owner, out SyncRef r) && _hostSets.TryGetValue(r, out HostSet set)) desired = set.Icons;
            // without a host set: strip only once the full sync told us the host has none, and only owners it can name
            if (desired == null && (!_haveFullSync || !_codec.TryGetPortableRef(owner, out _, out _))) return;
            CollectLive(em, owner, _live);
            _pending.Clear();
            if (desired != null)
            {
                foreach (SyncIcon d in desired)
                {
                    Entity prefab = _ids.GetEntity(d.Prefab);
                    if (prefab == Entity.Null || !em.HasComponent<NotificationIconData>(prefab)) continue;
                    Entity target = Resolve(d.Target);
                    bool targetLost = target == Entity.Null && d.Target.Kind != SyncRef.None;
                    byte flags = d.Flags;
                    if (targetLost) flags = (byte)(flags & ~FlagTargetLocation);
                    bool custom = (flags & FlagCustomLocation) != 0 || targetLost;
                    var location = new float3(d.X, d.Y, d.Z);
                    int match = -1;
                    for (int i = 0; i < _live.Count; i++)
                    {
                        LiveIcon l = _live[i];
                        if (l.Matched || l.Prefab != prefab || ((l.Flags ^ flags) & FlagSecondary) != 0) continue;
                        if (((l.Flags | flags) & FlagIgnoreTarget) == 0 && l.Target != target) continue;
                        match = i;
                        break;
                    }
                    if (match >= 0)
                    {
                        LiveIcon l = _live[match];
                        l.Matched = true;
                        _live[match] = l;
                        bool same = l.Priority == d.Priority && (l.Flags & IconSetDigest.ComparedFlags) == (flags & IconSetDigest.ComparedFlags)
                                    && (!custom || math.distancesq(l.Location, location) < 0.25f);
                        if (same) continue;
                    }
                    _pending.Add(new Pending { Prefab = prefab, Target = target, Priority = d.Priority, Flags = flags, Custom = custom, Location = location });
                }
            }
            foreach (LiveIcon l in _live)
            {
                if (!l.Matched) _pending.Add(new Pending { Remove = true, Prefab = l.Prefab, Target = l.Target, Flags = (byte)(l.Flags & FlagSecondary) });
            }
            if (_pending.Count == 0)
            {
                _attempts.Remove(owner);
                return;
            }
            _attempts.TryGetValue(owner, out int attempts);
            attempts++;
            _attempts[owner] = attempts;
            if (attempts > MaxAttemptsPerOwner)
            {
                if (attempts == MaxAttemptsPerOwner + 1) _log.Info(LogCategory.Integrity, "icons of " + owner + " (" + r + ") do not converge (" + _pending.Count + " commands); left as they are until the host changes them");
                return;
            }
            if (!created)
            {
                buffer = _iconSystem.CreateCommandBuffer();
                created = true;
            }
            foreach (Pending p in _pending)
            {
                if (p.Remove)
                {
                    buffer.Remove(owner, p.Prefab, p.Target, (IconFlags)p.Flags);
                }
                else if (p.Custom)
                {
                    buffer.Add(owner, p.Prefab, p.Location, (IconPriority)p.Priority, IconClusterLayer.Default, (IconFlags)(p.Flags & ~FlagCustomLocation), p.Target);
                }
                else
                {
                    buffer.Add(owner, p.Prefab, (IconPriority)p.Priority, IconClusterLayer.Default, (IconFlags)p.Flags, p.Target);
                }
                _commandsIssued++;
            }
            _verify.Add((_frame + VerifyAfterFrames, owner));
        }

        /// <summary>The owner's live default-layer icons (not being resolved, not tool previews).</summary>
        private static void CollectLive(EntityManager em, Entity owner, List<LiveIcon> output)
        {
            output.Clear();
            if (!em.HasBuffer<IconElement>(owner)) return;
            DynamicBuffer<IconElement> buffer = em.GetBuffer<IconElement>(owner, true);
            for (int i = 0; i < buffer.Length; i++)
            {
                Entity icon = buffer[i].m_Icon;
                if (!em.Exists(icon) || em.HasComponent<Deleted>(icon) || em.HasComponent<Game.Tools.Temp>(icon) || !em.HasComponent<Icon>(icon) || !em.HasComponent<PrefabRef>(icon)) continue;
                Icon data = em.GetComponentData<Icon>(icon);
                if (data.m_ClusterLayer != IconClusterLayer.Default) continue;
                output.Add(new LiveIcon
                {
                    Prefab = em.GetComponentData<PrefabRef>(icon).m_Prefab,
                    Target = em.HasComponent<Target>(icon) ? em.GetComponentData<Target>(icon).m_Target : Entity.Null,
                    Priority = (byte)data.m_Priority,
                    Flags = (byte)data.m_Flags,
                    Location = data.m_Location,
                });
            }
        }

        /// <summary>Drops the local status decisions from the vanilla icon command queues (see the class remarks).</summary>
        private void Filter()
        {
            if (_filterBroken) return;
            if (!_filterReady)
            {
                _queuesField = Diagnostics.Probes.IconQueues;
                _depsField = Diagnostics.Probes.IconDependencies;
                if (_queuesField == null || _depsField == null)
                {
                    _filterBroken = true;
                    _log.Warn(LogCategory.Integrity, "IconCommandSystem queues not found (game update?): local warning icons are corrected after they appear");
                    return;
                }
                _filterReady = true;
                _log.Info(LogCategory.Integrity, "icon authority filter active: warning icons on this client come from the host");
            }
            var queues = _queuesField.GetValue(_iconSystem) as List<NativeQueue<IconCommandBuffer.Command>>;
            if (queues == null || queues.Count == 0) return;
            ((JobHandle)_depsField.GetValue(_iconSystem)).Complete();
            EntityManager em = _codec.EntityManager;
            for (int i = 0; i < queues.Count; i++)
            {
                NativeQueue<IconCommandBuffer.Command> queue = queues[i];
                if (!queue.IsCreated) continue;
                int count = queue.Count;
                if (count == 0) continue;
                _keep.Clear();
                for (int k = 0; k < count; k++)
                {
                    IconCommandBuffer.Command c = queue.Dequeue();
                    if (Keep(em, c)) _keep.Add(c);
                    else _dropped++;
                }
                foreach (IconCommandBuffer.Command c in _keep) queue.Enqueue(c);
            }
            double now = _clock.Seconds;
            if (_dropped != _droppedLogged && now - _lastDropLog > 60)
            {
                _lastDropLog = now;
                _log.Debug(LogCategory.Integrity, "icon filter: " + (_dropped - _droppedLogged) + " local status icon commands dropped (the host's icons are shown instead)");
                _droppedLogged = _dropped;
            }
        }

        private bool Keep(EntityManager em, in IconCommandBuffer.Command c)
        {
            IconCommandBuffer.CommandFlags f = c.m_CommandFlags;
            if ((f & IconCommandBuffer.CommandFlags.Temp) != 0) return true;                  // tool previews and validation
            if (c.m_ClusterLayer != IconClusterLayer.Default) return true;                     // markers, transaction animations
            if ((f & (IconCommandBuffer.CommandFlags.Add | IconCommandBuffer.CommandFlags.Remove)) == 0) return true; // Update
            Entity owner = c.m_Owner;
            if (!em.Exists(owner) || em.HasComponent<Deleted>(owner) || em.HasComponent<Game.Tools.Temp>(owner)) return true;
            if ((f & IconCommandBuffer.CommandFlags.Remove) != 0)
            {
                if (c.m_Target != Entity.Null && (!em.Exists(c.m_Target) || em.HasComponent<Deleted>(c.m_Target))) return true;
                if (RemovesDeletedIcon(em, c)) return true;
            }
            return !_codec.TryGetPortableRef(owner, out _, out _); // owners the host cannot name stay local
        }

        /// <summary>IconDeletedSystem's removal of an icon entity that was deleted (bookkeeping, not a status decision).</summary>
        private static bool RemovesDeletedIcon(EntityManager em, in IconCommandBuffer.Command c)
        {
            if (!em.HasBuffer<IconElement>(c.m_Owner)) return false;
            DynamicBuffer<IconElement> buffer = em.GetBuffer<IconElement>(c.m_Owner, true);
            for (int i = 0; i < buffer.Length; i++)
            {
                Entity icon = buffer[i].m_Icon;
                if (!em.Exists(icon) || !em.HasComponent<Deleted>(icon)) continue;
                if (c.m_Prefab == Entity.Null) return true;
                if (em.HasComponent<PrefabRef>(icon) && em.GetComponentData<PrefabRef>(icon).m_Prefab == c.m_Prefab) return true;
            }
            return false;
        }

        // ================================================================== client: digest

        /// <summary>Client, at the end of the frame (after the icon playback): fallback detection and the digest comparison.</summary>
        public void ClientFrameEnd(double now)
        {
            if (_codec == null || !_clientActive) return;
            if (_filterBroken || !_filterReady)
            {
                MarkCreatedIconOwners();
                if (now - _lastFallbackSweep > FallbackSweepInterval)
                {
                    _lastFallbackSweep = now;
                    _sweepAll = true;
                }
            }
            if (!_hasDigest) return;
            if (now - _digestAt > 5)
            {
                _hasDigest = false;
                return;
            }
            if (_dirty.Count > 0 || _sweepAll || _verify.Count > 0 || _fullSyncSince >= 0 || _unresolved.Count > 0) return;
            _hasDigest = false;
            IconSetDigest.Accumulator local = LocalDigest();
            if (local.Matches(_digestOwners, _digestIcons, _digestSum))
            {
                if (_iconState != 1 && _iconState != 0) _log.Info(LogCategory.Integrity, "icons match the host again");
                _iconState = 1;
                _digestMismatches = 0;
                _fullSyncsWithoutMatch = 0;
                return;
            }
            _digestMismatches++;
            if (_digestMismatches < 2) return;
            _iconState = 2;
            double wait = _fullSyncsWithoutMatch >= 3 ? FullSyncBackoff : FullSyncInterval;
            if (now - _lastFullSyncRequest < wait) return;
            _log.Info(LogCategory.Integrity, "icons differ from the host (host " + _digestIcons + " icons on " + _digestOwners + " objects, here " + local.Icons + " on " + local.Owners
                                             + "); requesting a full icon sync" + (_fullSyncsWithoutMatch >= 3 ? " (earlier ones did not help)" : ""));
            _lastFullSyncRequest = now;
            _fullSyncsWithoutMatch++;
            _requestFullSync = true;
            _digestMismatches = 0;
        }

        private IconSetDigest.Accumulator LocalDigest()
        {
            var d = new IconSetDigest.Accumulator();
            EntityManager em = _codec.EntityManager;
            NativeArray<Entity> owners = _owners.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity owner in owners)
                {
                    if (!_codec.TryGetPortableRef(owner, out uint id, out DerivedKey key)) continue;
                    ulong ownerHash = ToRef(id, key).NormalizedHash();
                    CollectLive(em, owner, _live);
                    if (_live.Count == 0) continue;
                    d.AddOwner();
                    foreach (LiveIcon l in _live)
                    {
                        d.AddIcon(IconSetDigest.Member(ownerHash, _ids.GetNetId(l.Prefab), Portable(l.Target).NormalizedHash(), l.Priority, l.Flags));
                    }
                }
            }
            finally
            {
                owners.Dispose();
            }
            return d;
        }

        private void MarkCreatedIconOwners()
        {
            if (_createdIcons.IsEmptyIgnoreFilter) return;
            NativeArray<Owner> owners = _createdIcons.ToComponentDataArray<Owner>(Allocator.Temp);
            try
            {
                foreach (Owner o in owners)
                {
                    if (o.m_Owner != Entity.Null) _dirty.Add(o.m_Owner);
                }
            }
            finally
            {
                owners.Dispose();
            }
        }

        /// <summary>Commands issued by the reconciliation since creation (diagnostics).</summary>
        public long CommandsIssued => _commandsIssued;
    }
}
