namespace SkylineTogether.Core
{
    /// <summary>
    /// Receives member changes for the incremental bucket sums (docs/history/ARCHITECTURE-original.md §8.2): the sum subtracts mix64(old) and adds
    /// mix64(new) in O(1). A member of 0 means "absent" (added: old = 0; removed: new = 0).
    /// </summary>
    public interface IBucketSink
    {
        void OnMemberChanged(ReplicationClass cls, NetSlot slot, ulong oldMember, ulong newMember);
    }
}
