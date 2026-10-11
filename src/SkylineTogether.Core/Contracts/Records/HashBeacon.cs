namespace SkylineTogether.Core
{
    /// <summary>
    /// A HASH beacon (ARCH §4.6.3, §8.4): the host's bucket sum and member count of one class and bucket, describing
    /// exactly what the receiving peer has been sent before the beacon.
    /// </summary>
    public struct HashBeacon
    {
        public ReplicationClass Class;
        /// <summary>Bucket index (slot >> 10).</summary>
        public int Bucket;
        /// <summary>Σ mix64(member hash) modulo 2^64.</summary>
        public ulong Sum;
        public int Count;
    }
}
