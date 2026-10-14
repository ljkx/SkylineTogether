using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Fields;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Fields
{
    /// <summary>Clamping client paint patches, map digests and the per-map resync request.</summary>
    public sealed class CellMapTrustTests
    {
        private static void Put(byte[] cells, int at, int value)
        {
            cells[at] = (byte)value;
            cells[at + 1] = (byte)(value >> 8);
        }

        private static int GetU(byte[] cells, int at) => cells[at] | (cells[at + 1] << 8);

        private static int GetS(byte[] cells, int at) => (short)GetU(cells, at);

        [Test]
        public void ResourceValuesOutsideTheBrushRangeAreClamped()
        {
            // two 16-byte resource cells: fertility, ore, oil base at 0, 4, 8 (ushort, 0..10000)
            var cells = new byte[32];
            Put(cells, 0, 65535);
            Put(cells, 4, 9000);
            Put(cells, 8, 10001);
            Put(cells, 16, 0);
            Put(cells, 20, 40000);
            var rules = new[] { new CellFieldClamp(0, false, 0, 10000), new CellFieldClamp(4, false, 0, 10000), new CellFieldClamp(8, false, 0, 10000) };
            Assert.Equal(3, CellMapMessage.ClampFields(cells, 2, 16, rules));
            Assert.Equal(10000, GetU(cells, 0));
            Assert.Equal(9000, GetU(cells, 4));
            Assert.Equal(10000, GetU(cells, 8));
            Assert.Equal(0, GetU(cells, 16));
            Assert.Equal(10000, GetU(cells, 20));
        }

        [Test]
        public void NegativeSignedValuesAreClampedToo()
        {
            // a 6-byte groundwater cell: amount (short) at 0, polluted at 2 (not painted), max at 4
            var cells = new byte[6];
            Put(cells, 0, -5 & 0xFFFF);
            Put(cells, 2, 12345);
            Put(cells, 4, 20000);
            var rules = new[] { new CellFieldClamp(0, true, 0, 10000), new CellFieldClamp(4, true, 0, 10000) };
            Assert.Equal(2, CellMapMessage.ClampFields(cells, 1, 6, rules));
            Assert.Equal(0, GetS(cells, 0));
            Assert.Equal(12345, GetS(cells, 2), "unpainted fields are untouched");
            Assert.Equal(10000, GetS(cells, 4));
        }

        [Test]
        public void APatchKeepsTheHostsOwnOutOfRangeValuesOfUnpaintedCells()
        {
            var layout = new CellMapLayout(4, 4, 2, 4);
            var map = new byte[layout.MapBytes];
            Put(map, 0, 30000); // cell (0,0): a map-generated value above the brush range
            var patch = new byte[2 * 2];
            Put(patch, 0, 30000); // not painted: the host's value comes back unchanged
            Put(patch, 2, 50000); // cell (1,0): painted out of range
            var rules = new[] { new CellFieldClamp(0, false, 0, 10000) };
            Assert.Equal(1, CellMapMessage.ClampPatch(layout, map, 0, 0, 2, 1, patch, rules));
            Assert.Equal(30000, GetU(patch, 0));
            Assert.Equal(10000, GetU(patch, 2));
            Assert.Equal(1, CellMapMessage.MergeMasked(layout, map, 0, 0, 2, 1, patch, new byte[] { 0xFF, 0xFF }));
            Assert.Equal(30000, GetU(map, 0));
            Assert.Equal(10000, GetU(map, 2));
        }

        [Test]
        public void ClampRulesMustFitTheCell()
        {
            var cells = new byte[4];
            Assert.Throws<System.ArgumentException>(() => CellMapMessage.ClampFields(cells, 1, 4, new[] { new CellFieldClamp(3, false, 0, 1) }));
            Assert.Throws<System.ArgumentException>(() => CellMapMessage.ClampFields(cells, 1, 4, new[] { new CellFieldClamp(0, false, 5, 1) }));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => CellMapMessage.ClampFields(cells, 2, 4, new CellFieldClamp[0]));
            Assert.Equal(0, CellMapMessage.ClampFields(cells, 1, 4, null));
        }

        [Test]
        public void DigestsDifferWhenATileDiffers()
        {
            var a = new ulong[] { 3, 5, 7, 9 };
            var b = new ulong[] { 3, 5, 7, 9 };
            Assert.Equal(CellMapMessage.Digest(a, 4), CellMapMessage.Digest(b, 4));
            b[2] = 8;
            Assert.NotEqual(CellMapMessage.Digest(a, 4), CellMapMessage.Digest(b, 4));
            var swapped = new ulong[] { 5, 3, 7, 9 };
            Assert.NotEqual(CellMapMessage.Digest(a, 4), CellMapMessage.Digest(swapped, 4), "order matters");
        }

        [Test]
        public void APeerStateHasADigestOnlyWhenItIsUpToDate()
        {
            var layout = new CellMapLayout(32, 32, 2, 16);
            var map = new byte[layout.MapBytes];
            for (int i = 0; i < map.Length; i++) map[i] = (byte)(i * 7);
            var hashes = new ulong[layout.TileCount];
            layout.HashTiles(map, hashes, new byte[layout.MaxTileBytes]);
            var state = new CellMapPeerState(layout.TileCount);
            Assert.False(state.TryDigest(hashes, out _), "nothing sent yet");
            for (int t = 0; t < layout.TileCount - 1; t++) state.MarkSent(t, hashes[t]);
            Assert.False(state.TryDigest(hashes, out _), "one tile still to send");
            state.MarkSent(layout.TileCount - 1, hashes[layout.TileCount - 1]);
            Assert.True(state.TryDigest(hashes, out ulong digest));
            // the client hashes its own copy the same way and compares
            var client = (byte[])map.Clone();
            var clientHashes = new ulong[layout.TileCount];
            layout.HashTiles(client, clientHashes, new byte[layout.MaxTileBytes]);
            Assert.Equal(digest, CellMapMessage.Digest(clientHashes, layout.TileCount));
            client[100] ^= 1;
            layout.HashTiles(client, clientHashes, new byte[layout.MaxTileBytes]);
            Assert.NotEqual(digest, CellMapMessage.Digest(clientHashes, layout.TileCount));
        }

        [Test]
        public void DigestAndResyncMessagesRoundTrip()
        {
            var layout = new CellMapLayout(64, 64, 4, 16);
            uint id = CellMapMessage.MapId("NaturalResources");
            var w = new ByteWriter(64);
            CellMapMessage.WriteDigest(w, id, layout, 0x1122334455667788UL);
            var r = new ByteReader(w.ToArray(), 0, w.Length);
            CellMapMessage.Header h = CellMapMessage.ReadHeader(r);
            Assert.Equal(CellMapMessage.TypeDigest, h.Type);
            Assert.Equal(id, h.MapId);
            Assert.True(h.Matches(layout));
            Assert.Equal(0x1122334455667788UL, CellMapMessage.ReadDigest(r));

            w.Reset();
            CellMapMessage.WriteResync(w, id, layout);
            r = new ByteReader(w.ToArray(), 0, w.Length);
            h = CellMapMessage.ReadHeader(r);
            Assert.Equal(CellMapMessage.TypeResync, h.Type);
            Assert.True(r.IsAtEnd);
        }

        [Test]
        public void ADigestWithTrailingBytesIsMalformed()
        {
            var layout = new CellMapLayout(64, 64, 4, 16);
            var w = new ByteWriter(64);
            CellMapMessage.WriteDigest(w, 7, layout, 1);
            w.WriteByte(0);
            var r = new ByteReader(w.ToArray(), 0, w.Length);
            CellMapMessage.ReadHeader(r);
            Assert.Throws<DecodeException>(() => CellMapMessage.ReadDigest(r));
        }

        [Test]
        public void UnknownMessageTypesAreStillRejected()
        {
            var layout = new CellMapLayout(64, 64, 4, 16);
            var w = new ByteWriter(64);
            CellMapMessage.WriteResync(w, 7, layout);
            byte[] data = w.ToArray();
            data[1] = 5;
            Assert.Throws<DecodeException>(() => CellMapMessage.ReadHeader(new ByteReader(data, 0, data.Length)));
            data[1] = 0;
            Assert.Throws<DecodeException>(() => CellMapMessage.ReadHeader(new ByteReader(data, 0, data.Length)));
        }

        [Test]
        public void TheHostServesOneResyncPerPeerAndMapPerInterval()
        {
            var gate = new CellMapResyncGate();
            Assert.True(gate.TryAcceptHost(1, 10, 0));
            Assert.False(gate.TryAcceptHost(1, 10, 5));
            Assert.True(gate.TryAcceptHost(1, 11, 5), "another map");
            Assert.True(gate.TryAcceptHost(2, 10, 5), "another peer");
            Assert.True(gate.TryAcceptHost(1, 10, CellMapResyncGate.HostInterval));
            gate.ForgetPeer(1);
            Assert.True(gate.TryAcceptHost(1, 11, 6), "a peer that left starts over");
        }

        [Test]
        public void TheClientBacksOffWhileAMapKeepsDiffering()
        {
            var gate = new CellMapResyncGate();
            Assert.True(gate.TryRequestClient(10, 0));
            Assert.Equal(CellMapResyncGate.MinClientInterval, gate.ClientInterval(10));
            Assert.False(gate.TryRequestClient(10, 10));
            Assert.True(gate.TryRequestClient(10, 30));
            Assert.Equal(CellMapResyncGate.MinClientInterval * 2, gate.ClientInterval(10));
            for (int i = 0; i < 10; i++) gate.TryRequestClient(10, 100000 * (i + 1));
            Assert.Equal(CellMapResyncGate.MaxClientInterval, gate.ClientInterval(10));
            gate.ClientMatched(10);
            Assert.Equal(0.0, gate.ClientInterval(10));
        }
    }
}
