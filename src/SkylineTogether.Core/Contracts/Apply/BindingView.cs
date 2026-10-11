namespace SkylineTogether.Core
{
    /// <summary>
    /// What the apply planner may know about a slot on the executing peer (ARCH §4.7.2): binding, liveness
    /// (<c>EntityManager.Exists</c>, ARCH §3.3) and the identity recorded at binding time.
    /// </summary>
    public struct BindingView
    {
        /// <summary>The slot is bound in the side tables.</summary>
        public bool Bound;
        /// <summary>The bound entity still exists (version-checked); a bound but dead slot is unbound and re-planned.</summary>
        public bool Alive;
        public EntityKey Entity;
        public byte Generation;
        public Family Family;
        public PrefabRank Prefab;
        public TypeSetId TypeSet;
        /// <summary>When a BIND attached this slot to a local prediction: the time of that bind in seconds; NaN otherwise
        /// (ARCH §4.11.4: uncomputed host path state of a just-bound prediction is held for 5 s).</summary>
        public double BoundFromPredictionAtSeconds;

        /// <summary>An unbound slot (with <see cref="BoundFromPredictionAtSeconds"/> = NaN, unlike <c>default</c>).</summary>
        public static BindingView Unbound => new BindingView { Prefab = PrefabRank.None, BoundFromPredictionAtSeconds = double.NaN };
    }
}
