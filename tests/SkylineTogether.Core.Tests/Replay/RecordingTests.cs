using System;
using System.Collections.Generic;
using System.IO;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Replay;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Tests.Session;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Tests.Replay
{
    /// <summary>A stream that keeps its bytes after disposal (the tests read them afterwards).</summary>
    internal sealed class KeptStream : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            // keep the bytes
        }
    }

    /// <summary>Collects what a transport delivers, with the clock reading of each delivery.</summary>
    internal sealed class SinkLog : ITransportEventSink
    {
        private readonly IClock _clock;
        public readonly List<(double At, byte[] Data, Lane Lane)> Messages = new List<(double, byte[], Lane)>();
        public int Connected;
        public int Disconnected;

        public SinkLog(IClock clock)
        {
            _clock = clock;
        }

        public void OnConnected(IConnection connection) => Connected++;

        public void OnDisconnected(IConnection connection, int reason, string text) => Disconnected++;

        public void OnMessage(IConnection connection, Lane lane, byte[] buffer, int offset, int count)
        {
            var copy = new byte[count];
            Buffer.BlockCopy(buffer, offset, copy, 0, count);
            Messages.Add((_clock.Seconds, copy, lane));
        }
    }

    public sealed class RecordingFormatTests
    {
        private static RecordingHeader Header() => new RecordingHeader
        {
            ModBuild = 42,
            GameVersion = "1.2.3",
            SessionName = "Anna's city",
            HostName = "Anna",
            CityName = "Riverside",
            CreatedUtcTicks = 638000000000000000,
            RecorderName = "Test recorder",
        };

        private static byte[] Sample(out byte[] big)
        {
            var stream = new KeptStream();
            var w = new RecordingWriter(stream, Header());
            big = new byte[200_000];
            new DeterministicRandom(3).NextBytes(big, 0, big.Length);
            w.Connected(0.0, 10);
            w.Message(0.25, 11, Lane.Reliable, new byte[] { 2, 0, 1, 2, 3 }, 0, 5);
            w.Message(0.5, 12, Lane.Bulk, big, 0, big.Length);
            w.Barrier(1.0, 13, RecordingBarrier.LoadDone);
            w.Note(1.5, 14, "checkpoint 1 begins");
            w.Message(2.0, 15, Lane.Unreliable, new byte[] { 8, 0 }, 0, 2);
            w.Disconnected(2.5, 16, 7, "bye");
            w.End(3.0, 17, "stopped by the host");
            w.Message(4.0, 18, Lane.Reliable, new byte[] { 1, 1 }, 0, 2); // after the end: ignored
            Assert.Equal(3L, w.Messages);
            Assert.True(w.Ended);
            w.Dispose();
            return stream.ToArray();
        }

        private static List<RecordingEntry> ReadAll(RecordingReader r)
        {
            var list = new List<RecordingEntry>();
            while (r.TryRead(out RecordingEntry e)) list.Add(e);
            return list;
        }

        [Test]
        public void EntriesRoundTripWithTheirTimesFramesAndBytes()
        {
            byte[] file = Sample(out byte[] big);
            var r = new RecordingReader(new MemoryStream(file));
            Assert.Equal("Riverside", r.Header.CityName);
            Assert.Equal(42UL, r.Header.ModBuild);
            Assert.Equal("Test recorder", r.Header.RecorderName);
            Assert.Equal(ProtocolVersion.Current, r.Header.ProtocolVersion);
            List<RecordingEntry> all = ReadAll(r);
            Assert.False(r.Truncated);
            Assert.False(r.Corrupt);
            Assert.Equal(8, all.Count);
            Assert.Equal(RecordingEntryKind.Connected, all[0].Kind);
            Assert.Equal(10U, all[0].Frame);
            Assert.Equal(RecordingEntryKind.Message, all[1].Kind);
            Assert.Equal(0.25, all[1].Seconds);
            Assert.SequenceEqual(new byte[] { 2, 0, 1, 2, 3 }, all[1].Data);
            Assert.Equal(Lane.Bulk, all[2].Lane);
            Assert.SequenceEqual(big, all[2].Data);
            Assert.Equal(RecordingBarrier.LoadDone, all[3].Barrier);
            Assert.Equal("checkpoint 1 begins", all[4].Text);
            Assert.Equal(Lane.Unreliable, all[5].Lane);
            Assert.Equal(7, all[6].Reason);
            Assert.Equal("bye", all[6].Text);
            Assert.Equal(RecordingEntryKind.End, all[7].Kind);
            Assert.Equal("stopped by the host", all[7].Text);
            Assert.Equal((long)file.Length, r.Position);
        }

        [Test]
        public void ACutOffFileEndsAtTheLastCompleteEntry()
        {
            byte[] file = Sample(out _);
            int headerEnd = (int)new RecordingReader(new MemoryStream(file)).Position;
            Assert.Throws<DecodeException>(() => new RecordingReader(new MemoryStream(file, 0, headerEnd - 2)), "a cut header is refused");
            foreach (int cut in new[] { headerEnd + 3, headerEnd + 14, file.Length / 2, file.Length - 3 })
            {
                var r = new RecordingReader(new MemoryStream(file, 0, cut));
                List<RecordingEntry> all = ReadAll(r);
                Assert.True(r.Truncated, "cut at " + cut);
                Assert.False(r.Corrupt);
                Assert.True(all.Count < 8);
            }
        }

        [Test]
        public void ADamagedEntryStopsTheStreamAsCorrupt()
        {
            byte[] file = Sample(out _);
            file[file.Length / 2] ^= 0x5A; // inside the big message
            var r = new RecordingReader(new MemoryStream(file));
            List<RecordingEntry> all = ReadAll(r);
            Assert.True(r.Corrupt);
            Assert.Contains("checksum", r.CorruptReason);
            Assert.Equal(2, all.Count, "the entries before the damage are intact");
        }

        [Test]
        public void SomethingElseIsRefused()
        {
            Assert.Throws<DecodeException>(() => new RecordingReader(new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })));
            byte[] file = Sample(out _);
            file[5] = 99; // version
            Assert.Throws<DecodeException>(() => new RecordingReader(new MemoryStream(file)));
            file = Sample(out _);
            file[9] ^= 1; // header body
            Assert.Throws<DecodeException>(() => new RecordingReader(new MemoryStream(file)));
        }

        [Test]
        public void ARefusedRecordingClosesAnOwnedStreamOnly()
        {
            byte[] bad = { 1, 2, 3, 4, 5, 6, 7, 8 };
            var owned = new MemoryStream(bad);
            Assert.Throws<DecodeException>(() => new RecordingReader(owned));
            Assert.False(owned.CanRead, "the reader owned the stream: a refused header closes it");
            var borrowed = new MemoryStream(bad);
            Assert.Throws<DecodeException>(() => new RecordingReader(borrowed, ownsStream: false));
            Assert.True(borrowed.CanRead, "a borrowed stream stays open");
        }

        [Test]
        public void AFailingStreamStopsTheRecordingWithoutThrowing()
        {
            var stream = new BrokenStream();
            var w = new RecordingWriter(stream, Header());
            stream.Broken = true;
            Assert.DoesNotThrow(() => w.Message(1, 1, Lane.Reliable, new byte[10], 0, 10));
            Assert.True(w.Failed);
            Assert.Contains("IOException", w.FailureReason);
            Assert.DoesNotThrow(() => w.End(2, 2, "x"));
            Assert.Equal(0L, w.Messages);
        }

        private sealed class BrokenStream : MemoryStream
        {
            public bool Broken;

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Broken) throw new IOException("disk full");
                base.Write(buffer, offset, count);
            }
        }
    }

    public sealed class PlaybackTransportTests
    {
        private static byte[] Paced()
        {
            var stream = new KeptStream();
            var w = new RecordingWriter(stream, new RecordingHeader { HostName = "Anna" });
            w.Connected(0, 1);
            w.Message(0.0, 1, Lane.Reliable, new byte[] { 1 }, 0, 1);
            w.Message(0.5, 2, Lane.Reliable, new byte[] { 2 }, 0, 1);
            w.Barrier(1.0, 3, RecordingBarrier.LoadDone);
            w.Message(2.0, 4, Lane.Reliable, new byte[] { 3 }, 0, 1);
            w.Note(3.0, 5, "halfway");
            w.Message(4.0, 6, Lane.Unreliable, new byte[] { 4 }, 0, 1);
            w.End(5.0, 7, "stopped");
            w.Dispose();
            return stream.ToArray();
        }

        [Test]
        public void TheJoinPlaysAtOnceThenTheBarrierWaitsForTheClient()
        {
            var clock = new ManualClock(100);
            var p = new PlaybackTransport(new RecordingReader(new MemoryStream(Paced())), clock, new PlaybackOptions { Speed = 1 }, NullLog.Instance);
            var sink = new SinkLog(clock);
            IConnection c = p.Connect(new Endpoint());
            Assert.Equal("Recorded host", c.Peer.DisplayName);
            p.Poll(sink);
            Assert.Equal(1, sink.Connected);
            Assert.Equal(2, sink.Messages.Count, "everything before the barrier at once");
            Assert.Equal(RecordingBarrier.LoadDone, p.WaitingFor.Value);
            clock.Advance(30);
            p.Poll(sink);
            Assert.Equal(2, sink.Messages.Count, "the barrier holds however long it takes");
            Assert.False(p.Paced);
        }

        [Test]
        public void AfterTheBarrierTheRecordedTimingIsReproduced()
        {
            var clock = new ManualClock(100);
            var p = new PlaybackTransport(new RecordingReader(new MemoryStream(Paced())), clock, new PlaybackOptions { Speed = 1 }, NullLog.Instance);
            var sink = new SinkLog(clock);
            p.Connect(new Endpoint());
            p.Poll(sink);
            p.Release(RecordingBarrier.LoadDone);
            p.Poll(sink); // passes the barrier at t=100 (recorded 1.0)
            Assert.True(p.Paced);
            Assert.Equal(2, sink.Messages.Count);
            for (int i = 0; i < 9; i++)
            {
                clock.Advance(0.1);
                p.Poll(sink);
            }
            Assert.Equal(2, sink.Messages.Count, "the message recorded 1 s after the barrier is not due after 0.9 s");
            clock.Advance(0.2);
            p.Poll(sink);
            Assert.Equal(3, sink.Messages.Count);
            Assert.Equal(3, sink.Messages[2].Data[0]);
            // the brakes: the playback clock stands still
            p.Hold = () => true;
            for (int i = 0; i < 50; i++)
            {
                clock.Advance(0.1);
                p.Poll(sink);
            }
            Assert.Equal(3, sink.Messages.Count);
            p.Hold = null;
            clock.Advance(1.5);
            p.Poll(sink);
            Assert.Equal(3, sink.Messages.Count, "1.6 s played of the 2 s gap");
            Assert.Equal("halfway", p.LastNote);
            clock.Advance(0.5);
            p.Poll(sink);
            Assert.Equal(4, sink.Messages.Count);
            Assert.Equal(Lane.Unreliable, sink.Messages[3].Lane);
            clock.Advance(2);
            p.Poll(sink);
            Assert.True(p.Finished);
            Assert.Equal("stopped", p.EndReason);
            Assert.Equal(0, sink.Disconnected, "the end of the recording leaves the connection to the caller");
        }

        [Test]
        public void FastPlaybackIgnoresTheTimingButKeepsTheLimits()
        {
            var clock = new ManualClock(0);
            var p = new PlaybackTransport(new RecordingReader(new MemoryStream(Paced())), clock, new PlaybackOptions { Speed = 0, MaxMessagesPerPoll = 1 }, NullLog.Instance);
            var sink = new SinkLog(clock);
            p.Connect(new Endpoint());
            p.Release(RecordingBarrier.LoadDone);
            for (int i = 0; i < 3; i++) p.Poll(sink);
            Assert.Equal(3, sink.Messages.Count, "one per poll");
            p.Poll(sink);
            p.Poll(sink);
            Assert.Equal(4, sink.Messages.Count);
            Assert.True(p.Finished);
        }

        [Test]
        public void TheClientsLoadReportReleasesTheBarrierAndItsMessagesAreCounted()
        {
            var clock = new ManualClock(0);
            var p = new PlaybackTransport(new RecordingReader(new MemoryStream(Paced())), clock, new PlaybackOptions(), NullLog.Instance);
            var sink = new SinkLog(clock);
            IConnection c = p.Connect(new Endpoint());
            p.Poll(sink);
            var channel = new PeerChannel(c, PeerId.Host, clock, NullLog.Instance);
            var payload = new SkylineTogether.Core.Buffers.ByteWriter(16);
            payload.WriteBool(false);
            SessionPayloads.WriteText(payload, "failed");
            var body = new SkylineTogether.Core.Buffers.ByteWriter(32);
            ControlMessage.Write(body, ControlKind.LoadDone, payload.Buffer, 0, payload.Length);
            channel.Send(MessageKind.Control, body);
            channel.Pump();
            p.Poll(sink);
            Assert.Equal(RecordingBarrier.LoadDone, p.WaitingFor.Value, "a failed load does not release it");
            payload.Reset();
            payload.WriteBool(true);
            SessionPayloads.WriteText(payload, "");
            body.Reset();
            ControlMessage.Write(body, ControlKind.LoadDone, payload.Buffer, 0, payload.Length);
            channel.Send(MessageKind.Control, body);
            var big = new byte[600_000]; // fragmented
            channel.Send(MessageKind.Proposal, big, 0, big.Length);
            channel.Pump();
            p.Poll(sink);
            Assert.Null(p.WaitingFor);
            Assert.True(p.Paced);
            Assert.Equal(2L, p.SentByKind[MessageKind.Control]);
            Assert.Equal(1L, p.SentByKind[MessageKind.Proposal], "fragments are counted once, reassembled");
            c.Close(0, "bye", false);
            Assert.True(p.ClosedByClient);
            Assert.Equal(SendResult.Closed, c.Send(Lane.Reliable, Delivery.ReliableOrdered, new byte[] { 1, 0 }, 0, 2));
        }

        [Test]
        public void ATruncatedRecordingFinishesWithAReason()
        {
            byte[] file = Paced();
            var clock = new ManualClock(0);
            var p = new PlaybackTransport(new RecordingReader(new MemoryStream(file, 0, file.Length - 5)), clock, new PlaybackOptions { Speed = 0 }, NullLog.Instance);
            var sink = new SinkLog(clock);
            p.Connect(new Endpoint());
            p.Release(RecordingBarrier.LoadDone);
            p.Poll(sink);
            Assert.True(p.Finished);
            Assert.Contains("cut off", p.EndReason);
            Assert.Equal(4, sink.Messages.Count);
            Assert.Throws<InvalidOperationException>(() => p.Listen(new ListenOptions()));
        }
    }

    public sealed class CompositeTransportTests
    {
        [Test]
        public void AttachedPartsArePolledAndDisposedWithTheHost()
        {
            var composite = new CompositeTransport(null);
            Assert.Equal(TransportKind.Loopback, composite.Kind);
            composite.Listen(new ListenOptions()); // no network part: nothing to do
            Assert.Throws<InvalidOperationException>(() => composite.Connect(new Endpoint()));
            var (host, client) = LoopbackTransport.CreatePair();
            host.Listen(new ListenOptions { MaxConnections = 1 });
            composite.Attach(host);
            composite.Attach(host);
            Assert.Equal(1, composite.AttachedCount);
            client.Connect(new Endpoint());
            var clock = new ManualClock(0);
            var sink = new SinkLog(clock);
            composite.Poll(sink);
            Assert.Equal(1, sink.Connected);
            Assert.True(composite.Detach(host));
            Assert.False(composite.Detach(host));
            composite.Attach(host);
            composite.Dispose();
            Assert.False(host.IsListening, "disposed with the host");
            Assert.Throws<ObjectDisposedException>(() => composite.Attach(host));
        }
    }
}
