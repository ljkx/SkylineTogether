using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;

namespace SkylineTogether.Core.Session
{
    /// <summary>Where a player is in the session.</summary>
    public enum PlayerState : byte
    {
        Handshaking,
        WaitingForSnapshot,
        Downloading,
        Loading,
        Live
    }

    /// <summary>One row of the player list the host broadcasts (<see cref="ControlKind.Players"/>).</summary>
    public sealed class PlayerInfo
    {
        public PeerId Id;
        public string Name;
        public ulong SteamId;
        public SessionRole Role;
        public PlayerState State;
        public int PingMs;
        /// <summary>Join progress 0..1 while downloading or loading.</summary>
        public float Progress;

        public PlayerInfo Clone() => (PlayerInfo)MemberwiseClone();
    }

    /// <summary>The game speed the host dictates (<see cref="ControlKind.SpeedChanged"/>).</summary>
    public struct SpeedState : IEquatable<SpeedState>
    {
        /// <summary>The slowest and the fastest exact speed a peer accepts (a speed mod's range; the game's own speeds are 1, 2 and 4).</summary>
        public const float MinExact = 0.01f;
        public const float MaxExact = 8f;

        /// <summary>
        /// The speed rounded to the game's 1, 2 or 4: what a peer that does not read <see cref="Exact"/> runs at
        /// (<see cref="Bucket"/>).
        /// </summary>
        public int Speed;
        public bool Paused;
        /// <summary>The session holds the simulation (a player is joining or resyncing).</summary>
        public bool Held;
        /// <summary>
        /// The exact speed multiplier when it is none of the game's 1, 2 or 4 (a speed mod such as Advanced Simulation Speed
        /// selects 0.25, 3 or 8), else 0. Travels as an optional field after the two bytes older peers read.
        /// </summary>
        public float Exact;

        /// <summary>The multiplier the simulation runs at (when not paused): <see cref="Exact"/>, else <see cref="Speed"/>.</summary>
        public float Multiplier => Exact > 0f ? Exact : Speed;

        /// <summary>The game speed a multiplier is rounded to for peers that only know the game's own: 1, 2 or 4.</summary>
        public static int Bucket(float multiplier) => multiplier >= 3.5f ? 4 : multiplier >= 1.5f ? 2 : 1;

        /// <summary>The state for a speed multiplier (clamped to the accepted range; exact only when it is not 1, 2 or 4).</summary>
        public static SpeedState Of(float multiplier, bool paused, bool held)
        {
            float m = Sanitize(multiplier);
            int bucket = Bucket(m);
            return new SpeedState { Speed = bucket, Paused = paused, Held = held, Exact = m == bucket ? 0f : m };
        }

        /// <summary>A multiplier within <see cref="MinExact"/>..<see cref="MaxExact"/> (NaN and infinity count as 1).</summary>
        public static float Sanitize(float multiplier)
        {
            if (float.IsNaN(multiplier) || float.IsInfinity(multiplier)) return 1f;
            return Math.Max(MinExact, Math.Min(MaxExact, multiplier));
        }

