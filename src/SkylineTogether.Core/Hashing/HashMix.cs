namespace SkylineTogether.Core.Hashing
{
    /// <summary>
    /// Integer mixers with fixed, documented definitions; their outputs are part of the wire contract (bucket sums and
    /// stable ids), so they must never change.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class HashMix
    {
        /// <summary>
        /// The SplitMix64 output function: <c>z = x + 0x9E3779B97F4A7C15</c>, then
        /// <c>z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9</c>, <c>z = (z ^ (z >> 27)) * 0x94D049BB133111EB</c>,
        /// <c>z ^ (z >> 31)</c>. A bijection with full avalanche; bucket sums add <c>Mix64(member)</c> (ARCHITECTURE.md#bucket-digests).
        /// </summary>
        public static ulong Mix64(ulong x)
        {
            unchecked
            {
                ulong z = x + 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        /// <summary>Order-dependent combination: <c>Mix64(a * 0xC2B2AE3D27D4EB4F + b)</c> (bijective in each argument).</summary>
        public static ulong Combine(ulong a, ulong b)
        {
            unchecked
            {
                return Mix64(a * 0xC2B2AE3D27D4EB4FUL + b);
            }
        }

        /// <summary>XOR of the two halves: <c>(uint)x ^ (uint)(x >> 32)</c> (e.g. system-state stable ids, ARCHITECTURE.md#global-state-and-panels).</summary>
        public static uint Fold32(ulong x) => unchecked((uint)x ^ (uint)(x >> 32));
    }
}
