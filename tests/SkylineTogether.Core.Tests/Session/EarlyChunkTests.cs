using System;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// Snapshot chunks that arrive before the snapshot manifest (ARCHITECTURE.md#snapshot-join): the host hands the
    /// manifest to the transport first, but with independently ordered lanes (Steam lanes, the network simulator) a
    /// lost and resent manifest arrives after the chunks behind it. The client keeps such chunks and feeds them in.
    /// </summary>
    public sealed class EarlyChunkTests
    {
        [Test]
        public void ChunksThatArriveBeforeTheManifestAreKept()
        {
            // one chunk: the case that used to hang (every chunk dropped, nothing left to ask for)
            var rig = new SessionRig(snapshotBytes: 100_000);
            var info = new FakeInfo();
            var loader = new FakeLoader();
            LoopbackTransport inner = rig.HostTransport.CreateClient(new PeerIdentity(1000, "loop:Anna", "Anna"));
            var transport = new ManifestLastTransport(inner);
            var client = new SessionClient(transport, info, loader, new ClientOptions { PlayerName = "Anna", SteamId = 1000 }, rig.Clock, rig.Log);
            client.Connect(new Endpoint());
            rig.Clients.Add((client, loader, info));
            rig.Run(400);
            Assert.True(transport.HeldManifest, "the manifest was delivered after a chunk");
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
            Assert.SequenceEqual(rig.Snapshots.Data, loader.Target.ToArray(), "the loaded bytes are the host's snapshot");
        }

        /// <summary>Delivers the client's first snapshot manifest only after the first blob chunk.</summary>
        private sealed class ManifestLastTransport : ITransport
        {
            private readonly ITransport _inner;

            public ManifestLastTransport(ITransport inner)
            {
                _inner = inner;
            }

            public bool HeldManifest { get; private set; }

            public TransportKind Kind => _inner.Kind;

            public void Listen(ListenOptions options) => _inner.Listen(options);

            public IConnection Connect(Endpoint endpoint) => _inner.Connect(endpoint);

            public void Poll(ITransportEventSink sink) => _inner.Poll(new Sink(this, sink));

            public void Dispose() => _inner.Dispose();

            private sealed class Sink : ITransportEventSink
            {
                private readonly ManifestLastTransport _owner;
                private readonly ITransportEventSink _sink;

                public Sink(ManifestLastTransport owner, ITransportEventSink sink)
                {
                    _owner = owner;
                    _sink = sink;
                }

                public void OnConnected(IConnection connection) => _sink.OnConnected(connection);

                public void OnDisconnected(IConnection connection, int reason, string text) => _sink.OnDisconnected(connection, reason, text);

                public void OnMessage(IConnection connection, Lane lane, byte[] buffer, int offset, int count)
                {
                    if (_held == null && !_owner.HeldManifest && IsManifest(buffer, offset, count))
                    {
                        _held = new byte[count];
                        Buffer.BlockCopy(buffer, offset, _held, 0, count);
                        _heldLane = lane;
                        return;
                    }
                    _sink.OnMessage(connection, lane, buffer, offset, count);
                    if (_held != null && count > 0 && buffer[offset] == (byte)MessageKind.BlobChunk)
                    {
                        byte[] held = _held;
                        _held = null;
                        _owner.HeldManifest = true;
                        _sink.OnMessage(connection, _heldLane, held, 0, held.Length);
                    }
                }

                private byte[] _held;
                private Lane _heldLane;

                // uncompressed envelope: u8 kind | u8 flags | u8 control kind | ...
                private static bool IsManifest(byte[] b, int offset, int count) =>
                    count > 3 && b[offset] == (byte)MessageKind.Control && b[offset + 1] == 0 && b[offset + 2] == (byte)ControlKind.SnapshotManifest;
            }
        }
    }
}
