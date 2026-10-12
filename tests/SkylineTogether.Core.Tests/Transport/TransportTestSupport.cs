using System;
using System.Collections.Generic;
using System.IO;
using SkylineTogether.Core.Hashing;

namespace SkylineTogether.Core.Tests.Transport
{
    /// <summary>A received message (copied out of the transport's buffer).</summary>
    internal struct ReceivedMessage
    {
        public IConnection Connection;
        public Lane Lane;
        public byte[] Data;
    }

    /// <summary>Records every transport event; an optional hook sees each message while its buffer is valid.</summary>
    internal sealed class RecordingSink : ITransportEventSink
    {
        public readonly List<IConnection> Connected = new List<IConnection>();
        public readonly List<(IConnection Connection, int Reason, string Text)> Disconnected = new List<(IConnection, int, string)>();
        public readonly List<ReceivedMessage> Messages = new List<ReceivedMessage>();
        /// <summary>"C" (connected), "M" (message), "D" (disconnected) in arrival order.</summary>
        public readonly List<string> Events = new List<string>();
        public Action<IConnection, Lane, byte[], int, int> MessageHook;
        public bool KeepMessages = true;
        public long MessageCount;
        public long ByteCount;

        public void OnConnected(IConnection connection)
        {
            Events.Add("C");
            Connected.Add(connection);
        }

        public void OnDisconnected(IConnection connection, int reason, string text)
        {
            Events.Add("D");
            Disconnected.Add((connection, reason, text));
        }

        public void OnMessage(IConnection connection, Lane lane, byte[] buffer, int offset, int count)
        {
            if (KeepMessages) Events.Add("M");
            MessageCount++;
            ByteCount += count;
            if (KeepMessages)
            {
                var data = new byte[count];
                Buffer.BlockCopy(buffer, offset, data, 0, count);
                Messages.Add(new ReceivedMessage { Connection = connection, Lane = lane, Data = data });
            }
            MessageHook?.Invoke(connection, lane, buffer, offset, count);
        }

        public void Clear()
        {
            Connected.Clear();
            Disconnected.Clear();
            Messages.Clear();
            Events.Clear();
            MessageCount = 0;
            ByteCount = 0;
        }
    }

    /// <summary>
    /// A scriptable connection for outbox tests: <see cref="Decide"/> answers each send (Ok by default); accepted
    /// messages are recorded, and with <see cref="TrackPending"/> accepted reliable bytes stay "pending" until
    /// <see cref="Drain"/>. Thread-safe.
    /// </summary>
    internal sealed class FakeConnection : IConnection
    {
        public readonly List<(Lane Lane, Delivery Delivery, byte[] Data)> Sent = new List<(Lane, Delivery, byte[])>();
        public Func<Lane, Delivery, int, SendResult> Decide;
        public bool TrackPending;
        public int PendingReliable;
        public int PendingUnreliable;
        public int SendRate;
        public int SendCalls;
        public int BusyAnswers;
        public bool Closed;
        public bool ThrowOnStats;

        public int Id { get; set; } = 7;

        public PeerIdentity Peer { get; set; } = new PeerIdentity(1, "fake", "Fake");

        public SendResult Send(Lane lane, Delivery delivery, byte[] buffer, int offset, int count)
        {
            lock (this)
            {
                SendCalls++;
                SendResult result = Decide?.Invoke(lane, delivery, count) ?? SendResult.Ok;
                if (result == SendResult.Ok)
                {
                    var data = new byte[count];
                    Buffer.BlockCopy(buffer, offset, data, 0, count);
                    Sent.Add((lane, delivery, data));
                    if (TrackPending && delivery == Delivery.ReliableOrdered) PendingReliable += count;
                }
                else if (result == SendResult.Busy)
                {
                    BusyAnswers++;
                }
                return result;
            }
        }

        public ConnectionStats GetStats()
        {
            lock (this)
            {
                if (ThrowOnStats) throw new InvalidOperationException("stats unavailable");
                return new ConnectionStats
                {
                    PendingReliableBytes = PendingReliable,
                    PendingUnreliableBytes = PendingUnreliable,
                    SendRateBytesPerSecond = SendRate,
                    Quality = 1f
                };
            }
        }

        public void Close(int reason, string text, bool linger)
        {
            lock (this) Closed = true;
        }

        /// <summary>The transport put <paramref name="bytes"/> on the wire.</summary>
        public void Drain(int bytes)
        {
            lock (this) PendingReliable = Math.Max(0, PendingReliable - bytes);
        }

        public List<(Lane Lane, Delivery Delivery, byte[] Data)> SentSnapshot()
        {
            lock (this) return new List<(Lane, Delivery, byte[])>(Sent);
        }
    }

    /// <summary>Message payloads that carry their lane and a sequence number, for ordering checks.</summary>
    internal static class TestMessages
    {
        /// <summary>[u8 tag][u32 sequence][filler up to <paramref name="size"/>] (size ≥ 5).</summary>
        public static byte[] Make(byte tag, int sequence, int size)
        {
            var data = new byte[Math.Max(5, size)];
            data[0] = tag;
            data[1] = (byte)sequence;
            data[2] = (byte)(sequence >> 8);
            data[3] = (byte)(sequence >> 16);
            data[4] = (byte)(sequence >> 24);
            for (int i = 5; i < data.Length; i++) data[i] = (byte)(sequence * 31 + i);
            return data;
        }

