using System;
using SkylineTogether.Core.Hashing;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// The city snapshot's integrity checks at session level (ARCHITECTURE.md#blob-transfer): chunks damaged on the bulk
    /// lane are asked for again and the join completes with the host's exact bytes; a snapshot whose chunks are
    /// consistent but whose content differs from the announced digest ends the join instead of loading a damaged city.
    /// </summary>
    public sealed class SnapshotIntegrityTests
    {
        [Test]
        public void ChunksDamagedOnTheWayAreResentAndTheJoinCompletes()
        {
            var rig = new SessionRig(snapshotBytes: 1_300_000); // 5 chunks of 256 KiB
            var (client, loader, transport) = AddDamagingClient(rig, index => index == 1 || index == 3, rehash: false);
            rig.Run(600);
            Assert.Equal(2, transport.Damaged, "the first copy of chunks 1 and 3 was damaged");
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
            Assert.SequenceEqual(rig.Snapshots.Data, loader.Target.ToArray(), "the loaded bytes are the host's snapshot");
            Assert.True(rig.Log.Contains("failed its xxHash64 check"), "the receiver rejected them");
            Assert.True(rig.Log.Contains("0 duplicate, 2 corrupt"), "and counted them");
            Assert.True(rig.Log.Contains("digest verified"));
        }

        [Test]
        public void ASnapshotThatDiffersFromItsDigestEndsTheJoin()
        {
            // a chunk changed and re-hashed: every per-chunk check passes, only the whole-snapshot digest can catch it
            var rig = new SessionRig(snapshotBytes: 1_300_000);
            var (client, loader, transport) = AddDamagingClient(rig, index => index == 2, rehash: true);
            rig.Run(600);
            Assert.Equal(1, transport.Damaged);
            Assert.Equal(ClientState.Ended, client.State);
            Assert.Equal(ClientEndKind.JoinFailed, client.EndKind, client.EndReason);
            Assert.Contains("checksum mismatch", client.EndReason);
            Assert.Equal(0, loader.Loads, "a damaged city is never loaded");
        }

        private static (SessionClient Client, FakeLoader Loader, DamagingTransport Transport) AddDamagingClient(SessionRig rig, Func<int, bool> damage, bool rehash)
        {
            var info = new FakeInfo();
            var loader = new FakeLoader();
            LoopbackTransport inner = rig.HostTransport.CreateClient(new PeerIdentity(1000, "loop:Anna", "Anna"));
            var transport = new DamagingTransport(inner, damage, rehash);
            var client = new SessionClient(transport, info, loader, new ClientOptions { PlayerName = "Anna", SteamId = 1000 }, rig.Clock, rig.Log);
            client.Connect(new Endpoint());
            rig.Clients.Add((client, loader, info));
            return (client, loader, transport);
        }

        /// <summary>
        /// Flips a payload bit in the first copy of the chosen blob chunks the client receives; with <c>rehash</c> it also
        /// rewrites the chunk's xxHash64, so the chunk itself looks intact.
        /// </summary>
        private sealed class DamagingTransport : ITransport
        {
            private readonly ITransport _inner;
            private readonly Func<int, bool> _damage;
            private readonly bool _rehash;
            private readonly System.Collections.Generic.HashSet<int> _done = new System.Collections.Generic.HashSet<int>();

            public DamagingTransport(ITransport inner, Func<int, bool> damage, bool rehash)
            {
                _inner = inner;
                _damage = damage;
                _rehash = rehash;
            }

            public int Damaged { get; private set; }

            public TransportKind Kind => _inner.Kind;

            public void Listen(ListenOptions options) => _inner.Listen(options);

            public IConnection Connect(Endpoint endpoint) => _inner.Connect(endpoint);

            public void Poll(ITransportEventSink sink) => _inner.Poll(new Sink(this, sink));

            public void Dispose() => _inner.Dispose();

            // uncompressed envelope: u8 kind | u8 flags | body; a chunk body: u32 blobId | u32 index | u32 length | u64 hash | payload
            private byte[] MaybeDamage(byte[] buffer, int offset, int count)
            {
                const int body = 2;
                if (count < body + 21 || buffer[offset] != (byte)MessageKind.BlobChunk || buffer[offset + 1] != 0) return null;
                int index = BitConverter.ToInt32(buffer, offset + body + 4);
                if (!_damage(index) || !_done.Add(index)) return null;
                var copy = new byte[count];
                Buffer.BlockCopy(buffer, offset, copy, 0, count);
                int payload = body + 20;
                copy[payload + 1000] ^= 0x40;
                if (_rehash)
                {
                    ulong hash = XxHash64.Hash(copy, payload, count - payload);
                    BitConverter.GetBytes(hash).CopyTo(copy, body + 12);
                }
                Damaged++;
                return copy;
            }

            private sealed class Sink : ITransportEventSink
            {
                private readonly DamagingTransport _owner;
                private readonly ITransportEventSink _sink;

                public Sink(DamagingTransport owner, ITransportEventSink sink)
                {
                    _owner = owner;
                    _sink = sink;
                }

                public void OnConnected(IConnection connection) => _sink.OnConnected(connection);

                public void OnDisconnected(IConnection connection, int reason, string text) => _sink.OnDisconnected(connection, reason, text);

                public void OnMessage(IConnection connection, Lane lane, byte[] buffer, int offset, int count)
                {
                    byte[] damaged = _owner.MaybeDamage(buffer, offset, count);
                    if (damaged != null) _sink.OnMessage(connection, lane, damaged, 0, damaged.Length);
                    else _sink.OnMessage(connection, lane, buffer, offset, count);
                }
            }
        }
    }
}
