using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// Sessions under injected faults (loss, duplication, reordering, truncation, bit flips, huge length
    /// prefixes, garbage), hostile and silent peers, stalls and slow consumers. Every case must end deterministically: the
    /// faulty peer is dropped or carries on, the other players stay live, nothing throws out of a tick, the log does not
    /// flood, and a new player can still join afterwards.
    /// </summary>
    public sealed class FaultInjectionTests
    {
        private static void RunUntil(SessionRig rig, Func<bool> done, int maxFrames = 3000)
        {
            for (int i = 0; i < maxFrames && !done(); i++) rig.Run(1);
        }

        private static void RunSeconds(SessionRig rig, double seconds, double step = 0.5)
        {
            for (double t = 0; t < seconds; t += step)
            {
                rig.Clock.Advance(step);
                rig.Host.Tick();
                foreach (var c in rig.Clients) c.Client.Tick();
            }
        }

        /// <summary>A client whose transport injects faults (its outbound messages and the host's messages to it).</summary>
        private static (SessionClient Client, FaultInjectingTransport Faults) AddFaultyClient(SessionRig rig, string name, int seed)
        {
            var info = new FakeInfo();
            var loader = new FakeLoader();
            ulong id = (ulong)(5000 + rig.Clients.Count);
            LoopbackTransport inner = rig.HostTransport.CreateClient(new PeerIdentity(id, "loop:" + name, name));
            var faults = new FaultInjectingTransport(inner, seed);
            var client = new SessionClient(faults, info, loader, new ClientOptions { PlayerName = name, SteamId = id }, rig.Clock, rig.Log);
            client.Connect(new Endpoint());
            rig.Clients.Add((client, loader, info));
            rig.ClientTransports.Add(inner);
            return (client, faults);
        }

        private static bool HasPlayer(SessionRig rig, string name)
        {
            foreach (PlayerInfo p in rig.Host.GetPlayers())
            {
                if (p.Name == name) return true;
            }
            return false;
        }

        private static void AssertNewPlayerCanJoin(SessionRig rig, string name)
        {
            var (late, _, _) = rig.AddClient(name);
            RunUntil(rig, () => late.State == ClientState.Live || late.State == ClientState.Ended);
            Assert.Equal(ClientState.Live, late.State, "a player joining after the faults: " + late.EndReason);
        }

        /// <summary>Traffic in both directions: host broadcasts (some fragmented), client proposals and chat.</summary>
        private static void Traffic(SessionRig rig, SessionClient client, Random random, int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                int size = random.Next(0, 10) == 0 ? random.Next(500_000, 1_200_000) : random.Next(1, 4000);
                var body = new byte[size];
                random.NextBytes(body);
                rig.Host.Broadcast(MessageKind.Epoch, body, 0, body.Length);
                if (client.State == ClientState.Live)
                {
                    var proposal = new byte[random.Next(1, 2000)];
                    random.NextBytes(proposal);
                    client.SendToHost(MessageKind.Proposal, proposal, 0, proposal.Length);
                    if (i % 7 == 0) client.SendChat("hello " + i);
                }
                rig.Run(1);
            }
        }

        [Test]
        public void EachOutboundFaultLeavesTheHostAndTheOtherPlayersUnharmed()
        {
            foreach (Fault fault in new[] { Fault.Drop, Fault.Duplicate, Fault.Reorder, Fault.Truncate, Fault.BitFlip, Fault.HugeLength, Fault.Garbage })
            {
                var rig = new SessionRig();
                var (bob, _, _) = rig.AddClient("Bob");
                var (evil, faults) = AddFaultyClient(rig, "Evil", 7 + (int)fault);
                RunUntil(rig, () => bob.State == ClientState.Live && evil.State == ClientState.Live);
                Assert.Equal(ClientState.Live, evil.State, fault + ": the faulty client joins before the faults start");
                int errorsBefore = rig.Log.Count(LogLevel.Error);
                faults.Outbound(fault, 0.5);
                faults.Armed = true;
                Traffic(rig, evil, new Random(11), 150);
                faults.Armed = false;
                Assert.True(faults.Applied.ContainsKey(fault), fault + ": the fault was injected");
                rig.Run(60);
                Assert.Equal(ClientState.Live, bob.State, fault + ": the other player stays live");
                Assert.True(rig.Log.Count(LogLevel.Error) - errorsBefore < 20, fault + ": no error storm (" + (rig.Log.Count(LogLevel.Error) - errorsBefore) + " errors)");
                if (fault == Fault.HugeLength || fault == Fault.Garbage)
                {
                    // malformed envelopes are protocol violations: the peer is dropped and told nothing it can misread
                    Assert.False(HasPlayer(rig, "Evil"), fault + ": the peer that sends malformed data is dropped");
                }
                AssertNewPlayerCanJoin(rig, "Late" + fault);
            }
        }

        [Test]
        public void EachInboundFaultEndsTheClientCleanlyOrLeavesItLive()
        {
            foreach (Fault fault in new[] { Fault.Drop, Fault.Duplicate, Fault.Reorder, Fault.Truncate, Fault.BitFlip, Fault.HugeLength, Fault.Garbage })
            {
                var rig = new SessionRig();
                var (bob, _, _) = rig.AddClient("Bob");
                var (victim, faults) = AddFaultyClient(rig, "Victim", 31 + (int)fault);
                RunUntil(rig, () => bob.State == ClientState.Live && victim.State == ClientState.Live);
                faults.Inbound(fault, 0.3);
                faults.Armed = true;
                Traffic(rig, victim, new Random(5), 120);
                faults.Armed = false;
                Assert.True(faults.Applied.ContainsKey(fault), fault + ": the fault was injected");
                rig.Run(60);
                Assert.True(victim.State == ClientState.Live || victim.State == ClientState.Ended, fault + ": a definite state, not " + victim.State);
                if (victim.State == ClientState.Ended) Assert.True(!string.IsNullOrEmpty(victim.EndReason), fault + ": the player is told why");
                Assert.Equal(ClientState.Live, bob.State, fault + ": the other player stays live");
                AssertNewPlayerCanJoin(rig, "Late" + fault);
            }
        }

        [Test]
        public void SeedSweepOfMixedFaultsInBothDirectionsNeverThrows()
        {
            for (int seed = 1; seed <= 12; seed++)
            {
                var rig = new SessionRig();
                var (bob, _, _) = rig.AddClient("Bob");
                var (evil, faults) = AddFaultyClient(rig, "Mixed", seed);
                RunUntil(rig, () => bob.State == ClientState.Live && evil.State == ClientState.Live);
                foreach (Fault f in new[] { Fault.Drop, Fault.Duplicate, Fault.Reorder, Fault.Truncate, Fault.BitFlip, Fault.HugeLength, Fault.Garbage })
                {
                    faults.Outbound(f, 0.03);
                    faults.Inbound(f, 0.03);
                }
                faults.Armed = true;
                Traffic(rig, evil, new Random(seed), 200);
                faults.Armed = false;
                rig.Run(120);
                Assert.Equal(ClientState.Live, bob.State, "seed " + seed + ": the other player stays live");
                Assert.True(evil.State == ClientState.Live || evil.State == ClientState.Ended, "seed " + seed + ": " + evil.State);
                AssertNewPlayerCanJoin(rig, "Late" + seed);
            }
        }

        [Test]
        public void AFragmentDeclaringMegabytesCostsNoMemoryAndIsRefusedBeforeAdmission()
        {
            var rig = new SessionRig();
            LoopbackTransport raw = rig.HostTransport.CreateClient(new PeerIdentity(999, "loop:raw", "Raw"));
            IConnection c = raw.Connect(new Endpoint());
            rig.Run(2);
            Assert.Equal(1, rig.HostTransport.ConnectionCount);
            var w = new ByteWriter(64);
            w.WriteByte((byte)MessageKind.Control);
            w.WriteByte((byte)EnvelopeFlags.Fragment);
            w.WriteVarUInt32(1);
            w.WriteVarUInt32(0);
            w.WriteVarUInt32(2);
            w.WriteVarUInt32((uint)Limits.MaxBodyBytes(MessageKind.Control)); // 8 MB declared
            w.WriteBytes(new byte[100], 0, 100);
            byte[] frag = w.ToArray();
            long before = GC.GetAllocatedBytesForCurrentThread();
            c.Send(Lane.Reliable, Delivery.ReliableOrdered, frag, 0, frag.Length);
            rig.Run(3);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated < 2_000_000, "allocated " + allocated + " bytes for a fragment declaring 8 MB");
            Assert.Equal(0, rig.Host.GetPlayers().Count - 1, "nobody was admitted");
            bool dropped = false;
            foreach (string line in rig.Log.Lines) dropped |= line.Contains("malformed message");
            Assert.True(dropped, "the unadmitted connection was dropped as malformed");
        }

        [Test]
        public void AnAdmittedPeersHugeFragmentIsBufferedOnlyAsItArrives()
        {
            var rig = new SessionRig();
            var (anna, _, _) = rig.AddClient("Anna");
            RunUntil(rig, () => anna.State == ClientState.Live);
            IConnection hostSide = rig.ClientTransports[0].GetConnections()[0];
            var w = new ByteWriter(64);
            w.WriteByte((byte)MessageKind.Proposal);
            w.WriteByte((byte)EnvelopeFlags.Fragment);
            w.WriteVarUInt32(77);
            w.WriteVarUInt32(0);
            w.WriteVarUInt32(40);
            w.WriteVarUInt32((uint)Limits.MaxBodyBytes(MessageKind.Proposal)); // the largest proposal
            w.WriteBytes(new byte[64], 0, 64);
            byte[] frag = w.ToArray();
            long before = GC.GetAllocatedBytesForCurrentThread();
            hostSide.Send(Lane.Reliable, Delivery.ReliableOrdered, frag, 0, frag.Length);
            rig.Host.Tick();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated < 1_000_000, "allocated " + allocated + " bytes for the first fragment of a large message");
        }

        [Test]
        public void ACompressionBombBeforeAdmissionIsRefusedWithoutDecompressing()
        {
            var rig = new SessionRig();
            LoopbackTransport raw = rig.HostTransport.CreateClient(new PeerIdentity(998, "loop:bomb", "Bomb"));
            IConnection c = raw.Connect(new Endpoint());
            rig.Run(2);
            var w = new ByteWriter(64);
            w.WriteByte((byte)MessageKind.Control);
            w.WriteByte((byte)(EnvelopeFlags.Compressed | EnvelopeFlags.HasUncompressedSize));
            w.WriteVarUInt32(4 * 1024 * 1024); // 4 MB declared
            w.WriteByte((byte)CompressionKind.ManagedLz4);
            w.WriteBytes(new byte[32], 0, 32);
            byte[] bomb = w.ToArray();
            long before = GC.GetAllocatedBytesForCurrentThread();
            c.Send(Lane.Reliable, Delivery.ReliableOrdered, bomb, 0, bomb.Length);
            rig.Run(3);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated < 2_000_000, "allocated " + allocated + " bytes for a 4 MB bomb before admission");
            Assert.Equal(0, rig.HostTransport.ConnectionCount, "the connection was closed");
        }

        [Test]
        public void AThrowingListenerNeitherStarvesTheOthersNorCountsAgainstThePeer()
        {
            var rig = new SessionRig();
            int received = 0;
            rig.Host.AppMessage += (peer, e) => throw new InvalidOperationException("a feature's bug");
            rig.Host.AppMessage += (peer, e) => received++;
            var (anna, _, _) = rig.AddClient("Anna");
            RunUntil(rig, () => anna.State == ClientState.Live);
            for (int i = 0; i < 80; i++)
            {
                anna.SendToHost(MessageKind.Proposal, new byte[] { 1, 2, 3 }, 0, 3);
                rig.Run(1);
            }
            rig.Run(10);
            Assert.Equal(80, received, "the second listener got every message");
            Assert.Equal(80L, rig.Host.ListenerFailures);
            Assert.Equal(ClientState.Live, anna.State, "a listener's failure is not the peer's fault");
            Assert.True(rig.Log.Count(LogLevel.Error) < 20, "listener failures are logged with a limit");
        }

        [Test]
        public void ASilentLiveClientIsDroppedByTheHostAndAMuteHostEndsTheClient()
        {
            var rig = new SessionRig();
            var (anna, _, _) = rig.AddClient("Anna");
            RunUntil(rig, () => anna.State == ClientState.Live);
            rig.RunHostOnly(100);
            Assert.True(HasPlayer(rig, "Anna"), "100 s of silence is still tolerated");
            rig.RunHostOnly(30);
            Assert.False(HasPlayer(rig, "Anna"), "a live client silent for over 120 s is dropped");

            var rig2 = new SessionRig();
            var (ben, _, _) = rig2.AddClient("Ben");
            RunUntil(rig2, () => ben.State == ClientState.Live);
            rig2.RunClientOnly(ben, 100);
            Assert.Equal(ClientState.Live, ben.State, "100 s without the host is still tolerated");
            rig2.RunClientOnly(ben, 30);
            Assert.Equal(ClientState.Ended, ben.State);
            Assert.Contains("stopped responding", ben.EndReason);
        }

        [Test]
        public void AStallOfTheHostDropsNobody()
        {
            var rig = new SessionRig();
            var (anna, _, _) = rig.AddClient("Anna");
            RunUntil(rig, () => anna.State == ClientState.Live);
            RunSeconds(rig, 70); // past the first minute after going live (the queue-age limit applies)
            rig.HostTransport.SendBufferBytes = 1024; // something stays queued in the host's outbox
            var body = new byte[200_000];
            rig.Host.Broadcast(MessageKind.Epoch, body, 0, body.Length);
            rig.Host.Tick();
            // the host's game freezes for 90 s (a long save, a hitch): nothing ticks
            rig.Clock.Advance(90);
            rig.Host.Tick();
            rig.HostTransport.SendBufferBytes = LoopbackTransport.DefaultSendBufferBytes;
            RunSeconds(rig, 5);
            Assert.True(HasPlayer(rig, "Anna"), "the host's own stall is not the client's fault");
            Assert.Equal(ClientState.Live, anna.State, anna.EndReason);
        }

        [Test]
        public void AJoinThatNeverFinishesIsDroppedAtTheDeadline()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", MaxJoinSeconds = 100 });
            var (anna, _, _) = rig.AddClient("Anna", tweakLoader: l => l.Defer = true); // the load never finishes
            RunUntil(rig, () => anna.State == ClientState.Loading);
            Assert.True(rig.Host.HoldSimulation, "the host holds its simulation for the joiner");
            RunSeconds(rig, 120);
            Assert.False(HasPlayer(rig, "Anna"), "the joiner is dropped at the deadline");
            Assert.False(rig.Host.HoldSimulation, "the host's simulation runs again");
            Assert.Equal(ClientState.Ended, anna.State);
            Assert.Contains("took too long", anna.EndReason);
        }

        [Test]
        public void ASnapshotThatNeverArrivesIsAbandonedAndALateOneIsReleased()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", SnapshotTimeoutSeconds = 50 });
            rig.Snapshots.Hold = true;
            var (anna, _, _) = rig.AddClient("Anna");
            RunUntil(rig, () => anna.State == ClientState.WaitingForSnapshot);
            RunSeconds(rig, 60);
            Assert.Equal(ClientState.Ended, anna.State);
            Assert.Contains("took too long", anna.EndReason);
            // the save system delivers the abandoned snapshot after all: it is released, nobody gets it
            rig.Snapshots.Hold = false;
            rig.Snapshots.DeliverPending();
            Assert.Equal(1, rig.Snapshots.Released.Count, "the late snapshot was released");
            AssertNewPlayerCanJoin(rig, "Ben");
        }

        [Test]
        public void ChatFloodsAreThrottled()
        {
            var rig = new SessionRig();
            var (anna, _, _) = rig.AddClient("Anna");
            RunUntil(rig, () => anna.State == ClientState.Live);
            int lines = 0;
            rig.Host.ChatReceived += c => { if (!c.IsSystem) lines++; };
            for (int i = 0; i < 100; i++) anna.SendChat("spam " + i);
            rig.Run(5);
            Assert.InRange(lines, 1, 6);
        }

        [Test]
        public void AClientThatCannotKeepUpIsDroppedAndOthersAreNot()
        {
            var rig = new SessionRig();
            var (bob, _, _) = rig.AddClient("Bob");
            var (slow, _, _) = rig.AddClient("Slow");
            RunUntil(rig, () => bob.State == ClientState.Live && slow.State == ClientState.Live);
            var body = new byte[256 * 1024];
            // the slow client's game stops reading; the host keeps broadcasting
            for (double t = 0; t < 130; t += 0.5)
            {
                rig.Clock.Advance(0.5);
                rig.Host.Broadcast(MessageKind.Epoch, body, 0, body.Length);
                rig.Host.Tick();
                bob.Tick();
            }
            Assert.False(HasPlayer(rig, "Slow"), "the client that cannot keep up is dropped (its rejoin with a fresh snapshot is the catch-up)");
            Assert.Equal(ClientState.Live, bob.State, "the others keep playing");
            for (int i = 0; i < 50; i++)
            {
                rig.Clock.Advance(0.1);
                rig.Host.Tick();
                slow.Tick();
                bob.Tick();
            }
            Assert.Equal(ClientState.Ended, slow.State, "the slow client learns that it was dropped");
        }

        [Test]
        public void HostStopMidDownloadEndsTheJoinerWithAReason()
        {
            var rig = new SessionRig(snapshotBytes: 3_000_000);
            var (anna, _, _) = rig.AddClient("Anna");
            RunUntil(rig, () => anna.State == ClientState.Downloading);
            rig.Host.Stop("The host left the city.");
            for (int i = 0; i < 200 && anna.State != ClientState.Ended; i++)
            {
                rig.Clock.Advance(0.1);
                anna.Tick();
            }
            Assert.Equal(ClientState.Ended, anna.State);
            Assert.True(!string.IsNullOrEmpty(anna.EndReason), "the joiner is told why");
        }
    }
}
