using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Sync
{
    /// <summary>The utility network a flow-status record belongs to (ARCH §4.11.5).</summary>
    public enum FlowNetwork : byte
    {
        Electricity = 1,
        Water = 2,
    }

    /// <summary>
    /// What a flow node is to its owner (ARCH §3.7 "Flow edge" derived key). Flow nodes have no identity of their own: the
    /// graph systems hang them off net nodes and edges (<c>ElectricityNodeConnection</c>/<c>WaterPipeNodeConnection</c>),
    /// valve nodes off building sub-net nodes (<c>*ValveConnection</c>), building nodes off buildings
    /// (<c>*BuildingConnection</c>), plus the two global super nodes of each flow system. An owner has at most one node per
    /// role, so (owner net id, role) names a flow node on every peer.
    /// </summary>
    public enum FlowNodeRole : byte
    {
        None = 0,
        /// <summary><c>ElectricityNodeConnection.m_ElectricityNode</c> / <c>WaterPipeNodeConnection.m_WaterPipeNode</c> of a net node or edge.</summary>
        Net = 1,
        /// <summary><c>*ValveConnection.m_ValveNode</c> of a building's marker net node.</summary>
        Valve = 2,
        /// <summary><c>ElectricityBuildingConnection.m_TransformerNode</c>.</summary>
        Transformer = 3,
        /// <summary>End node of the building's producer edge.</summary>
        Producer = 4,
        /// <summary>Start node of the building's consumer edge.</summary>
        Consumer = 5,
        /// <summary>Start node of a battery's charge edge.</summary>
        Charge = 6,
        /// <summary>End node of a battery's discharge edge.</summary>
        Discharge = 7,
        /// <summary>The flow system's source super node (owner 0).</summary>
        Source = 8,
        /// <summary>The flow system's sink super node (owner 0).</summary>
        Sink = 9,
    }

    /// <summary>A flow node named portably: the owner's net id (0 for the super nodes) and the role.</summary>
    public readonly struct FlowNodeKey : IEquatable<FlowNodeKey>
    {
        public readonly uint Owner;
        public readonly FlowNodeRole Role;

        public FlowNodeKey(uint owner, FlowNodeRole role)
        {
            Owner = owner;
            Role = role;
        }

        /// <summary>A key both peers can resolve: a bound owner, or a super node.</summary>
        public bool IsValid => Role == FlowNodeRole.Source || Role == FlowNodeRole.Sink
            ? Owner == 0
            : Owner != 0 && Role > FlowNodeRole.None && Role <= FlowNodeRole.Discharge;

        public bool Equals(FlowNodeKey other) => Owner == other.Owner && Role == other.Role;

        public override bool Equals(object obj) => obj is FlowNodeKey k && Equals(k);

        public override int GetHashCode() => unchecked((int)(Owner * 16u) ^ (int)Role);

        public override string ToString() => Role + "(" + Owner + ")";
    }

    /// <summary>A flow edge named portably: its start and end flow node (direction matters).</summary>
    public readonly struct FlowKey : IEquatable<FlowKey>
    {
        public readonly FlowNodeKey Start;
        public readonly FlowNodeKey End;

        public FlowKey(FlowNodeKey start, FlowNodeKey end)
        {
            Start = start;
            End = end;
        }

        public bool IsValid => Start.IsValid && End.IsValid;

        /// <summary>The owners a view test looks at (0 for a super node).</summary>
        public uint StartOwner => Start.Owner;

        public uint EndOwner => End.Owner;

        public bool Equals(FlowKey other) => Start.Equals(other.Start) && End.Equals(other.End);

        public override bool Equals(object obj) => obj is FlowKey k && Equals(k);

        public override int GetHashCode()
        {
            unchecked
            {
                uint h = Start.Owner * 0x9E3779B1u;
                h = (h ^ (uint)Start.Role) * 0x85EBCA77u;
                h = (h ^ End.Owner) * 0xC2B2AE3Du;
                return (int)(h ^ ((uint)End.Role << 24) ^ (h >> 15));
            }
        }

        public override string ToString() => Start + "->" + End;
    }

    /// <summary>
    /// The flow status of one flow edge. Electricity: <see cref="Flow"/>, <see cref="Capacity"/> and
    /// <c>ElectricityFlowEdgeFlags</c> (direction, Bottleneck, BeyondBottleneck, Disconnected). Water: fresh flow and
    /// capacity in <see cref="Flow"/>/<see cref="Capacity"/>, the sewage pair, fresh pollution and <c>WaterPipeEdgeFlags</c>.
    /// </summary>
    public struct FlowValue : IEquatable<FlowValue>
    {
        public int Flow;
        public int Capacity;
        public int SewageFlow;
        public int SewageCapacity;
        public float Pollution;
        public byte Flags;

        /// <summary>Flows are resent when they move by more than 1/<see cref="FlowSteps"/> of their magnitude.</summary>
        public const int FlowSteps = 64;

        public bool Equals(FlowValue other) => Flow == other.Flow && Capacity == other.Capacity && SewageFlow == other.SewageFlow
                                              && SewageCapacity == other.SewageCapacity && Pollution.Equals(other.Pollution) && Flags == other.Flags;

        public override bool Equals(object obj) => obj is FlowValue v && Equals(v);

        public override int GetHashCode() => unchecked(Flow * 31 + Capacity * 17 + SewageFlow * 13 + SewageCapacity * 7 + Pollution.GetHashCode() * 3 + Flags);

        public override string ToString() => "flow " + Flow + "/" + Capacity + (SewageCapacity != 0 || SewageFlow != 0 ? ", sewage " + SewageFlow + "/" + SewageCapacity : "")
                                             + (Pollution != 0f ? ", pollution " + Pollution : "") + ", flags " + Flags;

        /// <summary>
        /// True when <paramref name="now"/> differs from what the peer was sent in a way an infoview shows: flags and
        /// capacities exactly; flows when they start, stop, reverse or move by more than 1/<see cref="FlowSteps"/> of
        /// their magnitude (the colour is flow / capacity, so the shown ratio is off by less than that); pollution
        /// likewise. Small fluctuations of a busy network therefore do not resend every edge every second.
        /// </summary>
        public static bool Significant(in FlowValue sent, in FlowValue now)
        {
            if (sent.Flags != now.Flags || sent.Capacity != now.Capacity || sent.SewageCapacity != now.SewageCapacity) return true;
            if (FlowMoved(sent.Flow, now.Flow) || FlowMoved(sent.SewageFlow, now.SewageFlow)) return true;
            if (sent.Pollution.Equals(now.Pollution)) return false;
            if (sent.Pollution == 0f || now.Pollution == 0f || float.IsNaN(sent.Pollution) || float.IsNaN(now.Pollution)) return true;
            float scale = Math.Max(Math.Abs(sent.Pollution), Math.Abs(now.Pollution));
            return Math.Abs(sent.Pollution - now.Pollution) > Math.Max(1e-5f, scale / FlowSteps);
        }

        private static bool FlowMoved(int a, int b)
        {
            if (a == b) return false;
            if (a == 0 || b == 0 || (a < 0) != (b < 0)) return true;
            long scale = Math.Max(Math.Abs((long)a), Math.Abs((long)b));
            return Math.Abs((long)a - b) > Math.Max(1L, scale / FlowSteps);
        }
    }

    /// <summary>
    /// One pass over a flow graph: the value of every keyed flow edge. A key seen twice (two edges between the same two
    /// flow nodes in the same direction) is ambiguous on every peer and left out.
    /// </summary>
    public sealed class FlowStatusTable
    {
        private readonly Dictionary<FlowKey, FlowValue> _values = new Dictionary<FlowKey, FlowValue>();
        private readonly HashSet<FlowKey> _ambiguous = new HashSet<FlowKey>();

        public int Count => _values.Count;

        public int AmbiguousCount => _ambiguous.Count;

        public IReadOnlyDictionary<FlowKey, FlowValue> Values => _values;

        public void Clear()
        {
            _values.Clear();
            _ambiguous.Clear();
        }

        /// <summary>Adds one edge; false when the key is invalid or ambiguous (then it is dropped).</summary>
        public bool Add(FlowKey key, FlowValue value)
        {
            if (!key.IsValid || _ambiguous.Contains(key)) return false;
            if (_values.ContainsKey(key))
            {
                _values.Remove(key);
                _ambiguous.Add(key);
                return false;
            }
            _values[key] = value;
            return true;
        }

        public bool IsAmbiguous(FlowKey key) => _ambiguous.Contains(key);

        public bool TryGet(FlowKey key, out FlowValue value) => _values.TryGetValue(key, out value);
    }

    /// <summary>
    /// Host: the flow-status channel of one peer for one network (ARCH §4.11.5). While the peer has the network's topic
    /// active, every finished pass is compared with what the peer was sent; activation clears that memory, so the first
    /// pass after it is a keyframe (the peer's derived edges may have been rebuilt while it was not watching).
    /// </summary>
    public sealed class FlowStatusChannel
    {
        private readonly Dictionary<FlowKey, FlowValue> _sent = new Dictionary<FlowKey, FlowValue>();
        private readonly List<FlowKey> _gone = new List<FlowKey>();
        private readonly List<FlowKey> _later = new List<FlowKey>();

        public bool Active { get; private set; }

        /// <summary>True from activation until a pass left nothing to send.</summary>
        public bool KeyframePending { get; private set; }

        public int SentCount => _sent.Count;

        /// <summary>Sets the topic state; true when the channel was just activated (keyframe).</summary>
        public bool SetActive(bool active)
        {
            if (active == Active) return false;
            Active = active;
            _sent.Clear();
            KeyframePending = active;
            return active;
        }

        /// <summary>
        /// The keys whose values must go to the peer: missing from what it was sent, or changed significantly
        /// (<see cref="FlowValue.Significant"/>). Edges <paramref name="inView"/> come first. Keys that left the table are
        /// forgotten (their edges are gone on the peer as well).
        /// </summary>
        public void Plan(FlowStatusTable table, Func<FlowKey, bool> inView, List<FlowKey> output)
        {
            if (table == null) throw new ArgumentNullException(nameof(table));
            if (output == null) throw new ArgumentNullException(nameof(output));
            output.Clear();
            if (!Active) return;
            _gone.Clear();
            foreach (FlowKey k in _sent.Keys)
            {
                if (!table.TryGet(k, out _)) _gone.Add(k);
            }
            foreach (FlowKey k in _gone) _sent.Remove(k);
            _later.Clear();
            foreach (KeyValuePair<FlowKey, FlowValue> kv in table.Values)
            {
                if (_sent.TryGetValue(kv.Key, out FlowValue sent) && !FlowValue.Significant(sent, kv.Value)) continue;
                if (inView == null || inView(kv.Key)) output.Add(kv.Key);
                else _later.Add(kv.Key);
            }
            output.AddRange(_later);
            _later.Clear();
            if (output.Count == 0) KeyframePending = false;
        }

        /// <summary>Records that <paramref name="value"/> went to the peer.</summary>
        public void MarkSent(FlowKey key, FlowValue value) => _sent[key] = value;

        public bool TryGetSent(FlowKey key, out FlowValue value) => _sent.TryGetValue(key, out value);
    }

    /// <summary>
    /// Body of <see cref="MessageKind.FlowStatus"/> (host → client):
    /// <code>
    /// u8 version | u8 network | u8 flags (1 keyframe) | u32 n | n × entry
    /// entry: varint startOwner | varint endOwner | u8 roles (start &lt;&lt; 4 | end) | zigzag flow | zigzag capacity | u8 flags
    ///        [water: zigzag sewageFlow | zigzag sewageCapacity | f32 pollution]
    /// </code>
    /// </summary>
    public static class FlowStatusMessage
    {
        public const byte Version = 1;
        public const byte FlagKeyframe = 1;

        /// <summary>Entries in one message at most.</summary>
        public const int MaxEntries = 8192;

        /// <summary>Upper bound of one entry's bytes (budget planning).</summary>
        public const int MaxEntryBytes = 5 + 5 + 1 + 5 + 5 + 1 + 5 + 5 + 4;

        /// <summary>Writes the header; returns the position of the entry count for <see cref="End"/>.</summary>
        public static int Begin(ByteWriter w, FlowNetwork network, byte flags)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            if (network != FlowNetwork.Electricity && network != FlowNetwork.Water) throw new ArgumentOutOfRangeException(nameof(network));
            w.WriteByte(Version);
            w.WriteByte((byte)network);
            w.WriteByte(flags);
            int position = w.Length;
            w.WriteUInt32(0);
            return position;
        }

        public static void WriteEntry(ByteWriter w, FlowNetwork network, FlowKey key, in FlowValue value)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            if (!key.IsValid) throw new ArgumentException("invalid flow key " + key, nameof(key));
            w.WriteVarUInt32(key.Start.Owner);
            w.WriteVarUInt32(key.End.Owner);
            w.WriteByte((byte)(((byte)key.Start.Role << 4) | (byte)key.End.Role));
            w.WriteVarInt32(value.Flow);
            w.WriteVarInt32(value.Capacity);
            w.WriteByte(value.Flags);
            if (network == FlowNetwork.Water)
            {
                w.WriteVarInt32(value.SewageFlow);
                w.WriteVarInt32(value.SewageCapacity);
                w.WriteSingle(value.Pollution);
            }
        }

        public static void End(ByteWriter w, int countPosition, int count)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            if (count < 0 || count > MaxEntries) throw new ArgumentOutOfRangeException(nameof(count));
            w.PatchUInt32(countPosition, (uint)count);
        }

        /// <summary>Decodes a message into <paramref name="output"/>; throws <see cref="DecodeException"/> for malformed input.</summary>
        public static FlowNetwork Read(byte[] buffer, int offset, int count, List<KeyValuePair<FlowKey, FlowValue>> output, out byte flags)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            output.Clear();
            var r = new ByteReader(buffer, offset, count);
            byte version = r.ReadByte();
            if (version != Version) throw new DecodeException("flow status version " + version);
            byte network = r.ReadByte();
            if (network != (byte)FlowNetwork.Electricity && network != (byte)FlowNetwork.Water) throw new DecodeException("flow status network " + network);
            flags = r.ReadByte();
            uint n = r.ReadUInt32();
            if (n > MaxEntries || n * 6u > (uint)r.Remaining) throw new DecodeException("flow status with " + n + " entries");
            bool water = network == (byte)FlowNetwork.Water;
            for (uint i = 0; i < n; i++)
            {
                uint startOwner = r.ReadVarUInt32();
                uint endOwner = r.ReadVarUInt32();
                byte roles = r.ReadByte();
                var key = new FlowKey(new FlowNodeKey(startOwner, (FlowNodeRole)(roles >> 4)), new FlowNodeKey(endOwner, (FlowNodeRole)(roles & 15)));
                if (!key.IsValid) throw new DecodeException("invalid flow key " + key);
                var value = new FlowValue { Flow = r.ReadVarInt32(), Capacity = Math.Max(0, r.ReadVarInt32()), Flags = r.ReadByte() };
                if (water)
                {
                    value.SewageFlow = r.ReadVarInt32();
                    value.SewageCapacity = Math.Max(0, r.ReadVarInt32());
                    float pollution = r.ReadSingle();
                    // capacities are never negative and the pollution share is finite and not negative
                    value.Pollution = float.IsNaN(pollution) || float.IsInfinity(pollution) ? 0f : Math.Max(0f, pollution);
                }
                output.Add(new KeyValuePair<FlowKey, FlowValue>(key, value));
            }
            if (!r.IsAtEnd) throw new DecodeException("flow status has " + r.Remaining + " trailing bytes");
            return (FlowNetwork)network;
        }
    }
}
