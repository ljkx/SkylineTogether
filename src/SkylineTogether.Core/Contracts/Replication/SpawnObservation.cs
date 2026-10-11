namespace SkylineTogether.Core
{
    /// <summary>What the host capture saw about an entity it spawns or primes (ARCH §4.4.2 step 2, §4.4.8).</summary>
    public struct SpawnObservation
    {
        public EntityKey Entity;
        public Family Family;
        public PrefabRank Prefab;
        /// <summary>The entity's replicated signature.</summary>
        public TypeSetId TypeSet;
        /// <summary>The host <c>UpdateFrame</c> (movers).</summary>
        public byte UpdateFrame;
        public SpawnFlags Flags;
    }
}
