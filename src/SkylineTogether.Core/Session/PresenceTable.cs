using System;
using System.Collections.Generic;
using SkylineTogether.Core.Protocol;

namespace SkylineTogether.Core.Session
{
    /// <summary>
    /// The latest presence of every remote player (ARCH §11.4), fed by <see cref="SessionHost.PresenceReceived"/> or
    /// <see cref="SessionClient.PresenceReceived"/>. Entries expire when a player stops sending (crash, alt-tab, packet
    /// loss) and are removed when the player leaves. Main thread only.
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public sealed class PresenceTable
    {
        /// <summary>One remote player's presence.</summary>
        public sealed class Entry
        {
            public PeerId Peer;
            public float X, Y, Z;
            public PresenceTool Tool;
            public bool CursorVisible;
            public PrefabRank Prefab = PrefabRank.None;
            public uint Color;
            public float BrushRadius;
            /// <summary>When the last update arrived (<see cref="IClock.Seconds"/>).</summary>
            public double ReceivedAt;
        }

        private readonly List<Entry> _entries = new List<Entry>();

        /// <summary>Seconds without an update after which an entry is dropped.</summary>
        public double StaleSeconds { get; set; } = 5.0;

        /// <summary>Changes whenever an entry is added, removed, or its tool or prefab changes (cursor moves do not count).</summary>
        public int Version { get; private set; }

        public IReadOnlyList<Entry> Entries => _entries;

        /// <summary>Stores a received presence; the local player's own peer is never stored.</summary>
        public void Update(PresenceMessage m, double now, PeerId local)
        {
            if (m == null || m.From == local) return;
            Entry e = Find(m.From);
            if (e == null)
            {
                e = new Entry { Peer = m.From };
                _entries.Add(e);
                _entries.Sort((a, b) => a.Peer.Value.CompareTo(b.Peer.Value));
                Version++;
            }
            PresenceTool tool = PresenceToolKind.Tool(m.ToolKind);
            bool visible = PresenceToolKind.CursorVisible(m.ToolKind);
            if (tool != e.Tool || !m.Prefab.Equals(e.Prefab) || visible != e.CursorVisible || m.Color != e.Color) Version++;
            e.X = m.CursorX;
            e.Y = m.CursorY;
            e.Z = m.CursorZ;
            e.Tool = tool;
            e.CursorVisible = visible;
            e.Prefab = m.Prefab;
            e.Color = m.Color;
            e.BrushRadius = m.BrushRadius;
            e.ReceivedAt = now;
        }

        public Entry Find(PeerId peer)
        {
            foreach (Entry e in _entries)
            {
                if (e.Peer == peer) return e;
            }
            return null;
        }

        public bool Remove(PeerId peer)
        {
            int i = _entries.FindIndex(e => e.Peer == peer);
            if (i < 0) return false;
            _entries.RemoveAt(i);
            Version++;
            return true;
        }

        /// <summary>Drops every entry whose peer is not in <paramref name="present"/> (players who left).</summary>
        public void RetainOnly(ICollection<PeerId> present)
        {
            if (present == null) throw new ArgumentNullException(nameof(present));
            if (_entries.RemoveAll(e => !present.Contains(e.Peer)) > 0) Version++;
        }

        /// <summary>Drops entries older than <see cref="StaleSeconds"/>.</summary>
        public void Prune(double now)
        {
            if (_entries.RemoveAll(e => now - e.ReceivedAt > StaleSeconds) > 0) Version++;
        }

        public void Clear()
        {
            if (_entries.Count == 0) return;
            _entries.Clear();
            Version++;
        }
    }
}
