using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SkylineTogether.Core.Json;

namespace SkylineTogether.Core.Session
{
    /// <summary>How a Steam friend shows in the invite list.</summary>
    public enum FriendState
    {
        /// <summary>Playing Cities: Skylines II (<see cref="FriendList.GameAppId"/>).</summary>
        InGame,
        Online,
        /// <summary>Away, snoozing or busy.</summary>
        Away,
        Offline,
    }

    /// <summary>One row of the invite list.</summary>
    public struct FriendEntry
    {
        public ulong Id;
        public string Name;
        public FriendState State;

        /// <summary>Already a player of the current session.</summary>
        public bool InSession;
    }

    /// <summary>
    /// The pure part of the invite list: classifying a friend from Steam's persona state and the game they play, the
    /// order of the rows, and the JSON the UI binding <c>friends</c> carries:
    /// <c>[{"id": "&lt;SteamID64&gt;", "name": "…", "state": "ingame"|"online"|"away"|"offline", "inSession": bool}]</c>.
    /// </summary>
    public static class FriendList
    {
        /// <summary>Cities: Skylines II on Steam.</summary>
        public const uint GameAppId = 949230;

        /// <summary>The longest list the binding carries (Steam allows 2000 friends; the panel needs far fewer).</summary>
        public const int MaxEntries = 500;

        /// <summary>
        /// The state of a friend from Steam's <c>EPersonaState</c> value (0 offline, 1 online, 2 busy, 3 away, 4 snooze,
        /// 5 looking to trade, 6 looking to play, 7 invisible) and the app they play (0 when none).
        /// </summary>
        public static FriendState Classify(int personaState, uint playingAppId)
        {
            if (playingAppId == GameAppId) return FriendState.InGame;
            switch (personaState)
            {
                case 1:
                case 5:
                case 6:
                    return FriendState.Online;
                case 2:
                case 3:
                case 4:
                    return FriendState.Away;
                default:
                    return FriendState.Offline; // offline, invisible (shown to friends as offline) and unknown values
            }
        }

        public static string StateText(FriendState state)
        {
            switch (state)
            {
                case FriendState.InGame: return "ingame";
                case FriendState.Online: return "online";
                case FriendState.Away: return "away";
                default: return "offline";
            }
        }

        /// <summary>In-game first, then online, away and offline; by name (case-insensitive) within a state, then by ID.</summary>
        public static void Sort(List<FriendEntry> friends)
        {
            friends?.Sort(Compare);
        }

        public static int Compare(FriendEntry a, FriendEntry b)
        {
            int c = ((int)a.State).CompareTo((int)b.State);
            if (c != 0) return c;
            c = string.Compare(a.Name ?? "", b.Name ?? "", StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Name ?? "", b.Name ?? "");
            return c != 0 ? c : a.Id.CompareTo(b.Id);
        }

        /// <summary>The binding's JSON array (at most <see cref="MaxEntries"/> rows, in the given order).</summary>
        public static string ToJson(IReadOnlyList<FriendEntry> friends)
        {
            var sw = new StringWriter(CultureInfo.InvariantCulture);
            var w = new JsonWriter(sw, false);
            w.BeginArray();
            int count = friends == null ? 0 : Math.Min(friends.Count, MaxEntries);
            for (int i = 0; i < count; i++)
            {
                FriendEntry f = friends[i];
                w.BeginObject();
                w.Name("id");
                w.Value(f.Id.ToString(CultureInfo.InvariantCulture)); // a string: a SteamID64 does not fit a JS number
                w.Name("name");
                w.Value(f.Name ?? "");
                w.Name("state");
                w.Value(StateText(f.State));
                w.Name("inSession");
                w.Value(f.InSession);
                w.EndObject();
            }
            w.EndArray();
            return sw.ToString();
        }

        /// <summary>A Steam ID sent by the UI (decimal text); false for anything else.</summary>
        public static bool TryParseId(string text, out ulong id) => LobbyInfo.IsSteamIdText((text ?? "").Trim(), out id);
    }
}
