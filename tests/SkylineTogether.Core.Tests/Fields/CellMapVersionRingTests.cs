using System;
using System.Collections.Generic;
using System.Threading;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Fields;
using SkylineTogether.Core.Hashing;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Fields
{
    /// <summary>CellMapVersionRing: versions, XOR deltas and keyframe fallback on the host, application on clients.</summary>
    public sealed class CellMapVersionRingTests
    {
        private const int MapBytes = 64 * 64 * 4;

        private static byte[] RandomMap(DeterministicRandom random, int bytes = MapBytes)
        {
            var map = new byte[bytes];
            random.NextBytes(map, 0, bytes);
            return map;
        }

        private static byte[] Mutate(DeterministicRandom random, byte[] map, int changes)
        {
            var copy = (byte[])map.Clone();
            for (int i = 0; i < changes; i++) copy[random.Next(copy.Length)] ^= (byte)(1 + random.Next(255));
            return copy;
        }

        private static byte[] Record(CellMapVersionRing ring, int map, int fromVersion, out bool keyframe)
        {
            var writer = new ByteWriter();
            Assert.True(ring.WriteDelta(map, fromVersion, writer, out keyframe), "a record is written");
            return writer.ToArray();
        }

        private static CellMapApplyResult Apply(CellMapVersionRing client, byte[] record, out int map, out int version)
        {
            var reader = new ByteReader(record, 0, record.Length);
            CellMapApplyResult result = client.ApplyDelta(reader, out map, out version);
            Assert.True(reader.IsAtEnd, "the reader ends behind the record (" + result + ")");
            return result;
        }

        private static byte[] Current(CellMapVersionRing ring, int map)
        {
            var bytes = new byte[ring.CurrentLength(map)];
            Assert.Equal(bytes.Length, ring.CopyCurrent(map, bytes, 0));
            return bytes;
        }

        [Test]
        public void PublishNumbersVersionsAndSkipsIdenticalContent()
        {
            var random = new DeterministicRandom(9001);
            var ring = new CellMapVersionRing(3);
            Assert.Equal(3, ring.MapCount);
            Assert.Equal(4, ring.MaxVersions);
            Assert.Equal(0, ring.CurrentVersion(0));
            Assert.Equal(0, ring.CurrentLength(0));
            Assert.Equal(0UL, ring.CurrentDigest(0));
            byte[] a = RandomMap(random);
            Assert.Equal(1, ring.Publish(0, a, a.Length));
            Assert.Equal(1, ring.Publish(0, (byte[])a.Clone(), a.Length), "identical bytes keep the version");
            byte[] b = Mutate(random, a, 3);
            Assert.Equal(2, ring.Publish(0, b, b.Length));
            Assert.Equal(1, ring.Publish(1, a, a.Length), "maps are numbered independently");
            Assert.Equal(2, ring.CurrentVersion(0));
            Assert.Equal(XxHash64.Hash(b, 0, b.Length), ring.CurrentDigest(0));
            Assert.SequenceEqual(b, Current(ring, 0));
            Assert.Equal(3, ring.Publish(0, a, 100), "a different length is new content");
            Assert.Equal(100, ring.CurrentLength(0));
            b[0] ^= 1;
            Assert.Equal(3, ring.CurrentVersion(0), "the ring keeps its own copy");
        }

        [Test]
        public void NothingIsWrittenWhenThereIsNothingToSend()
        {
            var random = new DeterministicRandom(9002);
            var ring = new CellMapVersionRing(1);
            var writer = new ByteWriter();
            Assert.False(ring.WriteDelta(0, 0, writer, out bool keyframe), "no version yet");
            Assert.False(keyframe);
            byte[] a = RandomMap(random);
            ring.Publish(0, a, a.Length);
            Assert.False(ring.WriteDelta(0, 1, writer, out keyframe), "the peer is current");
            Assert.Equal(0, writer.Length);
        }

        [Test]
        public void DeltasFromRecentVersionsAreXorDeltas()
        {
            var random = new DeterministicRandom(9003);
            var host = new CellMapVersionRing(1);
            var versions = new List<byte[]> { null };
            byte[] map = RandomMap(random);
            for (int v = 1; v <= 4; v++)
            {
                map = Mutate(random, map, 20);
                versions.Add(map);
                Assert.Equal(v, host.Publish(0, map, map.Length));
            }
            for (int from = 1; from <= 3; from++)
            {
                byte[] record = Record(host, 0, from, out bool keyframe);
                Assert.False(keyframe, "version " + from + " is in the ring");
                Assert.True(record.Length < 400, "a delta of a few changes is small (" + record.Length + " bytes)");
                var client = new CellMapVersionRing(1);
                byte[] keyframeRecord = KeyframeOf(versions[from]);
                Assert.Equal(CellMapApplyResult.Applied, Apply(client, keyframeRecord, out _, out _));
                Assert.Equal(CellMapApplyResult.Applied, Apply(client, Rebase(record, from), out int mapIndex, out int version));
                Assert.Equal(0, mapIndex);
                Assert.Equal(4, version);
                Assert.SequenceEqual(versions[4], Current(client, 0), "from version " + from);
            }
        }

        [Test]
        public void KeyframeFallbackAfterFourVersions()
        {
            var random = new DeterministicRandom(9004);
            var host = new CellMapVersionRing(1);
            byte[] map = RandomMap(random);
            for (int v = 1; v <= 5; v++)
            {
                map = Mutate(random, map, 5);
                host.Publish(0, map, map.Length);
            }
            Record(host, 0, 1, out bool keyframe);
            Assert.True(keyframe, "version 1 left the four-version ring");
            Record(host, 0, 2, out keyframe);
            Assert.False(keyframe, "version 2 is the oldest kept version");
            Assert.False(host.Contains(0, 1));
            Assert.True(host.Contains(0, 2));
            Record(host, 0, 0, out keyframe);
            Assert.True(keyframe, "a peer without a version gets a keyframe");
            Record(host, 0, 99, out keyframe);
            Assert.True(keyframe, "an unknown future version gets a keyframe");
            Record(host, 0, -3, out keyframe);
            Assert.True(keyframe, "a negative version gets a keyframe");
            var client = new CellMapVersionRing(1);
            Assert.Equal(CellMapApplyResult.Applied, Apply(client, Record(host, 0, 1, out _), out _, out int version));
            Assert.Equal(5, version);
            Assert.SequenceEqual(map, Current(client, 0));
            Assert.Equal(host.CurrentDigest(0), client.CurrentDigest(0), "host and client digests agree");
        }

        [Test]
        public void SizeChangesAndResetsForceKeyframes()
        {
            var random = new DeterministicRandom(9005);
            var host = new CellMapVersionRing(1);
            byte[] small = RandomMap(random, 100);
            byte[] large = RandomMap(random, 200);
            host.Publish(0, small, small.Length);
            host.Publish(0, large, large.Length);
            Record(host, 0, 1, out bool keyframe);
            Assert.True(keyframe, "a base of another size cannot be XORed");
            host.Reset(0);
            Assert.Equal(0, host.CurrentVersion(0));
            Assert.False(host.WriteDelta(0, 0, new ByteWriter(), out _), "nothing after a reset");
            Assert.Equal(3, host.Publish(0, large, large.Length), "numbering continues after a reset");
            Record(host, 0, 2, out keyframe);
            Assert.True(keyframe, "versions from before the reset are gone");
            host.Clear();
            Assert.Equal(0, host.CurrentVersion(0));
        }

        [Test]
        public void ClientSkipsStaleRecordsAndReportsMissingBases()
        {
            var random = new DeterministicRandom(9006);
            var host = new CellMapVersionRing(2);
            var client = new CellMapVersionRing(2);
            byte[] v1 = RandomMap(random);
            host.Publish(1, v1, v1.Length);
            byte[] keyframe1 = Record(host, 1, 0, out _);
            byte[] v2 = Mutate(random, v1, 10);
            host.Publish(1, v2, v2.Length);
            byte[] delta12 = Record(host, 1, 1, out _);
            byte[] v3 = Mutate(random, v2, 10);
            host.Publish(1, v3, v3.Length);
            byte[] delta23 = Record(host, 1, 2, out _);

            Assert.Equal(CellMapApplyResult.MissingBase, Apply(client, delta23, out int map, out int version), "base 2 is not held");
            Assert.Equal(1, map);
            Assert.Equal(3, version);
            Assert.Equal(0, client.CurrentVersion(1));
            Assert.Equal(CellMapApplyResult.Applied, Apply(client, keyframe1, out _, out _));
            Assert.Equal(CellMapApplyResult.Stale, Apply(client, keyframe1, out _, out _), "a repeated keyframe");
            Assert.Equal(CellMapApplyResult.Applied, Apply(client, delta12, out _, out _));
            Assert.Equal(CellMapApplyResult.Stale, Apply(client, delta12, out _, out _), "a repeated delta");
            Assert.Equal(CellMapApplyResult.Applied, Apply(client, delta23, out _, out _));
            Assert.SequenceEqual(v3, Current(client, 1));
            Assert.Equal(0, client.CurrentVersion(0), "other maps are untouched");

            // several records in one buffer are consumed one after another
            var stream = new ByteWriter();
            stream.WriteBytes(keyframe1, 0, keyframe1.Length);
            stream.WriteBytes(delta12, 0, delta12.Length);
            stream.WriteBytes(delta23, 0, delta23.Length);
            var fresh = new CellMapVersionRing(2);
            var reader = new ByteReader(stream.Buffer, 0, stream.Length);
            for (int i = 0; i < 3; i++) Assert.Equal(CellMapApplyResult.Applied, fresh.ApplyDelta(reader, out _, out _));
            Assert.True(reader.IsAtEnd);
            Assert.SequenceEqual(v3, Current(fresh, 1));
        }

        [Test]
        public void InFlightDeltasApplyAgainstAnOlderAcknowledgedVersion()
        {
            // the host still bases on version 1 (the ack of version 2 is in flight) while the client already holds 2
            var random = new DeterministicRandom(9007);
            var host = new CellMapVersionRing(1);
            var client = new CellMapVersionRing(1);
            byte[] v1 = RandomMap(random);
            host.Publish(0, v1, v1.Length);
            Apply(client, Record(host, 0, 0, out _), out _, out _);
            byte[] v2 = Mutate(random, v1, 8);
            host.Publish(0, v2, v2.Length);
            Apply(client, Record(host, 0, 1, out _), out _, out _);
            byte[] v3 = Mutate(random, v2, 8);
            host.Publish(0, v3, v3.Length);
            byte[] delta13 = Record(host, 0, 1, out bool keyframe);
            Assert.False(keyframe);
            Assert.Equal(CellMapApplyResult.Applied, Apply(client, delta13, out _, out int version));
            Assert.Equal(3, version);
            Assert.SequenceEqual(v3, Current(client, 0));
        }

        [Test]
        public void KeyframesDropOlderVersionsAndReplaceAnotherLineage()
        {
            var random = new DeterministicRandom(9008);
            var host = new CellMapVersionRing(1);
            var client = new CellMapVersionRing(1);
            byte[] map = RandomMap(random);
            for (int v = 1; v <= 3; v++)
            {
                map = Mutate(random, map, 4);
                host.Publish(0, map, map.Length);
                Apply(client, Record(host, 0, v - 1, out _), out _, out _);
            }
            Assert.True(client.Contains(0, 2));
            // a restarted host starts at version 1 again: its keyframe replaces the client's map
            var restarted = new CellMapVersionRing(1);
            byte[] other = RandomMap(random);
            restarted.Publish(0, other, other.Length);
            Assert.Equal(CellMapApplyResult.Applied, Apply(client, Record(restarted, 0, 0, out _), out _, out int version));
            Assert.Equal(1, version);
            Assert.SequenceEqual(other, Current(client, 0));
            Assert.False(client.Contains(0, 2), "versions of the old lineage are dropped");
            Assert.False(client.Contains(0, 3));
        }

        [Test]
        public void UnknownMapsAreSkipped()
        {
            var random = new DeterministicRandom(9009);
            var host = new CellMapVersionRing(5);
            byte[] map = RandomMap(random, 300);
            host.Publish(4, map, map.Length);
            var client = new CellMapVersionRing(2);
            Assert.Equal(CellMapApplyResult.UnknownMap, Apply(client, Record(host, 4, 0, out _), out int index, out int version));
            Assert.Equal(4, index);
            Assert.Equal(1, version);
        }

        [Test]
        public void MalformedRecordsThrowAndLeaveTheRingIntact()
        {
            var random = new DeterministicRandom(9010);
            var host = new CellMapVersionRing(1);
            byte[] v1 = RandomMap(random);
            host.Publish(0, v1, v1.Length);
            byte[] keyframe = Record(host, 0, 0, out _);
            byte[] v2 = Mutate(random, v1, 30);
            host.Publish(0, v2, v2.Length);
            byte[] delta = Record(host, 0, 1, out _);
            var client = new CellMapVersionRing(1);
            Apply(client, keyframe, out _, out _);
            ulong digest = client.CurrentDigest(0);
            FieldTestSupport.AssertEveryTruncationFails(delta, r => client.ApplyDelta(r, out _, out _), "cell-map record");
            FieldTestSupport.AssertCorruptionOnlyThrowsDecodeException(delta, r =>
            {
                var scratch = new CellMapVersionRing(1);
                Apply(scratch, keyframe, out _, out _);
                scratch.ApplyDelta(r, out _, out _);
            }, 9011, 2000, "cell-map record");
            Assert.Equal(1, client.CurrentVersion(0), "failed records changed nothing");
            Assert.Equal(digest, client.CurrentDigest(0));

            // a delta whose payload is broken in its last pair must not damage the base version
            byte[] broken = (byte[])delta.Clone();
            broken[broken.Length - 1] ^= 0x40;
            try
            {
                client.ApplyDelta(new ByteReader(broken, 0, broken.Length), out _, out _);
            }
            catch (DecodeException)
            {
            }
            Assert.True(client.CurrentVersion(0) == 1 || client.CurrentVersion(0) == 2);
            if (client.CurrentVersion(0) == 1) Assert.SequenceEqual(v1, Current(client, 0));

            Assert.Throws<DecodeException>(() => client.ApplyDelta(new ByteReader(new byte[] { 2, 0, 0, 0, 1, 0, 0, 0, 0, 0 }, 0, 10), out _, out _), "format version");
            Assert.Throws<DecodeException>(() => client.ApplyDelta(new ByteReader(new byte[] { 1, 2, 0, 0, 1, 0, 0, 0, 0, 0 }, 0, 10), out _, out _), "flags");
            Assert.Throws<DecodeException>(() => client.ApplyDelta(new ByteReader(new byte[] { 1, 1, 0, 3, 4, 0, 0, 0, 0, 0 }, 0, 10), out _, out _), "keyframe with a base");
            Assert.Throws<DecodeException>(() => client.ApplyDelta(new ByteReader(new byte[] { 1, 0, 0, 4, 4, 0, 0, 0, 0, 0 }, 0, 10), out _, out _), "delta base not older");
            Assert.Throws<DecodeException>(() => client.ApplyDelta(new ByteReader(new byte[] { 1, 1, 0, 0, 0, 0, 0, 0, 0, 0 }, 0, 10), out _, out _), "version 0");
        }

        [Test]
        public void ExpectedLengthsRejectForeignMapSizes()
        {
            var random = new DeterministicRandom(9017);
            var host = new CellMapVersionRing(1);
            byte[] large = RandomMap(random, 128 * 128 * 6);
            host.Publish(0, large, large.Length);
            byte[] record = Record(host, 0, 0, out _);
            var client = new CellMapVersionRing(1);
            client.SetExpectedLength(0, 64 * 64 * 6);
            Assert.Equal(CellMapApplyResult.SizeMismatch, Apply(client, record, out int map, out int version), "a map of another size is skipped");
            Assert.Equal(0, map);
            Assert.Equal(1, version);
            Assert.Equal(0, client.CurrentVersion(0));
            Assert.Equal(1L, client.GetStats().SizeMismatch);
            client.SetExpectedLength(0, large.Length);
            Assert.Equal(CellMapApplyResult.Applied, Apply(client, record, out _, out _));
            client.SetExpectedLength(0, -1);
            Assert.Throws<ArgumentOutOfRangeException>(() => client.SetExpectedLength(0, -2));
            Assert.Throws<ArgumentOutOfRangeException>(() => client.SetExpectedLength(0, CellMapVersionRing.MaxMapBytes + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => client.SetExpectedLength(1, 10));
        }

        [Test]
        public void StatsCountVersionsRecordsAndOutcomes()
        {
            var random = new DeterministicRandom(9016);
            var host = new CellMapVersionRing(2);
            var client = new CellMapVersionRing(1);
            byte[] v1 = RandomMap(random);
            host.Publish(0, v1, v1.Length);
            host.Publish(0, v1, v1.Length);   // identical: no new version
            byte[] keyframe = Record(host, 0, 0, out _);
            byte[] v2 = Mutate(random, v1, 4);
            host.Publish(0, v2, v2.Length);
            byte[] delta = Record(host, 0, 1, out _);
            host.Publish(1, v1, 10);
            byte[] unknown = Record(host, 1, 0, out _);
            CellMapRingStats hostStats = host.GetStats();
            Assert.Equal(3L, hostStats.Versions);
            Assert.Equal(1L, hostStats.Deltas);
            Assert.Equal(2L, hostStats.Keyframes);
            Assert.Equal((long)(keyframe.Length + delta.Length + unknown.Length), hostStats.BytesWritten);

            Apply(client, delta, out _, out _);       // missing base
            Apply(client, keyframe, out _, out _);    // applied
            Apply(client, keyframe, out _, out _);    // stale
            Apply(client, delta, out _, out _);       // applied
            Apply(client, unknown, out _, out _);     // unknown map
            CellMapRingStats clientStats = client.GetStats();
            Assert.Equal(2L, clientStats.Applied);
            Assert.Equal(1L, clientStats.Stale);
            Assert.Equal(1L, clientStats.MissingBase);
            Assert.Equal(1L, clientStats.UnknownMap);
            Assert.Equal(0L, clientStats.Versions);
        }

        [Test]
        public void HostLogsWhyAPeerNeedsAKeyframe()
        {
            var random = new DeterministicRandom(9018);
            var log = new ListLog(LogLevel.Debug);
            var host = new CellMapVersionRing(1) { Log = log };
            byte[] map = RandomMap(random);
            for (int v = 1; v <= 5; v++)
            {
                map = Mutate(random, map, 3);
                host.Publish(0, map, map.Length);
            }
            Record(host, 0, 3, out bool keyframe);
            Assert.False(keyframe);
            Assert.Equal(0, log.Lines.Count, "a delta logs nothing");
            Record(host, 0, 0, out _);
            Assert.True(log.Contains("Debug [fields] cell map 0: keyframe of version 5 for a peer at version 0 (the peer holds no version)"), string.Join(" / ", log.Lines));
            Record(host, 0, 1, out _);
            Assert.True(log.Contains("version 1 (the peer's version left the ring)"));
            Record(host, 0, 42, out _);
            Assert.True(log.Contains("version 42 (the peer acknowledged a version this ring never issued)"));
            host.Publish(0, map, 10);
            Record(host, 0, 5, out keyframe);
            Assert.True(keyframe);
            Assert.True(log.Contains("(the map size changed from " + map.Length + " to 10 bytes)"));
            Assert.Equal(0, log.Count(LogLevel.Info), "keyframes are routine: debug only");
            var quiet = new ListLog(LogLevel.Info);
            host.Log = quiet;
            Record(host, 0, 0, out _);
            Assert.Equal(0, quiet.Lines.Count, "nothing is written when debug is disabled");
        }

        [Test]
        public void ClientLogsSkippedRecordsWithThrottledWarnings()
        {
            var random = new DeterministicRandom(9019);
            var host = new CellMapVersionRing(4);
            byte[] v1 = RandomMap(random, 600);
            host.Publish(3, v1, v1.Length);
            byte[] keyframe = Record(host, 3, 0, out _);
            byte[] v2 = Mutate(random, v1, 5);
            host.Publish(3, v2, v2.Length);
            byte[] delta = Record(host, 3, 1, out _);

            var log = new ListLog(LogLevel.Debug);
            var client = new CellMapVersionRing(4) { Log = log };
            client.SetExpectedLength(3, 500);
            for (int i = 0; i < 5; i++) Assert.Equal(CellMapApplyResult.SizeMismatch, Apply(client, keyframe, out _, out _));
            Assert.Equal(3, log.Count(LogLevel.Warn), "warnings on the 1st, 2nd and 4th of 5 mismatches");
            Assert.True(log.Contains("Warn [fields] cell-map record for map 3 skipped: it has 600 bytes, the local map 500 (the peers' maps differ; first occurrence)"),
                        string.Join(" / ", log.Lines));
            Assert.True(log.Contains("occurrence 4)"));
            Assert.False(log.Contains("occurrence 3)") || log.Contains("occurrence 5)"));

            log.Clear();
            client.SetExpectedLength(3, -1);
            Assert.Equal(CellMapApplyResult.MissingBase, Apply(client, delta, out _, out _));
            Assert.True(log.Contains("Debug [fields] cell-map delta 1 -> 2 for map 3 skipped: base not held (current version 0); a keyframe is needed"),
                        string.Join(" / ", log.Lines));
            Assert.Equal(0, log.Count(LogLevel.Warn), "a missing base is routine (debug)");

            log.Clear();
            var small = new CellMapVersionRing(2) { Log = log };
            for (int i = 0; i < 3; i++) Assert.Equal(CellMapApplyResult.UnknownMap, Apply(small, keyframe, out _, out _));
            Assert.Equal(2, log.Count(LogLevel.Warn), "warnings on the 1st and 2nd of 3 unknown-map records");
            Assert.True(log.Contains("cell-map record for map 3 skipped: this ring has 2 maps (the peers' map registries differ; first occurrence)"),
                        string.Join(" / ", log.Lines));

            log.Clear();
            Assert.Equal(CellMapApplyResult.Applied, Apply(client, keyframe, out _, out _));
            Assert.Equal(CellMapApplyResult.Applied, Apply(client, delta, out _, out _));
            Assert.Equal(0, log.Lines.Count, "applied records log nothing");
        }

        [Test]
        public void RandomisedSessionsConverge()
        {
            var random = new DeterministicRandom(9012);
            const int maps = 3;
            var host = new CellMapVersionRing(maps);
            var clients = new CellMapVersionRing[4];
            var acked = new int[clients.Length, maps];
            for (int c = 0; c < clients.Length; c++) clients[c] = new CellMapVersionRing(maps);
            var current = new byte[maps][];
            for (int m = 0; m < maps; m++)
            {
                current[m] = RandomMap(random, 500 + 300 * m);
                host.Publish(m, current[m], current[m].Length);
            }
            int keyframes = 0, deltas = 0;
            var writer = new ByteWriter();
            for (int tick = 0; tick < 400; tick++)
            {
                int m = random.Next(maps);
                if (random.Next(3) == 0)
                {
                    current[m] = Mutate(random, current[m], 1 + random.Next(30));
                    host.Publish(m, current[m], current[m].Length);
                }
                for (int c = 0; c < clients.Length; c++)
                {
                    if (random.Next(4) == 0) continue;   // this peer gets nothing this tick
                    for (int k = 0; k < maps; k++)
                    {
                        writer.Reset();
                        if (!host.WriteDelta(k, acked[c, k], writer, out bool keyframe)) continue;
                        if (keyframe) keyframes++;
                        else deltas++;
                        CellMapApplyResult result = clients[c].ApplyDelta(new ByteReader(writer.Buffer, 0, writer.Length), out int index, out int version);
                        Assert.Equal(k, index);
                        Assert.True(result == CellMapApplyResult.Applied || result == CellMapApplyResult.Stale, "tick " + tick + ": " + result);
                        if (random.Next(3) != 0) acked[c, k] = clients[c].CurrentVersion(k);   // acks are lost or late a third of the time
                    }
                }
            }
            for (int c = 0; c < clients.Length; c++)
            {
                for (int k = 0; k < maps; k++)
                {
                    writer.Reset();
                    if (host.WriteDelta(k, clients[c].CurrentVersion(k), writer, out _))
                    {
                        clients[c].ApplyDelta(new ByteReader(writer.Buffer, 0, writer.Length), out _, out _);
                    }
                    Assert.Equal(host.CurrentVersion(k), clients[c].CurrentVersion(k), "client " + c + " map " + k);
                    Assert.SequenceEqual(current[k], Current(clients[c], k), "client " + c + " map " + k);
                }
            }
            Assert.True(deltas > keyframes, "deltas (" + deltas + ") dominate keyframes (" + keyframes + ")");
        }

        [Test(TimeoutSeconds = 120)]
        public void ConcurrentPublishAndDeltaBuildingIsSafe()
        {
            var host = new CellMapVersionRing(2);
            var seed = new DeterministicRandom(9013);
            byte[] initial = RandomMap(seed);
            host.Publish(0, initial, initial.Length);
            host.Publish(1, initial, initial.Length);
            Exception failure = null;
            int stop = 0;
            var readers = new Thread[3];
            for (int t = 0; t < readers.Length; t++)
            {
                int id = t;
                readers[t] = new Thread(() =>
                {
                    try
                    {
                        var client = new CellMapVersionRing(2);
                        var writer = new ByteWriter();
                        var random = new DeterministicRandom(100 + (ulong)id);
                        while (Volatile.Read(ref stop) == 0)
                        {
                            int map = random.Next(2);
                            writer.Reset();
                            if (!host.WriteDelta(map, client.CurrentVersion(map), writer, out _)) continue;
                            CellMapApplyResult result = client.ApplyDelta(new ByteReader(writer.Buffer, 0, writer.Length), out _, out _);
                            if (result != CellMapApplyResult.Applied && result != CellMapApplyResult.Stale) throw new InvalidOperationException(result.ToString());
                        }
                    }
                    catch (Exception e)
                    {
                        Interlocked.CompareExchange(ref failure, e, null);
                    }
                });
                readers[t].Start();
            }
            var publisher = new DeterministicRandom(9014);
            byte[] map0 = initial, map1 = initial;
            for (int i = 0; i < 3000; i++)
            {
                map0 = Mutate(publisher, map0, 5);
                host.Publish(0, map0, map0.Length);
                if (i % 3 == 0)
                {
                    map1 = Mutate(publisher, map1, 5);
                    host.Publish(1, map1, map1.Length);
                }
            }
            Volatile.Write(ref stop, 1);
            foreach (Thread reader in readers) reader.Join();
            Assert.Null(failure, failure?.ToString());
        }

        [Test]
        public void ArgumentsAreValidated()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CellMapVersionRing(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CellMapVersionRing(1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CellMapVersionRing(CellMapVersionRing.MaxMapCount + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CellMapVersionRing(1, CellMapVersionRing.MaxRingVersions + 1));
            var ring = new CellMapVersionRing(2);
            Assert.Throws<ArgumentOutOfRangeException>(() => ring.Publish(2, new byte[1], 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => ring.Publish(-1, new byte[1], 1));
            Assert.Throws<ArgumentNullException>(() => ring.Publish(0, null, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => ring.Publish(0, new byte[1], 2));
            Assert.Throws<ArgumentNullException>(() => ring.WriteDelta(0, 0, null, out _));
            Assert.Throws<ArgumentOutOfRangeException>(() => ring.CurrentVersion(5));
            Assert.Throws<ArgumentNullException>(() => ring.ApplyDelta(null, out _, out _));
            ring.Publish(0, new byte[10], 10);
            Assert.Throws<ArgumentOutOfRangeException>(() => ring.CopyCurrent(0, new byte[9], 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => ring.CopyCurrent(0, new byte[10], 1));
            Assert.Equal(0, ring.CopyCurrent(1, new byte[0], 0), "an empty map copies nothing");
            var single = new CellMapVersionRing(1, 1);
            var random = new DeterministicRandom(9015);
            byte[] a = RandomMap(random, 50), b = Mutate(random, a, 3);
            single.Publish(0, a, a.Length);
            single.Publish(0, b, b.Length);
            Record(single, 0, 1, out bool keyframe);
            Assert.True(keyframe, "a one-version ring only sends keyframes");
        }

        /// <summary>A keyframe record of <paramref name="map"/> as version <c>1</c> of map 0 (built by a fresh host ring).</summary>
        private static byte[] KeyframeOf(byte[] map)
        {
            var ring = new CellMapVersionRing(1);
            ring.Publish(0, map, map.Length);
            return Record(ring, 0, 0, out _);
        }

        /// <summary>
        /// Moves a delta record from base <paramref name="from"/> to base 1, so it applies on a client that received the
        /// base as a version-1 keyframe (only the header fields change; the payload is the same XOR delta).
        /// </summary>
        private static byte[] Rebase(byte[] record, int from)
        {
            var reader = new ByteReader(record, 0, record.Length);
            byte format = reader.ReadByte();
            byte flags = reader.ReadByte();
            uint map = reader.ReadVarUInt32();
            uint recordedFrom = reader.ReadVarUInt32();
            Assert.Equal((uint)from, recordedFrom);
            uint to = reader.ReadVarUInt32();
            uint count = reader.ReadVarUInt32();
            uint payloadLength = reader.ReadUInt32();
            ArraySegment<byte> payload = reader.ReadSegment((int)payloadLength);
            return FieldTestSupport.Write(w =>
            {
                w.WriteByte(format);
                w.WriteByte(flags);
                w.WriteVarUInt32(map);
                w.WriteVarUInt32(1);
                w.WriteVarUInt32(to);
                w.WriteVarUInt32(count);
                w.WriteUInt32(payloadLength);
                w.WriteBytes(payload.Array, payload.Offset, payload.Count);
            });
        }
    }
}
