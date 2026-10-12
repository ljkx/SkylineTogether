using SkylineTogether.Core;
using UnityEngine;

namespace SkylineTogether.Session
{
    /// <summary>
    /// Ticks the session once per rendered frame, independent of the game's state: in the main menu, while a city
    /// loads (the joiner keeps downloading and answering pings) and in game.
    /// </summary>
    public sealed class SessionDriver : MonoBehaviour
    {
        private void Update()
        {
            Mod.Sessions?.Tick();
            try
            {
                Mod.Toasts?.Tick();
                Mod.Invites?.Tick();
            }
            catch (System.Exception ex)
            {
                Mod.Log?.Error(LogCategory.Ui, "toasts or Steam invites failed", ex);
            }
        }

        private void OnApplicationQuit()
        {
            Mod.Sessions?.Leave();
        }
    }
}
