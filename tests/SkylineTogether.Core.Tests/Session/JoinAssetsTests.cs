using System;
using System.Collections.Generic;
using System.IO;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>polish: package-bundled render prefabs travel in front of the join snapshot.</summary>
    public sealed class JoinAssetsTests
    {
        private static List<JoinAssetEntry> Sample() => new List<JoinAssetEntry>
        {
            new JoinAssetEntry { Kind = JoinAssetKind.Prefab, TypeName = "Colossal.IO.AssetDatabase.PrefabAsset", Name = "My Climate", Guid = "0123456789abcdef0123456789abcdef", Data = new byte[] { 1, 2, 3 } },
            new JoinAssetEntry { Kind = JoinAssetKind.Texture, TypeName = "Colossal.IO.AssetDatabase.TextureAsset", Name = "RockDiffuse", Guid = "fedcba9876543210fedcba9876543210", Data = new byte[5000] },
        };

        private static byte[] Blob(byte[] prefix, byte[] save)
        {
            var b = new byte[prefix.Length + save.Length];
            Buffer.BlockCopy(prefix, 0, b, 0, prefix.Length);
            Buffer.BlockCopy(save, 0, b, prefix.Length, save.Length);
            return b;
        }

        private static (MemoryStream Save, JoinBlobSplitter Splitter) Feed(byte[] blob, int chunk)
        {
            var save = new MemoryStream();
            var splitter = new JoinBlobSplitter(new NonClosing(save));
            for (int i = 0; i < blob.Length; i += chunk) splitter.Write(blob, i, Math.Min(chunk, blob.Length - i));
            splitter.Dispose();
            return (save, splitter);
        }

        [Test]
        public void TheSplitterSeparatesBundleAndSaveForAnyChunking()
        {
            byte[] prefix = JoinAssetBundle.Write(Sample());
            var saveData = new byte[20000];
            new Random(5).NextBytes(saveData);
            byte[] blob = Blob(prefix, saveData);
            foreach (int chunk in new[] { 1, 3, 9, 10, 4096, blob.Length })
            {
                (MemoryStream save, JoinBlobSplitter s) = Feed(blob, chunk);
                Assert.Equal(saveData.Length, (int)save.Length, "chunk " + chunk);
                Assert.True(save.ToArray().AsSpan().SequenceEqual(saveData), "save bytes, chunk " + chunk);
                Assert.Equal(2, s.Bundle.Count);
                Assert.Equal("My Climate", s.Bundle[0].Name);
                Assert.Equal(JoinAssetKind.Texture, s.Bundle[1].Kind);
                Assert.Equal(5000, s.Bundle[1].Data.Length);
                Assert.Equal("fedcba9876543210fedcba9876543210", s.Bundle[1].Guid);
            }
        }

        [Test]
        public void AnEmptyBundleAndAPlainSaveBothWork()
        {
            var saveData = new byte[] { 9, 8, 7, 6, 5, 4, 3, 2, 1, 0, 11, 12 };
            (MemoryStream save, JoinBlobSplitter s) = Feed(Blob(JoinAssetBundle.Write(null), saveData), 2);
            Assert.Equal(0, s.Bundle.Count);
            Assert.True(save.ToArray().AsSpan().SequenceEqual(saveData));
            // an older host: no container, every byte is save data
            (MemoryStream plain, JoinBlobSplitter p) = Feed(saveData, 4);
            Assert.Null(p.Bundle);
            Assert.True(plain.ToArray().AsSpan().SequenceEqual(saveData));
            // shorter than a header
            (MemoryStream tiny, _) = Feed(new byte[] { 1, 2, 3 }, 1);
            Assert.Equal(3, (int)tiny.Length);
        }

        [Test]
        public void MalformedBundlesAreRefused()
        {
            byte[] prefix = JoinAssetBundle.Write(Sample());
            byte[] badVersion = (byte[])prefix.Clone();
            badVersion[4] = 99;
            Assert.Throws<DecodeException>(() => Feed(badVersion, 64));
            byte[] truncated = (byte[])prefix.Clone();
            truncated[5] = 3; // body length too short for its entries
            truncated[6] = 0;
            truncated[7] = 0;
            truncated[8] = 0;
            Assert.Throws<DecodeException>(() => Feed(truncated, 64));
        }

        [Test]
        public void RenderPrefabRefsRoundTrip()
        {
            string v = RenderPrefabRef.Encode("ClimatePrefab", "My|Climate", "0123456789abcdef0123456789abcdef");
            Assert.True(RenderPrefabRef.TryDecode(v, out string type, out string name, out string hash));
            Assert.Equal("ClimatePrefab", type);
            Assert.Equal("My|Climate", name);
            Assert.Equal("0123456789abcdef0123456789abcdef", hash);
            Assert.True(RenderPrefabRef.TryDecode(RenderPrefabRef.Encode("WaterRenderSettingsPrefab", "Default", ""), out _, out _, out hash));
            Assert.Equal("", hash);
            Assert.False(RenderPrefabRef.TryDecode("nonsense", out _, out _, out _));
            Assert.False(RenderPrefabRef.TryDecode("|x|", out _, out _, out _));
            Assert.False(RenderPrefabRef.TryDecode(null, out _, out _, out _));
        }

        /// <summary>Keeps the memory stream readable after the splitter disposed it.</summary>
        private sealed class NonClosing : Stream
        {
            private readonly Stream _s;
            public NonClosing(Stream s) { _s = s; }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => _s.Length;
            public override long Position { get => _s.Position; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => _s.Write(buffer, offset, count);
        }
    }
}
