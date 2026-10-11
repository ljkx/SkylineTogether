using System;
using System.Globalization;
using System.Threading;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Hashing;

namespace SkylineTogether.Core.Fields
{
    /// <summary>
    /// The last versions of every replicated cell map (ARCH §4.10.8, §4.6.4): air, ground and noise pollution,
    /// groundwater, soil water, natural resources, land value, attractiveness, telecom, population and availability
    /// grids, traffic ambience (<c>CellMapSystem&lt;T&gt;.GetMap</c>, R07 §8). One ring serves all peers.
    /// <para>
    /// <b>Host</b>: the channel copies a changed map in with <see cref="Publish"/> (identical content keeps the current
    /// version) and builds each peer's record with <see cref="WriteDelta"/> from the version that peer last
    /// acknowledged: the XOR delta when that version is still in the ring (and has the same size), otherwise a
    /// keyframe. Version numbers start at 1 and are never reused by one ring, so an acknowledgement can never select
    /// the wrong base. <b>Client</b>: a ring of the same map count holds the host's versions;
    /// <see cref="ApplyDelta"/> applies a record and keeps the last <see cref="MaxVersions"/> versions, so a delta
    /// against any recently acknowledged version applies even while newer records are in flight;
    /// <see cref="CopyCurrent"/> gives the bytes to write into the game's map (<c>GetMap(readOnly: false)</c>, copy,
    /// <c>AddWriter</c>). The client declares each map's real size with <see cref="SetExpectedLength"/>, which bounds
    /// what a record can make it allocate, and resets its ring (<see cref="Clear"/>) when it joins a new session.
    /// </para>
    /// <para>
    /// Record format (deterministic):
    /// <code>
    /// u8 version (1) | u8 flags (bit 0 keyframe) | varuint mapIndex | varuint fromVersion (0 for a keyframe)
    /// | varuint toVersion | varuint byteCount | u32 payloadLength | payload = XorRle delta (against zeros for a keyframe)
    /// </code>
    /// The payload length lets a client skip records it cannot use without decoding them. The record is not
    /// compressed here; the epoch message that carries it is (ARCH §4.6.5). Thread-safe (one internal lock).
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class CellMapVersionRing
    {
        /// <summary>Format version of the records.</summary>
        public const byte FormatVersion = 1;

        /// <summary>Largest map in bytes (the largest vanilla map, groundwater, has 256² × 6 bytes = 384 KiB).</summary>
        public const int MaxMapBytes = 16 * 1024 * 1024;

        /// <summary>Largest number of maps of one ring.</summary>
        public const int MaxMapCount = 1024;

        /// <summary>Largest number of versions kept per map.</summary>
        public const int MaxRingVersions = 64;

        private const byte FlagKeyframe = 1;

        private sealed class Version
        {
            public int Number;                       // 0 = slot unused
            public byte[] Data = Array.Empty<byte>(); // capacity may exceed Length (arrays are reused)
            public int Length;
            public ulong Digest;
        }

        private sealed class MapRing
        {
            public readonly Version[] Slots;
            public int Current = -1;                 // slot index of the current version, -1 = none
            public int Issued;                       // highest version number this ring ever stored for the map
            public int ExpectedLength = -1;          // client: the only accepted map size, -1 = any up to MaxMapBytes
            public long SizeMismatches;              // client: records of this map skipped for their size (log throttling)

            public MapRing(int versions)
            {
                Slots = new Version[versions];
                for (int i = 0; i < versions; i++) Slots[i] = new Version();
            }
        }

        private readonly object _sync = new object();
        private readonly MapRing[] _maps;
        private CellMapRingStats _stats;
        private long _unknownMap;   // counted outside the lock

        public CellMapVersionRing(int mapCount, int maxVersions = 4)
        {
            if (mapCount <= 0 || mapCount > MaxMapCount)
            {
                throw new ArgumentOutOfRangeException(nameof(mapCount), mapCount, "a ring has 1 to " + MaxMapCount + " maps");
            }
            if (maxVersions <= 0 || maxVersions > MaxRingVersions)
            {
                throw new ArgumentOutOfRangeException(nameof(maxVersions), maxVersions, "a ring keeps 1 to " + MaxRingVersions + " versions");
            }
            _maps = new MapRing[mapCount];
            for (int i = 0; i < mapCount; i++) _maps[i] = new MapRing(maxVersions);
            MaxVersions = maxVersions;
        }

        /// <summary>Number of maps.</summary>
        public int MapCount => _maps.Length;

        /// <summary>Versions kept per map (the design uses four).</summary>
        public int MaxVersions { get; }

        /// <summary>
        /// Optional log (category <see cref="LogCategory.Fields"/>; null keeps the ring silent): why a peer needed a
        /// keyframe (debug) and why a client skipped a record (missing base: debug; unknown map or foreign map size:
        /// warning on the 1st, 2nd, 4th, 8th, ... occurrence, because such a mismatch repeats with every record).
        /// Messages are written outside the internal lock.
        /// </summary>
        public ILog Log { get; set; }

        /// <summary>
        /// Host: stores <c>data[0 .. count)</c> as the new current version of the map and returns its number; when the
        /// bytes equal the current version, nothing is stored and the current number is returned.
        /// </summary>
        public int Publish(int mapIndex, byte[] data, int count)
        {
            MapRing map = Map(mapIndex);
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (count < 0 || count > data.Length) throw new ArgumentOutOfRangeException(nameof(count), count, "count must lie in [0, data.Length]");
            if (count > MaxMapBytes) throw new ArgumentOutOfRangeException(nameof(count), count, "a map has at most " + MaxMapBytes + " bytes");
            ulong digest = XxHash64.Hash(data, 0, count);
            lock (_sync)
            {
                if (map.Current >= 0)
                {
                    Version current = map.Slots[map.Current];
                    if (current.Length == count && current.Digest == digest && SameBytes(current.Data, data, count)) return current.Number;
                }
                int number = checked(map.Issued + 1);
                int slot = map.Current < 0 ? 0 : (map.Current + 1) % map.Slots.Length;
                Version target = map.Slots[slot];
                EnsureCapacity(target, count);
                Buffer.BlockCopy(data, 0, target.Data, 0, count);
                target.Number = number;
                target.Length = count;
                target.Digest = digest;
                map.Current = slot;
                map.Issued = number;
                _stats.Versions++;
                return number;
            }
        }

        /// <summary>
        /// Host: appends the record that brings a peer from <paramref name="fromVersion"/> (the version it last
        /// acknowledged; 0 = none) to the current version. A version that left the ring, never existed or has another
        /// size yields a keyframe (<paramref name="keyframe"/> = true). Returns false, writing nothing, when no version
        /// was published yet or the peer is already current.
        /// </summary>
        public bool WriteDelta(int mapIndex, int fromVersion, ByteWriter output, out bool keyframe)
        {
            MapRing map = Map(mapIndex);
            if (output == null) throw new ArgumentNullException(nameof(output));
            keyframe = false;
            string keyframeReason = null;
            int currentNumber;
            lock (_sync)
            {
                if (map.Current < 0) return false;
                Version current = map.Slots[map.Current];
                currentNumber = current.Number;
                if (fromVersion == current.Number) return false;
                Version basis = fromVersion > 0 && fromVersion < current.Number ? Find(map, fromVersion) : null;
                if (basis == null)
                {
                    keyframeReason = fromVersion <= 0 ? "the peer holds no version"
                                   : fromVersion > current.Number ? "the peer acknowledged a version this ring never issued"
                                   : "the peer's version left the ring";
                }
                else if (basis.Length != current.Length)
                {
                    keyframeReason = "the map size changed from " + Num(basis.Length) + " to " + Num(current.Length) + " bytes";
                    basis = null;
                }
                keyframe = basis == null;
                int recordStart = output.Length;
                output.WriteByte(FormatVersion);
                output.WriteByte(keyframe ? FlagKeyframe : (byte)0);
                output.WriteVarUInt32((uint)mapIndex);
                output.WriteVarUInt32(keyframe ? 0u : (uint)fromVersion);
                output.WriteVarUInt32((uint)current.Number);
                output.WriteVarUInt32((uint)current.Length);
                int lengthPosition = output.Reserve(4);
                int payloadStart = output.Length;
                XorRle.EncodeDelta(keyframe ? null : basis.Data, current.Data, current.Length, output);
                output.PatchUInt32(lengthPosition, (uint)(output.Length - payloadStart));
                if (keyframe) _stats.Keyframes++;
                else _stats.Deltas++;
                _stats.BytesWritten += output.Length - recordStart;
            }
            ILog log = Log;
            if (keyframeReason != null && log != null && log.IsEnabled(LogLevel.Debug))
            {
                log.Write(LogLevel.Debug, LogCategory.Fields, "cell map " + Num(mapIndex) + ": keyframe of version " + Num(currentNumber)
                                                              + " for a peer at version " + Num(fromVersion) + " (" + keyframeReason + ")");
            }
            return true;
        }

        /// <summary>The current version of the map (0 = none).</summary>
        public int CurrentVersion(int mapIndex)
        {
            MapRing map = Map(mapIndex);
            lock (_sync)
            {
                return map.Current < 0 ? 0 : map.Slots[map.Current].Number;
            }
        }

        /// <summary>Byte length of the current version (0 = none).</summary>
        public int CurrentLength(int mapIndex)
        {
            MapRing map = Map(mapIndex);
            lock (_sync)
            {
                return map.Current < 0 ? 0 : map.Slots[map.Current].Length;
            }
        }

        /// <summary>xxHash64 (seed 0) of the current version's bytes (0 = none); host and client compare it in audits.</summary>
        public ulong CurrentDigest(int mapIndex)
        {
            MapRing map = Map(mapIndex);
            lock (_sync)
            {
                return map.Current < 0 ? 0 : map.Slots[map.Current].Digest;
            }
        }

        /// <summary>True when <paramref name="version"/> of the map is still held (a delta from it can be built or applied).</summary>
        public bool Contains(int mapIndex, int version)
        {
            MapRing map = Map(mapIndex);
            lock (_sync)
            {
                return version > 0 && Find(map, version) != null;
            }
        }

        /// <summary>
        /// Copies the current version into <paramref name="destination"/> at <paramref name="offset"/> and returns its
        /// length (0 when no version exists).
        /// </summary>
        public int CopyCurrent(int mapIndex, byte[] destination, int offset)
        {
            MapRing map = Map(mapIndex);
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            lock (_sync)
            {
                if (map.Current < 0) return 0;
                Version current = map.Slots[map.Current];
                if (offset < 0 || offset > destination.Length - current.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(offset), offset, "the destination has no room for " + current.Length + " bytes");
                }
                Buffer.BlockCopy(current.Data, 0, destination, offset, current.Length);
                return current.Length;
            }
        }

