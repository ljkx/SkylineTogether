using System;
using SkylineTogether.Core;
using SkylineTogether.Diagnostics;
using UnityEngine;

namespace SkylineTogether.Session
{
    /// <summary>
    /// Ticks the session once per rendered frame, independent of the game's state: in the main menu, while a city
    /// loads (the joiner keeps downloading and answering pings) and in game. Also drives the toasts, Steam invites and
    /// the UI probe. Nothing it calls may throw into Unity (failures are counted by <see cref="Guard"/>).
    /// </summary>
    public sealed class SessionDriver : MonoBehaviour
    {
        private void Update()
        {
            try
            {
                Mod.Sessions?.Tick();
                Mod.Replay?.Tick(); // solo testing: the recorder after the host's tick, the replay's end
            }
            catch (Exception ex)
            {
                Guard.Report(Features.Session, "session driver", ex);
            }
            try
            {
                Mod.Toasts?.Tick();
                Mod.Invites?.Tick();
            }
            catch (Exception ex)
            {
                Guard.Report(Features.Ui, "toasts or Steam invites", ex);
            }
            try
            {
                Probes.Tick(Mod.Log, Time.realtimeSinceStartupAsDouble);
            }
            catch (Exception ex)
            {
                Guard.Report(Features.Ui, "UI probe", ex);
            }
            Guard.Run(Features.Mods, "mod integration", Compat.ModIntegration.Tick); // binds other mods once they are loaded
        }

        private void OnApplicationQuit()
        {
            try
            {
                Mod.Sessions?.Leave();
            }
            catch (Exception ex)
            {
                Mod.Log?.Warn(LogCategory.Session, "leaving the session on quit failed", ex);
            }
        }
    }
}
