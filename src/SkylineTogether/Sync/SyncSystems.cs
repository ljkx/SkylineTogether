using System;
using Game;
using SkylineTogether.Core;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// The single apply point (R11 §6.2): MainLoop, right after <c>EndFrameBarrier</c> playback and before the
    /// raycast, the tools and the Modification phases, so deletions reach <c>OriginalDeletedSystem</c> and
    /// <c>SubElementDeleteSystem</c> and every derived system sees the tags.
    /// </summary>
    public sealed class SyncApplySystem : GameSystemBase
    {
        protected override void OnUpdate()
        {
            try
            {
                Mod.Sync?.ApplyIncoming();
            }
            catch (Exception ex)
            {
                Mod.Log?.Error(LogCategory.Replication, "apply failed", ex);
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
            try
            {
                Mod.Sync?.CaptureOutgoing(UnityEngine.Time.realtimeSinceStartupAsDouble);
            }
            catch (Exception ex)
            {
                Mod.Log?.Error(LogCategory.Replication, "capture failed", ex);
            }
        }
    }

    /// <summary>First in the ApplyTool phase, which only runs when the active tool applies: records the preview's
    /// originals and cost before the vanilla apply systems consume them.</summary>
    public sealed class SyncToolApplySystem : GameSystemBase
    {
        protected override void OnUpdate()
        {
            try
            {
                Mod.Sync?.OnToolApply();
            }
            catch (Exception ex)
            {
                Mod.Log?.Error(LogCategory.Replication, "tool hook failed", ex);
            }
        }
    }
}