        public bool Equals(SpeedState o) => Speed == o.Speed && Paused == o.Paused && Held == o.Held && Exact.Equals(o.Exact);
        public override bool Equals(object obj) => obj is SpeedState o && Equals(o);
        public override int GetHashCode() => Speed ^ (Paused ? 64 : 0) ^ (Held ? 128 : 0) ^ Exact.GetHashCode();
        public override string ToString() => (Held ? "held " : "") + (Paused ? "paused" : "speed " + (Exact > 0f ? Exact.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : Speed.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>The session settings sent in <see cref="WelcomeMessage.SessionSettings"/>.</summary>
    public sealed class SessionInfo
    {
        public string SessionName;
        public string HostName;
        /// <summary>Gameplay permissions for builders.</summary>
        public Permission BuilderPermissions = Permission.All;
    }

    /// <summary>What a <see cref="ControlKind.PlayerAction"/> request asks for.</summary>
    public enum PlayerActionKind : byte
    {
        Kick,
        SetRole
    }

    /// <summary>Codecs of the small session payloads carried in <see cref="ControlMessage"/>s.</summary>
    public static class SessionPayloads
    {
        private const int MaxPlayers = 255;

        public static void WritePlayers(ByteWriter w, IReadOnlyList<PlayerInfo> players)
        {
            w.WriteVarUInt32((uint)players.Count);
            foreach (PlayerInfo p in players)
            {
                w.WriteByte(p.Id.Value);
                MessageFields.WriteText(w, p.Name, Limits.MaxNameChars);
                w.WriteUInt64(p.SteamId);
                w.WriteByte((byte)p.Role);
                w.WriteByte((byte)p.State);
                w.WriteVarUInt32((uint)Math.Max(0, p.PingMs));
                w.WriteSingle(p.Progress);
            }
        }

        public static List<PlayerInfo> ReadPlayers(ByteReader r)
        {
            uint count = r.ReadVarUInt32();
            if (count > MaxPlayers) throw new DecodeException("player list of " + count + " entries");
            var list = new List<PlayerInfo>((int)count);
            for (int i = 0; i < count; i++)
            {
                list.Add(new PlayerInfo
                {
                    Id = new PeerId(r.ReadByte()),
                    Name = MessageFields.ReadText(r, Limits.MaxNameChars, "player name"),
                    SteamId = r.ReadUInt64(),
                    Role = (SessionRole)r.ReadByte(),
                    State = (PlayerState)r.ReadByte(),
                    PingMs = (int)Math.Min(r.ReadVarUInt32(), 99999),
                    Progress = r.ReadSingle(),
                });
            }
            return list;
        }

        // flag bits of the speed payload's second byte; the exact multiplier follows as a float when ExactFlag is set. Older
        // peers read the two bytes only (the rounded 1, 2 or 4) and ignore the rest.
        private const int PausedFlag = 1, HeldFlag = 2, ExactFlag = 4;

        public static void WriteSpeed(ByteWriter w, SpeedState s)
        {
            bool exact = s.Exact >= SpeedState.MinExact && s.Exact <= SpeedState.MaxExact;
            w.WriteByte((byte)Math.Max(0, Math.Min(255, s.Speed)));
            w.WriteByte((byte)((s.Paused ? PausedFlag : 0) | (s.Held ? HeldFlag : 0) | (exact ? ExactFlag : 0)));
            if (exact) w.WriteSingle(s.Exact);
        }

        public static SpeedState ReadSpeed(ByteReader r)
        {
            int speed = r.ReadByte();
            int flags = r.ReadByte();
            float exact = 0f;
            if ((flags & ExactFlag) != 0 && r.Remaining >= 4)
            {
                float value = r.ReadSingle();
                // a value outside the accepted range (or not a number) is ignored: the rounded speed applies
                if (value >= SpeedState.MinExact && value <= SpeedState.MaxExact) exact = value;
            }
            return new SpeedState { Speed = speed, Paused = (flags & PausedFlag) != 0, Held = (flags & HeldFlag) != 0, Exact = exact };
        }

        public static void WriteSessionInfo(ByteWriter w, SessionInfo s)
        {
            MessageFields.WriteText(w, s.SessionName, Limits.MaxNameChars);
            MessageFields.WriteText(w, s.HostName, Limits.MaxNameChars);
            w.WriteUInt32((uint)s.BuilderPermissions);
        }

        public static SessionInfo ReadSessionInfo(ByteReader r)
        {
            return new SessionInfo
            {
                SessionName = MessageFields.ReadText(r, Limits.MaxNameChars, "session name"),
                HostName = MessageFields.ReadText(r, Limits.MaxNameChars, "player name"),
                BuilderPermissions = (Permission)r.ReadUInt32(),
            };
        }

        /// <summary>
        /// <see cref="ControlKind.LoadProgress"/>: f32 fraction of the current phase (0..1) | u8 <see cref="JoinPhase"/> |
        /// u32 load heartbeat (the frames the joiner's game ran since its load started; it only grows while the game works
        /// on the load, also while the game's own progress value stands still, which it does through a saved game's whole
        /// deserialization).
        /// </summary>
        public static void WriteLoadProgress(ByteWriter w, JoinPhase phase, float fraction, uint heartbeat = 0)
        {
            w.WriteSingle(float.IsNaN(fraction) ? 0f : Math.Max(0f, Math.Min(1f, fraction)));
            w.WriteByte((byte)phase);
            w.WriteUInt32(heartbeat);
        }

        /// <summary>Reads <see cref="WriteLoadProgress"/>; the fraction is clamped to 0..1 and unknown phases read as loading.</summary>
        public static JoinPhase ReadLoadProgress(ByteReader r, out float fraction) => ReadLoadProgress(r, out fraction, out _);

        /// <summary>Reads <see cref="WriteLoadProgress"/> with the load heartbeat.</summary>
        public static JoinPhase ReadLoadProgress(ByteReader r, out float fraction, out uint heartbeat)
        {
            float value = r.ReadSingle();
            fraction = float.IsNaN(value) ? 0f : Math.Max(0f, Math.Min(1f, value));
            JoinPhase phase = r.ReadByte() == (byte)JoinPhase.Downloading ? JoinPhase.Downloading : JoinPhase.Loading;
            heartbeat = r.ReadUInt32();
            return phase;
        }

        /// <summary><see cref="ControlKind.PlayerAction"/>: u8 action | u8 target peer | u8 role (ignored for a kick).</summary>
        public static void WritePlayerAction(ByteWriter w, PlayerActionKind action, PeerId target, SessionRole role)
        {
            w.WriteByte((byte)action);
            w.WriteByte(target.Value);
            w.WriteByte((byte)role);
        }

        public static PlayerActionKind ReadPlayerAction(ByteReader r, out PeerId target, out SessionRole role)
        {
            var action = (PlayerActionKind)r.ReadByte();
            target = new PeerId(r.ReadByte());
            role = (SessionRole)r.ReadByte();
            return action;
        }

        public static void WriteText(ByteWriter w, string text) => MessageFields.WriteText(w, text, 1024);

        public static string ReadText(ByteReader r) => MessageFields.ReadText(r, 1024, "text");

        /// <summary>Builds a readable comparison of two mod lists, or null when they match.</summary>
        public static string CompareMods(IReadOnlyList<ModEntry> host, IReadOnlyList<ModEntry> client) => MismatchReport.CompareMods(host, client)?.Describe();
    }
}
