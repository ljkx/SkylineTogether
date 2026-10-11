using System;

namespace SkylineTogether.Core
{
    /// <summary>
    /// Facts and policy flags of a catalogued type (ARCH §3.6, §3.7, §4.2.5-4.2.7, §4.7.8, §4.11). The first three are
    /// catalog facts; the others come from the policy table (<c>policies.json</c>, see <see cref="PolicyTableModel"/>).
    /// The values are part of the catalog fingerprint input and must not change.
    /// </summary>
    [Flags]
    public enum TypeFlags : uint
    {
        None = 0,
        /// <summary>Catalog fact: a dynamic buffer type.</summary>
        Buffer = 1,
        /// <summary>Catalog fact: an enableable type (its enabled bit is replicated).</summary>
        Enableable = 2,
        /// <summary>Catalog fact: <c>ISerializeAsEnabled</c>; the enabled bit is ignored (R01 §2).</summary>
        SerializeAsEnabled = 4,
        /// <summary>Players may author the whole type in proposals (ARCH §5.2, App. F).</summary>
        PlayerAuthorable = 8,
        /// <summary>Hashed as a multiset (order-insensitive), e.g. data-replicated reverse buffers (ARCH §4.2.4).</summary>
        HashAsSet = 16,
        /// <summary>Opted out of the end-of-frame re-application of spawn payloads (ARCH §4.7.8).</summary>
        NoReapply = 32,
        /// <summary>Sent only to the inspecting peer, never hashed (ARCH §4.2.2).</summary>
        OnDemand = 64,
        /// <summary>Never replicated (ARCH §4.2.5).</summary>
        Excluded = 128,
        /// <summary>Has a hand-written merge policy: masked fields stay local (ARCH §4.2.5).</summary>
        Merge = 256,
        /// <summary>A data-carrying <c>IEmptySerializable</c> replicated as data (reverse-buffer audit rule, ARCH §4.2.4).</summary>
        DataReplicatedPresence = 512,
        /// <summary>Source data: S rather than V on structure families (ARCH §4.2.7).</summary>
        Source = 1024,
        /// <summary>Only part of the type is player-authorable (an <c>authorableMask</c> function, ARCH §3.8).</summary>
        AuthorableMask = 2048,
        /// <summary>Only adding or removing the type is authorable (the vanilla apply adds a default value, App. F).</summary>
        PresenceAuthorable = 4096,
        /// <summary>A forward reference whose change co-captures reverse buffers on the owners (ARCH §4.2.6).</summary>
        ForwardReference = 8192,
        /// <summary>Kept in the client's received-value cache (ARCH §3.7).</summary>
        CacheReceived = 16384,
        /// <summary>Restored by the route guard inside preview windows (ARCH §4.11.4).</summary>
        Guarded = 32768,
        /// <summary>Unresolved derived keys decode to a stand-in instead of <c>Entity.Null</c> (ARCH §4.11.3).</summary>
        PresenceSafe = 65536,
        /// <summary>Has deferred fields: replicated and hashed, but applied later by a dedicated system (ARCH §4.2.5).</summary>
        DeferredFields = 131072
    }
}
