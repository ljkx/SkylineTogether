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
                return;
            }
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
