using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// Semantic UI intents (ARCHITECTURE.md#client-proposals) for player actions whose vanilla handler writes the world directly, without any
    /// tag a capture could scope (R10 §2: M4/M5/M6 mechanisms) or through an API that only the host may run (dev tree,
    /// map tiles). They travel in the <see cref="GlobalChannel.Action"/> channel (client → host, delta mode); the host
    /// validates each one and runs the vanilla executor, and the result reaches everyone through normal replication.
    /// </summary>
    public enum ActionKind : byte
    {
        /// <summary><c>DevTreeSystem.Purchase(node)</c>; <see cref="ActionIntent.Target"/> = node prefab.</summary>
        DevTreePurchase = 1,
        /// <summary>Map tile purchase; <see cref="ActionIntent.Ids"/> = tiles, <see cref="ActionIntent.Amount"/> = the cost the client saw (informational: the host prices the tiles itself, <see cref="MapTilePricing"/>).</summary>
        PurchaseMapTiles = 2,
        /// <summary><c>Building.m_Flags</c> Historical bit; <see cref="ActionIntent.Amount"/> 1 or 0.</summary>
        SetHistorical = 3,
        /// <summary>The whole <c>VehicleModel</c> buffer; <see cref="ActionIntent.Ids"/> = (primary, secondary) prefab pairs, 0 = none.</summary>
        SetVehicleModels = 4,
        /// <summary>The whole <c>ServiceDistrict</c> buffer of a building; <see cref="ActionIntent.Ids"/> = districts.</summary>
        SetServiceDistricts = 5,
        /// <summary><c>CustomMeshColor</c>; <see cref="ActionIntent.Values"/> = 3 colours × RGBA, or empty to reset to default.</summary>
        SetMeshColor = 6,
        /// <summary>Route <c>Color</c> (line colour); <see cref="ActionIntent.Values"/> = RGBA.</summary>
        SetRouteColor = 7,
        /// <summary>
        /// Derived sub-elements removed by the bulldozer's "sub-elements" mode (prefab sub-objects of buildings and nets
        /// have no net id); <see cref="ActionIntent.Keys"/> name them by owner, prefab and position.
        /// </summary>
        DeleteSubElements = 8,
    }

    /// <summary>
    /// Wire name of a derived entity (the game layer's derived key: kind 0 lane, 1 sub-object; owner and prefab net ids;
    /// index among the owner's children; position quantised to 1/64 m).
    /// </summary>
    public struct SubElementKey
    {
        public byte Kind;
        public uint OwnerNetId;
        public uint PrefabNetId;
        public int Index;
        public int X, Y, Z;
    }

    /// <summary>One intent. Unused fields stay empty.</summary>
    public sealed class ActionIntent
    {
        public ActionKind Kind;
        public uint Target;
        public uint[] Ids = Array.Empty<uint>();
        public float[] Values = Array.Empty<float>();
        public long Amount;
        /// <summary><see cref="ActionKind.DeleteSubElements"/>: the derived entities.</summary>
        public SubElementKey[] Keys = Array.Empty<SubElementKey>();

        public override string ToString() => Kind + " #" + Target + (Ids.Length > 0 ? " ids " + Ids.Length : "") + (Values.Length > 0 ? " values " + Values.Length : "")
                                             + (Amount != 0 ? " amount " + Amount : "") + (Keys.Length > 0 ? " keys " + Keys.Length : "");
    }

    /// <summary>Wire format and validation rules of <see cref="ActionIntent"/> lists.</summary>
    public static class ActionPayload
    {
        public const int MaxIntents = 4096;
        public const int MaxIds = 4096;
        public const int MaxValues = 64;
        public const int MaxKeys = 4096;

        public static byte[] Write(IReadOnlyList<ActionIntent> intents)
        {
            if (intents.Count > MaxIntents) throw new ArgumentException("too many intents (" + intents.Count + ")", nameof(intents));
            var w = new ByteWriter(64 + intents.Count * 16);
            w.WriteByte((byte)GlobalPayloadMode.Delta);
            w.WriteVarUInt32((uint)intents.Count);
            foreach (ActionIntent a in intents)
            {
                if (a.Ids.Length > MaxIds || a.Values.Length > MaxValues || a.Keys.Length > MaxKeys) throw new ArgumentException("intent too large: " + a, nameof(intents));
                w.WriteByte((byte)a.Kind);
                w.WriteVarUInt32(a.Target);
                w.WriteVarUInt32((uint)a.Ids.Length);
                foreach (uint id in a.Ids) w.WriteVarUInt32(id);
                w.WriteVarUInt32((uint)a.Values.Length);
                foreach (float v in a.Values) w.WriteSingle(v);
                w.WriteVarInt64(a.Amount);
                w.WriteVarUInt32((uint)a.Keys.Length);
                foreach (SubElementKey k in a.Keys)
                {
                    w.WriteByte(k.Kind);
                    w.WriteVarUInt32(k.OwnerNetId);
                    w.WriteVarUInt32(k.PrefabNetId);
                    w.WriteVarInt32(k.Index);
                    w.WriteVarInt32(k.X);
                    w.WriteVarInt32(k.Y);
                    w.WriteVarInt32(k.Z);
                }
            }
            return w.ToArray();
        }

        /// <summary>Decodes a payload; throws <see cref="DecodeException"/> for malformed input.</summary>
        public static List<ActionIntent> Read(byte[] data, int offset, int count)
        {
            var r = new ByteReader(data, offset, count);
            try
            {
                var mode = (GlobalPayloadMode)r.ReadByte();
                if (mode != GlobalPayloadMode.Delta) throw new DecodeException("action payload mode " + (byte)mode);
                uint n = r.ReadVarUInt32();
                if (n > MaxIntents || n > (uint)r.Remaining) throw new DecodeException("action payload with " + n + " intents");
                var list = new List<ActionIntent>((int)n);
                for (uint i = 0; i < n; i++)
                {
                    var a = new ActionIntent { Kind = (ActionKind)r.ReadByte(), Target = r.ReadVarUInt32() };
                    uint ids = r.ReadVarUInt32();
                    if (ids > MaxIds || ids > (uint)r.Remaining) throw new DecodeException("intent with " + ids + " ids");
                    a.Ids = new uint[ids];
                    for (int k = 0; k < ids; k++) a.Ids[k] = r.ReadVarUInt32();
                    uint values = r.ReadVarUInt32();
                    if (values > MaxValues || values * 4 > (uint)r.Remaining) throw new DecodeException("intent with " + values + " values");
                    a.Values = new float[values];
                    for (int k = 0; k < values; k++) a.Values[k] = r.ReadSingle();
                    a.Amount = r.ReadVarInt64();
                    uint keys = r.ReadVarUInt32();
                    if (keys > MaxKeys || keys * 7 > (uint)r.Remaining) throw new DecodeException("intent with " + keys + " keys");
                    a.Keys = new SubElementKey[keys];
                    for (int k = 0; k < keys; k++)
                    {
                        a.Keys[k] = new SubElementKey
                        {
                            Kind = r.ReadByte(),
                            OwnerNetId = r.ReadVarUInt32(),
                            PrefabNetId = r.ReadVarUInt32(),
                            Index = r.ReadVarInt32(),
                            X = r.ReadVarInt32(),
                            Y = r.ReadVarInt32(),
                            Z = r.ReadVarInt32(),
                        };
                    }
                    list.Add(a);
                }
                if (!r.IsAtEnd) throw new DecodeException("action payload has " + r.Remaining + " trailing bytes");
                return list;
            }
            catch (DecodeException)
            {
                throw;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IndexOutOfRangeException || ex is InvalidOperationException)
            {
                throw new DecodeException("malformed action payload: " + ex.Message);
            }
        }

        /// <summary>The permission an intent needs (ARCHITECTURE.md#roles-and-moderation).</summary>
        public static Permission Required(ActionKind kind)
        {
            switch (kind)
            {
                case ActionKind.DevTreePurchase: return Permission.Progression;
                case ActionKind.PurchaseMapTiles: return Permission.MapTiles;
                case ActionKind.SetHistorical:
                case ActionKind.SetVehicleModels:
                case ActionKind.SetServiceDistricts:
                    return Permission.Policies;
                case ActionKind.SetMeshColor:
                case ActionKind.SetRouteColor:
                    return Permission.Names;
                case ActionKind.DeleteSubElements: return Permission.Demolish;
                default: return Permission.All;
            }
        }

        /// <summary>Structural checks independent of the world (shape, finite values, sane amounts).</summary>
        public static RejectReason Validate(ActionIntent a)
        {
            if (a == null) return RejectReason.Invalid;
            foreach (float v in a.Values)
            {
                if (float.IsNaN(v) || float.IsInfinity(v)) return RejectReason.Invalid;
            }
            switch (a.Kind)
            {
                case ActionKind.DevTreePurchase:
                    return a.Target != 0 ? RejectReason.None : RejectReason.Invalid;
                case ActionKind.PurchaseMapTiles:
                    return a.Ids.Length > 0 && a.Ids.Length <= MapTilePricing.MaxTilesPerPurchase && a.Amount >= 0 && a.Amount <= int.MaxValue ? RejectReason.None : RejectReason.Invalid; // the amount is only logged (the host prices the tiles)
                case ActionKind.SetHistorical:
                    return a.Target != 0 && (a.Amount == 0 || a.Amount == 1) ? RejectReason.None : RejectReason.Invalid;
                case ActionKind.SetVehicleModels:
                    return a.Target != 0 && a.Ids.Length % 2 == 0 && a.Ids.Length <= 64 ? RejectReason.None : RejectReason.Invalid;
                case ActionKind.SetServiceDistricts:
                    return a.Target != 0 && a.Ids.Length <= 1024 ? RejectReason.None : RejectReason.Invalid;
                case ActionKind.SetMeshColor:
                    return a.Target != 0 && (a.Values.Length == 0 || a.Values.Length == 12) ? RejectReason.None : RejectReason.Invalid;
                case ActionKind.SetRouteColor:
                    return a.Target != 0 && a.Values.Length == 4 ? RejectReason.None : RejectReason.Invalid;
                case ActionKind.DeleteSubElements:
                    return a.Keys.Length > 0 ? RejectReason.None : RejectReason.Invalid;
                default:
                    return RejectReason.Invalid;
            }
        }
    }

    /// <summary>
    /// The host's set of unlocked prefabs (<see cref="GlobalChannel.Unlocks"/>, host → client full state, ARCHITECTURE.md#player-actions):
    /// the net ids of every prefab whose <c>Locked</c> component is disabled, ascending and delta-coded.
    /// </summary>
    public static class UnlockSet
    {
        public const int MaxIds = 1 << 20;

        /// <summary>Writes <paramref name="sortedIds"/> (ascending, no duplicates).</summary>
        public static byte[] Write(IReadOnlyList<uint> sortedIds)
        {
            if (sortedIds.Count > MaxIds) throw new ArgumentException("too many ids", nameof(sortedIds));
            var w = new ByteWriter(16 + sortedIds.Count * 2);
            w.WriteByte((byte)GlobalPayloadMode.Full);
            w.WriteVarUInt32((uint)sortedIds.Count);
            uint last = 0;
            for (int i = 0; i < sortedIds.Count; i++)
            {
                uint id = sortedIds[i];
                if (i > 0 && id <= last) throw new ArgumentException("ids must be ascending and unique", nameof(sortedIds));
                w.WriteVarUInt32(id - last);
                last = id;
            }
            return w.ToArray();
        }

        public static List<uint> Read(byte[] data, int offset, int count)
        {
            var r = new ByteReader(data, offset, count);
            try
            {
                var mode = (GlobalPayloadMode)r.ReadByte();
                if (mode != GlobalPayloadMode.Full) throw new DecodeException("unlock set mode " + (byte)mode);
                uint n = r.ReadVarUInt32();
                if (n > MaxIds || n > (uint)r.Remaining) throw new DecodeException("unlock set with " + n + " ids");
                var list = new List<uint>((int)n);
                uint last = 0;
                for (uint i = 0; i < n; i++)
                {
                    uint delta = r.ReadVarUInt32();
                    if (i > 0 && delta == 0) throw new DecodeException("unlock set ids are not ascending");
                    if (delta > uint.MaxValue - last) throw new DecodeException("unlock set id overflows");
                    last += delta;
                    list.Add(last);
                }
                if (!r.IsAtEnd) throw new DecodeException("unlock set has " + r.Remaining + " trailing bytes");
                return list;
            }
            catch (DecodeException)
            {
                throw;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IndexOutOfRangeException || ex is InvalidOperationException)
            {
                throw new DecodeException("malformed unlock set: " + ex.Message);
            }
        }

        /// <summary>
        /// Client: what to change so the local unlocked set equals the host's. <paramref name="toUnlock"/> gets the ids the
        /// host has unlocked and the client has not; <paramref name="toRelock"/> the ids unlocked here but locked on the
        /// host (both restricted to <paramref name="known"/>, the ids this client can resolve to a lockable prefab).
        /// </summary>
        public static void Diff(IReadOnlyList<uint> hostUnlocked, ICollection<uint> localUnlocked, ICollection<uint> known, List<uint> toUnlock, List<uint> toRelock)
        {
            var host = new HashSet<uint>(hostUnlocked);
            foreach (uint id in host)
            {
                if (known.Contains(id) && !localUnlocked.Contains(id)) toUnlock.Add(id);
            }
            foreach (uint id in localUnlocked)
            {
                if (known.Contains(id) && !host.Contains(id)) toRelock.Add(id);
            }
            toUnlock.Sort();
            toRelock.Sort();
        }
    }
}
