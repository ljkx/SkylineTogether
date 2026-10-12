using System;
using System.Collections.Generic;
using System.IO;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Hashing;
using SkylineTogether.Core.Protocol;

namespace SkylineTogether.Core.Replay
{
    /// <summary>
    /// Solo testing (record/replay verdict, ARCH §13.5): the file layout of one recording folder and the stream format.
    /// <para>
    /// A recording is what one client of a session receives, exactly as its transport delivered it: every message's raw
    /// bytes (envelope, compression and fragments included) with the host time and frame it arrived at, plus a few
    /// markers. The host records it through a virtual "recorder peer" that joins its own session like a real player (it
    /// gets the same join snapshot, prefab table, id grants and per-peer streams), so the replay feeds the very bytes a
    /// remote client would have received into the real <c>SessionClient</c> receive path.
    /// </para>
    /// <code>
    /// file   = "STREC" u8 version | varuint headerLength | header | u32 headerChecksum | entry*
    /// entry  = u8 kind | f64 seconds | u32 hostFrame | varuint payloadLength | payload | u32 checksum
    /// </code>
    /// The checksum is the low half of XXH64 over everything of the entry before it. A file whose game crashed ends in a
    /// partial entry: readers stop before it (<see cref="RecordingReader.Truncated"/>).
    /// </summary>
    public static class RecordingFormat
    {
        public static readonly byte[] Magic = { (byte)'S', (byte)'T', (byte)'R', (byte)'E', (byte)'C' };

        public const byte Version = 1;

        /// <summary>The message stream.</summary>
        public const string StreamFileName = "stream.strec";

        /// <summary>The join snapshot as the recorder peer downloaded it (informational; the stream carries its chunks).</summary>
        public const string SnapshotFileName = "snapshot.bin";

        /// <summary>A small JSON summary written when the recording ends (name, city, duration, counts).</summary>
        public const string ManifestFileName = "recording.json";

        /// <summary>The replay verdict, readable.</summary>
        public const string VerdictTextFileName = "verdict.txt";

        /// <summary>The replay verdict as JSON.</summary>
        public const string VerdictJsonFileName = "verdict.json";

        /// <summary>Largest payload of one entry (a transport message plus a little framing).</summary>
        public const int MaxPayloadBytes = Limits.MaxMessageBytes + 64;

        public const int MaxTextBytes = 4096;

        internal const int MaxHeaderBytes = 64 * 1024;
    }

    /// <summary>What a recording entry is.</summary>
    public enum RecordingEntryKind : byte
    {
        /// <summary>The recorder peer's connection to the host opened.</summary>
        Connected = 1,
        /// <summary>One transport message as the client received it (<see cref="RecordingEntry.Lane"/>, raw bytes).</summary>
        Message = 2,
        /// <summary>The host closed the connection (reason and text).</summary>
        Disconnected = 3,
        /// <summary>
        /// A point the replay must not pass before its own client did the same thing (<see cref="RecordingBarrier"/>):
        /// the recorder loads the snapshot at once, a replaying game takes a while.
        /// </summary>
        Barrier = 4,
        /// <summary>A free-text note (start and end of a checkpoint, the recorder's state).</summary>
        Note = 5,
        /// <summary>The recording ended normally (with the reason as text).</summary>
        End = 6,
    }

    /// <summary>Points of the client's own progress the replay waits for.</summary>
    public enum RecordingBarrier : byte
    {
        /// <summary>The client reported that its city load finished (<c>ControlKind.LoadDone</c>).</summary>
        LoadDone = 1,
    }

    /// <summary>The stream header: who recorded what (the replay refuses another protocol version).</summary>
    public sealed class RecordingHeader
    {
        public int ProtocolVersion = Protocol.ProtocolVersion.Current;
        public ulong ModBuild;
        public string GameVersion = "";
        public string SessionName = "";
        public string HostName = "";
        public string CityName = "";
        /// <summary>When the recording started (UTC ticks).</summary>
        public long CreatedUtcTicks;
        /// <summary>The name of the recorder peer as the host's player list shows it.</summary>
        public string RecorderName = "";

