using System;
using Game;
using SkylineTogether.Diagnostics;
using Unity.Collections;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// The single apply point (R11 §6.2): MainLoop, right after <c>EndFrameBarrier</c> playback and before the
    /// raycast, the tools and the Modification phases, so deletions reach <c>OriginalDeletedSystem</c> and
    /// <c>SubElementDeleteSystem</c> and every derived system sees the tags. Without a session it returns at once.
    /// </summary>
    public sealed class SyncApplySystem : GameSystemBase
    {
        protected override void OnUpdate()
        {
            SyncController sync = Mod.Sync;
            if (sync == null || !SyncController.SessionActive) return;
            try
            {
                sync.ApplyIncoming();
            }
            catch (Exception ex)
            {
                Guard.Report(Features.Replication, "apply", ex);
            }
        }
    }

    /// <summary>
    /// The capture point: the end of MainLoop, just before <c>PrepareCleanUpSystem</c> snapshots and clears the update
    /// tags, after every tool apply and Modification phase of the frame.
    /// </summary>
    public sealed class SyncCaptureSystem : GameSystemBase
    {
        protected override void OnUpdate()
        {
            SyncController sync = Mod.Sync;
            if (sync == null || !SyncController.SessionActive) return;
            try
            {
                sync.CaptureOutgoing(UnityEngine.Time.realtimeSinceStartupAsDouble);
            }
            catch (Exception ex)
            {
                Guard.Report(Features.Replication, "capture", ex);
            }
            // solo testing: the recorder's checkpoints (host) and the replay's icon comparison (client), after the capture
            Replay.ReplayController.Instance?.OnCaptureEnd();
        }
    }

    /// <summary>
    /// Host: binds this frame's new entities right before <c>SaveGameSystem</c> (join snapshots). On a client it also
    /// notices a save the player started (the host's city is saved as a local copy) and warns once per save.
    /// </summary>
    public sealed class SyncBindSystem : GameSystemBase
    {
        private Game.Serialization.SaveGameSystem _save;
        private bool _warned;

        protected override void OnCreate()
        {
            base.OnCreate();
            _save = World.GetOrCreateSystemManaged<Game.Serialization.SaveGameSystem>();
        }

        protected override void OnUpdate()
        {
            SyncController sync = Mod.Sync;
            if (sync == null || !SyncController.SessionActive) return;
            try
            {
                long start = Perf.Start();
                sync.BindCreated();
                Perf.Stop(Perf.Bind, start);
                bool saving = _save != null && _save.Enabled;
                if (saving && !_warned)
                {
                    _warned = true;
                    Mod.Sessions?.OnLocalSave();
                }
                else if (!saving)
                {
                    _warned = false;
                }
            }
            catch (Exception ex)
            {
                Guard.Report(Features.Replication, "bind", ex);
            }
        }
    }

    /// <summary>First in the ApplyTool phase, which only runs when the active tool applies: records the preview's
    /// originals and cost before the vanilla apply systems consume them.</summary>
    public sealed class SyncToolApplySystem : GameSystemBase
    {
        protected override void OnUpdate()
        {
            SyncController sync = Mod.Sync;
            if (sync == null || !SyncController.SessionActive) return;
            try
            {
                sync.OnToolApply();
            }
            catch (Exception ex)
            {
                Guard.Report(Features.Replication, "tool hook", ex);
            }
        }
    }
    /// <summary>
    /// Client, in ModificationEnd (where the game's own removal systems run on the host, which the client switches off):
    /// the clean-up of every entity deleted on this client this frame that the applier did not already clean up after:
    /// the player's own bulldozing and the objects the game deletes with it (vehicles of a demolished building, sub-objects),
    /// the snapshot agents without a host id, a mod tool's deletions (<see cref="DeletionCascade"/>, ARCHITECTURE.md#reference-safety).
    /// <list type="bullet">
    /// <item>agents and buildings: every agent link that names them, found through the lists and the referrer index (a
    /// demolished building is named by the <c>CurrentBuilding</c> of every citizen inside it, which no list leads to);</item>
    /// <item>vehicles: their riders leave them (the game's <c>ObjectEmergeSystem</c> is off on clients; a rider kept pointing
    /// at the destroyed vehicle); a creature leaves its vehicle's passenger list;</item>
    /// <item>buildings, nets, areas and owners of sub-objects or vehicles: noted for the next frame's <see cref="OrphanSweep"/> (children whose owner or parent is
    /// then gone), except a node or edge the player's own tool replaced or removed (tagged <c>Applied</c>): the game's
    /// ungated Modification systems re-own or delete its children in the same frame.</item>
    /// </list>
    /// Every clean-up it makes is a forensics line (<see cref="Forensics"/>).
    /// It runs before the game destroys them at the end of the frame, so nothing keeps naming them.
    /// </summary>
    public sealed class RemovalCascadeSystem : GameSystemBase
    {
        private EntityQuery _deleted;

        protected override void OnCreate()
        {
            base.OnCreate();
            _deleted = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Common.Deleted>() },
                Any = new[]
                {
                    ComponentType.ReadOnly<Game.Citizens.Citizen>(), ComponentType.ReadOnly<Game.Citizens.Household>(), ComponentType.ReadOnly<Game.Citizens.HouseholdPet>(),
                    ComponentType.ReadOnly<Game.Companies.CompanyData>(), ComponentType.ReadOnly<Game.Buildings.Renter>(), ComponentType.ReadOnly<Game.Companies.Employee>(),
                    ComponentType.ReadOnly<Game.Buildings.Student>(), ComponentType.ReadOnly<Game.Buildings.Patient>(), ComponentType.ReadOnly<Game.Buildings.Occupant>(),
                    ComponentType.ReadOnly<Game.Buildings.Building>(), ComponentType.ReadOnly<Game.Vehicles.Vehicle>(), ComponentType.ReadOnly<Game.Creatures.Creature>(),
                    ComponentType.ReadOnly<Game.Objects.Object>(), ComponentType.ReadOnly<Game.Net.Node>(), ComponentType.ReadOnly<Game.Net.Edge>(),
                    ComponentType.ReadOnly<Game.Areas.Area>(),
                },
                None = new[] { ComponentType.ReadOnly<Game.Tools.Temp>() },
            });
        }

        protected override void OnUpdate()
        {
            SyncController sync = Mod.Sync;
            if (sync == null || !SyncController.SessionActive || sync.IsHost || !Features.Replication.IsAvailable)
            {
                DeletionCascade.DoneThisFrame.Clear();
                return;
            }
            if (_deleted.IsEmptyIgnoreFilter)
            {
                DeletionCascade.DoneThisFrame.Clear();
                return;
            }
            long start = Perf.Start();
            NativeArray<Entity> entities = _deleted.ToEntityArray(Allocator.Temp);
            try
            {
                EntityManager em = EntityManager;
                MoverClient movers = MoverSync.Instance?.Client;
                foreach (Entity e in entities)
                {
                    if (!em.Exists(e)) continue;
                    bool done = DeletionCascade.DoneThisFrame.Contains(e);
                    bool mover = em.HasComponent<Game.Vehicles.Vehicle>(e) || em.HasComponent<Game.Creatures.Creature>(e);
                    bool byTool = em.HasComponent<Game.Common.Applied>(e); // the player's own tool apply of this frame
                    if (mover)
                    {
                        // riders out of a vehicle the game deleted here (idempotent for the channel's own despawns)
                        if (!done && em.HasBuffer<Game.Vehicles.Passenger>(e) && em.GetBuffer<Game.Vehicles.Passenger>(e, true).Length > 0)
                        {
                            Forensics.Cleanup("removal cascade", em, e, "deleted here: its " + em.GetBuffer<Game.Vehicles.Passenger>(e, true).Length + " rider(s) leave it");
                        }
                        movers?.OnDeletedLocally(em, e);
                    }
                    else
                    {
                        // a node or edge the player's own tool replaced or removed: the game's own (ungated) Modification
                        // systems re-own or delete its sub-objects, sub-nets, lanes and zone blocks in this very frame, so the
                        // orphan sweep leaves its children to them (ARCHITECTURE.md#reference-safety)
                        bool toolNet = byTool && (em.HasComponent<Game.Net.Node>(e) || em.HasComponent<Game.Net.Edge>(e));
                        if (!toolNet && MayHaveChildren(em, e)) sync.Orphans.NoteDeleted(em, e);
                        if (done || !HasLinks(em, e)) continue;
                        // agents and movers that name a building or agent list the player (or the game) deleted here: the
                        // game's removal systems are off on clients; nothing of the tool apply itself is touched
                        int n = DeletionCascade.Apply(em, e, null);
                        DeletionCascade.LocalDeletions++;
                        if (n > 0) Forensics.Cleanup("removal cascade", em, e, "deleted here (" + (byTool ? "by the player's tool" : "by the game or a mod") + "): " + n + " agent link(s), list entries or movers cleaned");
                    }
                }
                // riders of the vehicles deleted above that their passenger lists did not name, this frame (before the
                // Cleanup phase destroys the vehicles)
                movers?.FlushStrayRiders(em);
            }
            catch (Exception ex)
            {
                Guard.Report(Features.Replication, "removal clean-up of local deletions", ex);
            }
            finally
            {
                entities.Dispose();
                DeletionCascade.DoneThisFrame.Clear();
                Perf.Stop(Perf.ReferenceRepair, start);
            }
        }

        /// <summary>
        /// What can own or carry other objects: buildings, nets, areas, anything with sub-objects, sub-nets, sub-areas or
        /// owned vehicles (a tree or a prop has nothing for the orphan sweep to look for).
        /// </summary>
        private static bool MayHaveChildren(EntityManager em, Entity e)
        {
            return em.HasComponent<Game.Buildings.Building>(e) || em.HasComponent<Game.Net.Node>(e) || em.HasComponent<Game.Net.Edge>(e) || em.HasComponent<Game.Areas.Area>(e)
                   || em.HasBuffer<Game.Objects.SubObject>(e) || em.HasBuffer<Game.Net.SubNet>(e) || em.HasBuffer<Game.Areas.SubArea>(e)
                   || em.HasBuffer<Game.Vehicles.OwnedVehicle>(e);
        }

        /// <summary>Agents, buildings and holders of an agent list (a tree or a road has no agent link to clean).</summary>
        private static bool HasLinks(EntityManager em, Entity e)
        {
            return em.HasComponent<Game.Buildings.Building>(e) || em.HasComponent<Game.Citizens.Citizen>(e) || em.HasComponent<Game.Citizens.Household>(e)
                   || em.HasComponent<Game.Citizens.HouseholdPet>(e) || em.HasComponent<Game.Companies.CompanyData>(e) || em.HasBuffer<Game.Buildings.Renter>(e)
                   || em.HasBuffer<Game.Companies.Employee>(e) || em.HasBuffer<Game.Buildings.Student>(e) || em.HasBuffer<Game.Buildings.Patient>(e)
                   || em.HasBuffer<Game.Buildings.Occupant>(e);
        }
    }

    /// <summary>
    /// Modification5, right before the game's <c>UpdateGroupSystem</c> (ARCHITECTURE.md#client-apply): no entity with
    /// <c>UpdateFrame</c> and <c>Created</c> reaches it without one of the update-group markers
    /// (<see cref="Core.Sync.UpdateGroupMarkers"/>). The game logs "UpdateFrame added to unsupported type" for such a chunk,
    /// gives a created entity an update frame out of range and indexes its Burst job with it. The applier never creates such
    /// an entity (<see cref="EntityCodec"/> checks every spawn), so this is the last line: each such chunk is named once per
    /// archetype in the log with its component types, family and prefab, a created entity of it is deleted (its id released
    /// and asked from the host again), and a deleted one (harmless to the game, but a sign of a link removed from it) is
    /// reported. An entity the player's own tool apply created this frame (<c>Applied</c>) is reported only: the game owns
    /// it, as in single player. During a session only.
    /// </summary>
    public sealed class UpdateFrameGuardSystem : GameSystemBase
    {
        private EntityQuery _unsupported;
        private readonly System.Collections.Generic.HashSet<EntityArchetype> _logged = new System.Collections.Generic.HashSet<EntityArchetype>();
        private World _sessionWorld;

        /// <summary>Created entities without a marker deleted this session (diagnostics).</summary>
        public static long Deleted;

        /// <summary>Deleted entities without a marker seen this session (diagnostics).</summary>
        public static long SeenDeleted;

        /// <summary>Entities the player's own tool created without a marker, left to the game this session (diagnostics).</summary>
        public static long LeftToTool;

        protected override void OnCreate()
        {
            base.OnCreate();
            var none = new System.Collections.Generic.List<ComponentType>();
            foreach (string name in Core.Sync.UpdateGroupMarkers.TypeNames)
            {
                Type t = FamilyClassifier.FindType(name);
                if (t != null) none.Add(ComponentType.ReadOnly(t));
            }
            _unsupported = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Simulation.UpdateFrame>() },
                Any = new[] { ComponentType.ReadOnly<Game.Common.Created>(), ComponentType.ReadOnly<Game.Common.Deleted>() },
                None = none.ToArray(),
            });
        }

        protected override void OnUpdate()
        {
            SyncController sync = Mod.Sync;
            if (sync == null || !SyncController.SessionActive || _unsupported.IsEmptyIgnoreFilter) return;
            if (_sessionWorld != World)
            {
                _sessionWorld = World;
                _logged.Clear();
            }
            try
            {
                sync.GuardUnsupported(EntityManager, _unsupported, _logged);
            }
            catch (Exception ex)
            {
                Guard.Report(Features.Replication, "update-group guard", ex);
            }
        }
    }
}
