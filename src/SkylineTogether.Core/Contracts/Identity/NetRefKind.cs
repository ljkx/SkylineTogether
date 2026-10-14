namespace SkylineTogether.Core
{
    /// <summary>What a <see cref="NetRef"/> points at (docs/history/ARCHITECTURE-original.md §4.3.3).</summary>
    public enum NetRefKind : byte
    {
        /// <summary><c>Entity.Null</c> (wire tag 0, payload 0).</summary>
        Null,

        /// <summary>A real entity that is not replicated (wire tag 0, payload 1); decodes to <c>Entity.Null</c>.</summary>
        Local,

        /// <summary>A bound replicated entity, by slot (wire tag 1).</summary>
        Slot,

        /// <summary>A prefab entity, by prefab rank (wire tag 2).</summary>
        Prefab,

        /// <summary>A client-predicted entity inside a proposal, by <see cref="LocalId"/> (wire tag 3, subkind 0).</summary>
        Provisional,

        /// <summary>A derived (locally regenerated) entity, by <see cref="DerivedKey"/> (wire tag 3, subkinds 1-7).</summary>
        Derived
    }
}
