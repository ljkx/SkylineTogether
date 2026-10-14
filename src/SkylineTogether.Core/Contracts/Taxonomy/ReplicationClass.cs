namespace SkylineTogether.Core
{
    /// <summary>
    /// Replication classes (ARCH §4.2.2). The numeric values travel on the wire (HASH beacons) and must not change.
    /// </summary>
    public enum ReplicationClass : byte
    {
        /// <summary>Never replicated.</summary>
        Excluded,

        /// <summary>Derived on every peer; never replicated (derived families).</summary>
        Derived,

        /// <summary>Structure: EDC to P0, shared and encoded once.</summary>
        S,

        /// <summary>Global singletons: hot poll, P0, change-driven at most 4 Hz.</summary>
        G,

        /// <summary>Simulation values on structure entities: SDC, P1 in attention, else P3.</summary>
        V,

        /// <summary>Agents (citizens, households, companies, pets, chirps, journal, life path).</summary>
        A,

        /// <summary>Transient agents (service requests, seekers, meetings): P3 only.</summary>
        ALow,

        /// <summary>Movers: global existence, motion per tick on the unreliable lane.</summary>
        M,

        /// <summary>Keyed state on derived entities (zone cells, sub-object state, station stops, gate-bypass routes).</summary>
        K,

        /// <summary>Dedicated channels (system state, prefab state, icons, names, cell maps, terrain, water, clock).</summary>
        Channel,

        /// <summary>Sent only to the peer that inspects it (paths of inspected vehicles); never hashed.</summary>
        OnDemand
    }
}
