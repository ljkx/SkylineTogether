using System;

namespace SkylineTogether.Core
{
    /// <summary>Host snapshot production (game-join <c>SnapshotService</c>, ARCH §7.2).</summary>
    public interface ISnapshotProvider
    {
        /// <summary>Starts a snapshot on the save/load queue; exactly one of the callbacks is invoked later (main thread).</summary>
        [ThreadAffinity(ThreadKind.Main)]
        void RequestSnapshot(Action<SnapshotReady> onReady, Action<string> onFailed);

        /// <summary>No joiner needs the snapshot any more; its temp file may be deleted.</summary>
        void Release(uint snapshotId);
    }
}
