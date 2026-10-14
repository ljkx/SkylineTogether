namespace SkylineTogether.Core
{
    /// <summary>
    /// Read view of the slot side tables (docs/history/ARCHITECTURE-original.md §3.3), implemented by core-identity's <c>SlotTable</c>. Every lookup is
    /// O(1) and version-checked: an entity whose version differs from the recorded one is a different entity. Only the
    /// main thread mutates the tables (on the host only inside the capture window).
    /// </summary>
    public interface ISlotTable
    {
        int BoundCount { get; }

        /// <summary>One above the highest slot ever bound.</summary>
        int HighWaterMark { get; }

        bool TryGetSlot(EntityKey entity, out NetSlot slot);

        /// <summary>Index-only lookup for <c>Write(Entity, ignoreVersion: true)</c> (<c>PathNode</c>).</summary>
        bool TryGetSlotByIndex(int entityIndex, out NetSlot slot);

        /// <summary>The bound entity; the caller still checks liveness with <c>EntityManager.Exists</c> (docs/history/ARCHITECTURE-original.md §3.3).</summary>
        bool TryGetEntity(NetSlot slot, out EntityKey entity);

        byte GenerationOf(NetSlot slot);

        Family FamilyOf(NetSlot slot);

        PrefabRank PrefabOf(NetSlot slot);

        TypeSetId TypeSetOf(NetSlot slot);

        /// <summary>Authored revision (docs/history/ARCHITECTURE-original.md §3.8).</summary>
        ushort RevisionOf(NetSlot slot);

        /// <summary>Host: the slot's SPAWN was emitted (or it was bound at session start, docs/history/ARCHITECTURE-original.md §3.1).</summary>
        bool IsSpawnEmitted(NetSlot slot);
    }
}
