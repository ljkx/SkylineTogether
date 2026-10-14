using System;
using System.Collections.Generic;
using SkylineTogether.Core.Hashing;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// The forward references of one agent whose owners keep a data-replicated reverse buffer (ARCHITECTURE.md#agents), as net ids (0 = none):
    /// <list type="bullet">
    /// <item><c>HouseholdMember.m_Household</c> / <c>HouseholdPet.m_Household</c> → <c>HouseholdCitizen</c> / <c>HouseholdAnimal</c>;</item>
    /// <item><c>PropertyRenter.m_Property</c> → <c>Renter</c>;</item>
    /// <item><c>Game.Citizens.Student.m_School</c> → <c>Game.Buildings.Student</c>;</item>
    /// <item><c>Worker.m_Workplace</c> → <c>Employee</c>;</item>
    /// <item><c>CurrentBuilding.m_CurrentBuilding</c> → <c>Occupant</c>/<c>Patient</c> (hospitals, prisons, shelters).</item>
    /// </list>
    /// </summary>
    public struct AgentRefs : IEquatable<AgentRefs>
    {
        public uint Household;
        public uint Property;
        public uint School;
        public uint Workplace;
        public uint Building;

        public bool IsEmpty => Household == 0 && Property == 0 && School == 0 && Workplace == 0 && Building == 0;

        public bool Equals(AgentRefs o) => Household == o.Household && Property == o.Property && School == o.School && Workplace == o.Workplace && Building == o.Building;

        public override bool Equals(object obj) => obj is AgentRefs o && Equals(o);

        public override int GetHashCode() => unchecked((int)(Household * 31 + Property * 17 + School * 13 + Workplace * 7 + Building));

        /// <summary>
        /// The owners whose reverse buffers changed when an agent's references went from <paramref name="before"/> to
        /// <paramref name="after"/> (a spawn has an empty <paramref name="before"/>, a despawn an empty
        /// <paramref name="after"/>): both the old and the new owner of every reference that differs.
        /// </summary>
        public static void CollectOwners(in AgentRefs before, in AgentRefs after, ICollection<uint> owners)
        {
            if (owners == null) throw new ArgumentNullException(nameof(owners));
            Pair(before.Household, after.Household, owners);
            Pair(before.Property, after.Property, owners);
            Pair(before.School, after.School, owners);
            Pair(before.Workplace, after.Workplace, owners);
            Pair(before.Building, after.Building, owners);
        }

        private static void Pair(uint a, uint b, ICollection<uint> owners)
        {
            if (a == b) return;
            if (a != 0 && !owners.Contains(a)) owners.Add(a);
            if (b != 0 && !owners.Contains(b)) owners.Add(b);
        }

        /// <summary>
        /// The key links that promote a change to the P0 stream (ARCHITECTURE.md#agents: household membership and property changes are
        /// promoted): household and property. School, workplace and current building travel with the values.
        /// </summary>
        public ulong KeyHash() => HashMix.Combine(HashMix.Combine(0x6A09E667F3BCC909UL, Household), Property);

        public override string ToString() => "household " + Household + ", property " + Property + ", school " + School + ", workplace " + Workplace + ", building " + Building;
    }

    /// <summary>
    /// Client: per-agent limits on refresh requests (<see cref="AgentRefreshMessage"/>), so an agent whose
    /// reference never resolves cannot make the client ask forever: each id may be asked for <see cref="MaxPerWindow"/>
    /// times per <see cref="WindowSeconds"/>; requests are batched and sent at most every <see cref="IntervalSeconds"/>.
    /// Not thread-safe.
    /// </summary>
    public sealed class RefreshThrottle
    {
        public const int MaxPerWindow = 3;
        public const double WindowSeconds = 60;
        public const double IntervalSeconds = 1.0;

        private struct Entry
        {
            public double WindowStart;
            public int Count;
        }

        private readonly Dictionary<uint, Entry> _entries = new Dictionary<uint, Entry>();
        private readonly List<uint> _pending = new List<uint>();
        private readonly HashSet<uint> _pendingSet = new HashSet<uint>();
        private double _lastSent = double.NegativeInfinity;
        private long _refused;

        public int PendingCount => _pending.Count;

        /// <summary>Requests refused because an id reached its limit.</summary>
        public long Refused => _refused;

        /// <summary>Notes that <paramref name="id"/> should be asked for again; false when it reached its limit.</summary>
        public bool Note(uint id, double now)
        {
            if (id == 0) return false;
            if (_pendingSet.Contains(id)) return true;
            _entries.TryGetValue(id, out Entry e);
            if (e.Count == 0 || now - e.WindowStart >= WindowSeconds)
            {
                e.WindowStart = now;
                e.Count = 0;
            }
            if (e.Count >= MaxPerWindow)
            {
                _refused++;
                return false;
            }
            e.Count++;
            _entries[id] = e;
            _pendingSet.Add(id);
            _pending.Add(id);
            return true;
        }

        /// <summary>The ids to request now (at most <paramref name="max"/>); false when nothing is due.</summary>
        public bool TakeDue(double now, int max, List<uint> output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (_pending.Count == 0 || now - _lastSent < IntervalSeconds || max <= 0) return false;
            _lastSent = now;
            int n = Math.Min(max, _pending.Count);
            for (int i = 0; i < n; i++)
            {
                output.Add(_pending[i]);
                _pendingSet.Remove(_pending[i]);
            }
            _pending.RemoveRange(0, n);
            return true;
        }

        /// <summary>Drops the id's history (despawned: a reused id starts fresh).</summary>
        public void Forget(uint id)
        {
            _entries.Remove(id);
            if (_pendingSet.Remove(id)) _pending.Remove(id);
        }

        /// <summary>Drops entries whose window ended (bounded memory).</summary>
        public void Sweep(double now)
        {
            var old = new List<uint>();
            foreach (KeyValuePair<uint, Entry> pair in _entries)
            {
                if (now - pair.Value.WindowStart >= WindowSeconds && !_pendingSet.Contains(pair.Key)) old.Add(pair.Key);
            }
            foreach (uint id in old) _entries.Remove(id);
        }

        public int TrackedCount => _entries.Count;

        public void Clear()
        {
            _entries.Clear();
            _pending.Clear();
            _pendingSet.Clear();
            _lastSent = double.NegativeInfinity;
            _refused = 0;
        }
    }
}
