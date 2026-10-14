using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// Stable ids of the global-state channels (ARCHITECTURE.md#global-state-and-panels): a 32-bit FNV-1a hash of the channel's string id, never a
    /// position in a list, so a peer with a different channel set can never route one channel's payload into another.
    /// </summary>
    public static class GlobalChannel
    {
        /// <summary>Tax rate table (<c>TaxSystem</c>), keyed by table index.</summary>
        public static readonly uint Tax = Id("Tax");
        /// <summary>Service budgets (<c>ServiceBudgetData</c>), keyed by the service prefab's net id.</summary>
        public static readonly uint Budget = Id("Budget");
        /// <summary>Service fees (City <c>ServiceFee</c> buffer), keyed by <c>PlayerResource</c>.</summary>
        public static readonly uint Fee = Id("Fee");
        /// <summary>Loan amount (City <c>Loan</c>), key 0.</summary>
        public static readonly uint Loan = Id("Loan");
        /// <summary>Policy changes (<c>Modify</c> events) on the City, districts, buildings or lines.</summary>
        public static readonly uint Policy = Id("Policy");
        /// <summary>Custom names (<c>NameSystem</c>), keyed by the named entity's net id.</summary>
        public static readonly uint Name = Id("Name");
        /// <summary>The city name (<c>CityConfigurationSystem.cityName</c>), key 0.</summary>
        public static readonly uint CityName = Id("CityName");
        /// <summary>Semantic UI intents (<see cref="ActionIntent"/>), client → host only.</summary>
        public static readonly uint Action = Id("Action");
        /// <summary>The host's unlocked prefabs (<see cref="UnlockSet"/>), host → clients only.</summary>
        public static readonly uint Unlocks = Id("Unlocks");

        /// <summary>The channel id of a string id (FNV-1a, 32 bit).</summary>
        public static uint Id(string name)
        {
            uint h = 2166136261u;
            if (name == null) return h;
            foreach (char c in name)
            {
                h ^= (byte)c;
                h *= 16777619u;
                h ^= (byte)(c >> 8);
                h *= 16777619u;
            }
            return h;
        }
    }

    /// <summary>What a global-state payload describes.</summary>
    public enum GlobalPayloadMode : byte
    {
        /// <summary>The complete state of the channel (host → clients); the receiver converges to it.</summary>
        Full = 1,
        /// <summary>Only the entries a client changed (client → host proposal); the host sets each of them.</summary>
        Delta = 2,
    }

    /// <summary>One policy change: the target (City, district, building or line) and policy prefab as net ids.</summary>
    public struct PolicyChange : IEquatable<PolicyChange>
    {
        public uint Target;
        public uint Policy;
        /// <summary><c>PolicyFlags</c> (bit 0: active).</summary>
        public byte Flags;
        /// <summary>Slider value of slider policies.</summary>
        public float Adjustment;

        public bool Equals(PolicyChange o) => Target == o.Target && Policy == o.Policy && Flags == o.Flags && Adjustment.Equals(o.Adjustment);

        public override bool Equals(object obj) => obj is PolicyChange o && Equals(o);

        public override int GetHashCode() => unchecked((int)Target * 397 ^ (int)Policy * 31 ^ Flags ^ Adjustment.GetHashCode());

        public override string ToString() => "#" + Target + " policy #" + Policy + " flags " + Flags + " adj " + Adjustment;
    }

    /// <summary>
    /// Wire format of global-state payloads (the bytes of a <see cref="SyncRecordKind.Global"/> record): a mode byte, an
    /// entry count, then the entries. Keyed integers, floats and strings cover tax tables, budgets, fees, the loan and
    /// names; system blobs (demand, statistics) are carried raw and never pass through here.
    /// </summary>
    public static class GlobalPayload
    {
        public const int MaxEntries = 1 << 16;
        public const int MaxStringBytes = 1024;

        public static byte[] WriteInts(GlobalPayloadMode mode, IReadOnlyList<KeyValuePair<uint, int>> entries)
        {
            var w = Begin(mode, entries.Count);
            foreach (KeyValuePair<uint, int> e in entries)
            {
                w.WriteVarUInt32(e.Key);
                w.WriteVarInt32(e.Value);
            }
            return w.ToArray();
        }

        public static GlobalPayloadMode ReadInts(byte[] data, int offset, int count, List<KeyValuePair<uint, int>> into)
        {
            var r = new ByteReader(data, offset, count);
            GlobalPayloadMode mode = BeginRead(r, out int n);
            for (int i = 0; i < n; i++) into.Add(new KeyValuePair<uint, int>(r.ReadVarUInt32(), r.ReadVarInt32()));
            End(r);
            return mode;
        }

        public static byte[] WriteFloats(GlobalPayloadMode mode, IReadOnlyList<KeyValuePair<uint, float>> entries)
        {
            var w = Begin(mode, entries.Count);
            foreach (KeyValuePair<uint, float> e in entries)
            {
                w.WriteVarUInt32(e.Key);
                w.WriteSingle(e.Value);
            }
            return w.ToArray();
        }

        public static GlobalPayloadMode ReadFloats(byte[] data, int offset, int count, List<KeyValuePair<uint, float>> into)
        {
            var r = new ByteReader(data, offset, count);
            GlobalPayloadMode mode = BeginRead(r, out int n);
            for (int i = 0; i < n; i++) into.Add(new KeyValuePair<uint, float>(r.ReadVarUInt32(), r.ReadSingle()));
            End(r);
            return mode;
        }

        /// <summary>Strings longer than <see cref="MaxStringBytes"/> UTF-8 bytes are cut (names are short in practice).</summary>
        public static byte[] WriteStrings(GlobalPayloadMode mode, IReadOnlyList<KeyValuePair<uint, string>> entries)
        {
            var w = Begin(mode, entries.Count);
            foreach (KeyValuePair<uint, string> e in entries)
            {
                w.WriteVarUInt32(e.Key);
                w.WriteString(Clip(e.Value), MaxStringBytes);
            }
            return w.ToArray();
        }

        public static GlobalPayloadMode ReadStrings(byte[] data, int offset, int count, List<KeyValuePair<uint, string>> into)
        {
            var r = new ByteReader(data, offset, count);
            GlobalPayloadMode mode = BeginRead(r, out int n);
            for (int i = 0; i < n; i++) into.Add(new KeyValuePair<uint, string>(r.ReadVarUInt32(), r.ReadString(MaxStringBytes)));
            End(r);
            return mode;
        }

        public static byte[] WritePolicies(GlobalPayloadMode mode, IReadOnlyList<PolicyChange> entries)
        {
            var w = Begin(mode, entries.Count);
            foreach (PolicyChange p in entries)
            {
                w.WriteVarUInt32(p.Target);
                w.WriteVarUInt32(p.Policy);
                w.WriteByte(p.Flags);
                w.WriteSingle(p.Adjustment);
            }
            return w.ToArray();
        }

        public static GlobalPayloadMode ReadPolicies(byte[] data, int offset, int count, List<PolicyChange> into)
        {
            var r = new ByteReader(data, offset, count);
            GlobalPayloadMode mode = BeginRead(r, out int n);
            for (int i = 0; i < n; i++)
            {
                into.Add(new PolicyChange { Target = r.ReadVarUInt32(), Policy = r.ReadVarUInt32(), Flags = r.ReadByte(), Adjustment = r.ReadSingle() });
            }
            End(r);
            return mode;
        }

        private static ByteWriter Begin(GlobalPayloadMode mode, int count)
        {
            if (count > MaxEntries) throw new ArgumentException("too many entries (" + count + ")", nameof(count));
            var w = new ByteWriter(16 + count * 6);
            w.WriteByte((byte)mode);
            w.WriteVarUInt32((uint)count);
            return w;
        }

        private static GlobalPayloadMode BeginRead(ByteReader r, out int count)
        {
            var mode = (GlobalPayloadMode)r.ReadByte();
            if (mode != GlobalPayloadMode.Full && mode != GlobalPayloadMode.Delta) throw new DecodeException("unknown global payload mode " + (byte)mode);
            uint n = r.ReadVarUInt32();
            if (n > MaxEntries || n > (uint)r.Remaining) throw new DecodeException("global payload with " + n + " entries");
            count = (int)n;
            return mode;
        }

        private static void End(ByteReader r)
        {
            if (!r.IsAtEnd) throw new DecodeException("global payload has " + r.Remaining + " trailing bytes");
        }

        private static string Clip(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            // a UTF-16 char is at most 3 UTF-8 bytes (surrogate pairs: 4 bytes for 2 chars)
            int max = MaxStringBytes / 3;
            if (s.Length <= max) return s;
            int cut = max;
            if (char.IsHighSurrogate(s[cut - 1])) cut--;
            return s.Substring(0, cut);
        }
    }

    /// <summary>Change detection between two keyed snapshots (client: local edits against the last converged state).</summary>
    public static class KeyedDiff
    {
        /// <summary>
        /// Appends to <paramref name="changed"/> every entry of <paramref name="current"/> that is new or differs from
        /// <paramref name="baseline"/>, and to <paramref name="removed"/> (when not null) every baseline key that is gone.
        /// Returns whether anything differs.
        /// </summary>
        public static bool Diff<TValue>(IDictionary<uint, TValue> baseline, IDictionary<uint, TValue> current, List<KeyValuePair<uint, TValue>> changed, List<uint> removed, IEqualityComparer<TValue> comparer = null)
        {
            comparer = comparer ?? EqualityComparer<TValue>.Default;
            bool any = false;
            foreach (KeyValuePair<uint, TValue> e in current)
            {
                if (baseline.TryGetValue(e.Key, out TValue old) && comparer.Equals(old, e.Value)) continue;
                changed.Add(e);
                any = true;
            }
            if (removed != null)
            {
                foreach (KeyValuePair<uint, TValue> e in baseline)
                {
                    if (current.ContainsKey(e.Key)) continue;
                    removed.Add(e.Key);
                    any = true;
                }
            }
            return any;
        }
    }

    /// <summary>
    /// Client side: after the local player changed a channel, host state for that channel is held back for a short
    /// time, so a host message that was sent before the host saw the change cannot flick the UI back. Only the newest
    /// held payload is kept and applied when the hold ends (the stream is ordered, so it is the most recent host state).
    /// </summary>
    public sealed class ChannelHold
    {
        private readonly double _holdSeconds;
        private readonly Dictionary<uint, double> _until = new Dictionary<uint, double>();
        private readonly Dictionary<uint, byte[]> _deferred = new Dictionary<uint, byte[]>();
        private readonly List<uint> _scratch = new List<uint>();

        public ChannelHold(double holdSeconds)
        {
            _holdSeconds = holdSeconds;
        }

        /// <summary>The local player changed <paramref name="channel"/> at <paramref name="now"/>.</summary>
        public void Hold(uint channel, double now)
        {
            _until[channel] = now + _holdSeconds;
        }

        public bool IsHeld(uint channel, double now) => _until.TryGetValue(channel, out double until) && now < until;

        /// <summary>True when the payload was kept for later (the channel is held); false means apply it now.</summary>
        public bool TryDefer(uint channel, byte[] payload, double now)
        {
            if (!IsHeld(channel, now)) return false;
            _deferred[channel] = payload;
            return true;
        }

        /// <summary>Ends expired holds; appends the payloads that were held back for them (to be applied now).</summary>
        public void TakeReleased(double now, List<KeyValuePair<uint, byte[]>> into)
        {
            if (_until.Count == 0) return;
            _scratch.Clear();
            foreach (KeyValuePair<uint, double> e in _until)
            {
                if (now >= e.Value) _scratch.Add(e.Key);
            }
            foreach (uint channel in _scratch)
            {
                _until.Remove(channel);
                if (_deferred.TryGetValue(channel, out byte[] payload))
                {
                    _deferred.Remove(channel);
                    into.Add(new KeyValuePair<uint, byte[]>(channel, payload));
                }
            }
        }

        public void Clear()
        {
            _until.Clear();
            _deferred.Clear();
        }
    }

    /// <summary>
    /// Host side: whether a channel's current state must go out. It goes out when its hash differs from the last one
    /// sent, when it was marked dirty (a proposal was applied, a peer joined), and at least every
    /// <c>keyframeSeconds</c> as a safety net.
    /// </summary>
    public sealed class ChannelSendGate
    {
        private readonly double _keyframeSeconds;
        private readonly Dictionary<uint, (ulong Hash, double Time)> _sent = new Dictionary<uint, (ulong, double)>();
        private readonly HashSet<uint> _dirty = new HashSet<uint>();
        private bool _allDirty;

        public ChannelSendGate(double keyframeSeconds)
        {
            _keyframeSeconds = keyframeSeconds;
        }

        public bool AnyDirty => _allDirty || _dirty.Count > 0;

        public void MarkDirty(uint channel) => _dirty.Add(channel);

        public void MarkAllDirty() => _allDirty = true;

        public bool IsDirty(uint channel) => _allDirty || _dirty.Contains(channel);

        /// <summary>Drops a dirty mark without sending (the channel has nothing to send).</summary>
        public void ClearDirty(uint channel) => _dirty.Remove(channel);

        /// <summary>Decides and, when true, records the hash as sent at <paramref name="now"/>.</summary>
        public bool ShouldSend(uint channel, ulong hash, double now)
        {
            bool send = IsDirty(channel) || !_sent.TryGetValue(channel, out var last) || last.Hash != hash || now - last.Time >= _keyframeSeconds;
            _dirty.Remove(channel);
            if (send) _sent[channel] = (hash, now);
            return send;
        }

        /// <summary>Solo testing: the hash of the channel's last sent state; false when it was never sent.</summary>
        public bool TryGetSent(uint channel, out ulong hash)
        {
            if (_sent.TryGetValue(channel, out var last))
            {
                hash = last.Hash;
                return true;
            }
            hash = 0;
            return false;
        }

        /// <summary>Called after every channel was evaluated once: an all-dirty mark has been served.</summary>
        public void EndPass() => _allDirty = false;

        public void Clear()
        {
            _sent.Clear();
            _dirty.Clear();
            _allDirty = false;
        }
    }
}
