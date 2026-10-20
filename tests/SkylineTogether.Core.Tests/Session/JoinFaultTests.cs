using System.Linq;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// Joins that end through the host's own fault cost the joiner nothing (R2 N9), and a player who rejoins after a crash
    /// keeps their name (N6).
    /// </summary>
    public sealed class JoinFaultTests
    {
        private static void RunSeconds(SessionRig rig, double seconds)
        {
            for (double t = 0; t < seconds; t += 1.0)
            {
                rig.Clock.Advance(1.0);
                rig.Host.Tick();
                foreach (var c in rig.Clients) c.Client.Tick();
            }
        }

        [Test]
        public void TheHostGivesUpOnItsSlowSaveBeforeTheJoinerAndItIsNotTheJoinersFailure()
        {
            var rig = new SessionRig();
            ulong id = 9401;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                rig.Snapshots.Hold = true;
                var (client, _, _) = rig.AddClient("Anna", steamId: id);
                rig.Run(100);
                Assert.Equal(ClientState.WaitingForSnapshot, client.State, client.EndReason);
                RunSeconds(rig, 370);
                Assert.Equal(ClientState.Ended, client.State, "attempt " + attempt);
                Assert.Contains("could not save the city", client.EndReason, "the host said why (it gave up first)");
            }
            // three failed saves in a row: no cooldown for the joiner
            rig.Snapshots.Hold = false;
            var (again, _, _) = rig.AddClient("Anna", steamId: id);
            rig.Run(300);
            Assert.Equal(ClientState.Live, again.State, again.EndReason);
        }

        [Test]
        public void APlayerWhoRejoinsAfterACrashKeepsTheirName()
        {
            var rig = new SessionRig();
            var (first, _, _) = rig.AddClient("Anna", steamId: 9402);
            rig.Run(300);
            Assert.Equal(ClientState.Live, first.State, first.EndReason);
            // the game crashed: the old connection is still listed while the new one says hello
            rig.Clients.RemoveAt(0);
            RunSeconds(rig, 20);
            var (second, _, _) = rig.AddClient("Anna", steamId: 9402);
            rig.Run(300);
            Assert.Equal(ClientState.Live, second.State, second.EndReason);
            Assert.True(second.Players.Any(p => p.Name == "Anna"), "not Anna (2): " + string.Join(", ", second.Players.Select(p => p.Name)));
            Assert.False(second.Players.Any(p => p.Name == "Anna (2)"));
        }
    }
}