        /// <summary>
        /// Client: applies one record written by <see cref="WriteDelta"/> and reports the map and version it names. A
        /// keyframe replaces the map (unless it repeats the current version) and drops the older versions; a delta
        /// applies when it is newer than the current version and its base is held. Records that are not applied are
        /// skipped completely, so the reader always ends behind the record. Throws <see cref="DecodeException"/> on a
        /// malformed record, in which case the ring is unchanged.
        /// </summary>
        public CellMapApplyResult ApplyDelta(ByteReader input, out int mapIndex, out int version)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            mapIndex = -1;
            version = 0;
            byte format = input.ReadByte();
            if (format != FormatVersion) throw new DecodeException("cell-map record: unknown format version " + format.ToString(CultureInfo.InvariantCulture));
            byte flags = input.ReadByte();
            if ((flags & ~FlagKeyframe) != 0) throw new DecodeException("cell-map record: unknown flags 0x" + flags.ToString("x2", CultureInfo.InvariantCulture));
            bool isKeyframe = (flags & FlagKeyframe) != 0;
            uint map = input.ReadVarUInt32();
            uint from = input.ReadVarUInt32();
            uint to = input.ReadVarUInt32();
            uint count = input.ReadVarUInt32();
            uint payloadLength = input.ReadUInt32();
            if (to == 0 || to > int.MaxValue) throw new DecodeException("cell-map record: invalid version " + to);
            if (isKeyframe ? from != 0 : (from == 0 || from >= to))
            {
                throw new DecodeException("cell-map record: invalid base version " + from + " for version " + to + (isKeyframe ? " (keyframe)" : string.Empty));
            }
            if (count > MaxMapBytes) throw new DecodeException("cell-map record: map of " + count + " bytes exceeds the limit of " + MaxMapBytes);
            if (payloadLength > (uint)input.Remaining)
            {
                throw new DecodeException("cell-map record: payload of " + payloadLength + " bytes overruns the input (" + input.Remaining + " left)");
            }
            ArraySegment<byte> payload = input.ReadSegment((int)payloadLength);
            version = (int)to;
            mapIndex = map <= int.MaxValue ? (int)map : -1;
            if (map >= (uint)_maps.Length)
            {
                long unknown = Interlocked.Increment(ref _unknownMap);
                if (IsLogStep(unknown))
                {
                    Log.Warn(LogCategory.Fields, "cell-map record for map " + map.ToString(CultureInfo.InvariantCulture) + " skipped: this ring has "
                                                 + Num(_maps.Length) + " maps (the peers' map registries differ; " + Count(unknown) + ")");
                }
                return CellMapApplyResult.UnknownMap;
            }
            MapRing ring = _maps[map];
            int length = (int)count;
            CellMapApplyResult skipped = CellMapApplyResult.Applied;
            int expectedLength = -1, heldVersion = 0;
            long mismatches = 0;
            lock (_sync)
            {
                Version current = ring.Current < 0 ? null : ring.Slots[ring.Current];
                int currentNumber = current == null ? 0 : current.Number;
                Version basis = null;
                if (ring.ExpectedLength >= 0 && length != ring.ExpectedLength)
                {
                    _stats.SizeMismatch++;
                    mismatches = ++ring.SizeMismatches;
                    skipped = CellMapApplyResult.SizeMismatch;
                    expectedLength = ring.ExpectedLength;
                }
                else if (isKeyframe)
                {
                    if (current != null && currentNumber == version && current.Length == length)
                    {
                        _stats.Stale++;
                        return CellMapApplyResult.Stale;
                    }
                }
                else
                {
                    if (version <= currentNumber)
                    {
                        _stats.Stale++;
                        return CellMapApplyResult.Stale;
                    }
                    basis = Find(ring, (int)from);
                    if (basis == null || basis.Length != length)
                    {
                        _stats.MissingBase++;
                        skipped = CellMapApplyResult.MissingBase;
                        heldVersion = currentNumber;
                    }
                }
                if (skipped == CellMapApplyResult.Applied)
                {
                    ApplyLocked(ring, payload, isKeyframe, basis, version, length);
                    return CellMapApplyResult.Applied;
                }
            }
            if (skipped == CellMapApplyResult.SizeMismatch)
            {
                if (IsLogStep(mismatches))
                {
                    Log.Warn(LogCategory.Fields, "cell-map record for map " + Num(mapIndex) + " skipped: it has " + Num(length) + " bytes, the local map "
                                                 + Num(expectedLength) + " (the peers' maps differ; " + Count(mismatches) + ")");
                }
            }
            else
            {
                ILog log = Log;
                if (log != null && log.IsEnabled(LogLevel.Debug))
                {
                    log.Write(LogLevel.Debug, LogCategory.Fields, "cell-map delta " + from.ToString(CultureInfo.InvariantCulture) + " -> " + Num(version)
                                                                  + " for map " + Num(mapIndex) + " skipped: base not held (current version "
                                                                  + Num(heldVersion) + "); a keyframe is needed");
                }
            }
            return skipped;
        }

        /// <summary>The state change of <see cref="ApplyDelta"/> for a record that passed every check; called under the lock.</summary>
        private void ApplyLocked(MapRing ring, ArraySegment<byte> payload, bool isKeyframe, Version basis, int version, int length)
        {
            // check the payload completely before any slot changes, so a malformed record leaves the ring intact
            XorRle.ValidateExact(payload.Array, payload.Offset, payload.Count, length);
            int slot = ring.Current < 0 ? 0 : (ring.Current + 1) % ring.Slots.Length;
            Version target = ring.Slots[slot];
            if (isKeyframe)
            {
                EnsureCapacity(target, length);
                XorRle.ApplyExact(payload.Array, payload.Offset, payload.Count, target.Data, length, overwrite: true);
            }
            else
            {
                // with a full ring the slot after the current one can be the base itself: then the delta applies in place
                if (!ReferenceEquals(target, basis))
                {
                    EnsureCapacity(target, length);
                    Buffer.BlockCopy(basis.Data, 0, target.Data, 0, length);
                }
                XorRle.ApplyExact(payload.Array, payload.Offset, payload.Count, target.Data, length, overwrite: false);
            }
            target.Number = version;
            target.Length = length;
            target.Digest = XxHash64.Hash(target.Data, 0, length);
            ring.Current = slot;
            if (version > ring.Issued) ring.Issued = version;
            if (isKeyframe)
            {
                // a keyframe is a fresh baseline: older versions may come from another lineage (a host restart)
                for (int i = 0; i < ring.Slots.Length; i++)
                {
                    if (i == slot) continue;
                    ring.Slots[i].Number = 0;
                    ring.Slots[i].Length = 0;
                }
            }
            _stats.Applied++;
        }

        /// <summary>
        /// Client: accepts only records of exactly <paramref name="length"/> bytes for the map (the size of the local
        /// <c>CellMapSystem</c>'s map, <c>textureSize² × sizeof(T)</c>), so a record from a peer whose maps differ is
        /// skipped as <see cref="CellMapApplyResult.SizeMismatch"/> before anything is allocated. -1 accepts any size up
        /// to <see cref="MaxMapBytes"/> (the default).
        /// </summary>
        public void SetExpectedLength(int mapIndex, int length)
        {
            MapRing map = Map(mapIndex);
            if (length < -1 || length > MaxMapBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(length), length, "expected -1 (any) or a size up to " + MaxMapBytes + " bytes");
            }
            lock (_sync)
            {
                map.ExpectedLength = length;
            }
        }

        /// <summary>A snapshot of the counters since the ring was created.</summary>
        public CellMapRingStats GetStats()
        {
            lock (_sync)
            {
                CellMapRingStats stats = _stats;
                stats.UnknownMap = Interlocked.Read(ref _unknownMap);
                return stats;
            }
        }

        /// <summary>Forgets every version of one map (arrays are kept for reuse); later versions continue the numbering.</summary>
        public void Reset(int mapIndex)
        {
            MapRing map = Map(mapIndex);
            lock (_sync)
            {
                ResetMap(map);
            }
        }

        /// <summary>Forgets every version of every map (session reset or resync).</summary>
        public void Clear()
        {
            lock (_sync)
            {
                foreach (MapRing map in _maps) ResetMap(map);
            }
        }

        private MapRing Map(int mapIndex)
        {
            if (mapIndex < 0 || mapIndex >= _maps.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(mapIndex), mapIndex, "the ring has " + _maps.Length + " maps");
            }
            return _maps[mapIndex];
        }

        private static void ResetMap(MapRing map)
        {
            foreach (Version v in map.Slots)
            {
                v.Number = 0;
                v.Length = 0;
                v.Digest = 0;
            }
            map.Current = -1;
        }

        private static Version Find(MapRing map, int number)
        {
            foreach (Version v in map.Slots)
            {
                if (v.Number == number) return v;
            }
            return null;
        }

        private static void EnsureCapacity(Version v, int length)
        {
            if (v.Data.Length < length) v.Data = new byte[length];
        }

        private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

        /// <summary>Repeated warnings are logged on the 1st, 2nd, 4th, 8th, ... occurrence only.</summary>
        private static bool IsLogStep(long occurrence) => occurrence > 0 && (occurrence & (occurrence - 1)) == 0;

        private static string Count(long occurrence) => occurrence == 1 ? "first occurrence" : "occurrence " + occurrence.ToString(CultureInfo.InvariantCulture);

        private static unsafe bool SameBytes(byte[] a, byte[] b, int count)
        {
            if (count == 0) return true;
            fixed (byte* pa = a)
            fixed (byte* pb = b)
            {
                int i = 0;
                for (; i + 8 <= count; i += 8)
                {
                    if (*(ulong*)(pa + i) != *(ulong*)(pb + i)) return false;
                }
                for (; i < count; i++)
                {
                    if (pa[i] != pb[i]) return false;
                }
                return true;
            }
        }
    }
}