        internal void Write(ByteWriter w)
        {
            w.WriteVarInt32(ProtocolVersion);
            w.WriteUInt64(ModBuild);
            w.WriteString(GameVersion ?? "", RecordingFormat.MaxTextBytes);
            w.WriteString(SessionName ?? "", RecordingFormat.MaxTextBytes);
            w.WriteString(HostName ?? "", RecordingFormat.MaxTextBytes);
            w.WriteString(CityName ?? "", RecordingFormat.MaxTextBytes);
            w.WriteInt64(CreatedUtcTicks);
            w.WriteString(RecorderName ?? "", RecordingFormat.MaxTextBytes);
        }

        internal static RecordingHeader Read(ByteReader r)
        {
            var h = new RecordingHeader
            {
                ProtocolVersion = r.ReadVarInt32(),
                ModBuild = r.ReadUInt64(),
                GameVersion = r.ReadString(RecordingFormat.MaxTextBytes),
                SessionName = r.ReadString(RecordingFormat.MaxTextBytes),
                HostName = r.ReadString(RecordingFormat.MaxTextBytes),
                CityName = r.ReadString(RecordingFormat.MaxTextBytes),
                CreatedUtcTicks = r.ReadInt64(),
                RecorderName = r.ReadString(RecordingFormat.MaxTextBytes),
            };
            // fields added by later versions follow here; older readers ignore them
            return h;
        }
    }

    /// <summary>One decoded entry; fields are used according to <see cref="Kind"/>.</summary>
    public sealed class RecordingEntry
    {
        public RecordingEntryKind Kind;
        /// <summary>Host seconds since the recording started.</summary>
        public double Seconds;
        /// <summary>The host's simulation frame when the entry was written.</summary>
        public uint Frame;
        /// <summary><see cref="RecordingEntryKind.Message"/>: the lane and the raw message (its own array).</summary>
        public Lane Lane;
        public byte[] Data;
        /// <summary><see cref="RecordingEntryKind.Disconnected"/>: the transport's reason code.</summary>
        public int Reason;
        /// <summary><see cref="RecordingEntryKind.Disconnected"/>, <see cref="RecordingEntryKind.Note"/>, <see cref="RecordingEntryKind.End"/>.</summary>
        public string Text;
        public RecordingBarrier Barrier;

        public override string ToString() => Kind + " @" + Seconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + "s frame " + Frame
                                             + (Data != null ? " (" + Data.Length + " bytes)" : "") + (Text != null ? " " + Text : "");
    }

    /// <summary>
    /// Writes a recording stream (main thread). Every entry is complete before the next starts, and the buffered output
    /// is flushed about once a second, so a crash loses at most the last second. Write errors stop the recording
    /// (<see cref="Failed"/>) instead of throwing into the session.
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public sealed class RecordingWriter : IDisposable
    {
        private readonly Stream _stream;
        private readonly bool _ownsStream;
        private readonly ByteWriter _entry = new ByteWriter(64 * 1024);
        private readonly ByteWriter _payload = new ByteWriter(64 * 1024);
        private double _lastFlushAt = double.NegativeInfinity;
        private bool _disposed;

        public RecordingWriter(Stream stream, RecordingHeader header, bool ownsStream = true)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            if (header == null) throw new ArgumentNullException(nameof(header));
            _ownsStream = ownsStream;
            Header = header;
            var body = new ByteWriter(512);
            header.Write(body);
            if (body.Length > RecordingFormat.MaxHeaderBytes) throw new ArgumentException("recording header too large", nameof(header));
            _entry.WriteBytes(RecordingFormat.Magic, 0, RecordingFormat.Magic.Length);
            _entry.WriteByte(RecordingFormat.Version);
            _entry.WriteVarUInt32((uint)body.Length);
            _entry.WriteBytes(body.Buffer, 0, body.Length);
            _entry.WriteUInt32(Checksum(_entry.Buffer, 0, _entry.Length));
            _stream.Write(_entry.Buffer, 0, _entry.Length);
            BytesWritten = _entry.Length;
        }

