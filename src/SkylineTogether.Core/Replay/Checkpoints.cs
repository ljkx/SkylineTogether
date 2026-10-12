using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Integrity;
using SkylineTogether.Core.Sync;

namespace SkylineTogether.Core.Replay
{
    /// <summary>One owner's icons in a checkpoint: the owner (portable reference) and the digest of its icons.</summary>
    public struct IconOwnerDigest
    {
        public SyncRef Owner;
        public uint Icons;
        public ulong Sum;

        public IconOwnerDigest(SyncRef owner, uint icons, ulong sum)
        {
            Owner = owner;
            Icons = icons;
            Sum = sum;
        }
    }

    /// <summary>The hash of one global-state channel in a checkpoint.</summary>
    public struct GlobalChannelHash
    {
        public uint Channel;
        public string Name;
        public ulong Hash;
        /// <summary>A strict channel must match (taxes, budgets, names); others are reported as warnings (system blobs).</summary>
        public bool Strict;

        public GlobalChannelHash(uint channel, string name, ulong hash, bool strict)
        {
            Channel = channel;
            Name = name;
            Hash = hash;
            Strict = strict;
        }
    }

    /// <summary>
    /// What the host knew at the end of a verification checkpoint (solo testing, ARCH §13.5), carried by the
    /// <see cref="SyncRecordKind.ReplayCheckpoint"/> end record: the UI-level line rows (<c>LineCheck</c>), the icon
    /// digests per owner (<c>IconSync</c>), the hashes of the global-state channels as last sent, and counts. Pure codec.
    /// </summary>
    public sealed class CheckpointSummary
    {
        private const byte Format = 1;

        public uint Index;
        public double HostSeconds;
        public uint HostFrame;
        /// <summary>Buckets covered by the checkpoint's <see cref="SyncRecordKind.ReplayHashes"/> records.</summary>
        public uint Buckets;
        /// <summary>Entities hashed.</summary>
        public long Entities;
        /// <summary>Entities whose members were recorded (their hash changed since the previous checkpoint).</summary>
        public long MembersRecorded;
        public bool HasLines;
        public List<LineStateRow> Lines = new List<LineStateRow>();
        public bool HasIcons;
        public uint IconOwners;
        public uint IconCount;
        public ulong IconSum;
        public List<IconOwnerDigest> IconOwnerDigests = new List<IconOwnerDigest>();
        public List<GlobalChannelHash> Globals = new List<GlobalChannelHash>();
        public string Note = "";

        public byte[] ToBytes()
        {
            var w = new ByteWriter(4096);
            Write(w);
            return w.ToArray();
        }

        public void Write(ByteWriter w)
        {
            w.WriteByte(Format);
            w.WriteVarUInt32(Index);
            w.WriteDouble(HostSeconds);
            w.WriteUInt32(HostFrame);
            w.WriteVarUInt32(Buckets);
            w.WriteVarInt64(Entities);
            w.WriteVarInt64(MembersRecorded);
            w.WriteBool(HasLines);
            if (HasLines) LineStateReport.WriteRows(w, Lines);
            w.WriteBool(HasIcons);
            if (HasIcons)
            {
                w.WriteVarUInt32(IconOwners);
                w.WriteVarUInt32(IconCount);
                w.WriteUInt64(IconSum);
                w.WriteVarUInt32((uint)IconOwnerDigests.Count);
                foreach (IconOwnerDigest d in IconOwnerDigests)
                {
                    d.Owner.Write(w);
                    w.WriteVarUInt32(d.Icons);
                    w.WriteUInt64(d.Sum);
                }
            }
            w.WriteVarUInt32((uint)Globals.Count);
            foreach (GlobalChannelHash g in Globals)
            {
                w.WriteUInt32(g.Channel);
                w.WriteString(g.Name ?? "", 256);
                w.WriteUInt64(g.Hash);
                w.WriteBool(g.Strict);
            }
            w.WriteString(Note ?? "", RecordingFormat.MaxTextBytes);
        }

        public static CheckpointSummary Read(byte[] data, int offset, int count) => Read(new ByteReader(data, offset, count));

