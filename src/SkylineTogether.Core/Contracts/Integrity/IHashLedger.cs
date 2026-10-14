namespace SkylineTogether.Core
{
    /// <summary>
    /// The client's hash ledger (docs/history/ARCHITECTURE-original.md §8.3): the member hash of the last value *received* per (slot, type), whether it
    /// was applied, parked or held, with the same bucket sums as the host. It proves "received = sent"; the rolling
    /// verifier proves "live = received". Implemented by core-integrity's <c>HashLedger</c>.
    /// </summary>
    public interface IHashLedger : IBucketSink
    {
        void Record(NetSlot slot, TypeRank type, uint hash);

        bool TryGet(NetSlot slot, TypeRank type, out uint hash);

        void RemoveSlot(NetSlot slot);

        ulong BucketSum(ReplicationClass cls, int bucket);

        int BucketCount(ReplicationClass cls, int bucket);
    }
}
