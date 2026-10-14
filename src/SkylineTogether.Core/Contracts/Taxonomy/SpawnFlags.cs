using System;

namespace SkylineTogether.Core
{
    /// <summary>Flags of a SPAWN record (docs/history/ARCHITECTURE-original.md §4.6.3); the values travel on the wire.</summary>
    [Flags]
    public enum SpawnFlags : byte
    {
        None = 0,
        /// <summary>The entity is a mover (created with <c>Applied</c> and the host's <c>UpdateFrame</c>).</summary>
        Mover = 1,
        /// <summary>The record carries the host <c>UpdateFrame</c> byte.</summary>
        HasUpdateFrame = 2,
        /// <summary>A full re-send by the repair ladder (upserts or re-creates, docs/history/ARCHITECTURE-original.md §8.5).</summary>
        Repair = 4
    }
}
