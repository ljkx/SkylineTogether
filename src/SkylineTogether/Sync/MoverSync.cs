using System;
using Game;
using SkylineTogether.Core;
using SkylineTogether.Session;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// Mover replication (ARCH K4, §4.9): owns the host side (<see cref="MoverHost"/>) and the client side
    /// (<see cref="MoverClient"/>) and wires them to the session's lifecycle. The three systems below are its only entry
    /// points from the frame loop; they are registered by <see cref="Mod"/>.
    /// </summary>
    public sealed class MoverSync
    {
        public MoverSync(SessionManager sessions, ILog log)
        {
            Host = new MoverHost(sessions, log);
            Client = new MoverClient(sessions, log);
            Instance = this;
            sessions.HostStarted += Host.OnHostStarted;
            sessions.ClientStarted += Client.OnClientStarted;
            sessions.SessionEnded += () =>
            {
                Host.OnSessionEnded();
                Client.OnSessionEnded();
            };
        }

        /// <summary>The instance created by <see cref="Mod"/> (null before the mod loaded).</summary>
        public static MoverSync Instance { get; private set; }

        public MoverHost Host { get; }

        public MoverClient Client { get; }
    }

    /// <summary>Host: mover existence and motion, right before <c>SaveGameSystem</c> (join snapshots carry the new ids).</summary>
    public sealed class MoverCaptureSystem : GameSystemBase
    {
        protected override void OnUpdate()
        {
            if (!SyncController.SessionActive || !Diagnostics.Features.Movers.IsAvailable) return;
            try
            {
                MoverSync.Instance?.Host.Update();
            }
            catch (Exception ex)
            {
                Diagnostics.Guard.Report(Diagnostics.Features.Movers, "mover capture", ex);
            }
        }
    }

    /// <summary>Client: mover records, right after the structure apply and before the Modification phases.</summary>
    public sealed class MoverApplySystem : GameSystemBase
    {
        protected override void OnUpdate()
        {
            if (!SyncController.SessionActive || !Diagnostics.Features.Movers.IsAvailable) return;
            try
            {
                MoverSync.Instance?.Client.Apply();
            }
            catch (Exception ex)
            {
                Diagnostics.Guard.Report(Diagnostics.Features.Movers, "mover apply", ex);
            }
        }
    }

    /// <summary>Client: just-in-time <c>TransformFrame</c> writer, PreCulling before <c>PreCullingSystem</c>.</summary>
    public sealed class MoverWriteSystem : GameSystemBase
    {
        protected override void OnUpdate()
        {
            if (!SyncController.SessionActive || !Diagnostics.Features.Movers.IsAvailable) return;
            try
            {
                MoverSync.Instance?.Client.Write();
            }
            catch (Exception ex)
            {
                Diagnostics.Guard.Report(Diagnostics.Features.Movers, "motion write", ex);
            }
        }
    }
}
