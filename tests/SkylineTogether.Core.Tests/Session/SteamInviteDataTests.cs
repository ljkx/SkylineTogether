using System.Collections.Generic;
using SkylineTogether.Core.Json;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>The pure parts of Steam invites: lobby launch options, lobby data and the friends list binding.</summary>
    public sealed class SteamInviteDataTests
    {
        [Test]
        public void LobbyLaunchOptionCarriesTheLobbyId()
        {
            Assert.True(InviteLink.TryParseLobbyArgs(new[] { "Cities2.exe", "+connect_lobby", "109775241234567890" }, out ulong lobby));
            Assert.Equal(109775241234567890UL, lobby);
            Assert.True(InviteLink.TryParseLobby("-continuelastsave +connect_lobby=109775241234567891 -x", out lobby));
            Assert.Equal(109775241234567891UL, lobby);
            Assert.False(InviteLink.TryParseLobby("+connect_lobby 12345", out _), "too short");
            Assert.False(InviteLink.TryParseLobby("+connect_lobby abc", out _));
            Assert.False(InviteLink.TryParseLobby("+skylinetogether_join 76561198000000001", out _));
            Assert.False(InviteLink.TryParseLobbyArgs(null, out _));
            // the join link still parses next to a lobby option
            Assert.True(InviteLink.TryParseArgs(new[] { "+connect_lobby", "109775241234567890", "+skylinetogether_join", "76561198000000002" }, out ulong host));
            Assert.Equal(76561198000000002UL, host);
            Assert.True(InviteLink.TryParse("+skylinetogether_join\t76561198000000003", out host));
            Assert.Equal(76561198000000003UL, host);
        }

        [Test]
        public void LobbyDataRoundTrips()
        {
            var info = new LobbyInfo { HostSteamId = 76561198000000001UL, SessionName = "Anna's city", Protocol = 3, Build = "1.2.3.4", GameVersion = "1.3.3f1", PasswordRequired = true };
            var data = new Dictionary<string, string>();
            foreach (KeyValuePair<string, string> p in info.ToPairs())
            {
                Assert.NotNull(p.Value, p.Key);
                data[p.Key] = p.Value;
            }
            Assert.Equal("+skylinetogether_join 76561198000000001", data[LobbyInfo.KeyConnect]);
            Assert.True(LobbyInfo.TryRead(k => data.TryGetValue(k, out string v) ? v : "", out LobbyInfo read));
            Assert.Equal(76561198000000001UL, read.HostSteamId);
            Assert.Equal("Anna's city", read.SessionName);
            Assert.Equal(3, read.Protocol);
            Assert.Equal("1.2.3.4", read.Build);
            Assert.Equal("1.3.3f1", read.GameVersion);
            Assert.True(read.PasswordRequired);
        }

        [Test]
        public void LobbyDataIsValidated()
        {
            // not ours
            var foreign = new Dictionary<string, string> { { LobbyInfo.KeyHost, "76561198000000001" } };
            Assert.False(LobbyInfo.TryRead(k => foreign.TryGetValue(k, out string v) ? v : "", out _));
            // ours, but no usable host (an address is never accepted)
            var bad = new Dictionary<string, string> { { LobbyInfo.KeyTag, LobbyInfo.TagValue }, { LobbyInfo.KeyHost, "203.0.113.7:27015" } };
            Assert.False(LobbyInfo.TryRead(k => bad.TryGetValue(k, out string v) ? v : "", out _));
            // the connect string is the fallback for the host
            bad[LobbyInfo.KeyConnect] = "+skylinetogether_join 76561198000000005";
            Assert.True(LobbyInfo.TryRead(k => bad.TryGetValue(k, out string v) ? v : "", out LobbyInfo read));
            Assert.Equal(76561198000000005UL, read.HostSteamId);
            Assert.False(read.PasswordRequired);
            Assert.Equal(0, read.Protocol);
            // a throwing or null getter
            Assert.False(LobbyInfo.TryRead(null, out _));
            Assert.False(LobbyInfo.TryRead(k => throw new System.InvalidOperationException(), out _));
            Assert.False(LobbyInfo.TryRead(k => null, out _));
            // long names are cut
            var info = new LobbyInfo { HostSteamId = 76561198000000001UL, SessionName = new string('x', 300) };
            foreach (KeyValuePair<string, string> p in info.ToPairs())
            {
                if (p.Key == LobbyInfo.KeyName) Assert.Equal(LobbyInfo.MaxNameLength, p.Value.Length);
            }
        }

        [Test]
        public void FriendsAreClassifiedAndOrdered()
        {
            Assert.Equal(FriendState.InGame, FriendList.Classify(1, FriendList.GameAppId));
            Assert.Equal(FriendState.InGame, FriendList.Classify(3, FriendList.GameAppId));
            Assert.Equal(FriendState.Online, FriendList.Classify(1, 0));
            Assert.Equal(FriendState.Online, FriendList.Classify(6, 255710)); // another game
            Assert.Equal(FriendState.Away, FriendList.Classify(2, 0));
            Assert.Equal(FriendState.Away, FriendList.Classify(3, 0));
            Assert.Equal(FriendState.Away, FriendList.Classify(4, 0));
            Assert.Equal(FriendState.Offline, FriendList.Classify(0, 0));
            Assert.Equal(FriendState.Offline, FriendList.Classify(7, 0));
            Assert.Equal(FriendState.Offline, FriendList.Classify(99, 0));

            var list = new List<FriendEntry>
            {
                new FriendEntry { Id = 5, Name = "zoe", State = FriendState.Offline },
                new FriendEntry { Id = 4, Name = "Bo", State = FriendState.Online },
                new FriendEntry { Id = 3, Name = "anna", State = FriendState.Online },
                new FriendEntry { Id = 2, Name = "Cy", State = FriendState.Away },
                new FriendEntry { Id = 1, Name = "Dan", State = FriendState.InGame },
            };
            FriendList.Sort(list);
            Assert.SequenceEqual(new ulong[] { 1, 3, 4, 2, 5 }, list.ConvertAll(f => f.Id));
        }

        [Test]
        public void FriendsJsonMatchesTheBindingContract()
        {
            var list = new List<FriendEntry>
            {
                new FriendEntry { Id = 76561198000000001UL, Name = "An\"na \\ ö", State = FriendState.InGame, InSession = true },
                new FriendEntry { Id = 76561198000000002UL, Name = null, State = FriendState.Away },
            };
            string json = FriendList.ToJson(list);
            JsonValue v = MiniJson.Parse(json, "friends");
            Assert.Equal(JsonKind.Array, v.Kind);
            Assert.Equal(2, v.Count);
            Assert.Equal("76561198000000001", v[0]["id"].AsString());
            Assert.Equal("An\"na \\ ö", v[0]["name"].AsString());
            Assert.Equal("ingame", v[0]["state"].AsString());
            Assert.True(v[0]["inSession"].AsBool());
            Assert.Equal("", v[1]["name"].AsString());
            Assert.Equal("away", v[1]["state"].AsString());
            Assert.False(v[1]["inSession"].AsBool(true));
            Assert.Equal("[]", FriendList.ToJson(null));
            Assert.Equal("online", FriendList.StateText(FriendState.Online));
            Assert.Equal("offline", FriendList.StateText(FriendState.Offline));

            Assert.True(FriendList.TryParseId(" 76561198000000001 ", out ulong id));
            Assert.Equal(76561198000000001UL, id);
            Assert.False(FriendList.TryParseId("-1", out _));
            Assert.False(FriendList.TryParseId("", out _));
            Assert.False(FriendList.TryParseId(null, out _));
            Assert.False(FriendList.TryParseId("99999999999999999999", out _), "overflow");
        }
    }
}