        public RecordingHeader Header { get; }

        /// <summary>Messages written so far.</summary>
        public long Messages { get; private set; }

        /// <summary>Bytes written so far (the file size once flushed).</summary>
        public long BytesWritten { get; private set; }

        /// <summary>Seconds of the newest entry.</summary>
        public double LastSeconds { get; private set; }

        /// <summary>Messages per envelope kind (first byte of the message), for the manifest.</summary>
        public IReadOnlyDictionary<byte, long> MessagesByKind => _byKind;

        private readonly Dictionary<byte, long> _byKind = new Dictionary<byte, long>();

        /// <summary>True once a write failed; nothing is written after that.</summary>
        public bool Failed { get; private set; }

        public string FailureReason { get; private set; }

        /// <summary>True once <see cref="End"/> was written.</summary>
        public bool Ended { get; private set; }

        public void Connected(double seconds, uint frame) => Write(RecordingEntryKind.Connected, seconds, frame);

        public void Message(double seconds, uint frame, Lane lane, byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            if (count + 1 > RecordingFormat.MaxPayloadBytes) throw new ArgumentException("message of " + count + " bytes is too large to record", nameof(count));
            _payload.Reset();
            _payload.WriteByte((byte)lane);
            _payload.WriteBytes(buffer, offset, count);
            if (WriteEntry(RecordingEntryKind.Message, seconds, frame))
            {
                Messages++;
                byte kind = count > 0 ? buffer[offset] : (byte)0;
                _byKind.TryGetValue(kind, out long n);
                _byKind[kind] = n + 1;
            }
        }

        public void Disconnected(double seconds, uint frame, int reason, string text)
        {
            _payload.Reset();
            _payload.WriteVarInt32(reason);
            _payload.WriteString(Trim(text), RecordingFormat.MaxTextBytes);
            WriteEntry(RecordingEntryKind.Disconnected, seconds, frame);
        }

        public void Barrier(double seconds, uint frame, RecordingBarrier barrier)
        {
            _payload.Reset();
            _payload.WriteByte((byte)barrier);
            WriteEntry(RecordingEntryKind.Barrier, seconds, frame);
        }

        public void Note(double seconds, uint frame, string text)
        {
            _payload.Reset();
            _payload.WriteString(Trim(text), RecordingFormat.MaxTextBytes);
            WriteEntry(RecordingEntryKind.Note, seconds, frame);
        }

        /// <summary>The recording ends here (written once; later entries are ignored).</summary>
        public void End(double seconds, uint frame, string reason)
        {
            if (Ended) return;
            _payload.Reset();
            _payload.WriteString(Trim(reason), RecordingFormat.MaxTextBytes);
            WriteEntry(RecordingEntryKind.End, seconds, frame);
            Ended = true;
            Flush();
        }

        /// <summary>Flushes when the last flush is older than a second (call once per frame).</summary>
        public void FlushIfDue(double now)
        {
            if (now - _lastFlushAt < 1.0) return;
            _lastFlushAt = now;
            Flush();
        }

        public void Flush()
        {
            if (Failed || _disposed) return;
            try
            {
                _stream.Flush();
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is NotSupportedException || ex is UnauthorizedAccessException)
            {
                Fail(ex);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            Flush();
            _disposed = true;
            if (!_ownsStream) return;
            try
            {
                _stream.Dispose();
            }
            catch (IOException)
            {
                // best effort
            }
        }

        private void Write(RecordingEntryKind kind, double seconds, uint frame)
        {
            _payload.Reset();
            WriteEntry(kind, seconds, frame);
        }

        private bool WriteEntry(RecordingEntryKind kind, double seconds, uint frame)
        {
            if (Failed || _disposed || Ended) return false;
            _entry.Reset();
            _entry.WriteByte((byte)kind);
            _entry.WriteDouble(seconds);
            _entry.WriteUInt32(frame);
            _entry.WriteVarUInt32((uint)_payload.Length);
            _entry.WriteBytes(_payload.Buffer, 0, _payload.Length);
            _entry.WriteUInt32(Checksum(_entry.Buffer, 0, _entry.Length));
            try
            {
                _stream.Write(_entry.Buffer, 0, _entry.Length);
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is NotSupportedException || ex is UnauthorizedAccessException)
            {
                Fail(ex);
                return false;
            }
            BytesWritten += _entry.Length;
            if (seconds > LastSeconds) LastSeconds = seconds;
            return true;
        }

        private void Fail(Exception ex)
        {
            Failed = true;
            FailureReason = ex.GetType().Name + ": " + ex.Message;
        }

        private static string Trim(string s)
        {
            s = s ?? "";
            return s.Length > 1000 ? s.Substring(0, 1000) : s;
        }

        internal static uint Checksum(byte[] buffer, int offset, int count) => unchecked((uint)XxHash64.Hash(buffer, offset, count, 0x5354524543UL));
    }

