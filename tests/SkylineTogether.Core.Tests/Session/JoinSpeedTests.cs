using System;
using System.Collections.Generic;
using System.IO;
using SkylineTogether.Core.Hashing;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Tests.Transport;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>loadspeed: join stage timings, the digest computed once per snapshot, and snapshot reuse.</summary>
    public sealed class JoinSpeedTests
    {
        // ------------------------------------------------------------------ JoinTimeline

        [Test]
        public void TimelineComputesStageDurationsAndIgnoresMissingStages()
        {
            var t = new JoinTimeline { Who = "Anna", SnapshotBytes = 10 * 1048576 };
            Assert.True(double.IsNaN(t.TotalSeconds), "nothing reached yet");
            t.Mark(JoinStage.Start, 100);
            t.Mark(JoinStage.Welcome, 100.5);
            t.Mark(JoinStage.TransferStarted, 104.5);
            t.Mark(JoinStage.Downloaded, 109.5);
            t.Mark(JoinStage.Verified, 109.6);
            t.Mark(JoinStage.LoadStarted, 109.6);
            t.Mark(JoinStage.Loaded, 129.6);
            t.Mark(JoinStage.Live, 130.0);
            t.Mark(JoinStage.Start, 50); // the first mark wins
            Assert.Near(0.5, t.ConnectSeconds, 1e-9);
            Assert.Near(4.0, t.WaitSeconds, 1e-9);
            Assert.Near(5.0, t.TransferSeconds, 1e-9);
            Assert.Near(2 * 1048576, t.TransferBytesPerSecond, 1);
            Assert.Near(0.1, t.VerifySeconds, 1e-9);
            Assert.Near(20.0, t.LoadSeconds, 1e-9);
            Assert.Near(0.4, t.ConfirmSeconds, 1e-9);
            Assert.Near(30.0, t.TotalSeconds, 1e-9);
            Assert.True(double.IsNaN(t.CatchUpSeconds), "no catch-up yet");
            Assert.Equal(-1L, JoinTimeline.Ms(t.CatchUpSeconds));
            Assert.Equal(30000L, JoinTimeline.Ms(t.TotalSeconds));
            t.Mark(JoinStage.Start, 90, overwrite: true);
            Assert.Near(40.0, t.TotalSeconds, 1e-9, "an overwrite replaces the mark");
            string line = t.Describe();
            Assert.Contains("of Anna", line);
            Assert.Contains("transfer 5.00 s for 10.0 MiB at 2.00 MiB/s", line);
            Assert.Contains("load 20.00 s", line);
            Assert.Contains("time to live 40.00 s", line);
        }

        [Test]
        public void TimelineReportsTheCatchUpAndClonesIndependently()
        {
            var t = new JoinTimeline();
            t.Mark(JoinStage.Live, 10);
            t.BacklogMessages = 40;
            t.BacklogRemaining = 12;
            Assert.True(t.CatchingUp);
            JoinTimeline copy = t.Clone();
            t.Mark(JoinStage.CaughtUp, 11.5);
            t.BacklogRemaining = 0;
            Assert.False(t.CatchingUp);
            Assert.Near(1.5, t.CatchUpSeconds, 1e-9);
            Assert.True(copy.CatchingUp, "the copy keeps its own stages");
            Assert.False(copy.Has(JoinStage.CaughtUp));
            Assert.Equal(40, copy.BacklogMessages);
            Assert.Contains("catch-up 1.50 s", t.Describe());
        }

        [Test]
        public void TimelineRejectsReversedStages()
        {
            var t = new JoinTimeline();
            t.Mark(JoinStage.Loaded, 5);
            t.Mark(JoinStage.Live, 4);
            Assert.True(double.IsNaN(t.ConfirmSeconds), "a reversed pair has no duration");
            t.Mark(JoinStage.Start, double.NaN);
            Assert.False(t.Has(JoinStage.Start), "NaN is not a mark");
        }

        // ------------------------------------------------------------------ digest while writing

        [Test]
        public void DigestingStreamComputesTheDigestOfEverythingWritten()
        {
            var data = new byte[1_234_567];
            new DeterministicRandom(3).NextBytes(data, 0, data.Length);
            var inner = new MemoryStream();
            var rng = new DeterministicRandom(9);
            using (var s = new DigestingWriteStream(inner, leaveOpen: true))
            {
                int at = 0;
                while (at < data.Length)
                {
                    int n = Math.Min(data.Length - at, rng.Next(1, 100_000));
                    s.Write(data, at, n);
                    at += n;
                }
                Assert.Equal((long)data.Length, s.BytesWritten);
                Assert.Equal(Digest128.Compute(data, 0, data.Length), s.Finish());
                Assert.False(s.CanSeek, "forward-only");
                Assert.Throws<NotSupportedException>(() => s.Position = 0);
                Assert.Throws<NotSupportedException>(() => s.Seek(0, SeekOrigin.Begin));
            }
            Assert.SequenceEqual(data, inner.ToArray(), "the bytes reached the inner stream unchanged");
            Assert.True(inner.CanWrite, "leaveOpen keeps the inner stream");
            var owned = new MemoryStream();
            new DigestingWriteStream(owned).Dispose();
            Assert.False(owned.CanWrite, "the inner stream is disposed with it");
        }

        [Test]
        public void SenderWithAKnownDigestReadsNothingBeforeTheFirstChunk()
        {
            var clock = new ManualClock(1);
            var log = new ListLog(LogLevel.Debug);
            const long size = 3 * 262144 + 777;
            var source = new SyntheticStream(size, 42);
            var buffer = new byte[size];
            SyntheticStream.Fill(42, 0, buffer, 0, buffer.Length);
            Digest128 digest = Digest128.Compute(buffer, 0, buffer.Length);
            var connection = new FakeConnection();
            var outbox = new PeerOutbox(connection, new PeerId(1), new OutboxConfig(), clock, log);
            var sender = new BlobSender(outbox, log);
            BlobManifestInfo info = sender.Start(5, source, "snapshot", BlobCodec.DefaultChunkBytes, digest);
            Assert.Equal(0L, source.ReadCalls, "no hashing pass");
            Assert.Equal(digest, info.Digest);
            Assert.Equal(0.0, sender.DigestMilliseconds);
            for (int i = 0; i < 100 && !(sender.IsComplete && outbox.QueuedMessages == 0); i++)
            {
                sender.Pump();
                outbox.Pump();
            }
            Assert.True(sender.IsComplete);
            var target = new MemoryStream();
            var receiver = new BlobReceiver(info, target, log);
            foreach ((Lane _, Delivery _, byte[] data) in connection.SentSnapshot()) receiver.OnChunk(data, 2, data.Length - 2);
            Assert.True(receiver.IsComplete);
            Assert.True(receiver.VerifyDigest(), "the known digest matches what arrived");
            Assert.SequenceEqual(buffer, target.ToArray());

            var hashed = new BlobSender(new PeerOutbox(new FakeConnection(), new PeerId(2), new OutboxConfig(), clock, log), log);
            BlobManifestInfo again = hashed.Start(6, new SyntheticStream(size, 42), "snapshot");
            Assert.Equal(digest, again.Digest, "hashing the source gives the same digest");
            Assert.True(hashed.DigestMilliseconds >= 0);
        }

        [Test]
        public void TheReceiverVerifiesTheDigestWhileChunksArriveWithoutReadingBack()
        {
            var log = new ListLog(LogLevel.Debug);
            const long size = 5 * 262144 + 12345;
            var buffer = new byte[size];
            SyntheticStream.Fill(77, 0, buffer, 0, buffer.Length);
            var connection = new FakeConnection();
            var clock = new ManualClock(1);
            var outbox = new PeerOutbox(connection, new PeerId(1), new OutboxConfig(), clock, log);
            var sender = new BlobSender(outbox, log);
            BlobManifestInfo info = sender.Start(9, new MemoryStream(buffer, false), "snapshot");
            for (int i = 0; i < 100 && !(sender.IsComplete && outbox.QueuedMessages == 0); i++)
            {
                sender.Pump();
                outbox.Pump();
            }
            // a write-only, forward-only target (like the game's asset write stream behind the bundle splitter)
            var sink = new MemoryStream();
            var target = new DigestingWriteStream(sink, leaveOpen: true);
            var receiver = new BlobReceiver(info, target, log);
            foreach ((Lane _, Delivery _, byte[] data) in connection.SentSnapshot()) Assert.True(receiver.OnChunk(data, 2, data.Length - 2));
            Assert.True(receiver.IsComplete);
            Assert.True(receiver.VerifyDigest(), "the running digest matches without reading anything back");
            Assert.Equal(info.Digest, target.Finish(), "what reached the target is the blob");
        }

        // ------------------------------------------------------------------ session: timings, digest once, reuse

        [Test]
        public void JoinStagesAreTimedOnTheClientAndTheHost()
        {
            var rig = new SessionRig(snapshotBytes: 1_000_000);
            var (client, loader, _) = rig.AddClient("Anna");
            loader.Defer = true;
            rig.Run(300);
            Assert.Equal(ClientState.Loading, client.State, client.EndReason);
            rig.Clock.Advance(2.5); // the game's load takes a while
            loader.Finish();
            rig.Run(30);
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
            JoinTimeline c = client.Timeline;
            foreach (JoinStage s in new[] { JoinStage.Start, JoinStage.Welcome, JoinStage.TransferStarted, JoinStage.Downloaded, JoinStage.Verified, JoinStage.LoadStarted, JoinStage.Loaded, JoinStage.Live })
            {
                Assert.True(c.Has(s), "client stage " + s);
            }
            Assert.Equal(1_000_000L, c.SnapshotBytes);
            Assert.True(c.LoadSeconds >= 2.5, "the load is timed: " + c.LoadSeconds);
            Assert.True(c.TransferBytesPerSecond > 0);
            Assert.False(c.Has(JoinStage.CaughtUp), "the replication layer reports the catch-up");
            client.ReportBacklog(3);
            Assert.True(c.CatchingUp);
            client.ReportBacklog(0);
            Assert.True(c.Has(JoinStage.CaughtUp));
            Assert.False(c.CatchingUp);
            Assert.True(rig.Log.Contains("join timings of this player"), "the client logs its timings once caught up");

            IReadOnlyList<JoinTimeline> joins = rig.Host.RecentJoins;
            Assert.Equal(1, joins.Count);
            JoinTimeline h = joins[0];
            Assert.Equal("Anna", h.Who);
            foreach (JoinStage s in new[] { JoinStage.Start, JoinStage.SnapshotRequested, JoinStage.SnapshotReady, JoinStage.TransferStarted, JoinStage.TransferSent, JoinStage.Downloaded, JoinStage.Loaded, JoinStage.Live })
            {
                Assert.True(h.Has(s), "host stage " + s);
            }
            Assert.Equal(1_000_000L, h.SnapshotBytes);
            Assert.False(h.DigestReused, "the fake provider gives no digest: hashed once here");
            Assert.True(h.TotalSeconds >= 2.5);
            Assert.Equal(1, rig.Host.JoinsCompleted);
            Assert.NotNull(rig.Host.LastJoin);
            Assert.True(rig.Log.Contains("join timings of Anna"), "the host logs the timings when the player is live");
        }

        [Test]
        public void AProviderDigestMeansTheHostNeverHashesTheFile()
        {
            var rig = new SessionRig(snapshotBytes: 700_000);
            rig.Snapshots.ProvideDigest = true;
            var (client, loader, _) = rig.AddClient("Anna");
            rig.Run(300);
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
            Assert.SequenceEqual(rig.Snapshots.Data, loader.Target.ToArray());
            Assert.False(rig.Log.Contains("(hashed in"), "no hashing pass on the host");
            Assert.True(rig.Host.RecentJoins[0].DigestReused);
        }

        [Test]
        public void APlayerAdmittedWhileAnUnchangedSnapshotIsTakenRidesOnIt()
        {
            var rig = new SessionRig();
            rig.Snapshots.Hold = true;
            var (anna, la, _) = rig.AddClient("Anna");
            rig.Run(60);
            var (ben, lb, _) = rig.AddClient("Ben");
            rig.Run(60);
            Assert.Equal(ClientState.WaitingForSnapshot, ben.State, ben.EndReason);
            Assert.Equal(1, rig.Snapshots.Requests);
            rig.Snapshots.Hold = false;
            rig.Snapshots.DeliverPending();
            rig.Run(400);
            Assert.Equal(ClientState.Live, anna.State, anna.EndReason);
            Assert.Equal(ClientState.Live, ben.State, ben.EndReason);
            Assert.Equal(1, rig.Snapshots.Requests, "nothing changed: one snapshot for both");
            Assert.SequenceEqual(rig.Snapshots.Data, lb.Target.ToArray());
            Assert.SequenceEqual(rig.Snapshots.Data, la.Target.ToArray());
            Assert.Equal(1, rig.Snapshots.Released.Count, "the shared file was released once");
            Assert.Equal(1, CountLines(rig.Log, "(hashed in"), "the digest was computed once for both transfers");
            Assert.Equal(1, CountLines(rig.Log, " (known)"), "the second transfer reused it");
            JoinTimeline benJoin = Find(rig.Host.RecentJoins, "Ben");
            Assert.True(benJoin.SnapshotReused);
            Assert.True(benJoin.DigestReused);
        }

        [Test]
        public void ABroadcastAfterTheRequestMeansALaterPlayerGetsANewSnapshot()
        {
            var rig = new SessionRig();
            rig.Snapshots.Hold = true;
            var (anna, _, _) = rig.AddClient("Anna");
            rig.Run(60);
            rig.Host.Broadcast(MessageKind.Epoch, new byte[] { 1, 2, 3 }, 0, 3); // the city changed
            var (ben, _, _) = rig.AddClient("Ben");
            rig.Run(60);
            rig.Snapshots.Hold = false;
            rig.Snapshots.DeliverPending();
            rig.Run(400);
            Assert.Equal(ClientState.Live, anna.State, anna.EndReason);
            Assert.Equal(ClientState.Live, ben.State, ben.EndReason);
            Assert.Equal(2, rig.Snapshots.Requests, "Ben needed a snapshot of the changed city");
            Assert.False(Find(rig.Host.RecentJoins, "Ben").SnapshotReused);
        }

        [Test]
        public void AFinishedUnchangedSnapshotServesAPlayerWhileTheFirstStillDownloads()
        {
            var rig = new SessionRig(snapshotBytes: 5_000_000);
            var (a, la, _) = rig.AddClient("Anna");
            rig.Run(3);
            var (b, lb, _) = rig.AddClient("Ben");
            rig.Run(800);
            Assert.Equal(ClientState.Live, a.State, a.EndReason);
            Assert.Equal(ClientState.Live, b.State, b.EndReason);
            Assert.Equal(1, rig.Snapshots.Requests);
            Assert.Equal(1, rig.Snapshots.Released.Count);
            Assert.SequenceEqual(rig.Snapshots.Data, lb.Target.ToArray());
        }

        [Test]
        public void ASnapshotOlderThanTheReuseWindowIsNotReused()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", SnapshotReuseSeconds = 5 });
            rig.Snapshots.Hold = true;
            rig.AddClient("Anna");
            rig.Run(60);
            rig.RunHostOnly(6); // the save takes longer than the window
            var (ben, _, _) = rig.AddClient("Ben");
            rig.Run(30);
            rig.Snapshots.Hold = false;
            rig.Snapshots.DeliverPending();
            rig.Run(400);
            Assert.Equal(ClientState.Live, ben.State, ben.EndReason);
            Assert.Equal(2, rig.Snapshots.Requests);
        }

        private static int CountLines(ListLog log, string text)
        {
            int n = 0;
            foreach (string line in log.Lines) if (line.Contains(text)) n++;
            return n;
        }

        private static JoinTimeline Find(IReadOnlyList<JoinTimeline> joins, string who)
        {
            foreach (JoinTimeline t in joins) if (t.Who == who) return t;
            Assert.Fail("no join of " + who);
            return null;
        }
    }
}
