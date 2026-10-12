using System;
using Game;
using SkylineTogether.Diagnostics;

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
                sync.BindCreated();
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
}
