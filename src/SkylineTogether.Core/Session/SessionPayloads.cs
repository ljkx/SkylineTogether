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
        /// <summary>Selected speed index (the game's 1, 2 or 3).</summary>
        public int Speed;
        public bool Paused;
        /// <summary>The session holds the simulation (a player is joining or resyncing).</summary>
        public bool Held;

        public bool Equals(SpeedState o) => Speed == o.Speed && Paused == o.Paused && Held == o.Held;
        public override bool Equals(object obj) => obj is SpeedState o && Equals(o);
        public override int GetHashCode() => Speed ^ (Paused ? 64 : 0) ^ (Held ? 128 : 0);
        public override string ToString() => (Held ? "held " : "") + (Paused ? "paused" : "speed " + Speed);
    }

    /// <summary>The session settings sent in <see cref="WelcomeMessage.SessionSettings"/>.</summary>
    public sealed class SessionInfo
    {
        public string SessionName;
        public string HostName;
        /// <summary>Gameplay permissions for builders.</summary>
        public Permission BuilderPermissions = Permission.All;
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

        public static void WriteSpeed(ByteWriter w, SpeedState s)
        {
            w.WriteByte((byte)Math.Max(0, Math.Min(255, s.Speed)));
            w.WriteByte((byte)((s.Paused ? 1 : 0) | (s.Held ? 2 : 0)));
        }

        public static SpeedState ReadSpeed(ByteReader r)
        {
            int speed = r.ReadByte();
            int flags = r.ReadByte();
            return new SpeedState { Speed = speed, Paused = (flags & 1) != 0, Held = (flags & 2) != 0 };
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

        /// <summary><see cref="ControlKind.LoadProgress"/>: f32 fraction of the current phase (0..1) | u8 <see cref="JoinPhase"/>.</summary>
        public static void WriteLoadProgress(ByteWriter w, JoinPhase phase, float fraction)
        {
            w.WriteSingle(float.IsNaN(fraction) ? 0f : Math.Max(0f, Math.Min(1f, fraction)));
            w.WriteByte((byte)phase);
        }

        /// <summary>Reads <see cref="WriteLoadProgress"/>; the fraction is clamped to 0..1 and unknown phases read as loading.</summary>
        public static JoinPhase ReadLoadProgress(ByteReader r, out float fraction)
        {
            float value = r.ReadSingle();
            fraction = float.IsNaN(value) ? 0f : Math.Max(0f, Math.Min(1f, value));
            return r.ReadByte() == (byte)JoinPhase.Downloading ? JoinPhase.Downloading : JoinPhase.Loading;
        }

        public static void WriteText(ByteWriter w, string text) => MessageFields.WriteText(w, text, 1024);

        public static string ReadText(ByteReader r) => MessageFields.ReadText(r, 1024, "text");

        /// <summary>Builds a readable comparison of two mod lists, or null when they match.</summary>
        public static string CompareMods(IReadOnlyList<ModEntry> host, IReadOnlyList<ModEntry> client) => MismatchReport.CompareMods(host, client)?.Describe();
    }
}
