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
    /// the reference clean-up of every household, citizen, pet, company and building deleted on this client this frame
    /// that the applier did not already clean up after: the player's own bulldozing, the snapshot agents without a host id,
    /// a mod tool's deletions (<see cref="DeletionCascade"/>, ARCHITECTURE.md#reference-safety). It runs before the game
    /// destroys them at the end of the frame, so nothing keeps naming them.
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
                foreach (Entity e in entities)
                {
                    if (DeletionCascade.DoneThisFrame.Contains(e)) continue;
                    DeletionCascade.Apply(em, e, null);
                    DeletionCascade.LocalDeletions++;
                }
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
    }
}