    /// <summary>
    /// Reads a recording stream entry by entry (any thread, one at a time). A partial last entry (the game was closed
    /// while recording) ends the stream with <see cref="Truncated"/>; a damaged entry ends it with <see cref="Corrupt"/>.
    /// A bad magic, version or header throws <see cref="DecodeException"/> from the constructor.
    /// </summary>
    public sealed class RecordingReader : IDisposable
    {
        private readonly Stream _stream;
        private readonly bool _ownsStream;
        private readonly ByteReader _reader = new ByteReader();
        private byte[] _buffer = new byte[64 * 1024];
        private long _position;
        private bool _done;

        public RecordingReader(Stream stream, bool ownsStream = true)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _ownsStream = ownsStream;
            int fixedBytes = RecordingFormat.Magic.Length + 1;
            if (!ReadExactly(_buffer, 0, fixedBytes)) throw new DecodeException("not a Skyline Together recording (too short)");
            for (int i = 0; i < RecordingFormat.Magic.Length; i++)
            {
                if (_buffer[i] != RecordingFormat.Magic[i]) throw new DecodeException("not a Skyline Together recording");
            }
            if (_buffer[RecordingFormat.Magic.Length] != RecordingFormat.Version) throw new DecodeException("recording format version " + _buffer[RecordingFormat.Magic.Length] + " is not supported (expected " + RecordingFormat.Version + ")");
            int at = fixedBytes;
            if (!ReadVarUInt(ref at, out uint length) || length > RecordingFormat.MaxHeaderBytes) throw new DecodeException("damaged recording header");
            Ensure(at + (int)length + 4);
            if (!ReadExactly(_buffer, at, (int)length + 4)) throw new DecodeException("truncated recording header");
            uint stored = (uint)(_buffer[at + length] | _buffer[at + length + 1] << 8 | _buffer[at + length + 2] << 16 | _buffer[at + length + 3] << 24);
            if (stored != RecordingWriter.Checksum(_buffer, 0, at + (int)length)) throw new DecodeException("damaged recording header (checksum)");
            _reader.Reset(_buffer, at, (int)length);
            try
            {
                Header = RecordingHeader.Read(_reader);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is IndexOutOfRangeException)
            {
                throw new DecodeException("damaged recording header: " + ex.Message);
            }
        }

        public RecordingHeader Header { get; }

        /// <summary>Bytes consumed so far (header included).</summary>
        public long Position => _position;

        /// <summary>The stream length when it is known (seekable streams), else -1.</summary>
        public long Length
        {
            get
            {
                try
                {
                    return _stream.CanSeek ? _stream.Length : -1;
                }
                catch (Exception)
                {
                    return -1;
                }
            }
        }

        /// <summary>The stream ended in a partial entry (the recording was interrupted).</summary>
        public bool Truncated { get; private set; }

        /// <summary>The stream ended at a damaged entry (<see cref="CorruptReason"/>).</summary>
        public bool Corrupt { get; private set; }

        public string CorruptReason { get; private set; }