        /// <summary>Reads a summary; throws <see cref="DecodeException"/> for malformed or unknown data.</summary>
        public static CheckpointSummary Read(ByteReader r)
        {
            try
            {
                byte format = r.ReadByte();
                if (format != Format) throw new DecodeException("checkpoint summary format " + format);
                var s = new CheckpointSummary
                {
                    Index = r.ReadVarUInt32(),
                    HostSeconds = r.ReadDouble(),
                    HostFrame = r.ReadUInt32(),
                    Buckets = r.ReadVarUInt32(),
                    Entities = r.ReadVarInt64(),
                    MembersRecorded = r.ReadVarInt64(),
                };
                s.HasLines = r.ReadBool();
                if (s.HasLines) s.Lines = LineStateReport.ReadRows(r);
                s.HasIcons = r.ReadBool();
                if (s.HasIcons)
                {
                    s.IconOwners = r.ReadVarUInt32();
                    s.IconCount = r.ReadVarUInt32();
                    s.IconSum = r.ReadUInt64();
                    uint n = r.ReadVarUInt32();
                    if (n > (uint)r.Remaining) throw new DecodeException("checkpoint with " + n + " icon owners");
                    for (uint i = 0; i < n; i++)
                    {
                        SyncRef owner = SyncRef.Read(r);
                        s.IconOwnerDigests.Add(new IconOwnerDigest(owner, r.ReadVarUInt32(), r.ReadUInt64()));
                    }
                }
                uint g = r.ReadVarUInt32();
                if (g > (uint)r.Remaining) throw new DecodeException("checkpoint with " + g + " global channels");
                for (uint i = 0; i < g; i++) s.Globals.Add(new GlobalChannelHash(r.ReadUInt32(), r.ReadString(256), r.ReadUInt64(), r.ReadBool()));
                s.Note = r.ReadString(RecordingFormat.MaxTextBytes);
                return s;
            }
            catch (DecodeException)
            {
                throw;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is IndexOutOfRangeException)
            {
                throw new DecodeException("malformed checkpoint summary: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Host side of the checkpoints: remembers each entity's integrity hash as of the last checkpoint, so only entities
    /// whose hash changed (or that are new) get their members recorded again. Pure.
    /// </summary>
    public sealed class CheckpointMemberTracker
    {
        private readonly Dictionary<uint, Dictionary<uint, ulong>> _byBucket = new Dictionary<uint, Dictionary<uint, ulong>>();

        /// <summary>Entities remembered.</summary>
        public int Count { get; private set; }

        /// <summary>
        /// The bucket's current hashes: returns (in <paramref name="changed"/>) the entities whose members must be
        /// recorded, remembers the new hashes and forgets ids that are gone.
        /// </summary>
        public void Update(uint bucket, IReadOnlyList<EntityHash> current, List<EntityHash> changed)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            changed?.Clear();
            _byBucket.TryGetValue(bucket, out Dictionary<uint, ulong> known);
            var next = current.Count == 0 ? null : new Dictionary<uint, ulong>(current.Count);
            for (int i = 0; i < current.Count; i++)
            {
                EntityHash h = current[i];
                if (known == null || !known.TryGetValue(h.NetId, out ulong old) || old != h.Hash) changed?.Add(h);
                next[h.NetId] = h.Hash;
            }
            Count -= known?.Count ?? 0;
            if (next == null) _byBucket.Remove(bucket);
            else
            {
                _byBucket[bucket] = next;
                Count += next.Count;
            }
        }

        public void Clear()
        {
            _byBucket.Clear();
            Count = 0;
        }
    }

    /// <summary>
    /// Replay side: the newest recorded members of each entity together with the hash they belong to. When the host's
    /// hash at a later checkpoint is the same, those members are still the host's (the host records them again
    /// whenever the hash changes), so a divergence can be explained component by component. Pure.
    /// </summary>
    public sealed class MemberArchive
    {
        private readonly Dictionary<uint, (ulong Hash, Family Family, ComponentHash[] Members)> _entries = new Dictionary<uint, (ulong, Family, ComponentHash[])>();

        public int Count => _entries.Count;

        public void Add(uint netId, Family family, ulong hash, IReadOnlyList<ComponentHash> members)
        {
            var copy = new ComponentHash[members?.Count ?? 0];
            for (int i = 0; i < copy.Length; i++) copy[i] = members[i];
            _entries[netId] = (hash, family, copy);
        }

        /// <summary>The members recorded for <paramref name="netId"/> when its hash was <paramref name="hash"/>.</summary>
        public bool TryGet(uint netId, ulong hash, out Family family, out IReadOnlyList<ComponentHash> members)
        {
            if (_entries.TryGetValue(netId, out var e) && e.Hash == hash)
            {
                family = e.Family;
                members = e.Members;
                return true;
            }
            family = default;
            members = null;
            return false;
        }

        /// <summary>The family last recorded for <paramref name="netId"/> (whatever hash), if any.</summary>
        public bool TryGetFamily(uint netId, out Family family)
        {
            if (_entries.TryGetValue(netId, out var e))
            {
                family = e.Family;
                return true;
            }
            family = default;
            return false;
        }

        public void Clear() => _entries.Clear();
    }

    /// <summary>Pure comparisons of the replay verdict.</summary>
    public static class ReplayDiff
    {
        /// <summary>
        /// Names what differs between the host's and the local members of one entity's integrity hash, the same way the
        /// rolling verification explains a persistent mismatch: "Game.Net.Curve (value), Game.Objects.Elevation (only on
        /// the host)". Empty when the members are equal.
        /// </summary>
        public static string ExplainMembers(IReadOnlyList<ComponentHash> host, IReadOnlyList<ComponentHash> mine, Func<ulong, string> typeName)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            if (mine == null) throw new ArgumentNullException(nameof(mine));
            Func<ulong, string> name = typeName ?? (h => "type 0x" + h.ToString("X16", CultureInfo.InvariantCulture));
            var local = new Dictionary<ulong, ComponentHash>();
            foreach (ComponentHash m in mine) local[m.TypeHash] = m;
            var parts = new List<string>();
            foreach (ComponentHash h in host)
            {
                if (!local.TryGetValue(h.TypeHash, out ComponentHash c))
                {
                    parts.Add(name(h.TypeHash) + " (only on the host)");
                    continue;
                }
                local.Remove(h.TypeHash);
                if (c.State != h.State) parts.Add(name(h.TypeHash) + (h.State == ComponentHashState.Value ? " (value on the host, presence here)" : " (presence on the host, value here)"));
                else if (c.State == ComponentHashState.Value && c.ValueHash != h.ValueHash) parts.Add(name(h.TypeHash) + " (value)");
            }
            var extra = new List<string>();
            foreach (ComponentHash c in local.Values) extra.Add(name(c.TypeHash) + " (only here)");
            extra.Sort(StringComparer.Ordinal);
            parts.AddRange(extra);
            return string.Join(", ", parts);
        }

        /// <summary>
        /// The icon owners whose digests differ: present on one side only, or with another count or sum. Owners are
        /// keyed by <see cref="SyncRef.NormalizedHash"/>; at most <paramref name="max"/> lines.
        /// </summary>
        public static List<string> CompareIconOwners(IReadOnlyList<IconOwnerDigest> host, IReadOnlyList<IconOwnerDigest> mine, int max = 50)
        {
            var result = new List<string>();
            var local = new Dictionary<ulong, IconOwnerDigest>();
            foreach (IconOwnerDigest d in mine) local[d.Owner.NormalizedHash()] = d;
            int more = 0;
            foreach (IconOwnerDigest h in host)
            {
                ulong key = h.Owner.NormalizedHash();
                if (!local.TryGetValue(key, out IconOwnerDigest c))
                {
                    Add(result, ref more, max, Describe(h.Owner) + ": " + h.Icons + " icon(s) on the host, none here");
                    continue;
                }
                local.Remove(key);
                if (c.Icons != h.Icons || c.Sum != h.Sum) Add(result, ref more, max, Describe(h.Owner) + ": " + h.Icons + " icon(s) on the host, " + c.Icons + " here" + (c.Icons == h.Icons ? " (different kinds or targets)" : ""));
            }
            var extra = new List<IconOwnerDigest>(local.Values);
            extra.Sort((a, b) => string.CompareOrdinal(Describe(a.Owner), Describe(b.Owner)));
            foreach (IconOwnerDigest c in extra) Add(result, ref more, max, Describe(c.Owner) + ": none on the host, " + c.Icons + " icon(s) here");
            if (more > 0) result.Add("... and " + more + " more");
            return result;
        }

        /// <summary>Readable name of an icon owner or target.</summary>
        public static string Describe(SyncRef r)
        {
            switch (r.Kind)
            {
                case SyncRef.NetId: return "#" + r.Id.ToString(CultureInfo.InvariantCulture);
                case SyncRef.LaneKey: return "lane " + r.Index.ToString(CultureInfo.InvariantCulture) + " of #" + r.Id.ToString(CultureInfo.InvariantCulture);
                case SyncRef.SubObjectKey: return "sub-object " + r.Index.ToString(CultureInfo.InvariantCulture) + " of #" + r.Id.ToString(CultureInfo.InvariantCulture);
                case SyncRef.Unnamed: return "an unnamed object";
                default: return "nothing";
            }
        }

        /// <summary>The global channels whose hashes differ (or that one side lacks), strict ones first.</summary>
        public static List<(string Name, bool Strict, string Text)> CompareGlobals(IReadOnlyList<GlobalChannelHash> host, IReadOnlyList<GlobalChannelHash> mine)
        {
            var result = new List<(string, bool, string)>();
            var local = new Dictionary<uint, GlobalChannelHash>();
            foreach (GlobalChannelHash g in mine) local[g.Channel] = g;
            foreach (GlobalChannelHash h in host)
            {
                if (!local.TryGetValue(h.Channel, out GlobalChannelHash c)) result.Add((h.Name, h.Strict, h.Name + ": not readable here"));
                else if (c.Hash != h.Hash) result.Add((h.Name, h.Strict, h.Name + ": differs from the host"));
            }
            result.Sort((a, b) => a.Item2 != b.Item2 ? (a.Item2 ? -1 : 1) : string.CompareOrdinal(a.Item1, b.Item1));
            return result;
        }

        private static void Add(List<string> list, ref int more, int max, string line)
        {
            if (list.Count < max) list.Add(line);
            else more++;
        }

        internal static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

        internal static string Seconds(double s)
        {
            if (s < 0 || double.IsNaN(s)) return "-";
            if (s < 120) return s.ToString("0.0", CultureInfo.InvariantCulture) + " s";
            var sb = new StringBuilder();
            int total = (int)Math.Round(s);
            if (total >= 3600) sb.Append(total / 3600).Append(" h ");
            sb.Append(total / 60 % 60).Append(" min ").Append(total % 60).Append(" s");
            return sb.ToString();
        }
    }
}
