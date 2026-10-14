namespace SkylineTogether.Core
{
    /// <summary>Section types of an epoch message (docs/history/ARCHITECTURE-original.md §4.6.2); the values travel on the wire.</summary>
    public enum SectionType : byte
    {
        /// <summary>TX: shared P0 records (type sets, results and binds, despawns, spawns, structs, values, touches, keyed).</summary>
        Tx = 1,
        /// <summary>VAL: per-peer pulled values.</summary>
        Values,
        /// <summary>SYS: system state blobs.</summary>
        SysState,
        /// <summary>PFB: prefab state.</summary>
        PrefabState,
        /// <summary>ICON: icon operations.</summary>
        Icons,
        /// <summary>EVT: events (XP toasts, milestone hints, journal, radio).</summary>
        Events,
        /// <summary>NAME: names.</summary>
        Names,
        /// <summary>MAP: cell-map deltas.</summary>
        CellMaps,
        /// <summary>CLK: clock anchor.</summary>
        Clock,
        /// <summary>HASH: bucket beacons.</summary>
        Hash,
        /// <summary>SES: session settings, roles, players.</summary>
        Session,
        /// <summary>INSP: inspection-closure samples with their sample tick.</summary>
        Inspection,
        /// <summary>TSET: type-set keyframe (the whole registry; first section of a joiner or resync).</summary>
        TypeSets
    }
}
