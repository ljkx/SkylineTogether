using System;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Session
{
    /// <summary>
    /// One connection seen as a message pipe: encodes (and compresses) message bodies into envelopes, fragments them
    /// above the transport cap, queues them in the connection's <see cref="PeerOutbox"/>, and on receipt decodes,
    /// reassembles and decodes again. Used on the main thread only (the session layer polls and pumps once per frame).
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public sealed class PeerChannel : IDisposable
    {
        private readonly EnvelopeCodec _codec;
        private readonly Fragmenter _fragmenter = new Fragmenter();
        private readonly Reassembler _reassembler = new Reassembler();
        private readonly ByteWriter _encoded = new ByteWriter(4096);
        private readonly ByteWriter _fragment = new ByteWriter(4096);
        private readonly ByteWriter _decodeScratch = new ByteWriter(4096);
        private readonly ByteWriter _decodeScratch2 = new ByteWriter(4096);
        private Lane _sendLane;
        private bool _sendOk;
        private readonly Action<byte[], int, int> _emit;
        private readonly IClock _clock;

        public PeerChannel(IConnection connection, PeerId peer, IClock clock, ILog log)
        {
            Connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _clock = clock;
            Outbox = new PeerOutbox(connection, peer, new OutboxConfig(), clock, log);
            _codec = new EnvelopeCodec(new ManagedLz4Compressor(), log);
            // one delegate for every send (the lambda with a captured local allocated two objects per message)
            _emit = (b, o, c) =>
            {
                if (Outbox.Send(_sendLane, Delivery.ReliableOrdered, b, o, c) != SendResult.Ok) _sendOk = false;
            };
        }

        public IConnection Connection { get; }

        public PeerOutbox Outbox { get; }

        /// <summary>
        /// The largest message body this channel accepts, below each kind's own limit (int.MaxValue: the kind's
        /// limit). The host lowers it for connections that have not been admitted: before admission only handshake
        /// messages are expected, and a tiny compressed message must not make it decompress megabytes.
        /// </summary>
        public int MaxBodyBytes { get; set; } = int.MaxValue;

        /// <summary>
        /// The kinds this channel accepts and their largest body (0: not accepted), read from the envelope's first byte
        /// before anything is decompressed or reassembled (null: every kind up to its own limit). A message of a kind
        /// that is not accepted is dropped unread (<see cref="DroppedKinds"/>), or rejected with a
        /// <see cref="DecodeException"/> when <see cref="RejectUnexpectedKinds"/> is set.
        /// </summary>
        public Func<MessageKind, int> InboundLimit { get; set; }

        /// <summary>A message of a kind <see cref="InboundLimit"/> does not accept is a protocol error (not just dropped).</summary>
        public bool RejectUnexpectedKinds { get; set; }

        /// <summary>Messages dropped unread because <see cref="InboundLimit"/> does not accept their kind.</summary>
        public long DroppedKinds { get; private set; }

        /// <summary>Bytes produced by decompressing received messages so far (the receiver's CPU cost of this peer).</summary>
        public long DecompressedBytes { get; private set; }

        /// <summary>Encodes and queues one message (reliable, ordered). False once the connection is closed.</summary>
        public bool Send(MessageKind kind, byte[] body, int offset, int count, Lane lane = Lane.Reliable)
        {
            _encoded.Reset();
            _codec.Encode(kind, body, offset, count, _encoded);
            return SendEncoded(kind, _encoded.Buffer, 0, _encoded.Length, lane);
        }

        /// <summary>The compression this channel encodes with (channels with the same kind encode a body identically).</summary>
        public CompressionKind Compression => _codec.Compressor?.Kind ?? CompressionKind.None;

        /// <summary>
        /// Encodes (and compresses) a body into <paramref name="output"/> exactly as <see cref="Send(MessageKind, byte[], int, int, Lane)"/> would, so
        /// a broadcast encodes once and hands the result to every channel with the same <see cref="Compression"/>
        /// (<see cref="SendEncoded"/>). The writer is not reset.
        /// </summary>
        public void Encode(MessageKind kind, byte[] body, int offset, int count, ByteWriter output) => _codec.Encode(kind, body, offset, count, output);

        /// <summary>Fragments and queues an envelope made by <see cref="Encode"/> (reliable, ordered). False once the connection is closed.</summary>
        public bool SendEncoded(MessageKind kind, byte[] envelope, int offset, int count, Lane lane = Lane.Reliable)
        {
            _sendLane = lane;
            _sendOk = true;
            _fragmenter.Split(kind, envelope, offset, count, _fragment, _emit);
            return _sendOk;
        }

        public bool Send(MessageKind kind, ByteWriter body, Lane lane = Lane.Reliable) => Send(kind, body.Buffer, 0, body.Length, lane);

        /// <summary>Sends a small message unreliably (pings, cursors); dropped when the transport is busy.</summary>
        public void SendUnreliable(MessageKind kind, ByteWriter body)
        {
            _encoded.Reset();
            _codec.Encode(kind, body.Buffer, 0, body.Length, _encoded);
            if (_encoded.Length <= Limits.MaxDatagramBytes) Outbox.Send(Lane.Unreliable, Delivery.Unreliable, _encoded.Buffer, 0, _encoded.Length);
        }

        /// <summary>
        /// Decodes one received transport message. Returns false while a fragmented message is incomplete; otherwise
        /// <paramref name="envelope"/> holds the complete, decompressed body (valid until the next call). Throws
        /// <see cref="DecodeException"/> for malformed data.
        /// </summary>
        public bool TryDecode(byte[] buffer, int offset, int count, out Envelope envelope)
        {
            envelope = default;
            int max = MaxBodyBytes;
            Func<MessageKind, int> limit = InboundLimit;
            if (limit != null && buffer != null && count > 0 && offset >= 0 && offset < buffer.Length)
            {
                // the kind is the envelope's first byte: an unexpected kind costs nothing (no decompression, no reassembly)
                var kind = (MessageKind)buffer[offset];
                int allowed = limit(kind);
                if (allowed <= 0)
                {
                    if (RejectUnexpectedKinds) throw new DecodeException(kind + ": not accepted on this connection now");
                    DroppedKinds++;
                    return false;
                }
                max = Math.Min(max, allowed);
            }
            envelope = _codec.Decode(buffer, offset, count, _decodeScratch, max);
            if ((envelope.Flags & EnvelopeFlags.Fragment) == 0)
            {
                if ((envelope.Flags & EnvelopeFlags.Compressed) != 0) DecompressedBytes += envelope.BodyCount;
                return true;
            }
            if (!_reassembler.Accept(envelope, out byte[] message, out int length, max, (_clock != null ? _clock.Seconds : 0.0))) return false;
            envelope = _codec.Decode(message, 0, length, _decodeScratch2, max);
            if ((envelope.Flags & EnvelopeFlags.Compressed) != 0) DecompressedBytes += envelope.BodyCount;
            return true;
        }

        /// <summary>
        /// Drops partially received fragmented messages whose last fragment arrived more than <paramref name="maxIdleSeconds"/>
        /// ago (a peer that starts large messages and never finishes them pins their memory otherwise). Returns the number
        /// dropped; a later fragment of a dropped message is a protocol error.
        /// </summary>
        public int EvictStalePartials(double maxIdleSeconds) => _reassembler.EvictIdle((_clock != null ? _clock.Seconds : 0.0), maxIdleSeconds);

        /// <summary>Partially received fragmented messages (diagnostics, tests).</summary>
        public int PartialMessages => _reassembler.PartialMessages;

        public void Pump() => Outbox.Pump();

        public void Close(int reason, string text)
        {
            try
            {
                Outbox.Pump();
            }
            catch (Exception)
            {
                // best effort before closing
            }
            Connection.Close(reason, text, true);
            Outbox.Dispose();
        }

        public void Dispose() => Outbox.Dispose();
    }
}
