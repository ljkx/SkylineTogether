using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// Reserved wire values (ARCHITECTURE.md#reserved-wire-values): message and control kinds of the original design that
    /// have no sender keep their numbers, and both session ends drop them, like unknown values, without disconnecting
    /// the sender or handing them to a listener.
    /// </summary>
    public sealed class ReservedKindTests
    {
        private static readonly MessageKind[] ReservedMessageKinds = { MessageKind.Clock, MessageKind.HashDetail, MessageKind.RepairRequest, MessageKind.BlobManifest };

        private static readonly ControlKind[] ReservedControlKinds =
        {
            ControlKind.ManifestRequest, ControlKind.SessionSettings, ControlKind.Roles, ControlKind.HostBusy, ControlKind.HostReady,
            ControlKind.SessionReset, ControlKind.ResyncRequest, ControlKind.ResyncStart, ControlKind.ResumeRequest, ControlKind.ResumeResult,
            ControlKind.ProposalReceived, ControlKind.RepairThrottled, ControlKind.LineRowsRequest, ControlKind.LineRows,
        };

        [Test]
        public void ReservedValuesKeepTheirNumbers()
        {
            Assert.Equal(10, (int)MessageKind.Clock);
            Assert.Equal(13, (int)MessageKind.HashDetail);
            Assert.Equal(14, (int)MessageKind.RepairRequest);
            Assert.Equal(15, (int)MessageKind.BlobManifest);
            var numbers = new List<int>();
            foreach (ControlKind k in ReservedControlKinds) numbers.Add((int)k);
            Assert.SequenceEqual(new[] { 2, 4, 6, 7, 8, 10, 15, 16, 22, 23, 24, 25, 27, 28 }, numbers);
            Assert.Equal(30, (int)ControlKind.PlayerAction);
        }

        [Test]
        public void ExactlyTheReservedValuesAreReserved()
        {
            var reserved = new HashSet<MessageKind>(ReservedMessageKinds);
            for (int k = 0; k < 256; k++)
            {
                var kind = (MessageKind)k;
                Assert.Equal(reserved.Contains(kind), Limits.IsReservedKind(kind), "message kind " + k);
                Assert.Equal(Limits.IsKnownKind(kind) && !reserved.Contains(kind), Limits.IsDeliverable(kind), "deliverable " + k);
            }
            var reservedControl = new HashSet<ControlKind>(ReservedControlKinds);
            for (int k = 0; k < 256; k++)
            {
                var kind = (ControlKind)k;
                Assert.Equal(reservedControl.Contains(kind), ControlMessage.IsReservedKind(kind), "control kind " + k);
            }
            Assert.True(ControlMessage.IsKnownKind(ControlKind.PlayerAction), "the last defined control kind is known");
            Assert.False(ControlMessage.IsKnownKind((ControlKind)200));
        }

        [Test]
        public void TheHostIgnoresReservedAndUnknownKindsFromALivePlayer()
        {
            var rig = new SessionRig();
            var (client, _, _) = rig.AddClient("Anna");
            rig.Run(400);
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
            var delivered = new List<MessageKind>();
            rig.Host.AppMessage += (peer, e) => delivered.Add(e.Kind);

            var body = new byte[] { 1, 2, 3, 4 };
            foreach (MessageKind k in ReservedMessageKinds) Assert.True(client.SendToHost(k, body, 0, body.Length), "sent " + k);
            foreach (int k in new[] { 22, 59, 61, 68, 89, 92, 101, 111, 200, 255 }) Assert.True(client.SendToHost((MessageKind)k, body, 0, body.Length), "sent " + k);
            foreach (ControlKind k in ReservedControlKinds) client.SendToHost(MessageKind.Control, Control(k), 0, Control(k).Length);
            byte[] unknownControl = Control((ControlKind)200);
            client.SendToHost(MessageKind.Control, unknownControl, 0, unknownControl.Length);
            rig.Run(20);

            Assert.Equal(0, delivered.Count, "no reserved or unknown kind reaches a listener");
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
            Assert.Equal(1, rig.Host.LivePeerCount, "the player is still in the session");

            // the path itself works: a defined kind is delivered
            client.SendToHost(MessageKind.Proposal, body, 0, body.Length);
            rig.Run(5);
            Assert.SequenceEqual(new[] { MessageKind.Proposal }, delivered);
        }

        [Test]
        public void TheClientIgnoresReservedAndUnknownKindsFromTheHost()
        {
            var rig = new SessionRig();
            var (client, loader, _) = rig.AddClient("Anna", tweakLoader: l => l.Defer = true);
            var delivered = new List<MessageKind>();
            client.AppMessage += e => delivered.Add(e.Kind);
            rig.Run(5);
            var body = new byte[] { 9, 8, 7 };
            // while the client loads (buffered kinds) and once it is live
            for (int round = 0; round < 2; round++)
            {
                foreach (MessageKind k in ReservedMessageKinds) rig.Host.Broadcast(k, body, 0, body.Length);
                foreach (int k in new[] { 22, 59, 200, 255 }) rig.Host.Broadcast((MessageKind)k, body, 0, body.Length);
                foreach (ControlKind k in ReservedControlKinds)
                {
                    byte[] control = Control(k);
                    rig.Host.Broadcast(MessageKind.Control, control, 0, control.Length);
                }
                byte[] unknownControl = Control((ControlKind)200);
                rig.Host.Broadcast(MessageKind.Control, unknownControl, 0, unknownControl.Length);
                rig.Run(200);
                if (round == 0)
                {
                    loader.Finish();
                    rig.Run(20);
                }
            }
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
            Assert.Equal(0, delivered.Count, "nothing reserved or unknown was buffered or delivered");

            rig.Host.Broadcast(MessageKind.Epoch, body, 0, body.Length);
            rig.Run(2);
            Assert.SequenceEqual(new[] { MessageKind.Epoch }, delivered);
        }

        private static byte[] Control(ControlKind kind)
        {
            var w = new ByteWriter(16);
            ControlMessage.Write(w, new ControlMessage { Kind = kind, Payload = new byte[] { 0x5A } });
            return w.ToArray();
        }
    }
}
