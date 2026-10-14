using System;
using System.Globalization;

namespace SkylineTogether.Core
{
    /// <summary>
    /// A machine-local ECS entity handle (<c>Unity.Entities.Entity</c>: index and version) in Core types. Indices are
    /// reused at once and versions are per process (docs/history/ARCHITECTURE-original.md §3), so a key is meaningful only on the machine that made it;
    /// an entity whose version differs from the recorded one is a different entity. <c>default</c> is the null entity,
    /// exactly like <c>Entity.Null</c> (<c>Unity.Entities/Entity.cs</c>).
    /// </summary>
    public readonly struct EntityKey : IEquatable<EntityKey>
    {
        /// <summary>The null entity (index 0, version 0).</summary>
        public static readonly EntityKey Null;

        public readonly int Index;
        public readonly int Version;

        public EntityKey(int index, int version)
        {
            Index = index;
            Version = version;
        }

        /// <summary>True for the null entity (index 0 and version 0, as <c>Entity.Null</c>).</summary>
        public bool IsNull => Index == 0 && Version == 0;

        public bool Equals(EntityKey other) => Index == other.Index && Version == other.Version;

        public override bool Equals(object obj) => obj is EntityKey other && Equals(other);

        public override int GetHashCode() => Index;

        public override string ToString()
        {
            return IsNull
                ? "Entity.Null"
                : "Entity(" + Index.ToString(CultureInfo.InvariantCulture) + ":" + Version.ToString(CultureInfo.InvariantCulture) + ")";
        }

        public static bool operator ==(EntityKey a, EntityKey b) => a.Index == b.Index && a.Version == b.Version;

        public static bool operator !=(EntityKey a, EntityKey b) => !(a == b);
    }
}
