using System;
using System.Collections.Generic;
using SkylineTogether.Core;
using SkylineTogether.Core.Sync;
using SkylineTogether.Diagnostics;
using Unity.Collections;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// Client: the owned and attached objects whose owner or parent this client deleted, deleted the game's way
    /// (ARCHITECTURE.md#reference-safety). The game deletes sub-objects, sub-nets, sub-areas and owned vehicles with their
    /// owner through the owner's own lists (<c>SubElementDeleteSystem</c>, <c>SubObjectSystem</c>); on a client those lists
    /// can be out of step with the children's <c>Owner</c> (children the host's records and the client's own generation
    /// made at different times), and the vehicle systems that clean up on the host are off. Children left behind kept an
    /// <c>Owner</c> or <c>Attached.m_Parent</c> naming a destroyed entity, which the game's Burst jobs follow unchecked.
    /// <para>
    /// <see cref="RemovalCascadeSystem"/> notes every object, net and area deleted in a frame (by the host's despawns, the
    /// player's own bulldozing, a cascade); the next frame starts with this sweep, before any game system: every owned or
    /// attached object (lanes excepted: the game regenerates them from their owner) whose owner or parent is one of them and
    /// no longer exists is made safe (<see cref="EntityCodec.SanitizeForDelete"/>) and deleted; an owned vehicle goes through
    /// the mover channel. A replicated child is unbound and asked from the host (it answers with its despawn when it deleted
    /// it too); an object that only lost its parent and is replicated is detached instead and asked for.
    /// </para>
    /// <para>
    /// Cost: the children a deleted entity's own lists name (sub-objects, sub-nets, sub-areas, owned vehicles, installed
    /// upgrades) are noted with it and checked the next frame directly. The scan over every owned and attached object,
    /// which finds the children those lists missed, runs as a background lap at most every
    /// <see cref="FullScanSeconds"/> (it ran on every frame after any deletion, a hitch of milliseconds in big cities
    /// while the host edited); the deletions are kept until the lap ran.
    /// </para>
    /// Main thread only.
    /// </summary>
    internal sealed class OrphanSweep
    {
        /// <summary>The scan over every owned and attached object runs at most this often (seconds).</summary>
        public const double FullScanSeconds = 1.0;

        /// <summary>
        /// Children noted for the direct check at most (they pile up while the client is not live yet and nothing sweeps);
        /// beyond it they are left to the full scan, which finds every child.
        /// </summary>
        public const int MaxCandidates = 1 << 16;

        private readonly IndexVersionSet _deleted = new IndexVersionSet();
        private readonly List<Entity> _orphans = new List<Entity>();
        private readonly List<Entity> _detach = new List<Entity>();
        private readonly List<Entity> _candidates = new List<Entity>();
        private EntityQuery _owned, _attached;
        private World _world;
        private long _deletedTotal, _detachedTotal, _sweeps, _fullScans;
        private double _lastFullScan = double.NegativeInfinity;

        /// <summary>The ids of replicated children it deleted or detached (the controller asks the host for them).</summary>
        public Action<uint> Request;

        public NetIdSystem Ids;

        public string Describe() => "orphan sweep: " + _sweeps + " sweeps (" + _fullScans + " full scans), " + _deletedTotal + " owned objects deleted with their owner, " + _detachedTotal + " detached from a deleted parent";

        /// <summary>
        /// Notes an entity deleted this frame (it still exists) and the children its own lists name; the next frame's
        /// sweep checks them, the next full scan looks for any other child.
        /// </summary>
        public void NoteDeleted(EntityManager em, Entity e)
        {
            _deleted.Add(e.Index, e.Version);
            try
            {
                AddChildren<Game.Objects.SubObject>(em, e, x => x.m_SubObject);
                AddChildren<Game.Net.SubNet>(em, e, x => x.m_SubNet);
                AddChildren<Game.Areas.SubArea>(em, e, x => x.m_Area);
                AddChildren<Game.Vehicles.OwnedVehicle>(em, e, x => x.m_Vehicle);
                AddChildren<Game.Buildings.InstalledUpgrade>(em, e, x => x.m_Upgrade);
            }
            catch (Exception)
            {
                // the full scan finds them
            }
        }

        private void AddChildren<T>(EntityManager em, Entity e, Func<T, Entity> child) where T : unmanaged, IBufferElementData
        {
            if (!em.HasBuffer<T>(e)) return;
            DynamicBuffer<T> buffer = em.GetBuffer<T>(e, true);
            for (int i = 0; i < buffer.Length; i++)
            {
                Entity c = child(buffer[i]);
                if (c == Entity.Null) continue;
                if (_candidates.Count >= MaxCandidates) return; // the full scan finds the rest
                _candidates.Add(c);
            }
        }

        public bool HasWork => _deleted.Count > 0;

        public void Run(EntityManager em, ILog log)
        {
            if (_deleted.Count == 0) return;
            long start = Perf.Start();
            double now = Perf.Now;
            bool full = now - _lastFullScan >= FullScanSeconds;
            try
            {
                Ensure(em);
                _sweeps++;
                _orphans.Clear();
                _detach.Clear();
                if (full)
                {
                    _lastFullScan = now;
                    _fullScans++;
                    Collect<Game.Common.Owner>(em, _owned, OwnerOf, _orphans);
                    Collect<Game.Objects.Attached>(em, _attached, ParentOf, _detach);
                }
                else
                {
                    CollectCandidates(em);
                }
                foreach (Entity e in _orphans) Delete(em, e);
                foreach (Entity e in _detach)
                {
                    if (!em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e)) continue;
                    uint id = Ids != null ? Ids.GetNetId(e) : 0;
                    if (id != 0)
                    {
                        // a replicated object keeps living without its parent until the host's record arrives
                        Game.Objects.Attached attached = em.GetComponentData<Game.Objects.Attached>(e);
                        Forensics.Cleanup("orphan sweep", em, e, "its parent " + Forensics.Describe(em, attached.m_Parent) + " was deleted here -> detached, asked from the host");
                        attached.m_Parent = Entity.Null;
                        em.SetComponentData(e, attached);
                        if (!em.HasComponent<Game.Common.Updated>(e)) em.AddComponent<Game.Common.Updated>(e);
                        _detachedTotal++;
                        Request?.Invoke(id);
                    }
                    else
                    {
                        Delete(em, e);
                    }
                }
                if (_orphans.Count + _detach.Count > 0)
                {
                    log?.Info(LogCategory.Integrity, "orphan sweep: " + _orphans.Count + " owned object(s) whose owner was deleted here deleted with it, " + _detach.Count
                                                     + " attached object(s) whose parent was deleted here handled (session: " + _deletedTotal + " deleted, " + _detachedTotal + " detached)");
                }
            }
            finally
            {
                if (full) _deleted.Clear(); // kept until a full scan looked for the children the lists missed
                _candidates.Clear();
                _orphans.Clear();
                _detach.Clear();
                Perf.Stop(Perf.ReferenceRepair, start);
            }
        }

        /// <summary>The children the deleted entities' own lists named, checked like the full scan checks every child.</summary>
        private void CollectCandidates(EntityManager em)
        {
            foreach (Entity c in _candidates)
            {
                if (!em.Exists(c) || em.HasComponent<Game.Common.Deleted>(c) || em.HasComponent<Game.Tools.Temp>(c) || em.HasComponent<Game.Net.Lane>(c)) continue;
                if (em.HasComponent<Game.Common.Owner>(c) && IsGoneDeleted(em, em.GetComponentData<Game.Common.Owner>(c).m_Owner))
                {
                    if (!_orphans.Contains(c)) _orphans.Add(c);
                }
                else if (em.HasComponent<Game.Objects.Attached>(c) && IsGoneDeleted(em, em.GetComponentData<Game.Objects.Attached>(c).m_Parent))
                {
                    if (!_detach.Contains(c)) _detach.Add(c);
                }
            }
        }

        private bool IsGoneDeleted(EntityManager em, Entity target) => target != Entity.Null && _deleted.Contains(target.Index, target.Version) && !em.Exists(target);

        private static readonly Func<Game.Common.Owner, Entity> OwnerOf = x => x.m_Owner;
        private static readonly Func<Game.Objects.Attached, Entity> ParentOf = x => x.m_Parent;

        private void Delete(EntityManager em, Entity e)
        {
            if (!em.Exists(e) || em.HasComponent<Game.Common.Deleted>(e)) return;
            bool mover = em.HasComponent<Game.Vehicles.Vehicle>(e) || em.HasComponent<Game.Creatures.Creature>(e);
            Entity owner = em.HasComponent<Game.Common.Owner>(e) ? em.GetComponentData<Game.Common.Owner>(e).m_Owner : Entity.Null;
            Forensics.Cleanup("orphan sweep", em, e, (owner != Entity.Null ? "its owner " + Forensics.Describe(em, owner) : "its parent") + " was deleted here -> deleted");
            if (mover && DeletionCascade.DeleteMover != null && DeletionCascade.DeleteMover(em, e))
            {
                _deletedTotal++;
                return;
            }
            uint id = Ids != null ? Ids.GetNetId(e) : 0;
            EntityCodec.SanitizeForDelete(em, e); // the owner is gone: the reference systems must not index it
            if (em.HasComponent<Game.Objects.Attached>(e))
            {
                Game.Objects.Attached attached = em.GetComponentData<Game.Objects.Attached>(e);
                if (attached.m_Parent != Entity.Null && !em.Exists(attached.m_Parent))
                {
                    attached.m_Parent = Entity.Null;
                    em.SetComponentData(e, attached);
                }
            }
            DeletionCascade.Apply(em, e, null); // a service upgrade's workers, a building's renters
            em.AddComponent<Game.Common.Deleted>(e);
            _deletedTotal++;
            if (id != 0 && Ids != null)
            {
                Ids.Unbind(id);
                Request?.Invoke(id);
            }
        }

        private void Collect<T>(EntityManager em, EntityQuery query, Func<T, Entity> get, List<Entity> output) where T : unmanaged, IComponentData
        {
            query.CompleteDependency();
            NativeArray<ArchetypeChunk> chunks = query.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                ComponentTypeHandle<T> handle = em.GetComponentTypeHandle<T>(true);
                EntityTypeHandle entityType = em.GetEntityTypeHandle();
                foreach (ArchetypeChunk chunk in chunks)
                {
                    NativeArray<T> values = chunk.GetNativeArray(ref handle);
                    NativeArray<Entity> entities = chunk.GetNativeArray(entityType);
                    for (int i = 0; i < values.Length; i++)
                    {
                        Entity target = get(values[i]);
                        if (target == Entity.Null || !_deleted.Contains(target.Index, target.Version)) continue;
                        if (em.Exists(target)) continue; // still there (the game destroys it later): not an orphan yet
                        output.Add(entities[i]);
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }
        }

        private void Ensure(EntityManager em)
        {
            World world = World.DefaultGameObjectInjectionWorld;
            if (_world == world && _world != null) return;
            _world = world;
            ComponentType[] none =
            {
                ComponentType.ReadOnly<Game.Common.Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>(), ComponentType.ReadOnly<Game.Net.Lane>(),
                ComponentType.ReadOnly<Game.Prefabs.PrefabData>(),
            };
            _owned = em.CreateEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<Game.Common.Owner>() }, None = none });
            _attached = em.CreateEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<Game.Objects.Attached>() }, None = none });
        }

        public void Reset()
        {
            _deleted.Clear();
            _candidates.Clear();
            _orphans.Clear();
            _detach.Clear();
            _world = null;
            _deletedTotal = _detachedTotal = _sweeps = _fullScans = 0;
            _lastFullScan = double.NegativeInfinity;
        }
    }
}
