using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// A <c>Clock</c> sample (host → client, unreliable; ARCH §6.1, §9.7): the host frame, the simulation's frame time
    /// and smoothed speed, the session speed and pause state and the host's monotonic time. The stream doubles as the
    /// heartbeat.
    /// <code>
    /// u32 hostFrame | f32 frameTime | f32 smoothSpeed | u8 sessionSpeed | bool paused | f64 hostTimeSeconds
    /// </code>
    /// Floats are written as 0 when not finite and rejected when read. The static codec methods are thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class ClockSample
    {
        public uint HostFrame;
        public float FrameTime;
        public float SmoothSpeed;
        public byte SessionSpeed;
        public bool Paused;
        public double HostTimeSeconds;

        public static void Write(ByteWriter w, ClockSample m)
        {
            MessageFields.CheckMessage(w, nameof(w));
            MessageFields.CheckMessage(m, nameof(m));
            w.WriteUInt32(m.HostFrame);
            MessageFields.WriteFloat(w, m.FrameTime);
            MessageFields.WriteFloat(w, m.SmoothSpeed);
            w.WriteByte(m.SessionSpeed);
            w.WriteBool(m.Paused);
            MessageFields.WriteDouble(w, m.HostTimeSeconds);
        }

        public static ClockSample Read(ByteReader r)
        {
            var m = new ClockSample();
            ReadInto(r, m);
            return m;
        }

        /// <summary>Reads into an existing instance (no allocation per sample).</summary>
        public static void ReadInto(ByteReader r, ClockSample into)
        {
            MessageFields.CheckMessage(r, nameof(r));
            MessageFields.CheckMessage(into, nameof(into));
            into.HostFrame = r.ReadUInt32();
            into.FrameTime = MessageFields.ReadFloat(r, "Clock.FrameTime");
            into.SmoothSpeed = MessageFields.ReadFloat(r, "Clock.SmoothSpeed");
            into.SessionSpeed = r.ReadByte();
            into.Paused = r.ReadBool();
            into.HostTimeSeconds = MessageFields.ReadDouble(r, "Clock.HostTimeSeconds");
        }

        /// <summary>Reads a whole message body: <see cref="Read"/> plus a check that nothing follows.</summary>
        public static ClockSample Decode(byte[] buffer, int offset, int count)
        {
            var r = new ByteReader(buffer, offset, count);
            ClockSample m = Read(r);
            MessageFields.EnsureEnd(r, "Clock");
            return m;
        }
    }
}
