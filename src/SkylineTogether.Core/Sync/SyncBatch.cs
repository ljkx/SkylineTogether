using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Sync
{
    /// <summary>Kinds of records in a <see cref="SyncBatch"/>.</summary>
    public enum SyncRecordKind : byte
    {
        /// <summary>Create the entity (or reconcile it when the id is already bound) with the given components.</summary>
        Spawn = 1,
        /// <summary>Structural or authored change: write the components, add/remove presence, tag Updated.</summary>
        Upsert = 2,
        /// <summary>Delete the entity.</summary>
        Despawn = 3,
        /// <summary>Simulation values only: write the components without tagging (no local re-derivation).</summary>
        Values = 4,
        /// <summary>Binds a net id to a prefab by its PrefabID.</summary>
        Prefab = 5,
        /// <summary>Zone cells of a derived zone block, keyed by owner edge and block geometry.</summary>
        ZoneCells = 6,
        /// <summary>Host → client: a range of net ids the client may assign to entities it creates.</summary>
        IdRange = 7,
        /// <summary>Client → host: the money the local apply charged (the host charges it for real).</summary>
        Cost = 8,
        /// <summary>Host → client: the host's simulation frame (clock follow).</summary>
        Clock = 9,
        /// <summary>Client → host: a request (<see cref="SyncRequest"/>).</summary>
        Request = 10,
        /// <summary>
        /// Global state (<c>GlobalStateSync</c>): a channel id (<see cref="GlobalChannel"/>) and an opaque payload.
        /// Host → client: the channel's full state; client → host: the entries the client changed.
        /// </summary>
        Global = 11,
        /// <summary>
        /// w9/perf: host → client, the host's net id frontier (<see cref="SyncRecord.RangeStart"/>; <see cref="NetIdCeiling"/>),
        /// so a client refuses ids far beyond it instead of growing its id tables without bound.
        /// </summary>
        IdCeiling = 12,

        // ---- integrity and icon channels (feat/integrity; values 40-49 are reserved for them by merge coordination) ----
        /// <summary>Host → client: the integrity digest of one bucket of net ids (rolling verification).</summary>
        BucketDigest = 40,
        /// <summary>Host → client: the integrity hash of every bound entity of one bucket (answer to <see cref="SyncRequest.BucketDetail"/>).</summary>
        EntityHashes = 41,
        /// <summary>Client → host: a request with a list of ids (<see cref="SyncRequest"/> values 40-49).</summary>
        RequestIds = 42,
        /// <summary>Host → client: the members of one entity's integrity hash (answer to <see cref="SyncRequest.ExplainEntities"/>).</summary>
        EntityExplain = 43,
        /// <summary>Host → client: the complete default-layer icon set of one owner (empty: the owner has no icons).</summary>
        IconSet = 44,
        /// <summary>Host → client: icon digest, or the start or end of a full icon sync (<see cref="IconControlMode"/>).</summary>
        IconControl = 45,
        /// <summary>Host → client: the UI-level line rows (<c>LineStateReport.WriteRows</c>).</summary>
        LineRows = 46,

        // ---- movers branch (ARCH §4.9; kinds 20-29 are reserved for it): existence of vehicles and creatures ----

        /// <summary>Host → client: a mover appeared (<see cref="MoverRecord"/>).</summary>
        MoverSpawn = 20,
        /// <summary>Host → client: a mover's discrete state or pinned pose changed (<see cref="MoverRecord"/>).</summary>
        MoverState = 21,
        /// <summary>Host → client: a mover disappeared.</summary>
        MoverDespawn = 22,

        // ---- mod compatibility (docs/design/MOD-COMPATIBILITY.md; values 110-119 are reserved for it) ----

        /// <summary>
        /// The enabled state of an entity's enableable replicated components (<c>IEnableableComponent</c>, for example
        /// <c>Game.Objects.Decoration</c> or Tree Controller's <c>LumberResource</c>); written right after the entity's
        /// record in the same batch. Both directions.
        /// </summary>
        EnabledBits = 110,
        /// <summary>Host → client: the host's values of simulation-relevant mod settings (the host's rules for everyone).</summary>
        ModSettings = 111,
        /// <summary>
        /// Host → client, right before a <see cref="Prefab"/> record of a prefab that is not part of the base game: where it
        /// comes from (package and Paradox Mods id, local asset, or the mod that creates it at runtime), so a client that
        /// lacks it can say what to subscribe to.
        /// </summary>
        PrefabSource = 112,

        // ---- agents (kinds 120-129) ----

        /// <summary>
        /// Host → client, inside a movers batch right after the mover's record: the agents the mover refers to
        /// (<see cref="MoverAgentLinks"/>: the citizen of a pedestrian, the household pet of an animal, the keeper and owner
        /// of a vehicle).
        /// </summary>
        MoverAgents = 120,
        // ---- gameplay (kinds 80-89): proposal validation and results ----

        /// <summary>
        /// Host → client: the number of this structural broadcast (<see cref="AuthoredLedger"/>), the last record of every
        /// host epoch batch; a client states the newest number it applied as the base of its proposals (conflict check).
        /// </summary>
        Epoch = 80,
        /// <summary>Client → host: sequence number, base epoch and flags of the proposal in this batch (ARCH §5.4.1).</summary>
        ProposalHeader = 81,
        /// <summary>Host → client (originator only): the result of a proposal; rejected ones are rolled back by the resend that follows.</summary>
        ProposalResult = 82,

        // ---- solo testing (record/replay verdict, ARCH §13.5; kinds 160-169 are reserved for it) ----

        /// <summary>
        /// Host → the recorder peer only: the integrity hash of every bound entity of one bucket at a verification
        /// checkpoint (<see cref="SyncRecord.Seq"/> = checkpoint), ordered after every record it covers.
        /// </summary>
        ReplayHashes = 160,
        /// <summary>
        /// Host → the recorder peer only: the members of one entity's integrity hash and that hash, written when the hash
        /// changed since the previous checkpoint (a replay names the differing components with it).
        /// </summary>
        ReplayMembers = 161,
        /// <summary>
        /// Host → the recorder peer only: a verification checkpoint begins or ends (<see cref="ReplayCheckpointPhase"/>);
        /// the end carries the summary (<c>CheckpointSummary</c>: line rows, icon digests, global-state hashes).
        /// </summary>
        ReplayCheckpoint = 162,
    }

    /// <summary>Which end of a verification checkpoint a <see cref="SyncRecordKind.ReplayCheckpoint"/> marks (solo testing).</summary>
    public enum ReplayCheckpointPhase : byte
    {
        Begin = 1,
        End = 2,
    }

    /// <summary>What a proposal batch contains (<see cref="SyncRecordKind.ProposalHeader"/>).</summary>
    [Flags]
    public enum ProposalFlags : byte
    {
        None = 0,
        /// <summary>A vanilla tool apply (stage B/C capture).</summary>
        ToolApply = 1,
        /// <summary>Un-applied changes of a direct-write tool from another mod (Move It, Zoning Toolkit, ...).</summary>
        DirectTool = 2,
        /// <summary>Deletions made through the UI (district, service upgrade, line) outside any tool.</summary>
        UiDelete = 4,
    }

    /// <summary>What a client asks the host for.</summary>
    public enum SyncRequest : byte
    {
        /// <summary>Another range of net ids.</summary>
        MoreIds = 1,
        /// <summary>The full state of every line (after a local route-tool preview window).</summary>
        ResendRoutes = 2,
        /// <summary>The full state of every replicated entity (repair).</summary>
        ResendAll = 3,

        // ---- with a list of ids (SyncRecordKind.RequestIds; feat/integrity; values 40-49 reserved) ----
        /// <summary>The per-entity hashes of the listed buckets.</summary>
        BucketDetail = 40,
        /// <summary>Re-send the listed entities (as spawns; ids the host does not have are answered with a despawn).</summary>
        ResendEntities = 41,
        /// <summary>The members of the listed entities' integrity hashes (diagnostics of a persistent mismatch).</summary>
        ExplainEntities = 42,
        /// <summary>Re-send the listed lines with all their waypoints and segments.</summary>
        ResendLines = 43,
        /// <summary>Every icon set the host has sent (no ids).</summary>
        IconFullSync = 44,
    }

    /// <summary>How one component travels.</summary>
    public enum SyncComponentKind : byte
    {
        /// <summary>A component's data through its <c>ISerializable</c>.</summary>
        Data = 0,
        /// <summary>A dynamic buffer: element count, then each element through <c>ISerializable</c>.</summary>
        Buffer = 1,
        /// <summary>Presence only (tags and <c>IEmptySerializable</c> types).</summary>
        Presence = 2,
    }

    /// <summary>One component of an entity record.</summary>
    public struct SyncComponent
    {
        public ulong TypeHash;
        public SyncComponentKind Kind;
        /// <summary>Serialized bytes (empty for <see cref="SyncComponentKind.Presence"/>).</summary>
        public byte[] Data;
        public int Offset;
        public int Count;
    }

    /// <summary>The enabled state of one enableable component (<see cref="SyncRecordKind.EnabledBits"/>).</summary>
    public struct SyncEnabledBit
    {
        public ulong TypeHash;
        public bool Enabled;

        public SyncEnabledBit(ulong typeHash, bool enabled)
        {
            TypeHash = typeHash;
            Enabled = enabled;
        }
    }

    /// <summary>A decoded record; fields are used according to <see cref="Kind"/>.</summary>
    public sealed class SyncRecord
    {
        public SyncRecordKind Kind;
        public uint NetId;
        public Family Family;
        /// <summary>For <see cref="SyncRecordKind.Spawn"/> and <see cref="SyncRecordKind.Upsert"/>: the full replicated type set
        /// (components not listed are removed on the receiver when they are replicated types).</summary>
        public bool CompleteSet;
        public readonly List<SyncComponent> Components = new List<SyncComponent>();
        /// <summary>For <see cref="SyncRecordKind.Prefab"/>: a readable name (diagnostics) and the game-encoded PrefabID.</summary>
        public string PrefabName;
        public byte[] PrefabData;
        public ZoneBlockKey Block;
        public uint[] ZoneIds;
        public uint RangeStart, RangeCount;
        public long Amount;
        public uint Frame;
        public SyncRequest Request;
        /// <summary>For <see cref="SyncRecordKind.Global"/>: the channel id and a copy of its payload.</summary>
        public uint Channel;
        public byte[] Payload;

        // ---- integrity and icon channels (feat/integrity) ----
        /// <summary><see cref="SyncRecordKind.BucketDigest"/>, <see cref="SyncRecordKind.EntityHashes"/>: the bucket.</summary>
        public uint Bucket;
        /// <summary><see cref="SyncRecordKind.BucketDigest"/>: entities in the bucket; <see cref="SyncRecordKind.IconControl"/>: owners.</summary>
        public uint Count;
        /// <summary><see cref="SyncRecordKind.IconControl"/>: icons.</summary>
        public uint Count2;
        /// <summary><see cref="SyncRecordKind.BucketDigest"/>, <see cref="SyncRecordKind.IconControl"/>: the digest.</summary>
        public ulong Hash;
        public List<EntityHash> Hashes;
        /// <summary><see cref="SyncRecordKind.RequestIds"/>: the ids (buckets, entities or lines).</summary>
        public uint[] Ids;
        public List<ComponentHash> ComponentHashes;
        /// <summary><see cref="SyncRecordKind.IconSet"/>: the owner and its icons.</summary>
        public SyncRef Owner;
        public List<SyncIcon> Icons;
        public IconControlMode IconMode;
        /// <summary>For <see cref="SyncRecordKind.MoverSpawn"/> and <see cref="SyncRecordKind.MoverState"/> (movers branch).</summary>
        public MoverRecord Mover;
        /// <summary>For <see cref="SyncRecordKind.MoverAgents"/> (with <see cref="NetId"/> = the mover).</summary>
        public MoverAgentLinks AgentLinks;
        /// <summary>For <see cref="SyncRecordKind.EnabledBits"/> (with <see cref="NetId"/>): the components' enabled states.</summary>
        public List<SyncEnabledBit> EnabledBits;
        /// <summary>For <see cref="SyncRecordKind.ModSettings"/>: mod, key and invariant value of each setting.</summary>
        public List<SettingEntry> Settings;
        /// <summary>For <see cref="SyncRecordKind.PrefabSource"/> (with <see cref="NetId"/> = the prefab's id): its origin.</summary>
        public Modding.AssetSource Source;

        // ---- gameplay branch ----
        /// <summary><see cref="SyncRecordKind.Epoch"/>: the broadcast number.</summary>
        public uint EpochNumber;
        /// <summary><see cref="SyncRecordKind.ProposalHeader"/>, <see cref="SyncRecordKind.ProposalResult"/>: the proposal's sequence number.</summary>
        public uint Seq;
        /// <summary><see cref="SyncRecordKind.ProposalHeader"/>: the newest host epoch the client had applied.</summary>
        public uint BaseEpoch;
        /// <summary><see cref="SyncRecordKind.ProposalHeader"/>: what the proposal contains.</summary>
        public ProposalFlags ProposalFlags;
        /// <summary><see cref="SyncRecordKind.ProposalResult"/>: <see cref="RejectReason.None"/> for an accepted proposal.</summary>
        public RejectReason Reason;

        // ---- solo testing ----
        /// <summary><see cref="SyncRecordKind.ReplayCheckpoint"/>: begin or end (the checkpoint number is <see cref="Seq"/>).</summary>
        public ReplayCheckpointPhase CheckpointPhase;

        public override string ToString() => Kind + " #" + NetId + (Components.Count > 0 ? " (" + Components.Count + " components)" : "");
    }

    /// <summary>Identity of a zone block without an entity: its owner edge, side and quantised geometry.</summary>
    public struct ZoneBlockKey : IEquatable<ZoneBlockKey>
    {
        public uint OwnerNetId;
        /// <summary>Block position x/z quantised to 1/64 m.</summary>
        public int X, Z;
        /// <summary>Block direction quantised to 1/1024.</summary>
        public int DirX, DirZ;
        public int SizeX, SizeY;

        public bool Equals(ZoneBlockKey o) => OwnerNetId == o.OwnerNetId && X == o.X && Z == o.Z && DirX == o.DirX && DirZ == o.DirZ && SizeX == o.SizeX && SizeY == o.SizeY;
        public override bool Equals(object obj) => obj is ZoneBlockKey o && Equals(o);
        public override int GetHashCode() => unchecked((int)OwnerNetId * 31 + X * 17 + Z * 13 + DirX * 7 + DirZ * 3 + SizeX + SizeY * 5);

        public static int Quantize(float v, float scale) => (int)Math.Round(v * scale);

        /// <summary>Squared distance in quantised units, for the nearest-match fallback.</summary>
        public long DistanceSquared(ZoneBlockKey o)
        {
            long dx = X - o.X, dz = Z - o.Z;
            return dx * dx + dz * dz;
        }
    }

    /// <summary>
    /// The replication batch: a self-describing list of entity, prefab and channel records. The host sends one per
    /// frame with changes (<c>MessageKind.Epoch</c>); a client sends its predicted edits the same way
    /// (<c>MessageKind.Proposal</c>). Component bytes are the game's own save encoding with entity references written
    /// as net ids, so the format follows game patches by itself.
    /// </summary>
    public static class SyncBatch
    {
        public const byte Version = 1;
        private const int MaxRecords = 1 << 22;
        private const int MaxComponents = 512;
        private const int MaxStringBytes = 1024;
        private const int MaxZoneCells = 4096;
        private const int MaxGlobalPayload = 1 << 22;
        /// <summary>Ids per <see cref="SyncRecordKind.RequestIds"/> and hashes per <see cref="SyncRecordKind.EntityHashes"/> record at most.</summary>
        public const int MaxIdsPerRecord = 4096;
        /// <summary>Icons per owner in one <see cref="SyncRecordKind.IconSet"/> record at most.</summary>
        public const int MaxIconsPerOwner = 256;
        private const int MaxPayloadBytes = 1 << 22;
        /// <summary>Mod settings per <see cref="SyncRecordKind.ModSettings"/> record at most.</summary>
        public const int MaxModSettings = 1024;
        private const int MaxSettingTextBytes = 4096;

        /// <summary>Bytes before the first record: the version byte and the record count.</summary>
        public const int HeaderBytes = 5;

        /// <summary>
        /// The kind of a batch's first record without decoding it (0 for an empty or truncated batch). Some producers own
        /// whole batches (global state, movers), so their consumers recognise them by this byte alone.
        /// </summary>
        public static byte FirstRecordKind(byte[] data, int offset, int count)
        {
            if (data == null || offset < 0 || count <= HeaderBytes || offset > data.Length - count) return 0;
            return data[offset + HeaderBytes];
        }

        /// <summary>
        /// A batch of the movers branch (<c>MoverHost</c> writes only mover records into its batches): owned by the
        /// client's <c>MoverClient</c>; the structure apply, the integrity check and global state never need it.
        /// </summary>
        public static bool IsMoverBatch(byte[] data, int offset, int count)
        {
            byte kind = FirstRecordKind(data, offset, count);
            return kind == (byte)SyncRecordKind.MoverSpawn || kind == (byte)SyncRecordKind.MoverState || kind == (byte)SyncRecordKind.MoverDespawn
                   || kind == (byte)SyncRecordKind.MoverAgents;
        }

        /// <summary>
        /// Record kinds a host accepts in a client's proposal. Everything else is host → client only (prefab bindings, id
        /// ranges, the clock, integrity answers, icons, line rows, movers); a host applying such a record from a client
        /// would let it rebind prefabs to arbitrary ids or overwrite host-owned state.
        /// </summary>
        public static bool AcceptedFromClient(SyncRecordKind kind)
        {
            switch (kind)
            {
                case SyncRecordKind.Spawn:
                case SyncRecordKind.Upsert:
                case SyncRecordKind.Values:
                case SyncRecordKind.Despawn:
                case SyncRecordKind.ZoneCells:
                case SyncRecordKind.Cost:
                case SyncRecordKind.Request:
                case SyncRecordKind.Global:
                case SyncRecordKind.RequestIds:
                case SyncRecordKind.EnabledBits:
                case SyncRecordKind.ProposalHeader:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Builds one batch: a version byte, a record count and the records. Reuse one writer with <see cref="Reset"/>.
        /// Not thread-safe.
        /// </summary>
        public sealed class Writer
        {
            private readonly ByteWriter _w = new ByteWriter(1 << 16);
            private int _countPosition;
            private uint _count;

            public Writer()
            {
                Reset();
            }

            public int RecordCount => (int)_count;

            public int Length => _w.Length;

            public bool IsEmpty => _count == 0;

            public void Reset()
            {
                _w.Reset();
                _w.WriteByte(Version);
                _countPosition = _w.Reserve(4);
                _count = 0;
            }

            /// <summary>Starts an entity record; follow with <see cref="AddComponent"/> calls and <see cref="EndEntity"/>.</summary>
            public void BeginEntity(SyncRecordKind kind, uint netId, Family family, bool completeSet)
            {
                if (kind != SyncRecordKind.Spawn && kind != SyncRecordKind.Upsert && kind != SyncRecordKind.Values) throw new ArgumentException("not an entity record kind", nameof(kind));
                _w.WriteByte((byte)kind);
                _w.WriteVarUInt32(netId);
                _w.WriteByte((byte)family);
                _w.WriteBool(completeSet);
                _componentCountPosition = _w.Reserve(2);
                _componentCount = 0;
            }

            private int _componentCountPosition;
            private int _componentCount;

            public void AddComponent(ulong typeHash, SyncComponentKind kind, byte[] data, int offset, int count)
            {
                if (_componentCount >= MaxComponents) throw new InvalidOperationException("too many components in one record");
                _w.WriteUInt64(typeHash);
                _w.WriteByte((byte)kind);
                if (kind != SyncComponentKind.Presence)
                {
                    _w.WriteVarUInt32((uint)count);
                    if (count > 0) _w.WriteBytes(data, offset, count);
                }
                _componentCount++;
            }

            public void EndEntity()
            {
                _w.Buffer[_componentCountPosition] = (byte)_componentCount;
                _w.Buffer[_componentCountPosition + 1] = (byte)(_componentCount >> 8);
                _count++;
            }

            public void Despawn(uint netId)
            {
                _w.WriteByte((byte)SyncRecordKind.Despawn);
                _w.WriteVarUInt32(netId);
                _count++;
            }

            public void Prefab(uint netId, string name, byte[] data, int offset, int count)
            {
                if (count > MaxStringBytes * 4) throw new ArgumentException("prefab id too long", nameof(count));
                _w.WriteByte((byte)SyncRecordKind.Prefab);
                _w.WriteVarUInt32(netId);
                _w.WriteString(Truncate(name), MaxStringBytes);
                _w.WriteVarUInt32((uint)count);
                if (count > 0) _w.WriteBytes(data, offset, count);
                _count++;
            }

            private static string Truncate(string s)
            {
                s = s ?? "";
                return s.Length > 200 ? s.Substring(0, 200) : s;
            }

            public void ZoneCells(ZoneBlockKey key, IReadOnlyList<uint> zoneIds)
            {
                if (zoneIds.Count > MaxZoneCells) throw new ArgumentException("too many zone cells", nameof(zoneIds));
                _w.WriteByte((byte)SyncRecordKind.ZoneCells);
                _w.WriteVarUInt32(key.OwnerNetId);
                _w.WriteVarInt32(key.X);
                _w.WriteVarInt32(key.Z);
                _w.WriteVarInt32(key.DirX);
                _w.WriteVarInt32(key.DirZ);
                _w.WriteVarInt32(key.SizeX);
                _w.WriteVarInt32(key.SizeY);
                _w.WriteVarUInt32((uint)zoneIds.Count);
                // run-length: (value, run) pairs
                int i = 0;
                while (i < zoneIds.Count)
                {
                    uint v = zoneIds[i];
                    int run = 1;
                    while (i + run < zoneIds.Count && zoneIds[i + run] == v) run++;
                    _w.WriteVarUInt32(v);
                    _w.WriteVarUInt32((uint)run);
                    i += run;
                }
                _count++;
            }

            public void IdRange(uint start, uint count)
            {
                _w.WriteByte((byte)SyncRecordKind.IdRange);
                _w.WriteVarUInt32(start);
                _w.WriteVarUInt32(count);
                _count++;
            }

            /// <summary>w9/perf: the host's id frontier (<see cref="SyncRecordKind.IdCeiling"/>).</summary>
            public void IdCeiling(uint frontier)
            {
                _w.WriteByte((byte)SyncRecordKind.IdCeiling);
                _w.WriteVarUInt32(frontier);
                _count++;
            }

            public void Cost(long amount)
            {
                _w.WriteByte((byte)SyncRecordKind.Cost);
                _w.WriteVarInt64(amount);
                _count++;
            }

            public void Request(SyncRequest request)
            {
                _w.WriteByte((byte)SyncRecordKind.Request);
                _w.WriteByte((byte)request);
                _count++;
            }

            public void Clock(uint frame)
            {
                _w.WriteByte((byte)SyncRecordKind.Clock);
                _w.WriteUInt32(frame);
                _count++;
            }

            /// <summary>A global-state record (<see cref="SyncRecordKind.Global"/>).</summary>
            public void Global(uint channel, byte[] data, int offset, int count)
            {
                if (count < 0 || count > MaxGlobalPayload) throw new ArgumentException("global payload of " + count + " bytes", nameof(count));
                _w.WriteByte((byte)SyncRecordKind.Global);
                _w.WriteUInt32(channel);
                _w.WriteVarUInt32((uint)count);
                if (count > 0) _w.WriteBytes(data, offset, count);
                _count++;
            }

            // ---- integrity and icon channels (feat/integrity) ----

            public void BucketDigest(uint bucket, uint count, ulong hash)
            {
                _w.WriteByte((byte)SyncRecordKind.BucketDigest);
                _w.WriteVarUInt32(bucket);
                _w.WriteVarUInt32(count);
                _w.WriteUInt64(hash);
                _count++;
            }

            public void EntityHashes(uint bucket, IReadOnlyList<EntityHash> hashes)
            {
                if (hashes.Count > MaxIdsPerRecord) throw new ArgumentException("too many entity hashes", nameof(hashes));
                _w.WriteByte((byte)SyncRecordKind.EntityHashes);
                _w.WriteVarUInt32(bucket);
                _w.WriteVarUInt32((uint)hashes.Count);
                for (int i = 0; i < hashes.Count; i++)
                {
                    _w.WriteVarUInt32(hashes[i].NetId);
                    _w.WriteUInt64(hashes[i].Hash);
                }
                _count++;
            }

            public void RequestIds(SyncRequest request, IReadOnlyList<uint> ids)
            {
                if (ids.Count > MaxIdsPerRecord) throw new ArgumentException("too many ids", nameof(ids));
                _w.WriteByte((byte)SyncRecordKind.RequestIds);
                _w.WriteByte((byte)request);
                _w.WriteVarUInt32((uint)ids.Count);
                for (int i = 0; i < ids.Count; i++) _w.WriteVarUInt32(ids[i]);
                _count++;
            }

            public void EntityExplain(uint netId, IReadOnlyList<ComponentHash> members)
            {
                if (members.Count > MaxComponents) throw new ArgumentException("too many members", nameof(members));
                _w.WriteByte((byte)SyncRecordKind.EntityExplain);
                _w.WriteVarUInt32(netId);
                _w.WriteVarUInt32((uint)members.Count);
                for (int i = 0; i < members.Count; i++)
                {
                    _w.WriteUInt64(members[i].TypeHash);
                    _w.WriteUInt64(members[i].ValueHash);
                    _w.WriteByte((byte)members[i].State);
                }
                _count++;
            }

            public void IconSet(SyncRef owner, IReadOnlyList<SyncIcon> icons)
            {
                if (icons.Count > MaxIconsPerOwner) throw new ArgumentException("too many icons", nameof(icons));
                _w.WriteByte((byte)SyncRecordKind.IconSet);
                owner.Write(_w);
                _w.WriteVarUInt32((uint)icons.Count);
                for (int i = 0; i < icons.Count; i++) icons[i].Write(_w);
                _count++;
            }

            public void IconControl(IconControlMode mode, uint owners, uint icons, ulong hash)
            {
                _w.WriteByte((byte)SyncRecordKind.IconControl);
                _w.WriteByte((byte)mode);
                _w.WriteVarUInt32(owners);
                _w.WriteVarUInt32(icons);
                _w.WriteUInt64(hash);
                _count++;
            }

            public void LineRows(byte[] data, int offset, int count)
            {
                if (count > MaxPayloadBytes) throw new ArgumentException("line rows too large", nameof(count));
                _w.WriteByte((byte)SyncRecordKind.LineRows);
                _w.WriteVarUInt32((uint)count);
                if (count > 0) _w.WriteBytes(data, offset, count);
                _count++;
            }

            /// <summary>A mover spawn or state record (movers branch, ARCH §4.9).</summary>
            public void Mover(SyncRecordKind kind, uint netId, MoverRecord mover)
            {
                if (kind != SyncRecordKind.MoverSpawn && kind != SyncRecordKind.MoverState) throw new ArgumentException("not a mover record kind", nameof(kind));
                _w.WriteByte((byte)kind);
                _w.WriteVarUInt32(netId);
                MoverRecordCodec.Write(_w, mover);
                _count++;
            }

            /// <summary>A mover despawn record (movers branch).</summary>
            public void MoverDespawn(uint netId)
            {
                _w.WriteByte((byte)SyncRecordKind.MoverDespawn);
                _w.WriteVarUInt32(netId);
                _count++;
            }

            /// <summary>The agents a mover refers to (<see cref="SyncRecordKind.MoverAgents"/>).</summary>
            public void MoverAgents(uint netId, MoverAgentLinks links)
            {
                if (links == null) throw new ArgumentNullException(nameof(links));
                _w.WriteByte((byte)SyncRecordKind.MoverAgents);
                _w.WriteVarUInt32(netId);
                MoverAgentLinksCodec.Write(_w, links);
                _count++;
            }

            /// <summary>The enabled states of an entity's enableable components (<see cref="SyncRecordKind.EnabledBits"/>).</summary>
            public void EnabledBits(uint netId, IReadOnlyList<SyncEnabledBit> bits)
            {
                if (bits.Count > MaxComponents) throw new ArgumentException("too many enabled bits", nameof(bits));
                _w.WriteByte((byte)SyncRecordKind.EnabledBits);
                _w.WriteVarUInt32(netId);
                _w.WriteVarUInt32((uint)bits.Count);
                for (int i = 0; i < bits.Count; i++)
                {
                    _w.WriteUInt64(bits[i].TypeHash);
                    _w.WriteBool(bits[i].Enabled);
                }
                _count++;
            }

            /// <summary>The host's values of simulation-relevant mod settings (<see cref="SyncRecordKind.ModSettings"/>).</summary>
            public void ModSettings(IReadOnlyList<SettingEntry> settings)
            {
                if (settings.Count > MaxModSettings) throw new ArgumentException("too many mod settings", nameof(settings));
                _w.WriteByte((byte)SyncRecordKind.ModSettings);
                _w.WriteVarUInt32((uint)settings.Count);
                for (int i = 0; i < settings.Count; i++)
                {
                    _w.WriteString(settings[i].Mod ?? "", MaxSettingTextBytes);
                    _w.WriteString(settings[i].Key ?? "", MaxSettingTextBytes);
                    _w.WriteString(settings[i].Value ?? "", MaxSettingTextBytes);
                }
                _count++;
            }

            /// <summary>Where the prefab bound to <paramref name="netId"/> comes from (<see cref="SyncRecordKind.PrefabSource"/>).</summary>
            public void PrefabSource(uint netId, Modding.AssetSource source)
            {
                if (source == null) throw new ArgumentNullException(nameof(source));
                _w.WriteByte((byte)SyncRecordKind.PrefabSource);
                _w.WriteVarUInt32(netId);
                _w.WriteByte((byte)source.Kind);
                _w.WriteString(Protocol.ProtocolText.Truncate(source.Name, Modding.AssetSource.MaxNameBytes / Protocol.ProtocolText.MaxUtf8BytesPerChar), Modding.AssetSource.MaxNameBytes);
                _w.WriteString(Protocol.ProtocolText.Truncate(source.PlatformId, Modding.AssetSource.MaxPlatformIdBytes / Protocol.ProtocolText.MaxUtf8BytesPerChar), Modding.AssetSource.MaxPlatformIdBytes);
                _count++;
            }

            // ---- gameplay branch ----

            /// <summary>Host: stamps this structural broadcast with its epoch number.</summary>
            public void Epoch(uint epoch)
            {
                _w.WriteByte((byte)SyncRecordKind.Epoch);
                _w.WriteVarUInt32(epoch);
                _count++;
            }

            /// <summary>Client: the header of the proposal in this batch.</summary>
            public void ProposalHeader(uint seq, uint baseEpoch, ProposalFlags flags)
            {
                _w.WriteByte((byte)SyncRecordKind.ProposalHeader);
                _w.WriteVarUInt32(seq);
                _w.WriteVarUInt32(baseEpoch);
                _w.WriteByte((byte)flags);
                _count++;
            }

            /// <summary>Host → originator: the result of proposal <paramref name="seq"/>.</summary>
            public void ProposalResult(uint seq, RejectReason reason)
            {
                _w.WriteByte((byte)SyncRecordKind.ProposalResult);
                _w.WriteVarUInt32(seq);
                _w.WriteVarUInt32((ushort)reason);
                _count++;
            }

            // ---- solo testing (record/replay verdict) ----

            /// <summary>The integrity hashes of one bucket at checkpoint <paramref name="checkpoint"/> (<see cref="SyncRecordKind.ReplayHashes"/>).</summary>
            public void ReplayHashes(uint checkpoint, uint bucket, IReadOnlyList<EntityHash> hashes)
            {
                if (hashes.Count > MaxIdsPerRecord) throw new ArgumentException("too many entity hashes", nameof(hashes));
                _w.WriteByte((byte)SyncRecordKind.ReplayHashes);
                _w.WriteVarUInt32(checkpoint);
                _w.WriteVarUInt32(bucket);
                _w.WriteVarUInt32((uint)hashes.Count);
                for (int i = 0; i < hashes.Count; i++)
                {
                    _w.WriteVarUInt32(hashes[i].NetId);
                    _w.WriteUInt64(hashes[i].Hash);
                }
                _count++;
            }

            /// <summary>One entity's integrity hash and its members (<see cref="SyncRecordKind.ReplayMembers"/>).</summary>
            public void ReplayMembers(uint netId, Family family, ulong hash, IReadOnlyList<ComponentHash> members)
            {
                if (members.Count > MaxComponents) throw new ArgumentException("too many members", nameof(members));
                _w.WriteByte((byte)SyncRecordKind.ReplayMembers);
                _w.WriteVarUInt32(netId);
                _w.WriteByte((byte)family);
                _w.WriteUInt64(hash);
                _w.WriteVarUInt32((uint)members.Count);
                for (int i = 0; i < members.Count; i++)
                {
                    _w.WriteUInt64(members[i].TypeHash);
                    _w.WriteUInt64(members[i].ValueHash);
                    _w.WriteByte((byte)members[i].State);
                }
                _count++;
            }

            /// <summary>The begin or end of a checkpoint with an opaque summary (<see cref="SyncRecordKind.ReplayCheckpoint"/>).</summary>
            public void ReplayCheckpoint(uint checkpoint, ReplayCheckpointPhase phase, uint frame, byte[] payload, int offset, int count)
            {
                if (count < 0 || count > MaxPayloadBytes) throw new ArgumentException("checkpoint summary of " + count + " bytes", nameof(count));
                _w.WriteByte((byte)SyncRecordKind.ReplayCheckpoint);
                _w.WriteVarUInt32(checkpoint);
                _w.WriteByte((byte)phase);
                _w.WriteUInt32(frame);
                _w.WriteVarUInt32((uint)count);
                if (count > 0) _w.WriteBytes(payload, offset, count);
                _count++;
            }

            /// <summary>The finished batch bytes (valid until the next write).</summary>
            public ArraySegment<byte> Finish()
            {
                _w.PatchUInt32(_countPosition, _count);
                return new ArraySegment<byte>(_w.Buffer, 0, _w.Length);
            }
        }

        /// <summary>
        /// Decodes a batch. Component data segments point into <paramref name="buffer"/>, which must stay unchanged while
        /// the records are used. Throws <see cref="DecodeException"/> for malformed input.
        /// </summary>
        public static List<SyncRecord> Read(byte[] buffer, int offset, int count)
        {
            var r = new ByteReader(buffer, offset, count);
            try
            {
                byte version = r.ReadByte();
                if (version != Version) throw new DecodeException("sync batch version " + version + " (expected " + Version + ")");
                uint n = r.ReadUInt32();
                if (n > MaxRecords) throw new DecodeException("sync batch with " + n + " records");
                var list = new List<SyncRecord>((int)Math.Min(n, 4096));
                for (uint i = 0; i < n; i++) list.Add(ReadRecord(r, buffer, offset));
                if (!r.IsAtEnd) throw new DecodeException("sync batch has " + r.Remaining + " trailing bytes");
                return list;
            }
            catch (DecodeException)
            {
                throw;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IndexOutOfRangeException || ex is InvalidOperationException)
            {
                throw new DecodeException("malformed sync batch: " + ex.Message);
            }
        }

        /// <summary>
        /// A family byte, which must name a <see cref="Family"/> (the applier picks archetypes, id tables and
        /// classification rules by it; an unknown value bound an entity to a family no rule knows).
        /// </summary>
        private static Family ReadFamily(ByteReader r)
        {
            byte b = r.ReadByte();
            if (b > (byte)Family.Generic) throw new DecodeException("unknown family " + b);
            return (Family)b;
        }

        /// <summary>A list length, checked against its limit and the bytes left (each element needs at least <paramref name="minBytes"/>).</summary>
        private static uint ReadCount(ByteReader r, int max, int minBytes, string what)
        {
            uint n = r.ReadVarUInt32();
            if (n > (uint)max || n > (uint)(r.Remaining / minBytes)) throw new DecodeException("record with " + n + " " + what);
            return n;
        }

        private static SyncRecord ReadRecord(ByteReader r, byte[] buffer, int baseOffset)
        {
            var rec = new SyncRecord { Kind = (SyncRecordKind)r.ReadByte() };
            switch (rec.Kind)
            {
                case SyncRecordKind.Spawn:
                case SyncRecordKind.Upsert:
                case SyncRecordKind.Values:
                {
                    rec.NetId = r.ReadVarUInt32();
                    rec.Family = ReadFamily(r);
                    rec.CompleteSet = r.ReadBool();
                    int components = r.ReadUInt16();
                    if (components > MaxComponents) throw new DecodeException("record with " + components + " components");
                    for (int c = 0; c < components; c++)
                    {
                        var comp = new SyncComponent { TypeHash = r.ReadUInt64(), Kind = (SyncComponentKind)r.ReadByte(), Data = buffer };
                        if (comp.Kind > SyncComponentKind.Presence) throw new DecodeException("unknown component kind " + (byte)comp.Kind);
                        if (comp.Kind != SyncComponentKind.Presence)
                        {
                            uint len = r.ReadVarUInt32();
                            if (len > (uint)r.Remaining) throw new DecodeException("component of " + len + " bytes overruns the batch");
                            comp.Offset = baseOffset + r.Position;
                            comp.Count = (int)len;
                            r.Skip((int)len);
                        }
                        rec.Components.Add(comp);
                    }
                    return rec;
                }
                case SyncRecordKind.Despawn:
                    rec.NetId = r.ReadVarUInt32();
                    return rec;
                case SyncRecordKind.Prefab:
                    rec.NetId = r.ReadVarUInt32();
                    rec.PrefabName = r.ReadString(MaxStringBytes);
                    uint idLength = r.ReadVarUInt32();
                    if (idLength > MaxStringBytes * 4 || idLength > (uint)r.Remaining) throw new DecodeException("prefab id of " + idLength + " bytes");
                    rec.PrefabData = new byte[idLength];
                    r.ReadBytes(rec.PrefabData, 0, (int)idLength);
                    return rec;
                case SyncRecordKind.ZoneCells:
                {
                    rec.Block = new ZoneBlockKey
                    {
                        OwnerNetId = r.ReadVarUInt32(),
                        X = r.ReadVarInt32(),
                        Z = r.ReadVarInt32(),
                        DirX = r.ReadVarInt32(),
                        DirZ = r.ReadVarInt32(),
                        SizeX = r.ReadVarInt32(),
                        SizeY = r.ReadVarInt32(),
                    };
                    uint cells = r.ReadVarUInt32();
                    if (cells > MaxZoneCells) throw new DecodeException("zone block with " + cells + " cells");
                    rec.ZoneIds = new uint[cells];
                    int i = 0;
                    while (i < cells)
                    {
                        uint v = r.ReadVarUInt32();
                        uint run = r.ReadVarUInt32();
                        if (run == 0 || run > cells - i) throw new DecodeException("bad zone cell run");
                        for (int k = 0; k < run; k++) rec.ZoneIds[i++] = v;
                    }
                    return rec;
                }
                case SyncRecordKind.IdRange:
                    rec.RangeStart = r.ReadVarUInt32();
                    rec.RangeCount = r.ReadVarUInt32();
                    return rec;
                case SyncRecordKind.IdCeiling:
                    rec.RangeStart = r.ReadVarUInt32();
                    return rec;
                case SyncRecordKind.Cost:
                    rec.Amount = r.ReadVarInt64();
                    return rec;
                case SyncRecordKind.Clock:
                    rec.Frame = r.ReadUInt32();
                    return rec;
                case SyncRecordKind.Request:
                    rec.Request = (SyncRequest)r.ReadByte();
                    return rec;
                case SyncRecordKind.Global:
                {
                    rec.Channel = r.ReadUInt32();
                    uint length = r.ReadVarUInt32();
                    if (length > MaxGlobalPayload || length > (uint)r.Remaining) throw new DecodeException("global payload of " + length + " bytes");
                    rec.Payload = new byte[length];
                    if (length > 0) r.ReadBytes(rec.Payload, 0, (int)length);
                    return rec;
                }
                case SyncRecordKind.BucketDigest:
                    rec.Bucket = r.ReadVarUInt32();
                    rec.Count = r.ReadVarUInt32();
                    rec.Hash = r.ReadUInt64();
                    return rec;
                case SyncRecordKind.EntityHashes:
                {
                    rec.Bucket = r.ReadVarUInt32();
                    uint n = ReadCount(r, MaxIdsPerRecord, 9, "entity hashes");
                    rec.Hashes = new List<EntityHash>((int)n);
                    for (uint i = 0; i < n; i++) rec.Hashes.Add(new EntityHash(r.ReadVarUInt32(), r.ReadUInt64()));
                    return rec;
                }
                case SyncRecordKind.RequestIds:
                {
                    rec.Request = (SyncRequest)r.ReadByte();
                    uint n = ReadCount(r, MaxIdsPerRecord, 1, "requested ids");
                    rec.Ids = new uint[n];
                    for (uint i = 0; i < n; i++) rec.Ids[i] = r.ReadVarUInt32();
                    return rec;
                }
                case SyncRecordKind.EntityExplain:
                {
                    rec.NetId = r.ReadVarUInt32();
                    uint n = ReadCount(r, MaxComponents, 17, "explained members");
                    rec.ComponentHashes = new List<ComponentHash>((int)n);
                    for (uint i = 0; i < n; i++)
                    {
                        var m = new ComponentHash(r.ReadUInt64(), r.ReadUInt64(), (ComponentHashState)r.ReadByte());
                        if (m.State > ComponentHashState.Presence) throw new DecodeException("unknown member state " + (byte)m.State);
                        rec.ComponentHashes.Add(m);
                    }
                    return rec;
                }
                case SyncRecordKind.IconSet:
                {
                    rec.Owner = SyncRef.Read(r);
                    uint n = ReadCount(r, MaxIconsPerOwner, 16, "icons");
                    rec.Icons = new List<SyncIcon>((int)n);
                    for (uint i = 0; i < n; i++) rec.Icons.Add(SyncIcon.Read(r));
                    return rec;
                }
                case SyncRecordKind.IconControl:
                    rec.IconMode = (IconControlMode)r.ReadByte();
                    if (rec.IconMode < IconControlMode.Digest || rec.IconMode > IconControlMode.FullEnd) throw new DecodeException("unknown icon control mode " + (byte)rec.IconMode);
                    rec.Count = r.ReadVarUInt32();
                    rec.Count2 = r.ReadVarUInt32();
                    rec.Hash = r.ReadUInt64();
                    return rec;
                case SyncRecordKind.LineRows:
                {
                    uint n = r.ReadVarUInt32();
                    if (n > MaxPayloadBytes || n > (uint)r.Remaining) throw new DecodeException("line rows of " + n + " bytes");
                    rec.Payload = new byte[n];
                    r.ReadBytes(rec.Payload, 0, (int)n);
                    return rec;
                }
                case SyncRecordKind.MoverSpawn:
                case SyncRecordKind.MoverState:
                    rec.NetId = r.ReadVarUInt32();
                    rec.Mover = MoverRecordCodec.Read(r);
                    return rec;
                case SyncRecordKind.MoverDespawn:
                    rec.NetId = r.ReadVarUInt32();
                    return rec;
                case SyncRecordKind.MoverAgents:
                    rec.NetId = r.ReadVarUInt32();
                    rec.AgentLinks = MoverAgentLinksCodec.Read(r);
                    return rec;
                case SyncRecordKind.EnabledBits:
                {
                    rec.NetId = r.ReadVarUInt32();
                    uint n = ReadCount(r, MaxComponents, 9, "enabled bits");
                    rec.EnabledBits = new List<SyncEnabledBit>((int)n);
                    for (uint i = 0; i < n; i++) rec.EnabledBits.Add(new SyncEnabledBit(r.ReadUInt64(), r.ReadBool()));
                    return rec;
                }
                case SyncRecordKind.ModSettings:
                {
                    uint n = ReadCount(r, MaxModSettings, 3, "mod settings");
                    rec.Settings = new List<SettingEntry>((int)n);
                    for (uint i = 0; i < n; i++)
                    {
                        rec.Settings.Add(new SettingEntry
                        {
                            Mod = r.ReadString(MaxSettingTextBytes),
                            Key = r.ReadString(MaxSettingTextBytes),
                            Value = r.ReadString(MaxSettingTextBytes),
                        });
                    }
                    return rec;
                }
                case SyncRecordKind.PrefabSource:
                {
                    rec.NetId = r.ReadVarUInt32();
                    byte kind = r.ReadByte();
                    rec.Source = new Modding.AssetSource
                    {
                        Kind = kind <= (byte)Modding.AssetSourceKind.RuntimePrefab ? (Modding.AssetSourceKind)kind : Modding.AssetSourceKind.Unknown,
                        Name = Protocol.ProtocolText.Sanitize(r.ReadString(Modding.AssetSource.MaxNameBytes)),
                        PlatformId = Protocol.ProtocolText.Sanitize(r.ReadString(Modding.AssetSource.MaxPlatformIdBytes)),
                    };
                    return rec;
                }
                case SyncRecordKind.Epoch:
                    rec.EpochNumber = r.ReadVarUInt32();
                    return rec;
                case SyncRecordKind.ProposalHeader:
                    rec.Seq = r.ReadVarUInt32();
                    rec.BaseEpoch = r.ReadVarUInt32();
                    rec.ProposalFlags = (ProposalFlags)r.ReadByte();
                    return rec;
                case SyncRecordKind.ProposalResult:
                {
                    rec.Seq = r.ReadVarUInt32();
                    uint reason = r.ReadVarUInt32();
                    if (reason > ushort.MaxValue) throw new DecodeException("proposal result reason " + reason);
                    rec.Reason = (RejectReason)reason;
                    return rec;
                }
                case SyncRecordKind.ReplayHashes:
                {
                    rec.Seq = r.ReadVarUInt32();
                    rec.Bucket = r.ReadVarUInt32();
                    uint n = ReadCount(r, MaxIdsPerRecord, 9, "checkpoint hashes");
                    rec.Hashes = new List<EntityHash>((int)n);
                    for (uint i = 0; i < n; i++) rec.Hashes.Add(new EntityHash(r.ReadVarUInt32(), r.ReadUInt64()));
                    return rec;
                }
                case SyncRecordKind.ReplayMembers:
                {
                    rec.NetId = r.ReadVarUInt32();
                    rec.Family = ReadFamily(r);
                    rec.Hash = r.ReadUInt64();
                    uint n = ReadCount(r, MaxComponents, 17, "checkpoint members");
                    rec.ComponentHashes = new List<ComponentHash>((int)n);
                    for (uint i = 0; i < n; i++)
                    {
                        var m = new ComponentHash(r.ReadUInt64(), r.ReadUInt64(), (ComponentHashState)r.ReadByte());
                        if (m.State > ComponentHashState.Presence) throw new DecodeException("unknown member state " + (byte)m.State);
                        rec.ComponentHashes.Add(m);
                    }
                    return rec;
                }
                case SyncRecordKind.ReplayCheckpoint:
                {
                    rec.Seq = r.ReadVarUInt32();
                    rec.CheckpointPhase = (ReplayCheckpointPhase)r.ReadByte();
                    if (rec.CheckpointPhase != ReplayCheckpointPhase.Begin && rec.CheckpointPhase != ReplayCheckpointPhase.End) throw new DecodeException("unknown checkpoint phase " + (byte)rec.CheckpointPhase);
                    rec.Frame = r.ReadUInt32();
                    uint n = r.ReadVarUInt32();
                    if (n > MaxPayloadBytes || n > (uint)r.Remaining) throw new DecodeException("checkpoint summary of " + n + " bytes");
                    rec.Payload = new byte[n];
                    if (n > 0) r.ReadBytes(rec.Payload, 0, (int)n);
                    return rec;
                }
                default:
                    throw new DecodeException("unknown sync record kind " + (byte)rec.Kind);
            }
        }
    }
}
