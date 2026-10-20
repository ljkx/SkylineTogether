using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using SkylineTogether.Core.Integrity;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Integrity
{
    /// <summary>
    /// Seeded properties of the integrity layer: bucket digests and detail comparison against a set model, and the
    /// divergence tracker in a simulated session where some entities follow the host with a lag, some are stuck until
    /// repaired and some are stubborn (a repair never helps). Plus the mod mismatch report against its definition.
    /// </summary>
    public sealed class IntegrityPropertyTests
    {
        [Test]
        public void DigestsAreOrderIndependentAndCompareMatchesASetModel()
        {
            Prop.ForAll("entity digest", 1900, 300, g =>
            {
                var ids = new HashSet<uint>();
                int n = g.Size(60);
                while (ids.Count < n) ids.Add((uint)g.Int(1, 400));
                var host = ids.Select(id => new EntityHash(id, g.ULong())).ToList();
                var client = new List<EntityHash>();
                foreach (EntityHash h in host)
                {
                    switch (g.Int(6))
                    {
                        case 0: break;                                                     // missing on the client
                        case 1: client.Add(new EntityHash(h.NetId, h.Hash ^ (1UL << g.Int(64)))); break;
                        default: client.Add(h); break;
                    }
                }
                for (int k = g.Int(4); k > 0; k--)
                {
                    uint extra = (uint)g.Int(400, 500);
                    if (!client.Exists(c => c.NetId == extra)) client.Add(new EntityHash(extra, g.ULong()));
                }
                var shuffled = host.OrderBy(_ => g.Int(1000)).ToList();
                EntityDigest.Accumulator a = EntityDigest.Of(host), b = EntityDigest.Of(shuffled);
                Assert.True(a.Matches(b.Count, b.Sum), "the digest ignores order");

                List<(uint NetId, EntityDifference Kind)> diffs = EntityDigest.Compare(host, client);
                var hostMap = host.ToDictionary(h => h.NetId, h => h.Hash);
                var clientMap = client.ToDictionary(h => h.NetId, h => h.Hash);
                var expected = new List<(uint, EntityDifference)>();
                foreach (uint id in hostMap.Keys.Union(clientMap.Keys).OrderBy(x => x))
                {
                    bool inHost = hostMap.TryGetValue(id, out ulong hh), inClient = clientMap.TryGetValue(id, out ulong ch);
                    if (!inClient) expected.Add((id, EntityDifference.MissingOnClient));
                    else if (!inHost) expected.Add((id, EntityDifference.ExtraOnClient));
                    else if (hh != ch) expected.Add((id, EntityDifference.Differs));
                }
                Assert.SequenceEqual(expected, diffs, "differences");
                EntityDigest.Accumulator c = EntityDigest.Of(client);
                Assert.Equal(diffs.Count == 0, a.Matches(c.Count, c.Sum), "the digests match exactly when nothing differs");
                foreach (EntityHash h in host)
                {
                    uint bucket = EntityDigest.BucketOf(h.NetId);
                    Assert.True(h.NetId >= EntityDigest.FirstId(bucket) && h.NetId <= EntityDigest.LastId(bucket), "bucket bounds");
                }
            });
        }

        [Test]
        public void DuplicateIdsInADetailAreJudgedByTheSameEntryEverywhere()
        {
            // a detail that lists an id twice (a damaged record) is judged by its first entry, as EntityDigest.Compare does
            var t = new DivergenceTracker();
            var o = new DivergenceTracker.Outcome();
            var host = new List<EntityHash> { new EntityHash(5, 1), new EntityHash(5, 2) };
            var client = new List<EntityHash> { new EntityHash(5, 1) };
            for (double now = 0; now < 30; now += 0.5)
            {
                t.OnDetail(host, client, now, o);
                Assert.Equal(0, o.Mismatching, "the first entries match");
                Assert.Equal(0, o.Repair.Count);
            }
            var differ = new List<EntityHash> { new EntityHash(7, 1), new EntityHash(7, 9) };
            var mine = new List<EntityHash> { new EntityHash(7, 9) };
            bool repaired = false;
            for (double now = 0; now < 30; now += 0.5)
            {
                t.OnDetail(differ, mine, now, o);
                Assert.Equal(1, o.Mismatching, "the first host entry differs");
                repaired |= o.Repair.Contains(7);
            }
            Assert.True(repaired, "and a stable difference is repaired");
        }

        [Test(TimeoutSeconds = 120)]
        public void TheTrackerRepairsStuckEntitiesLeavesFollowersAloneAndGivesUpOnStubbornOnes()
        {
            Prop.ForAll("divergence", 1910, 10, g =>
            {
                var t = new DivergenceTracker { ToleranceSeconds = 100 };
                var o = new DivergenceTracker.Outcome();
                const int Count = 80;
                var host = new Dictionary<uint, ulong>();
                var client = new Dictionary<uint, ulong>();
                var role = new Dictionary<uint, string>();
                var lagged = new Dictionary<uint, Queue<ulong>>();
                for (uint id = 1; id <= Count; id++)
                {
                    host[id] = g.ULong();
                    client[id] = host[id];
                    role[id] = g.Pick("calm", "calm", "calm", "follower", "stuck", "stubborn");
                    lagged[id] = new Queue<ulong>();
                    if (role[id] == "stuck" || role[id] == "stubborn") client[id] = ~host[id];
                }
                var repairsOf = new Dictionary<uint, int>();
                long repairs = 0;
                double now = 0;
                for (int round = 0; round < 1200; round++)
                {
                    now += 0.5;
                    foreach (uint id in host.Keys.ToList())
                    {
                        if (role[id] == "follower")
                        {
                            // the host changes it every round; the client applies each change two rounds later
                            host[id] = g.ULong();
                            lagged[id].Enqueue(host[id]);
                            if (lagged[id].Count > 2) client[id] = lagged[id].Dequeue();
                        }
                    }
                    var hostList = host.Select(kv => new EntityHash(kv.Key, kv.Value)).OrderBy(_ => g.Int(1000)).ToList();
                    var clientList = client.Select(kv => new EntityHash(kv.Key, kv.Value)).ToList();
                    t.OnDetail(hostList, clientList, now, o);
                    Assert.True(o.Repair.Count <= t.MaxRepairsPerDetail);
                    Assert.True(t.Tracked <= t.MaxTracked);
                    foreach (uint id in o.Repair)
                    {
                        Assert.True(client[id] != host[id], "#" + id + " is repaired only while it differs");
                        Assert.True(role[id] != "follower", "#" + id + " follows the host and must not be repaired");
                        repairsOf[id] = repairsOf.TryGetValue(id, out int n) ? n + 1 : 1;
                        repairs++;
                        if (role[id] == "stuck") client[id] = host[id];     // the re-sent value sticks
                        else client[id] = ~host[id] ^ (ulong)round;         // stubborn: the client regenerates its own value
                    }
                    Assert.True(repairs <= t.MaxRepairsPerMinute * (now / 60.0 + 1), "the global repair budget holds");
                }
                foreach (uint id in host.Keys)
                {
                    int n = repairsOf.TryGetValue(id, out int r) ? r : 0;
                    switch (role[id])
                    {
                        case "calm":
                        case "follower":
                            Assert.Equal(0, n, "#" + id + " (" + role[id] + ") repairs");
                            break;
                        case "stuck":
                            Assert.Equal(1, n, "#" + id + " is fixed by one repair");
                            Assert.Equal(host[id], client[id]);
                            break;
                        default:
                            Assert.True(n >= 1 && n <= (t.RepairsBeforeTolerance + t.MaxRepairsInHistory) * t.MaxTolerances, "#" + id + " stubborn: " + n + " repairs");
                            break;
                    }
                }
                Assert.True(t.GivenUpCount > 0 || !role.Values.Contains("stubborn"), "stubborn entities are given up eventually");
                Assert.Equal(role.Values.Count(r => r == "stubborn"), (int)t.GivenUpCount, "every stubborn entity is given up, nothing else");
            });
        }

        // ------------------------------------------------------------------ mismatch report

        [Test]
        public void ModMismatchReportsListExactlyTheBlockingDifferences()
        {
            var pool = new List<ModEntry>();
            for (int i = 0; i < 12; i++) pool.Add(new ModEntry { Name = "Gameplay Mod " + i, Version = "1." + i, PlatformId = i % 4 == 3 ? "" : (1000 + i).ToString(CultureInfo.InvariantCulture) });
            Prop.ForAll("mismatch report", 1920, 300, g =>
            {
                var host = new List<ModEntry>();
                var client = new List<ModEntry>();
                foreach (ModEntry m in pool)
                {
                    int roll = g.Int(6);
                    if (roll != 0) host.Add(m);
                    if (roll == 1) continue;                                   // client lacks it
                    if (roll == 2) client.Add(new ModEntry { Name = m.Name, Version = m.Version + "b", PlatformId = m.PlatformId });
                    else if (roll == 0 || g.OneIn(8) == false) client.Add(m);
                }
                MismatchReport report = MismatchReport.CompareMods(host, client);
                string Key(ModEntry m) => !string.IsNullOrEmpty(m.PlatformId) ? "id:" + m.PlatformId : "name:" + m.Name;
                var hostKeys = host.ToDictionary(Key);
                var clientKeys = client.ToDictionary(Key);
                var missing = hostKeys.Keys.Where(k => !clientKeys.ContainsKey(k)).OrderBy(k => k).ToList();
                var extra = clientKeys.Keys.Where(k => !hostKeys.ContainsKey(k)).OrderBy(k => k).ToList();
                var versions = hostKeys.Keys.Where(k => clientKeys.ContainsKey(k) && hostKeys[k].Version != clientKeys[k].Version).OrderBy(k => k).ToList();
                if (missing.Count + extra.Count + versions.Count == 0)
                {
                    Assert.Null(report, "nothing blocks");
                    return;
                }
                Assert.NotNull(report);
                Assert.SequenceEqual(missing, report.Missing.Select(Key).OrderBy(k => k), "missing");
                Assert.SequenceEqual(extra, report.Extra.Select(Key).OrderBy(k => k), "extra");
                Assert.SequenceEqual(versions.Select(k => hostKeys[k].Name).OrderBy(s => s), report.Versions.Select(v => v.Name).OrderBy(s => s), "versions");
                string text = report.Describe();
                foreach (ModEntry m in report.Missing) Assert.Contains(m.Name, text);
                foreach (ModEntry m in report.Extra) Assert.Contains(m.Name, text);

                MismatchReport back = MismatchReport.Decode(MismatchReport.Encode(report));
                Assert.Equal(report.Kind, back.Kind);
                Assert.SequenceEqual(report.Missing.Select(m => m.Name + "|" + m.Version + "|" + m.PlatformId), back.Missing.Select(m => m.Name + "|" + m.Version + "|" + m.PlatformId));
                Assert.SequenceEqual(report.Extra.Select(m => m.Name + "|" + m.Version), back.Extra.Select(m => m.Name + "|" + m.Version));
                Assert.SequenceEqual(report.Versions.Select(v => v.Name + "|" + v.HostVersion + "|" + v.ClientVersion), back.Versions.Select(v => v.Name + "|" + v.HostVersion + "|" + v.ClientVersion));
            });
            // hostile field content cannot break the line format
            var r = new MismatchReport { Kind = "mods" };
            r.Missing.Add(new ModEntry { Name = "a\tb\nc\rd", Version = "1", PlatformId = "x" });
            MismatchReport decoded = MismatchReport.Decode(MismatchReport.Encode(r));
            Assert.Equal(1, decoded.Missing.Count);
            Assert.Equal("a b c d", decoded.Missing[0].Name);
            Assert.Null(MismatchReport.Decode(null));
            Assert.Null(MismatchReport.Decode(new byte[0]));
        }
    }
}
