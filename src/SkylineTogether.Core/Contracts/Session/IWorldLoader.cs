using System;
using System.Collections.Generic;
using System.IO;

namespace SkylineTogether.Core
{
    /// <summary>The client's snapshot loader (game-join <c>SnapshotLoader</c>, ARCH §7.1 steps 7-11).</summary>
    public interface IWorldLoader
    {
        bool IsLoading { get; }

        /// <summary>The transient <c>SaveGameData</c> write stream that core-session's <c>BlobReceiver</c> writes the chunks into.</summary>
        [ThreadAffinity(ThreadKind.Main)]
        Stream OpenBlobTarget(SnapshotManifest manifest);

        /// <summary>Loads the opened target with the load overrides and the pushed settings; <paramref name="onDone"/> runs
        /// on the main thread after the post-load checks.</summary>
        [ThreadAffinity(ThreadKind.Main)]
        void BeginLoad(SnapshotManifest manifest, IReadOnlyList<SettingEntry> pushedSettings, Action<LoadOutcome> onDone);
    }
}
