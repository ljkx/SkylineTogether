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

        public static void WriteText(ByteWriter w, string text) => MessageFields.WriteText(w, text, 1024);

        public static string ReadText(ByteReader r) => MessageFields.ReadText(r, 1024, "text");

        /// <summary>Builds a readable comparison of two mod lists, or null when they match.</summary>
        public static string CompareMods(IReadOnlyList<ModEntry> host, IReadOnlyList<ModEntry> client)
        {
            var hostByName = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (ModEntry m in host) hostByName[Key(m)] = m;
            var clientByName = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (ModEntry m in client) clientByName[Key(m)] = m;
            var missing = new List<string>();
            var extra = new List<string>();
            var version = new List<string>();
            foreach (KeyValuePair<string, ModEntry> h in hostByName)
            {
                if (!clientByName.TryGetValue(h.Key, out ModEntry c)) missing.Add(h.Value.ToString());
                else if (!string.Equals(h.Value.Version ?? "", c.Version ?? "", StringComparison.Ordinal)
                         || (!string.IsNullOrEmpty(h.Value.DllHash) && !string.IsNullOrEmpty(c.DllHash)
                             && !string.Equals(h.Value.DllHash, c.DllHash, StringComparison.OrdinalIgnoreCase)))
                {
                    version.Add((h.Value.Name ?? "?") + " (host " + (h.Value.Version ?? "?") + ", you " + (c.Version ?? "?") + ")");
                }
            }
            foreach (KeyValuePair<string, ModEntry> c in clientByName)
            {
                if (!hostByName.ContainsKey(c.Key)) extra.Add(c.Value.ToString());
            }
            if (missing.Count == 0 && extra.Count == 0 && version.Count == 0) return null;
            missing.Sort(StringComparer.OrdinalIgnoreCase);
            extra.Sort(StringComparer.OrdinalIgnoreCase);
            version.Sort(StringComparer.OrdinalIgnoreCase);
            var sb = new System.Text.StringBuilder();
            if (missing.Count > 0) sb.Append("Missing (enable these): ").Append(string.Join(", ", missing)).Append(". ");
            if (extra.Count > 0) sb.Append("Not on host (disable these): ").Append(string.Join(", ", extra)).Append(". ");
            if (version.Count > 0) sb.Append("Different version: ").Append(string.Join(", ", version)).Append('.');
            return sb.ToString().Trim();
        }

        private static string Key(ModEntry m) => !string.IsNullOrEmpty(m.PlatformId) ? "id:" + m.PlatformId : "name:" + (m.Name ?? "");
    }
}
