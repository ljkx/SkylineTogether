using System;
using System.Collections.Generic;
using System.Globalization;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// The payload of <see cref="ControlKind.SnapshotManifest"/>: a <see cref="SnapshotManifest"/> (ARCH §7.1 step 6, §7.4).
    /// <code>
    /// u32 snapshotId | varint64 size (≤ 2 GiB) | u64 digestLo | u64 digestHi | varint chunkSize (1 ... 256 KiB) | varint epoch
    /// | u32 frameIndex | u32 hostBuildOrderNext | string gameModeName (≤ 256 B)
    /// | list renderPrefabIds (≤ 64, each ≤ 1024 B) | list referencedContent (≤ 4096, each ≤ 512 B)
    /// | familyCounts: varint (n + 1; 0 = null) | n × (u8 family | varint count)   (families strictly ascending)
    /// [ | assetSources: varint n | n × (u8 kind | string name | string platformId | varint prefabs | string example) ]
    /// </code>
    /// Lists are <c>varint (count + 1)</c> with 0 = null. The blob size is capped at 2 GiB (ARCH §14.5). The family
    /// counts are written in ascending family order, so equal manifests give equal bytes.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class SnapshotManifestCodec
    {
        public const long MaxSnapshotBytes = 2L * 1024 * 1024 * 1024;
        public const int MaxGameModeBytes = 256;
        public const int MaxRenderPrefabIds = 64;
        public const int MaxReferencedContent = 4096;
        public const int MaxPrefabIdBytes = 1024;
        public const int MaxContentBytes = 512;
        /// <summary>Mod compatibility: asset sources listed at most (the host keeps the ones with the most prefabs).</summary>
        public const int MaxAssetSources = 512;
        private const int FamilyCountLimit = (int)Family.Generic + 1;

        public static void Write(ByteWriter w, SnapshotManifest m)
        {
            MessageFields.CheckMessage(w, nameof(w));
            MessageFields.CheckMessage(m, nameof(m));
            if (m.Size < 0 || m.Size > MaxSnapshotBytes) throw new ArgumentOutOfRangeException(nameof(m), "snapshot size outside 0 ... 2 GiB");
            if (m.ChunkSize < 1 || m.ChunkSize > Limits.MaxBulkChunkBytes) throw new ArgumentOutOfRangeException(nameof(m), "chunk size outside 1 ... 256 KiB");
            w.WriteUInt32(m.SnapshotId);
            w.WriteVarUInt64((ulong)m.Size);
            w.WriteUInt64(m.DigestLo);
            w.WriteUInt64(m.DigestHi);
            w.WriteVarUInt32((uint)m.ChunkSize);
            w.WriteVarUInt32(m.Epoch.Value);
            w.WriteUInt32(m.FrameIndex);
            w.WriteUInt32(m.HostBuildOrderNext);
            MessageFields.WriteString(w, m.GameModeName, MaxGameModeBytes, "GameModeName");
            MessageFields.WriteStringArray(w, m.RenderPrefabIds, MaxRenderPrefabIds, MaxPrefabIdBytes, "RenderPrefabIds");
            MessageFields.WriteStringArray(w, m.ReferencedContent, MaxReferencedContent, MaxContentBytes, "ReferencedContent");

            Dictionary<Family, int> counts = m.FamilyCounts;
            if (counts == null)
            {
                w.WriteVarUInt32(0);
            }
            else
            {
                int present = 0;
                foreach (KeyValuePair<Family, int> pair in counts)
                {
                    if (pair.Key > Family.Generic) throw new ArgumentException("undefined family in FamilyCounts", nameof(m));
                    if (pair.Value < 0) throw new ArgumentException("negative family count", nameof(m));
                    present++;
                }
                w.WriteVarUInt32((uint)present + 1);
                for (int f = 0; f < FamilyCountLimit; f++)
                {
                    if (!counts.TryGetValue((Family)f, out int count)) continue;
                    w.WriteByte((byte)f);
                    w.WriteVarUInt32((uint)count);
                }
            }
            WriteAssetSources(w, m.AssetSources);
        }

        /// <summary>
        /// Mod compatibility: the optional trailing list of asset sources, written only when there is one (so a manifest
        /// without it keeps its bytes): <c>varint n | n × (u8 kind | string name | string platformId | varint prefabs |
        /// string example)</c>.
        /// </summary>
        private static void WriteAssetSources(ByteWriter w, List<Modding.AssetSourceUse> sources)
        {
            if (sources == null || sources.Count == 0) return;
            MessageFields.WriteCount(w, sources.Count, MaxAssetSources, "AssetSources");
            foreach (Modding.AssetSourceUse u in sources)
            {
                if (u?.Source == null) throw new ArgumentException("null asset source", nameof(sources));
                if (u.Prefabs < 0) throw new ArgumentException("negative prefab count", nameof(sources));
                w.WriteByte((byte)u.Source.Kind);
                MessageFields.WriteString(w, u.Source.Name, Modding.AssetSource.MaxNameBytes, "AssetSources.Name");
                MessageFields.WriteString(w, u.Source.PlatformId, Modding.AssetSource.MaxPlatformIdBytes, "AssetSources.PlatformId");
                w.WriteVarUInt32((uint)u.Prefabs);
                MessageFields.WriteString(w, u.Example, Modding.AssetSourceUse.MaxExampleBytes, "AssetSources.Example");
            }
        }

        private static List<Modding.AssetSourceUse> ReadAssetSources(ByteReader r)
        {
            if (r.IsAtEnd) return null;
            int n = MessageFields.ReadCount(r, MaxAssetSources, 5, "SnapshotManifest.AssetSources");
            if (n == 0) throw new DecodeException("SnapshotManifest: an empty asset source list is never written");
            var list = new List<Modding.AssetSourceUse>(n);
            for (int i = 0; i < n; i++)
            {
                byte kind = r.ReadByte();
                if (kind > (byte)Modding.AssetSourceKind.RuntimePrefab) throw new DecodeException("SnapshotManifest: unknown asset source kind " + kind.ToString(CultureInfo.InvariantCulture));
                var source = new Modding.AssetSource
                {
                    Kind = (Modding.AssetSourceKind)kind,
                    Name = ProtocolText.Sanitize(MessageFields.ReadString(r, Modding.AssetSource.MaxNameBytes, "SnapshotManifest.AssetSources.Name")),
                    PlatformId = ProtocolText.Sanitize(MessageFields.ReadString(r, Modding.AssetSource.MaxPlatformIdBytes, "SnapshotManifest.AssetSources.PlatformId")),
                };
                uint prefabs = r.ReadVarUInt32();
                if (prefabs > int.MaxValue) throw new DecodeException("SnapshotManifest: asset source count exceeds int.MaxValue");
                string example = ProtocolText.Sanitize(MessageFields.ReadString(r, Modding.AssetSourceUse.MaxExampleBytes, "SnapshotManifest.AssetSources.Example"));
                list.Add(new Modding.AssetSourceUse { Source = source, Prefabs = (int)prefabs, Example = example });
            }
            return list;
        }

        public static SnapshotManifest Read(ByteReader r)
        {
            MessageFields.CheckMessage(r, nameof(r));
            var m = new SnapshotManifest { SnapshotId = r.ReadUInt32() };
            ulong size = r.ReadVarUInt64();
            if (size > MaxSnapshotBytes) throw new DecodeException("SnapshotManifest: size " + WireFormat.Format(size) + " exceeds 2 GiB");
            m.Size = (long)size;
            m.DigestLo = r.ReadUInt64();
            m.DigestHi = r.ReadUInt64();
            uint chunk = r.ReadVarUInt32();
            if (chunk < 1 || chunk > Limits.MaxBulkChunkBytes) throw new DecodeException("SnapshotManifest: chunk size " + WireFormat.Format(chunk) + " outside 1 ... 256 KiB");
            m.ChunkSize = (int)chunk;
            m.Epoch = new EpochId(r.ReadVarUInt32());
            m.FrameIndex = r.ReadUInt32();
            m.HostBuildOrderNext = r.ReadUInt32();
            m.GameModeName = MessageFields.ReadString(r, MaxGameModeBytes, "SnapshotManifest.GameModeName");
            m.RenderPrefabIds = MessageFields.ReadStringArray(r, MaxRenderPrefabIds, MaxPrefabIdBytes, "SnapshotManifest.RenderPrefabIds");
            m.ReferencedContent = MessageFields.ReadStringArray(r, MaxReferencedContent, MaxContentBytes, "SnapshotManifest.ReferencedContent");

            int families = MessageFields.ReadNullableCount(r, FamilyCountLimit, 2, "SnapshotManifest.FamilyCounts");
            if (families < 0)
            {
                m.FamilyCounts = null;
                m.AssetSources = ReadAssetSources(r);
                return m;
            }
            m.FamilyCounts = new Dictionary<Family, int>(families);
            int previous = -1;
            for (int i = 0; i < families; i++)
            {
                byte family = r.ReadByte();
                if (family > (byte)Family.Generic) throw new DecodeException("SnapshotManifest: undefined family " + family.ToString(CultureInfo.InvariantCulture));
                if (family <= previous) throw new DecodeException("SnapshotManifest: family counts must be in strictly ascending family order");
                uint count = r.ReadVarUInt32();
                if (count > int.MaxValue) throw new DecodeException("SnapshotManifest: family count exceeds int.MaxValue");
                m.FamilyCounts.Add((Family)family, (int)count);
                previous = family;
            }
            m.AssetSources = ReadAssetSources(r);
            return m;
        }

        /// <summary>Reads a whole payload: <see cref="Read"/> plus a check that nothing follows.</summary>
        public static SnapshotManifest Decode(byte[] buffer, int offset, int count)
        {
            var r = new ByteReader(buffer, offset, count);
            SnapshotManifest m = Read(r);
            MessageFields.EnsureEnd(r, "SnapshotManifest");
            return m;
        }
    }
}