        public static byte Tag(byte[] data) => data[0];

        public static int Sequence(byte[] data) => data[1] | (data[2] << 8) | (data[3] << 16) | (data[4] << 24);

        public static int Sequence(byte[] buffer, int offset) => buffer[offset + 1] | (buffer[offset + 2] << 8) | (buffer[offset + 3] << 16) | (buffer[offset + 4] << 24);
    }

    /// <summary>
    /// A read-only seekable stream of deterministic pseudo-random content that is computed from the position (no
    /// memory, any length). <see cref="Mutate"/> changes the content from then on (a source that changes after the
    /// sender hashed it).
    /// </summary>
    internal sealed class SyntheticStream : Stream
    {
        private readonly long _length;
        private ulong _seed;
        private long _position;

        public SyntheticStream(long length, ulong seed)
        {
            _length = length;
            _seed = seed;
        }

        public long ReadCalls;

        public static byte ByteAt(ulong seed, long position)
        {
            ulong word = HashMix.Mix64(seed ^ (ulong)(position >> 3) * 0x9E3779B97F4A7C15UL);
            return (byte)(word >> (int)((position & 7) * 8));
        }

        public void Mutate(ulong newSeed) => _seed = newSeed;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                _position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            long available = _length - _position;
            if (available <= 0) return 0;
            int n = (int)Math.Min(count, available);
            Fill(_seed, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public static void Fill(ulong seed, long position, byte[] buffer, int offset, int count)
        {
            int i = 0;
            // byte-wise until the position is 8-aligned, then whole words
            while (i < count && ((position + i) & 7) != 0)
            {
                buffer[offset + i] = ByteAt(seed, position + i);
                i++;
            }
            while (count - i >= 8)
            {
                ulong word = HashMix.Mix64(seed ^ (ulong)((position + i) >> 3) * 0x9E3779B97F4A7C15UL);
                for (int b = 0; b < 8; b++) buffer[offset + i + b] = (byte)(word >> (b * 8));
                i += 8;
            }
            while (i < count)
            {
                buffer[offset + i] = ByteAt(seed, position + i);
                i++;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? _position + offset : _length + offset;
            Position = target;
            return target;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A target stream that stores nothing: every write is compared with the expected <see cref="SyntheticStream"/>
    /// content at its position, written chunks are remembered as ranges, and reads return the expected content (valid
    /// while <see cref="Mismatches"/> is 0). Options make it non-seekable or non-readable.
    /// </summary>
    internal sealed class VerifyingTargetStream : Stream
    {
        private readonly ulong _seed;
        private readonly long _length;
        private readonly bool _canSeek;
        private readonly bool _canRead;
        private readonly byte[] _expected = new byte[64 * 1024];
        private long _position;

        public VerifyingTargetStream(long length, ulong seed, bool canSeek = true, bool canRead = true)
        {
            _length = length;
            _seed = seed;
            _canSeek = canSeek;
            _canRead = canRead;
        }

        public long Mismatches;
        public long BytesWritten;
        public long Writes;
        public long ReadBytes;
        public int Flushes;
        public bool FailWrites;

        public override bool CanRead => _canRead;
        public override bool CanSeek => _canSeek;
        public override bool CanWrite => true;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set
            {
                if (!_canSeek) throw new NotSupportedException("not seekable");
                _position = value;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (FailWrites) throw new IOException("disk full (simulated)");
            Writes++;
            int done = 0;
            while (done < count)
            {
                int n = Math.Min(_expected.Length, count - done);
                SyntheticStream.Fill(_seed, _position + done, _expected, 0, n);
                for (int i = 0; i < n; i++)
                {
                    if (_expected[i] != buffer[offset + done + i])
                    {
                        Mismatches++;
                        break;
                    }
                }
                done += n;
            }
            _position += count;
            BytesWritten += count;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!_canRead) throw new NotSupportedException("not readable");
            long available = _length - _position;
            if (available <= 0) return 0;
            int n = (int)Math.Min(count, available);
            SyntheticStream.Fill(_seed, _position, buffer, offset, n);
            _position += n;
            ReadBytes += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (!_canSeek) throw new NotSupportedException("not seekable");
            _position = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? _position + offset : _length + offset;
            return _position;
        }

        public override void Flush() => Flushes++;
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>A write-only, non-seekable wrapper (for receivers whose target cannot take chunks out of order).</summary>
    internal sealed class ForwardOnlyStream : Stream
    {
        private readonly Stream _inner;

        public ForwardOnlyStream(Stream inner) => _inner = inner;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    internal static class Digests
    {
        public static Digest128 OfSynthetic(long length, ulong seed)
        {
            using (var s = new SyntheticStream(length, seed)) return Digest128.Compute(s);
        }
    }
}
