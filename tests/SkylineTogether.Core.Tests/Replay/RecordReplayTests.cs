using System;
using System.Collections.Generic;
using System.IO;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Replay;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Tests.Session;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Tests.Replay
{
    /// <summary>A host with the recorder peer attached, as the game sets it up for "Record test session".</summary>
    internal sealed class RecordRig
    {
        public readonly ManualClock Clock = new ManualClock(50);
        public readonly ListLog Log = new ListLog(LogLevel.Debug);
        public readonly FakeInfo Info = new FakeInfo();
        public readonly FakeSnapshots Snapshots;
        public readonly CompositeTransport Transport = new CompositeTransport(null);
        public readonly SessionHost Host;
        public readonly KeptStream Stream = new KeptStream();
        public readonly KeptStream SnapshotFile = new KeptStream();
        public readonly RecordingWriter Writer;
        public readonly RecorderPeer Recorder;
        public readonly List<(PeerId Peer, MessageKind Kind, byte[] Body)> FromRecorder = new List<(PeerId, MessageKind, byte[])>();
        public uint Frame = 1000;

        public RecordRig(int snapshotBytes = 700_000, HostOptions options = null)
        {
            Snapshots = new FakeSnapshots(snapshotBytes);
            Host = new SessionHost(Transport, Info, Snapshots, options ?? new HostOptions { HostName = "Hosty", SessionName = "Hosty's city" }, Clock, Log);
            Host.Start();
            Host.AppMessage += (peer, e) =>
            {
                var copy = new byte[e.BodyCount];
                Buffer.BlockCopy(e.Body, e.BodyOffset, copy, 0, e.BodyCount);
                FromRecorder.Add((peer, e.Kind, copy));
            };
            Writer = new RecordingWriter(Stream, new RecordingHeader { HostName = "Hosty", SessionName = "Hosty's city", ModBuild = Info.ModBuild, GameVersion = Info.GameVersion });
            Recorder = new RecorderPeer(Transport, Info, Writer, () => SnapshotFile, Clock, () => Frame, Log, new RecorderPeerOptions { Password = Host.Options.Password });
            Recorder.View = v =>
            {
                v.PivotX = 10;
                v.PivotZ = 20;
                v.Zoom = 300;
            };
            Recorder.Start();
        }

        public void Run(int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                Clock.Advance(1 / 60.0);
                Frame++;
                Host.Tick();
                Recorder.Tick();
            }
        }

        /// <summary>Stops the recorder, lets the host drop it, and returns the recording's bytes.</summary>
        public byte[] Finish()
        {
            Recorder.Stop("stopped by the test");
            for (int i = 0; i < 30 && !Recorder.Finished; i++) Run(1);
            Recorder.Dispose();
            Writer.Dispose();
            return Stream.ToArray();
        }
    }

    public sealed class RecordReplayTests
    {
        private static byte[] Batch(int seed, int size)
        {
            var b = new byte[size];
            new DeterministicRandom((ulong)seed).NextBytes(b, 0, size);
            return b;
        }

        [Test]
        public void TheRecorderJoinsLikeAPlayerAndAnswersLikeAPerfectClient()
        {
            var rig = new RecordRig();
            rig.Run(300);
            Assert.True(rig.Recorder.IsLive, rig.Recorder.Client.State + " " + rig.Recorder.Client.EndReason);
            Assert.Equal(1, rig.Host.LivePeerCount, "admitted and live like any player");
            Assert.Equal(1, rig.Snapshots.Requests, "the host took a join snapshot for it");
            Assert.SequenceEqual(rig.Snapshots.Data, rig.SnapshotFile.ToArray(), "the snapshot file is the host's snapshot");
            Assert.Equal(PeerId.Host, rig.Host.GetPlayers()[0].Id);
            Assert.Equal("Test recorder", rig.Host.GetPlayers()[1].Name);
            // the host streams to it
            rig.Host.Broadcast(MessageKind.Epoch, Batch(1, 100), 0, 100);
            rig.Host.SendTo(rig.Recorder.Peer, MessageKind.Epoch, Batch(2, 300), 0, 300);
            var control = new ByteWriter(16);
            control.WriteUInt32(77); // MotionControl sequence number, no operations
            control.WriteUInt16(0);
            rig.Host.Broadcast(MessageKind.MotionControl, control.Buffer, 0, control.Length);
            rig.Run(30);
            Assert.Equal(2L, rig.Recorder.Client.ApplyAcks.Received);
            bool icons = false, ack = false, view = false, motion = false;
            foreach ((PeerId peer, MessageKind kind, byte[] body) in rig.FromRecorder)
            {
                Assert.Equal(rig.Recorder.Peer, peer);
                if (kind == MessageKind.Proposal)
                {
                    List<SyncRecord> records = SyncBatch.Read(body, 0, body.Length);
                    icons |= records.Count == 1 && records[0].Kind == SyncRecordKind.RequestIds && records[0].Request == SyncRequest.IconFullSync;
                }
                if (kind == MessageKind.ApplyAck && ApplyAckMessage.Read(body, 0, body.Length) == 2) ack = true;
                if (kind == MessageKind.View && ViewReport.Read(new ByteReader(body, 0, body.Length)).Zoom == 300f) view = true;
                if (kind == MessageKind.Ack && SkylineTogether.Core.Motion.MotionClientStager.TryReadAck(body, 0, body.Length, out uint seq) && seq == 77) motion = true;
            }
            Assert.True(icons, "asks for the icon sets once live");
            Assert.True(ack, "acknowledges every epoch message as applied");
            Assert.True(view, "reports the camera");
            Assert.True(motion, "acknowledges motion control");
            byte[] file = rig.Finish();
            Assert.Equal(0, rig.Host.LivePeerCount, "the host dropped the recorder");
            Assert.Equal(0, rig.Transport.AttachedCount);
            Assert.True(file.Length > rig.Snapshots.Data.Length, "the stream carries the snapshot chunks");
        }

        [Test]
        public void AReplayedRecordingTakesTheRealClientThroughTheJoinAndDeliversTheSameMessages()
        {
            var rig = new RecordRig(snapshotBytes: 1_100_000, options: new HostOptions { HostName = "Hosty", SessionName = "Hosty's city", Password = "pw" });
            rig.Run(200);
            Assert.True(rig.Recorder.IsLive, rig.Recorder.Client.EndReason);
            var sent = new List<byte[]>();
            void Send(byte[] b)
            {
                rig.Host.SendTo(rig.Recorder.Peer, MessageKind.Epoch, b, 0, b.Length);
                sent.Add(b);
            }
            Send(Batch(1, 40));
            rig.Run(60); // a second later
            var compressible = new byte[90_000]; // compressed on the wire
            for (int i = 0; i < compressible.Length; i++) compressible[i] = (byte)(i / 100);
            Send(compressible);
            rig.Run(120); // two seconds later
            Send(Batch(3, 600_000)); // fragmented on the wire
            rig.Host.SendChat("hello recorder");
            rig.Run(30);
            byte[] file = rig.Finish();

            // replay into a fresh client whose city load takes a while
            var clock = new ManualClock(1000);
            var log = new ListLog(LogLevel.Debug);
            var playback = new PlaybackTransport(new RecordingReader(new MemoryStream(file)), clock, new PlaybackOptions { Speed = 1 }, log);
            var loader = new FakeLoader { Defer = true };
            var client = new SessionClient(playback, new FakeInfo(), loader, new ClientOptions { PlayerName = "Replayer", Password = PlaybackTransport.AnyPassword }, clock, log);
            var received = new List<(double At, byte[] Body)>();
            var chat = new List<string>();
            client.AppMessage += e =>
            {
                if (e.Kind != MessageKind.Epoch) return;
                var copy = new byte[e.BodyCount];
                Buffer.BlockCopy(e.Body, e.BodyOffset, copy, 0, e.BodyCount);
                received.Add((clock.Seconds, copy));
            };
            client.ChatReceived += c => chat.Add(c.Text);
            client.Connect(new Endpoint { Host = "replay" });
            void Tick(int frames)
            {
                for (int i = 0; i < frames; i++)
                {
                    clock.Advance(1 / 60.0);
                    client.Tick();
                }
            }
            Tick(60);
            Assert.Equal(ClientState.Loading, client.State, client.EndReason);
            Assert.SequenceEqual(rig.Snapshots.Data, loader.Target.ToArray(), "the real receiver assembled the recorded snapshot");
            Assert.Equal(RecordingBarrier.LoadDone, playback.WaitingFor.Value);
            Tick(600); // a slow load: nothing goes past the barrier
            Assert.Equal(ClientState.Loading, client.State);
            Assert.Equal(0, received.Count);
            loader.Finish();
            Tick(10); // the host's "live" was recorded a few frames after the recorder's load report
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
            Assert.Equal("Hosty's city", client.Session.SessionName);
            Assert.Equal(rig.Recorder.Peer, client.LocalPeer, "the recorded welcome");
            Tick(400);
            Assert.True(playback.Finished, playback.EndReason);
            Assert.Equal("stopped by the test", playback.EndReason);
            Assert.Equal(ClientState.Live, client.State, "the end of the recording does not end the session by itself");
            Assert.Equal(sent.Count, received.Count);
            for (int i = 0; i < sent.Count; i++) Assert.SequenceEqual(sent[i], received[i].Body, "message " + i);
            Assert.InRange(received[1].At - received[0].At, 0.9, 1.2, "recorded pace");
            Assert.InRange(received[2].At - received[1].At, 1.9, 2.2, "recorded pace");
            Assert.Contains("hello recorder", chat);
            Assert.True(playback.SentByKind.ContainsKey(MessageKind.Hello), "the client's own handshake went nowhere");
            Assert.False(playback.SentByKind.ContainsKey(MessageKind.Refuse));
            client.Leave();
            Assert.True(playback.ClosedByClient);
            client.Dispose();
        }
    }
}
