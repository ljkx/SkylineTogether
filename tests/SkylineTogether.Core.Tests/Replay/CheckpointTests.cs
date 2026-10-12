using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Integrity;
using SkylineTogether.Core.Replay;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Replay
{
    /// <summary>A replaying client's world for the checker tests.</summary>
    internal sealed class FakeReplayWorld : IReplayWorld
    {
        public readonly Dictionary<uint, ulong> Hashes = new Dictionary<uint, ulong>();
        public readonly Dictionary<uint, List<ComponentHash>> Members = new Dictionary<uint, List<ComponentHash>>();
        public readonly Dictionary<uint, string> Families = new Dictionary<uint, string>();
        public List<LineStateRow> Lines = new List<LineStateRow>();
        public bool Settled = true;
        public List<IconOwnerDigest> Icons = new List<IconOwnerDigest>();
        public readonly Dictionary<uint, ulong> Globals = new Dictionary<uint, ulong>();
        public double Seconds;

        public void HashBucket(uint bucket, List<EntityHash> output)
        {
            output.Clear();
            var ids = new List<uint>(Hashes.Keys);
            ids.Sort();
            foreach (uint id in ids)
            {
                if (EntityDigest.BucketOf(id) == bucket) output.Add(new EntityHash(id, Hashes[id]));
            }
        }

        public bool CollectMembers(uint netId, List<ComponentHash> output)
        {
            output.Clear();
            if (!Members.TryGetValue(netId, out List<ComponentHash> m)) return false;
            output.AddRange(m);
            return true;
        }

        public string FamilyOf(uint netId) => Families.TryGetValue(netId, out string f) ? f : "";

        public string TypeName(ulong typeHash) => typeHash == 1 ? "Game.Objects.Transform" : typeHash == 2 ? "Game.Net.Curve" : typeHash == 3 ? "Game.Common.Owner" : "T" + typeHash;

        public List<LineStateRow> LineRows() => Lines;

        public bool IconsSettled => Settled;

        public bool IconDigests(List<IconOwnerDigest> owners, out uint ownerCount, out uint iconCount, out ulong sum)
        {
            owners.AddRange(Icons);
            ownerCount = (uint)Icons.Count;
            iconCount = 0;
            sum = 0;
            foreach (IconOwnerDigest d in Icons)
            {
                iconCount += d.Icons;
                unchecked
                {
                    sum += d.Sum;
                }
            }
            return true;
        }

        public void GlobalHashes(IReadOnlyList<GlobalChannelHash> wanted, List<GlobalChannelHash> output)
        {
            foreach (GlobalChannelHash g in wanted)
            {
                if (Globals.TryGetValue(g.Channel, out ulong h)) output.Add(new GlobalChannelHash(g.Channel, g.Name, h, g.Strict));
            }
        }

        public double ReplaySeconds => Seconds;
    }

    public sealed class CheckpointTests
    {
        private static LineStateRow Row(uint line, int stops, bool broken = false)
        {
            var r = new LineStateRow { Line = new NetSlot(line), Stops = stops, LengthDecimetres = 1234, Active = true, IconsCompared = true };
            if (broken) r.MarkSegmentBroken(1);
            return r;
        }

        private static List<ComponentHash> M(params (ulong Type, ulong Value)[] members)
        {
            var list = new List<ComponentHash>();
            foreach ((ulong t, ulong v) in members) list.Add(new ComponentHash(t, v, v == 0 ? ComponentHashState.Presence : ComponentHashState.Value));
            return list;
        }

        private static List<SyncRecord> RoundTrip(SyncBatch.Writer w)
        {
            ArraySegment<byte> bytes = w.Finish();
            return SyncBatch.Read(bytes.Array, bytes.Offset, bytes.Count);
        }

        [Test]
        public void ReplayRecordsRoundTrip()
        {
            var w = new SyncBatch.Writer();
            w.ReplayHashes(3, 7, new List<EntityHash> { new EntityHash(900, 11), new EntityHash(901, 12) });
            w.ReplayMembers(900, Family.NetEdge, 11, M((1, 5), (3, 0)));
            byte[] summary = { 9, 8, 7 };
            w.ReplayCheckpoint(3, ReplayCheckpointPhase.End, 123456, summary, 0, summary.Length);
            List<SyncRecord> r = RoundTrip(w);
            Assert.Equal(3, r.Count);
            Assert.Equal(SyncRecordKind.ReplayHashes, r[0].Kind);
            Assert.Equal(3U, r[0].Seq);
            Assert.Equal(7U, r[0].Bucket);
            Assert.Equal(901U, r[0].Hashes[1].NetId);
            Assert.Equal(12UL, r[0].Hashes[1].Hash);
            Assert.Equal(Family.NetEdge, r[1].Family);
            Assert.Equal(11UL, r[1].Hash);
            Assert.Equal(ComponentHashState.Presence, r[1].ComponentHashes[1].State);
            Assert.Equal(ReplayCheckpointPhase.End, r[2].CheckpointPhase);
            Assert.Equal(123456U, r[2].Frame);
            Assert.SequenceEqual(summary, r[2].Payload);
            Assert.False(SyncBatch.AcceptedFromClient(SyncRecordKind.ReplayHashes), "host-only");
            // a bad phase is malformed
            ArraySegment<byte> bytes = w.Finish();
            byte[] copy = bytes.ToArray();
            int phaseAt = copy.Length - summary.Length - 1 - 4 - 1; // phase | u32 frame | varuint length | summary
            copy[phaseAt] = 9;
            Assert.Throws<DecodeException>(() => SyncBatch.Read(copy, 0, copy.Length));
        }

        [Test]
        public void ASummaryRoundTrips()
        {
            var s = new CheckpointSummary
            {
                Index = 4,
                HostSeconds = 61.5,
                HostFrame = 99999,
                Buckets = 30,
                Entities = 3456,
                MembersRecorded = 12,
                HasLines = true,
                HasIcons = true,
                IconOwners = 2,
                IconCount = 3,
                IconSum = 777,
                Note = "final",
            };
            s.Lines.Add(Row(10, 4, broken: true));
            s.IconOwnerDigests.Add(new IconOwnerDigest(SyncRef.ForNetId(55), 2, 700));
            s.IconOwnerDigests.Add(new IconOwnerDigest(new SyncRef { Kind = SyncRef.LaneKey, Id = 9, Prefab = 3, Index = 1 }, 1, 77));
            s.Globals.Add(new GlobalChannelHash(GlobalChannel.Tax, "Tax", 4242, true));
            s.Globals.Add(new GlobalChannelHash(5, "ResidentialDemandSystem", 1, false));
            CheckpointSummary r = CheckpointSummary.Read(s.ToBytes(), 0, s.ToBytes().Length);
            Assert.Equal(4U, r.Index);
            Assert.Equal(61.5, r.HostSeconds);
            Assert.Equal(99999U, r.HostFrame);
            Assert.Equal(3456L, r.Entities);
            Assert.Equal(1, r.Lines.Count);
            Assert.Equal(0, LineStateReport.Compare(s.Lines, r.Lines).Count);
            Assert.Equal(2, r.IconOwnerDigests.Count);
            Assert.Equal(SyncRef.LaneKey, r.IconOwnerDigests[1].Owner.Kind);
            Assert.Equal(777UL, r.IconSum);
            Assert.Equal("ResidentialDemandSystem", r.Globals[1].Name);
            Assert.False(r.Globals[1].Strict);
            Assert.Equal("final", r.Note);
            Assert.Throws<DecodeException>(() => CheckpointSummary.Read(new byte[] { 2, 0 }, 0, 2));
            byte[] cut = s.ToBytes();
            Assert.Throws<DecodeException>(() => CheckpointSummary.Read(cut, 0, cut.Length - 3));
        }

        [Test]
        public void TheMemberTrackerReportsNewAndChangedEntitiesOnly()
        {
            var t = new CheckpointMemberTracker();
            var changed = new List<EntityHash>();
            t.Update(0, new List<EntityHash> { new EntityHash(1, 10), new EntityHash(2, 20) }, changed);
            Assert.Equal(2, changed.Count, "first sight");
            t.Update(0, new List<EntityHash> { new EntityHash(1, 10), new EntityHash(2, 21), new EntityHash(3, 30) }, changed);
            Assert.Equal(2, changed.Count);
            Assert.Equal(2U, changed[0].NetId);
            Assert.Equal(3U, changed[1].NetId);
            Assert.Equal(3, t.Count);
            t.Update(0, new List<EntityHash> { new EntityHash(3, 30) }, changed);
            Assert.Equal(0, changed.Count);
            Assert.Equal(1, t.Count, "gone ids are forgotten");
            t.Update(0, new List<EntityHash> { new EntityHash(1, 10) }, changed);
            Assert.Equal(1, changed.Count, "an id that came back is new again");
            t.Update(0, new List<EntityHash>(), changed);
            Assert.Equal(0, t.Count);
        }

        [Test]
        public void MemberExplanationsNameTheComponents()
        {
            Func<ulong, string> name = h => h == 1 ? "Transform" : h == 2 ? "Curve" : h == 3 ? "Owner" : h == 4 ? "Elevation" : "T" + h;
            var host = M((1, 5), (2, 6), (3, 0), (4, 9));
            var mine = M((1, 5), (2, 7), (3, 8), (5, 1));
            Assert.Equal("Curve (value), Owner (presence on the host, value here), Elevation (only on the host), T5 (only here)", ReplayDiff.ExplainMembers(host, mine, name));
            Assert.Equal("", ReplayDiff.ExplainMembers(host, host, name));
            Assert.Contains("type 0x0000000000000002", ReplayDiff.ExplainMembers(M((2, 1)), M((2, 3)), null));
        }

        [Test]
        public void IconOwnersAndGlobalChannelsAreCompared()
        {
            var a = SyncRef.ForNetId(5);
            var b = SyncRef.ForNetId(6);
            var c = new SyncRef { Kind = SyncRef.SubObjectKey, Id = 7, Index = 2 };
            var host = new List<IconOwnerDigest> { new IconOwnerDigest(a, 2, 10), new IconOwnerDigest(b, 1, 3) };
            var mine = new List<IconOwnerDigest> { new IconOwnerDigest(a, 2, 11), new IconOwnerDigest(c, 1, 4) };
            List<string> d = ReplayDiff.CompareIconOwners(host, mine);
            Assert.Equal(3, d.Count);
            Assert.Equal("#5: 2 icon(s) on the host, 2 here (different kinds or targets)", d[0]);
            Assert.Equal("#6: 1 icon(s) on the host, none here", d[1]);
            Assert.Equal("sub-object 2 of #7: none on the host, 1 icon(s) here", d[2]);
            Assert.Equal(0, ReplayDiff.CompareIconOwners(host, host).Count);
            var g = ReplayDiff.CompareGlobals(new List<GlobalChannelHash> { new GlobalChannelHash(1, "Demand", 1, false), new GlobalChannelHash(2, "Tax", 2, true), new GlobalChannelHash(3, "Names", 3, true) },
                new List<GlobalChannelHash> { new GlobalChannelHash(1, "Demand", 9, false), new GlobalChannelHash(2, "Tax", 2, true) });
            Assert.Equal(2, g.Count);
            Assert.Equal("Names: not readable here", g[0].Text);
            Assert.True(g[0].Strict, "strict channels first");
            Assert.Equal("Demand", g[1].Name);
        }

        private static void Feed(ReplayChecker checker, SyncBatch.Writer w)
        {
            foreach (SyncRecord r in RoundTrip(w)) checker.OnRecord(r);
        }

        private static void Checkpoint(ReplayChecker checker, uint index, Dictionary<uint, ulong> hostHashes, Dictionary<uint, List<ComponentHash>> members, CheckpointSummary summary)
        {
            var w = new SyncBatch.Writer();
            w.ReplayCheckpoint(index, ReplayCheckpointPhase.Begin, 1000 + index, Array.Empty<byte>(), 0, 0);
            var byBucket = new SortedDictionary<uint, List<EntityHash>>();
            var ids = new List<uint>(hostHashes.Keys);
            ids.Sort();
            foreach (uint id in ids)
            {
                uint bucket = EntityDigest.BucketOf(id);
                if (!byBucket.TryGetValue(bucket, out List<EntityHash> list)) byBucket[bucket] = list = new List<EntityHash>();
                list.Add(new EntityHash(id, hostHashes[id]));
                if (members != null && members.TryGetValue(id, out List<ComponentHash> m)) w.ReplayMembers(id, Family.NetEdge, hostHashes[id], m);
            }
            foreach (KeyValuePair<uint, List<EntityHash>> p in byBucket) w.ReplayHashes(index, p.Key, p.Value);
            summary.Index = index;
            summary.Buckets = (uint)byBucket.Count;
            byte[] bytes = summary.ToBytes();
            w.ReplayCheckpoint(index, ReplayCheckpointPhase.End, 1000 + index, bytes, 0, bytes.Length);
            Feed(checker, w);
        }

        private static CheckpointSummary Summary(List<LineStateRow> lines, List<IconOwnerDigest> icons, params GlobalChannelHash[] globals)
        {
            var s = new CheckpointSummary { HasLines = true, Lines = lines, HasIcons = true };
            foreach (IconOwnerDigest d in icons)
            {
                s.IconOwnerDigests.Add(d);
                s.IconOwners++;
                s.IconCount += d.Icons;
                unchecked
                {
                    s.IconSum += d.Sum;
                }
            }
            s.Globals.AddRange(globals);
            return s;
        }

        [Test]
        public void TheCheckerFindsDivergencesExplainsThemAndPassesOnceTheyAreGone()
        {
            var world = new FakeReplayWorld();
            var verdict = new ReplayVerdict(new RecordingHeader { CityName = "Riverside" }, "20261012-1200 Riverside");
            var checker = new ReplayChecker(verdict, world);
            var hostHashes = new Dictionary<uint, ulong> { [1] = 10, [2] = 20, [3] = 30, [200] = 2000 };
            var hostMembers = new Dictionary<uint, List<ComponentHash>> { [1] = M((1, 5)), [2] = M((1, 5), (2, 6)), [3] = M((3, 0)), [200] = M((1, 1)) };
            world.Hashes[1] = 10;
            world.Hashes[2] = 21;
            world.Hashes[4] = 40;
            world.Hashes[200] = 2000;
            world.Members[2] = M((1, 5), (2, 7));
            world.Families[2] = "NetEdge";
            world.Families[4] = "Object";
            world.Lines = new List<LineStateRow> { Row(10, 4, broken: true) };
            world.Icons = new List<IconOwnerDigest> { new IconOwnerDigest(SyncRef.ForNetId(5), 1, 3) };
            world.Globals[GlobalChannel.Tax] = 1;
            world.Globals[77] = 5;
            var tax = new GlobalChannelHash(GlobalChannel.Tax, "Tax", 2, true);
            var demand = new GlobalChannelHash(77, "Demand", 6, false);
            world.Settled = false;
            Checkpoint(checker, 1, hostHashes, hostMembers, Summary(new List<LineStateRow> { Row(10, 4) }, new List<IconOwnerDigest> { new IconOwnerDigest(SyncRef.ForNetId(5), 2, 9) }, tax, demand));
            Assert.Equal(1, checker.CheckpointsCompleted);
            Assert.True(checker.Busy, "icons wait for the reconciliation");
            checker.FrameEnd();
            Assert.True(checker.Busy);
            world.Settled = true;
            checker.FrameEnd();
            Assert.False(checker.Busy);
            CheckpointResult c1 = verdict.Checkpoints[0];
            Assert.True(c1.Complete);
            Assert.Equal(1001U, c1.HostFrame);
            Assert.Equal(4L, c1.Entities);
            Assert.Equal(2, c1.Buckets);
            Assert.Equal(1, c1.Differs);
            Assert.Equal(1, c1.Missing);
            Assert.Equal(1, c1.Extra);
            Assert.Equal(1, c1.LineDifferences.Count);
            Assert.False(c1.IconsMatch.Value);
            Assert.Equal("#5: 2 icon(s) on the host, 1 here", c1.IconDifferences[0]);
            Assert.Equal(1, c1.StrictGlobalDifferences);
            Assert.Equal(2, c1.GlobalDifferences.Count);
            Assert.False(verdict.Passed);
            List<EntityDivergence> persistent = verdict.Persistent;
            Assert.Equal(3, persistent.Count);
            Assert.Equal("Game.Net.Curve (value)", persistent[0].Components);
            Assert.Equal("NetEdge", persistent[0].Family);
            Assert.Equal(DivergenceKind.MissingHere, persistent[1].Kind);
            Assert.Equal("NetEdge", persistent[1].Family, "the family from the recorded members");
            Assert.Equal(DivergenceKind.ExtraHere, persistent[2].Kind);
            Assert.Equal("Object", persistent[2].Family);

            // later: the client caught up (a repair, a late record): everything matches
            world.Hashes.Remove(4);
            world.Hashes[2] = 20;
            world.Hashes[3] = 30;
            world.Lines = new List<LineStateRow> { Row(10, 4) };
            world.Icons = new List<IconOwnerDigest> { new IconOwnerDigest(SyncRef.ForNetId(5), 2, 9) };
            world.Globals[GlobalChannel.Tax] = 2;
            Checkpoint(checker, 2, hostHashes, null, Summary(new List<LineStateRow> { Row(10, 4) }, new List<IconOwnerDigest> { new IconOwnerDigest(SyncRef.ForNetId(5), 2, 9) }, tax, demand));
            checker.FrameEnd();
            verdict.StreamCompleted = true;
            verdict.EndReason = "stopped by the host";
            Assert.True(verdict.Passed, verdict.Summary);
            Assert.Equal(0, verdict.Persistent.Count);
            Assert.Equal(3, verdict.Transient.Count);
            Assert.Contains("Passed: 2 checkpoints, 4 objects, 1 lines, icons, global state equal to the host (3 objects differed for a while)", verdict.Summary);
            string text = verdict.ToText();
            Assert.Contains("PASSED", text);
            Assert.Contains("#2 (NetEdge) differs: Game.Net.Curve (value) [checkpoints 1]", text);
            Assert.Contains("(warning) Demand: differs from the host", text);
            string json = verdict.ToJson();
            Assert.Contains("\"status\": \"passed\"", json.Replace("\":\"", "\": \""));
            Assert.True(SkylineTogether.Core.Json.MiniJson.Parse(json) != null, "valid JSON");
        }

        [Test]
        public void ErrorsAnUnfinishedStreamOrNoCheckpointFailTheVerdict()
        {
            var verdict = new ReplayVerdict(null, "x");
            Assert.Equal("incomplete", verdict.Status);
            Assert.Contains("no checkpoint was completed", verdict.Summary);
            var world = new FakeReplayWorld();
            var checker = new ReplayChecker(verdict, world);
            Checkpoint(checker, 1, new Dictionary<uint, ulong>(), null, new CheckpointSummary());
            verdict.StreamCompleted = false;
            verdict.EndReason = "the replay was stopped";
            Assert.False(verdict.Passed);
            Assert.Contains("did not reach the end (the replay was stopped)", verdict.Summary);
            verdict.StreamCompleted = true;
            Assert.True(verdict.Passed);
            verdict.AddError("applying a batch from Host failed: NullReferenceException");
            Assert.False(verdict.Passed);
            Assert.Contains("1 apply error", verdict.Summary);
            var bad = new SyncBatch.Writer();
            bad.ReplayCheckpoint(2, ReplayCheckpointPhase.End, 1, new byte[] { 200 }, 0, 1);
            Feed(checker, bad);
            Assert.Contains("unreadable summary", verdict.Errors[1]);
        }

        [Test]
        public void IconsChangedBeforeTheySettledAreNotCompared()
        {
            var verdict = new ReplayVerdict(null, "x");
            var world = new FakeReplayWorld { Settled = false };
            var checker = new ReplayChecker(verdict, world);
            Checkpoint(checker, 1, new Dictionary<uint, ulong>(), null, Summary(new List<LineStateRow>(), new List<IconOwnerDigest> { new IconOwnerDigest(SyncRef.ForNetId(5), 2, 9) }));
            var w = new SyncBatch.Writer();
            w.IconSet(SyncRef.ForNetId(5), new List<SyncIcon>());
            Feed(checker, w);
            checker.FrameEnd();
            Assert.False(checker.Busy);
            Assert.Null(verdict.Checkpoints[0].IconsMatch);
            Assert.Contains("newer icon changes", verdict.Notes[0]);
            // and a client whose icons never settle is compared anyway after a while
            Checkpoint(checker, 2, new Dictionary<uint, ulong>(), null, Summary(new List<LineStateRow>(), new List<IconOwnerDigest>()));
            for (int i = 0; i < ReplayChecker.IconSettleFrames; i++) checker.FrameEnd();
            Assert.True(verdict.Checkpoints[1].IconsMatch.Value);
        }
    }
}