        /// <summary>The next entry, or false at the end of the stream (normal, truncated or corrupt).</summary>
        public bool TryRead(out RecordingEntry entry)
        {
            entry = null;
            if (_done) return false;
            // u8 kind, f64 seconds, u32 frame, varuint length (1..5 bytes)
            int head = 1 + 8 + 4;
            int got = ReadUpTo(_buffer, 0, head);
            if (got == 0)
            {
                _done = true;
                return false;
            }
            if (got < head) return Stop(truncated: true, null);
            int at = head;
            if (!ReadVarUInt(ref at, out uint length))
            {
                return Stop(truncated: true, null);
            }
            if (length > RecordingFormat.MaxPayloadBytes) return Stop(false, "entry of " + length + " bytes");
            Ensure(at + (int)length + 4);
            if (!ReadExactly(_buffer, at, (int)length + 4)) return Stop(truncated: true, null);
            uint stored = (uint)(_buffer[at + length] | _buffer[at + length + 1] << 8 | _buffer[at + length + 2] << 16 | _buffer[at + length + 3] << 24);
            if (stored != RecordingWriter.Checksum(_buffer, 0, at + (int)length)) return Stop(false, "checksum mismatch at byte " + _position);
            var kind = (RecordingEntryKind)_buffer[0];
            _reader.Reset(_buffer, 1, 12);
            double seconds = _reader.ReadDouble();
            uint frame = _reader.ReadUInt32();
            _reader.Reset(_buffer, at, (int)length);
            try
            {
                entry = Decode(kind, seconds, frame, (int)length);
            }
            catch (Exception ex) when (ex is DecodeException || ex is ArgumentException || ex is InvalidOperationException || ex is IndexOutOfRangeException)
            {
                return Stop(false, "damaged " + kind + " entry: " + ex.Message);
            }
            if (entry == null) return Stop(false, "unknown entry kind " + (byte)kind);
            return true;
        }

        private RecordingEntry Decode(RecordingEntryKind kind, double seconds, uint frame, int length)
        {
            var e = new RecordingEntry { Kind = kind, Seconds = seconds, Frame = frame };
            switch (kind)
            {
                case RecordingEntryKind.Connected:
                    return e;
                case RecordingEntryKind.Message:
                    if (length < 1) throw new DecodeException("message entry without a lane");
                    e.Lane = (Lane)_reader.ReadByte();
                    e.Data = new byte[length - 1];
                    _reader.ReadBytes(e.Data, 0, length - 1);
                    return e;
                case RecordingEntryKind.Disconnected:
                    e.Reason = _reader.ReadVarInt32();
                    e.Text = _reader.ReadString(RecordingFormat.MaxTextBytes);
                    return e;
                case RecordingEntryKind.Barrier:
                    e.Barrier = (RecordingBarrier)_reader.ReadByte();
                    return e;
                case RecordingEntryKind.Note:
                case RecordingEntryKind.End:
                    e.Text = _reader.ReadString(RecordingFormat.MaxTextBytes);
                    return e;
                default:
                    return null;
            }
        }

        private bool Stop(bool truncated, string corrupt)
        {
            _done = true;
            if (truncated) Truncated = true;
            else
            {
                Corrupt = true;
                CorruptReason = corrupt;
            }
            return false;
        }

        public void Dispose()
        {
            if (_ownsStream) _stream.Dispose();
        }

        private void Ensure(int size)
        {
            if (_buffer.Length >= size) return;
            byte[] bigger = new byte[Math.Max(size, _buffer.Length * 2)];
            Buffer.BlockCopy(_buffer, 0, bigger, 0, _buffer.Length);
            _buffer = bigger;
        }

        /// <summary>Reads a varuint from the stream into the buffer at <paramref name="at"/> (advanced past it).</summary>
        private bool ReadVarUInt(ref int at, out uint value)
        {
            value = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                Ensure(at + 1);
                if (!ReadExactly(_buffer, at, 1)) return false;
                byte b = _buffer[at++];
                value |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return true;
            }
            return false;
        }

        private bool ReadExactly(byte[] buffer, int offset, int count) => ReadUpTo(buffer, offset, count) == count;

        private int ReadUpTo(byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = _stream.Read(buffer, offset + total, count - total);
                if (n <= 0) break;
                total += n;
            }
            _position += total;
            return total;
        }
    }
}
