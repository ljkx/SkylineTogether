using System.Collections.Generic;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>The host's per-peer limit of game speed requests (each accepted one is broadcast to everyone).</summary>
    public sealed class SpeedRequestLimitTests
    {
        [Test]
        public void AFloodOfSpeedRequestsIsCutToTheBurstThenTheRate()
        {
            var rig = new SessionRig();
            var (anna, _, _) = rig.AddClient("Anna");
            var (ben, _, _) = rig.AddClient("Ben");
            rig.Run(400);
            Assert.Equal(ClientState.Live, anna.State, anna.EndReason);
            Assert.Equal(ClientState.Live, ben.State, ben.EndReason);
            var requests = new List<PeerId>();
            rig.Host.SpeedRequested += (p, s) => requests.Add(p);

            for (int i = 0; i < 50; i++) anna.RequestSpeed(new SpeedState { Speed = 1 + i % 3 });
            ben.RequestSpeed(new SpeedState { Speed = 2 });
            rig.Run(10);
            Assert.Equal(SessionHost.SpeedRequestBurst, requests.FindAll(p => p == anna.LocalPeer).Count, "a burst, then nothing");
            Assert.Equal(1, requests.FindAll(p => p == ben.LocalPeer).Count, "each peer has its own limit");

            // the bucket refills at the configured rate (2 per second by default)
            requests.Clear();
            rig.Run(120); // two seconds at 60 frames per second
            for (int i = 0; i < 20; i++) anna.RequestSpeed(new SpeedState { Speed = 3 });
            rig.Run(10);
            int refilled = requests.FindAll(p => p == anna.LocalPeer).Count;
            Assert.InRange(refilled, 3, SessionHost.SpeedRequestBurst);
        }

        [Test]
        public void AnOrdinaryPlayerIsNeverLimited()
        {
            var rig = new SessionRig();
            var (anna, _, _) = rig.AddClient("Anna");
            rig.Run(400);
            var requests = new List<PeerId>();
            rig.Host.SpeedRequested += (p, s) => requests.Add(p);
            for (int i = 0; i < 10; i++)
            {
                anna.RequestSpeed(new SpeedState { Speed = 1 + i % 3, Paused = i % 2 == 0 });
                rig.Run(60); // one click a second
            }
            Assert.Equal(10, requests.Count);
        }
    }
}
