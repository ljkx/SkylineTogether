using System;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// w9/perf (robustness): the largest net id a client accepts from its host. The client's id tables are indexed by
    /// net id, so a single record with a huge id (a corrupted or hostile host) would grow them to the full 24-bit range
    /// (about 150 MB). The host announces its id frontier (<see cref="NetIdAllocator.Next"/>): in the join snapshot,
    /// with every id grant (a grant lies at the frontier) and in an <see cref="SyncRecordKind.IdCeiling"/> record
    /// whenever it moved (at most four times per second, ahead of the entity records of its batch). Ids up to the
    /// frontier plus <see cref="Slack"/> (room for what the host allocates between two announcements) are accepted;
    /// anything beyond is refused and counted. The ceiling only rises within a session. Not thread-safe.
    /// </summary>
    public sealed class NetIdCeiling
    {
        /// <summary>Ids accepted above the announced frontier (one million: far more than a host creates in a quarter second).</summary>
        public const uint Slack = 1u << 20;

        /// <summary>The ceiling before anything was announced (a client always accepts this many ids).</summary>
        public const uint Minimum = 1u << 20;

        private uint _announced;
        private long _refused;
        private uint _highestRefused;

        /// <summary>The highest frontier the host announced (0 before the first announcement).</summary>
        public uint Announced => _announced;

        /// <summary>The largest accepted id: min(<see cref="NetIdAllocator.MaxId"/>, max(<see cref="Minimum"/>, announced + <see cref="Slack"/>)).</summary>
        public uint Allowed
        {
            get
            {
                ulong ceiling = Math.Max((ulong)Minimum, (ulong)_announced + Slack);
                return (uint)Math.Min(ceiling, NetIdAllocator.MaxId);
            }
        }

        /// <summary>Ids refused so far (diagnostics).</summary>
        public long Refused => _refused;

        /// <summary>The largest id refused so far (diagnostics).</summary>
        public uint HighestRefused => _highestRefused;

        /// <summary>The host's frontier (or the end of a grant) is <paramref name="frontier"/>; the ceiling never falls.</summary>
        public void Announce(uint frontier)
        {
            if (frontier > _announced) _announced = Math.Min(frontier, NetIdAllocator.MaxId);
        }

        /// <summary>Announces the end of a granted range (start + count, overflow-safe).</summary>
        public void AnnounceRange(uint start, uint count) => Announce((uint)Math.Min((ulong)start + count, NetIdAllocator.MaxId));

        /// <summary>True when <paramref name="id"/> may be bound; a refused id is counted (true for the first refusal: log it).</summary>
        public bool Allows(uint id, out bool firstRefusal)
        {
            firstRefusal = false;
            if (id != 0 && id <= Allowed) return true;
            firstRefusal = _refused == 0;
            _refused++;
            if (id > _highestRefused) _highestRefused = id;
            return false;
        }

        /// <summary>The size an id table grows to for <paramref name="index"/>: doubling, at least index + 1, at most <paramref name="limit"/>.</summary>
        public static int GrowTo(int current, int index, int limit)
        {
            if (index < current) return current;
            long doubled = Math.Max(1L, (long)current) * 2;
            long size = Math.Max(index + 1L, Math.Min(doubled, limit));
            return (int)Math.Min(size, Math.Max(limit, index + 1L));
        }

        public void Reset()
        {
            _announced = 0;
            _refused = 0;
            _highestRefused = 0;
        }

        public string Describe() => "id ceiling " + Allowed + " (announced " + _announced + ")" + (_refused > 0 ? ", " + _refused + " ids refused (highest " + _highestRefused + ")" : "");
    }
}
