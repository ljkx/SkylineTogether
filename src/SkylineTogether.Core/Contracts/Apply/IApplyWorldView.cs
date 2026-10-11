namespace SkylineTogether.Core
{
    /// <summary>
    /// The abstract view of the executing peer's world that the apply planner reads (ARCH §4.7.2): implemented by the
    /// game's <c>WorldApplier</c> over the side tables and <c>EntityManager.Exists</c>, and by the FakeWorld in tests.
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public interface IApplyWorldView
    {
        BindingView Lookup(NetSlot slot);

        /// <summary>True when <paramref name="local"/> is a live prediction of this peer; returns its entity, prefab and family.</summary>
        bool IsPredictionOf(LocalId local, out EntityKey entity, out PrefabRank prefab, out Family family);
    }
}
