using System;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Hashing;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// A portable name of an entity on the wire (integrity and icon channels): a net id, a derived key (a lane or a
    /// prefab sub-object found again through its bound owner, its prefab, its index among the owner's children and its
    /// position quantised to 1/64 m), or <see cref="Unnamed"/> for an entity that exists on the sender but cannot be named
    /// on another peer. The game side converts it from and to its own <c>DerivedKey</c>.
    /// </summary>
    public struct SyncRef : IEquatable<SyncRef>
    {
        /// <summary>No entity (<c>Entity.Null</c>).</summary>
        public const byte None = 0;
        /// <summary><see cref="Id"/> is a net id.</summary>
        public const byte NetId = 1;
        /// <summary>A derived lane: <see cref="Id"/> is the owner's net id.</summary>
        public const byte LaneKey = 2;
        /// <summary>A derived prefab sub-object: <see cref="Id"/> is the owner's net id.</summary>
        public const byte SubObjectKey = 3;
        /// <summary>An entity the sender has but cannot name (receivers treat it as "none" for matching).</summary>
        public const byte Unnamed = 4;

        private const ulong NormalDomain = 0x2545F4914F6CDD1DUL;

        public byte Kind;
        public uint Id;
        public uint Prefab;
        public int Index;
        public int X, Y, Z;

        public static SyncRef ForNetId(uint netId) => netId == 0 ? default : new SyncRef { Kind = NetId, Id = netId };

        public bool IsNone => Kind == None;

        /// <summary>True for <see cref="None"/> and <see cref="Unnamed"/>: nothing a receiver can resolve.</summary>
        public bool IsUnresolvable => Kind == None || Kind == Unnamed;

        /// <summary>
        /// A hash that both peers compute alike for the same entity: a net id as it is; a derived key reduced to its kind,
        /// owner and prefab (index and position depend on each peer's own regeneration); none and unnamed are equal.
        /// </summary>
        public ulong NormalizedHash()
        {
            switch (Kind)
            {
                case NetId:
                    return HashMix.Combine(NormalDomain + NetId, Id);
                case LaneKey:
                case SubObjectKey:
                    return HashMix.Combine(HashMix.Combine(NormalDomain + Kind, Id), Prefab);
                default:
                    return NormalDomain;
            }
        }

        public void Write(ByteWriter w)
        {
            w.WriteByte(Kind);
            switch (Kind)
            {
                case NetId:
                    w.WriteVarUInt32(Id);
                    break;
                case LaneKey:
                case SubObjectKey:
                    w.WriteVarUInt32(Id);
                    w.WriteVarUInt32(Prefab);
                    w.WriteVarInt32(Index);
                    w.WriteVarInt32(X);
                    w.WriteVarInt32(Y);
                    w.WriteVarInt32(Z);
                    break;
            }
        }

        /// <summary>Reads a reference written by <see cref="Write"/>; throws <see cref="DecodeException"/> for an unknown kind.</summary>
        public static SyncRef Read(ByteReader r)
        {
            var s = new SyncRef { Kind = r.ReadByte() };
            switch (s.Kind)
            {
                case None:
                case Unnamed:
                    break;
                case NetId:
                    s.Id = r.ReadVarUInt32();
                    break;
                case LaneKey:
                case SubObjectKey:
                    s.Id = r.ReadVarUInt32();
                    s.Prefab = r.ReadVarUInt32();
                    s.Index = r.ReadVarInt32();
                    s.X = r.ReadVarInt32();
                    s.Y = r.ReadVarInt32();
                    s.Z = r.ReadVarInt32();
                    break;
                default:
                    throw new DecodeException("unknown entity reference kind " + s.Kind);
            }
            return s;
        }

        public bool Equals(SyncRef o) => Kind == o.Kind && Id == o.Id && Prefab == o.Prefab && Index == o.Index && X == o.X && Y == o.Y && Z == o.Z;

        public override bool Equals(object obj) => obj is SyncRef o && Equals(o);

        public override int GetHashCode() => unchecked((int)HashMix.Fold32(HashMix.Combine(HashMix.Combine(Kind, Id), HashMix.Combine((ulong)(uint)Index, (ulong)(uint)X ^ ((ulong)(uint)Z << 32)))));

        public override string ToString()
        {
            switch (Kind)
            {
                case NetId: return "#" + Id;
                case LaneKey: return "lane of #" + Id + "[" + Index + "]";
                case SubObjectKey: return "sub-object of #" + Id + "[" + Index + "]";
                case Unnamed: return "(unnamed)";
                default: return "(none)";
            }
        }
    }

    /// <summary>
    /// One default-layer notification icon of an owner as the host shows it (ARCHITECTURE.md#statistics-icons-and-flow-status): the notification prefab's
    /// net id, the optional target, priority and <c>IconFlags</c> (byte values of the game enums), and the icon's
    /// location (used when the host placed it explicitly or when the target cannot be named on the receiver).
    /// </summary>
    public struct SyncIcon
    {
        public uint Prefab;
        public SyncRef Target;
        public byte Priority;
        public byte Flags;
        public float X, Y, Z;

        public void Write(ByteWriter w)
        {
            w.WriteVarUInt32(Prefab);
            Target.Write(w);
            w.WriteByte(Priority);
            w.WriteByte(Flags);
            w.WriteSingle(X);
            w.WriteSingle(Y);
            w.WriteSingle(Z);
        }

        public static SyncIcon Read(ByteReader r)
        {
            return new SyncIcon
            {
                Prefab = r.ReadVarUInt32(),
                Target = SyncRef.Read(r),
                Priority = r.ReadByte(),
                Flags = r.ReadByte(),
                X = r.ReadSingle(),
                Y = r.ReadSingle(),
                Z = r.ReadSingle(),
            };
        }
    }

    /// <summary>The integrity hash of one bound entity (<see cref="SyncRecordKind.EntityHashes"/>).</summary>
    public struct EntityHash
    {
        public uint NetId;
        public ulong Hash;

        public EntityHash(uint netId, ulong hash)
        {
            NetId = netId;
            Hash = hash;
        }
    }

    /// <summary>How one component took part in an entity's integrity hash (<see cref="SyncRecordKind.EntityExplain"/>).</summary>
    public enum ComponentHashState : byte
    {
        /// <summary>The value was hashed.</summary>
        Value = 0,
        /// <summary>Only the component's presence was hashed (a tag, or a value that is not replicated).</summary>
        Presence = 1,
    }

    /// <summary>One member of an entity's integrity hash, for the explanation of a persistent mismatch.</summary>
    public struct ComponentHash
    {
        public ulong TypeHash;
        public ulong ValueHash;
        public ComponentHashState State;

        public ComponentHash(ulong typeHash, ulong valueHash, ComponentHashState state)
        {
            TypeHash = typeHash;
            ValueHash = valueHash;
            State = state;
        }
    }

    /// <summary>What a <see cref="SyncRecordKind.IconControl"/> record says.</summary>
    public enum IconControlMode : byte
    {
        /// <summary>Periodic digest of every icon the host has sent so far (owners, icons, hash).</summary>
        Digest = 1,
        /// <summary>A full icon sync starts: the receiver forgets the host's icon sets it holds.</summary>
        FullBegin = 2,
        /// <summary>A full icon sync ended: every owner without a set now has no host icons (digest fields valid).</summary>
        FullEnd = 3,
    }
}
