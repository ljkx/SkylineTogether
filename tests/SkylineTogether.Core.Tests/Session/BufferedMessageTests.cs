using System.Collections.Generic;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// <see cref="SessionClient.MessageBuffered"/> (docs/design/MOD-COMPATIBILITY.md §7): the game layer reads the host's
    /// prefab table while the client still loads, so runtime prefabs of mods exist before the city does. Every buffered
    /// message is shown once, with its body; nothing is shown once the client is live.
    /// </summary>
    public sealed class BufferedMessageTests
    {
        [Test]
        public void MessagesBufferedBeforeTheClientIsLiveAreShownOnce()
        {
            var rig = new SessionRig();
            var (client, loader, _) = rig.AddClient("Anna", tweakLoader: l => l.Defer = true);
            var buffered = new List<(MessageKind Kind, byte[] Body)>();
            var delivered = new List<MessageKind>();
            client.MessageBuffered += (kind, body) => buffered.Add((kind, body));
            client.AppMessage += e => delivered.Add(e.Kind);
            rig.Run(5);
            int before = buffered.Count;
            var body = new byte[] { 4, 5, 6 };
            rig.Host.Broadcast(MessageKind.Epoch, body, 0, body.Length);
            rig.Run(50);
            Assert.True(client.State != ClientState.Live, "still loading");
            Assert.Equal(before + 1, buffered.Count, "the broadcast was buffered and shown");
            Assert.Equal(MessageKind.Epoch, buffered[buffered.Count - 1].Kind);
            Assert.SequenceEqual(body, buffered[buffered.Count - 1].Body);
            Assert.False(delivered.Contains(MessageKind.Epoch), "not delivered before the client is live");

            loader.Finish();
            rig.Run(20);
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
            Assert.True(delivered.Contains(MessageKind.Epoch), "delivered once live");
            int shown = buffered.Count;
            rig.Host.Broadcast(MessageKind.Epoch, body, 0, body.Length);
            rig.Run(5);
            Assert.Equal(shown, buffered.Count, "a live client buffers nothing");
        }
    }
}
