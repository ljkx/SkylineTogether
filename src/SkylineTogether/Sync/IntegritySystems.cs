using System;
using Game;
using SkylineTogether.Core;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// ModificationEnd, directly before <c>IconCommandSystem</c> (ARCH §4.10.4): on a live client, drops local status
    /// icon commands and issues the host's icons (<see cref="IconSync.BeforeIconCommandSystem"/>). Does nothing on the
    /// host or outside a session.
    /// </summary>
    public sealed class IconAuthorityFilterSystem : GameSystemBase
    {
        protected override void OnUpdate()
        {
            try
            {
                Mod.Sync?.Integrity?.Icons.BeforeIconCommandSystem();
            }
            catch (Exception ex)
            {
                Mod.Log?.Error(LogCategory.Integrity, "icon authority filter failed", ex);
            }
        }
    }
}
