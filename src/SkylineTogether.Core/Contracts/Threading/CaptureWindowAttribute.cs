using System;

namespace SkylineTogether.Core
{
    /// <summary>
    /// Marks a host-engine member that mutates engine state and may run only inside the capture window, between
    /// <c>IHostReplicationEngine.BeginCapture</c> and <c>EndCapture</c> (docs/history/ARCHITECTURE-original.md §4.5.5). Implementations assert the
    /// phase in debug builds; callers outside the window go through the engine command queue instead.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class CaptureWindowAttribute : Attribute
    {
    }
}
