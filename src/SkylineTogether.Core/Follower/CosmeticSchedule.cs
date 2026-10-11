using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SkylineTogether.Core.Hashing;

namespace SkylineTogether.Core.Follower
{
    /// <summary>
    /// When the follower clock's cosmetic runner updates each cosmetic system (ARCH §6.1): the interval and offset
    /// arithmetic of <c>UpdateSystem.Update(phase, updateIndex, iterationIndex)</c> (<c>UpdateSystem.cs:223-269</c>).
    /// <c>SimulationSystem</c> increments <c>frameIndex</c> and then passes it as <c>updateIndex</c>
    /// (<c>SimulationSystem.cs:277-287</c>); a system runs for that tick when <c>(frameIndex &amp; (interval - 1)) == offset</c>
    /// (<c>UpdateSystem.cs:241</c>), with <c>interval = GetUpdateInterval(phase)</c> (a power of two, else
    /// <c>UpdateSystem.GetInterval</c> throws, <c>:294-307</c>) and <c>offset = GetUpdateOffset(phase)</c>
    /// (<c>GameSystemBase.cs:131-139</c>, default -1), and <c>ResetDependency()</c> is called before the update when
    /// <c>iterationIndex &gt;= interval</c> (<c>m_ResetInterval</c>, <c>UpdateSystem.cs:36, 247-250</c>).
    /// <para>
    /// A negative offset means "assign one": an interval of 1 gets offset 0 (<c>UpdateSystem.cs:443-448</c>), which
    /// <see cref="AutoOffset"/> reproduces, and <c>UpdateSystem.Refresh</c> spreads the others over their interval by
    /// registration order (<c>UpdateSystem.cs:309-380</c>), which a client cannot reproduce for a system it runs outside
    /// <c>UpdateSystem</c>. The schedule uses a stable hash of the type name instead (<see cref="AutoOffset"/>, ARCH §6.1),
    /// identical on every peer and every run. No vanilla cosmetic system overrides <c>GetUpdateOffset</c>; the phase within
    /// the interval differs from the host's only visually (snow and wetness visuals update at another frame of the same
    /// window). A caller that reads the effective offset from <c>UpdateSystem</c> (probe <c>P-SYSTEMS</c>) can pass it
    /// explicitly to reproduce the host's phase exactly.
    /// </para>
    /// Main thread only (the cosmetic runner in <c>ClientClockSystem</c>).
    /// </summary>
    public sealed class CosmeticSchedule
    {
        private struct Slot
        {
            public int Interval;
            public int Offset;
            public uint Mask;
        }

        private static readonly Encoding Utf8 = new UTF8Encoding(false, false);

        private readonly Dictionary<string, Slot> _slots = new Dictionary<string, Slot>(StringComparer.Ordinal);
        private readonly List<string> _order = new List<string>();

        /// <summary>
        /// Schedules <paramref name="systemTypeName"/>; adding a name again replaces its schedule.
        /// </summary>
        /// <param name="systemTypeName">Full type name of the cosmetic system.</param>
        /// <param name="interval">The system's <c>GetUpdateInterval(GameSimulation)</c>: a power of two from 1 to 2^30.</param>
        /// <param name="offset">The system's <c>GetUpdateOffset(GameSimulation)</c>: 0 to <paramref name="interval"/> - 1, or
        /// negative (-1 by default) for an offset derived from the type name (<see cref="AutoOffset"/>).</param>
        /// <exception cref="ArgumentException">Empty name.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Interval not a positive power of two, or an offset at or above the
        /// interval (vanilla would never run such a system).</exception>
        public void Add(string systemTypeName, int interval, int offset)
        {
            if (string.IsNullOrEmpty(systemTypeName)) throw new ArgumentException("a system type name is required", nameof(systemTypeName));
            if (interval < 1 || (interval & (interval - 1)) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(interval), interval,
                                                      systemTypeName + ": the update interval must be a positive power of two (UpdateSystem rejects others)");
            }
            if (offset >= interval)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), offset,
                                                      systemTypeName + ": the update offset must be below the interval " + Num(interval)
                                                      + " (UpdateSystem would never run it)");
            }
            int effective = offset < 0 ? AutoOffset(systemTypeName, interval) : offset;
            var slot = new Slot { Interval = interval, Offset = effective, Mask = (uint)(interval - 1) };
            if (!_slots.ContainsKey(systemTypeName)) _order.Add(systemTypeName);
            _slots[systemTypeName] = slot;
        }

        /// <summary>
        /// True when vanilla would update the system for tick <paramref name="frameIndex"/> (the incremented
        /// <c>SimulationSystem.frameIndex</c> of that tick); false for a name that was never added.
        /// </summary>
        public bool ShouldRun(string systemTypeName, uint frameIndex)
        {
            if (systemTypeName == null || !_slots.TryGetValue(systemTypeName, out Slot slot)) return false;
            return (frameIndex & slot.Mask) == (uint)slot.Offset;
        }

        /// <summary>
        /// True when vanilla would call <c>GameSystemBase.ResetDependency()</c> before updating the system in tick
        /// <paramref name="iteration"/> of a frame (0-based): <c>iteration &gt;= interval</c>, i.e. the system already ran
        /// in this frame (<c>UpdateSystem.cs:247-250</c>). Applies to <c>GameSystemBase</c> systems only (others have
        /// <c>m_ResetInterval = int.MaxValue</c>, <c>UpdateSystem.cs:36</c>).
        /// </summary>
        public bool ShouldResetDependency(string systemTypeName, int iteration)
        {
            if (systemTypeName == null || !_slots.TryGetValue(systemTypeName, out Slot slot)) return false;
            return iteration >= slot.Interval;
        }

        /// <summary>Removes a system (for example one the cosmetic write audit rejected). Returns false if it was not scheduled.</summary>
        public bool Remove(string systemTypeName)
        {
            if (systemTypeName == null || !_slots.Remove(systemTypeName)) return false;
            _order.Remove(systemTypeName);
            return true;
        }

        public bool Contains(string systemTypeName) => systemTypeName != null && _slots.ContainsKey(systemTypeName);

        public int Count => _slots.Count;

        /// <summary>The scheduled systems in the order they were first added.</summary>
        public IReadOnlyList<string> SystemTypeNames => _order.AsReadOnly();

        /// <summary>The interval and the effective offset (after <see cref="AutoOffset"/>) of a scheduled system.</summary>
        public bool TryGetSchedule(string systemTypeName, out int interval, out int offset)
        {
            if (systemTypeName != null && _slots.TryGetValue(systemTypeName, out Slot slot))
            {
                interval = slot.Interval;
                offset = slot.Offset;
                return true;
            }
            interval = 0;
            offset = 0;
            return false;
        }

        /// <summary>
        /// The offset used for a negative <c>GetUpdateOffset</c>:
        /// <c>HashMix.Fold32(XxHash64(UTF-8 bytes of the type name, seed 0)) &amp; (interval - 1)</c>, the same stable string
        /// hash as the system-state ids (<see cref="SystemStateEntryModel.StableId"/>).
        /// </summary>
        public static int AutoOffset(string systemTypeName, int interval)
        {
            if (systemTypeName == null) throw new ArgumentNullException(nameof(systemTypeName));
            if (interval < 1 || (interval & (interval - 1)) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(interval), interval, "the update interval must be a positive power of two");
            }
            byte[] utf8 = Utf8.GetBytes(systemTypeName);
            uint hash = HashMix.Fold32(XxHash64.Hash(utf8, 0, utf8.Length));
            return (int)(hash & (uint)(interval - 1));
        }

        private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
