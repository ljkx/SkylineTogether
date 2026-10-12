namespace SkylineTogether.Core.Scheduling
{
    /// <summary>Bit helpers missing from netstandard2.0 (<c>System.Numerics.BitOperations</c> is .NET Core 3.0+).</summary>
    internal static class BitUtil
    {
        private const ulong DeBruijn64 = 0x03F79D71B4CB0A89UL;

        private static readonly byte[] DeBruijnIndex =
        {
            0, 1, 48, 2, 57, 49, 28, 3, 61, 58, 50, 42, 38, 29, 17, 4,
            62, 55, 59, 36, 53, 51, 43, 22, 45, 39, 33, 30, 24, 18, 12, 5,
            63, 47, 56, 27, 60, 41, 37, 16, 54, 35, 52, 21, 44, 32, 23, 11,
            46, 26, 40, 15, 34, 20, 31, 10, 25, 14, 19, 9, 13, 8, 7, 6
        };

        /// <summary>Index of the lowest set bit; 64 for 0.</summary>
        public static int TrailingZeros(ulong value)
        {
            if (value == 0) return 64;
            unchecked
            {
                return DeBruijnIndex[((value & (0UL - value)) * DeBruijn64) >> 58];
            }
        }

        /// <summary>Number of set bits.</summary>
        public static int PopCount(ulong value)
        {
            unchecked
            {
                value -= (value >> 1) & 0x5555555555555555UL;
                value = (value & 0x3333333333333333UL) + ((value >> 2) & 0x3333333333333333UL);
                value = (value + (value >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
                return (int)((value * 0x0101010101010101UL) >> 56);
            }
        }
    }
}
