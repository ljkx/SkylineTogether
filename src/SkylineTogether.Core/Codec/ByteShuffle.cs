using System;

namespace SkylineTogether.Core.Codec
{
    /// <summary>
    /// The byte filter behind the strided byte arrays of the canonical layout (<c>IWriter.Write(NativeArray&lt;byte&gt;,
    /// int stride)</c>): a byte transposition that groups byte <c>j</c> of every <c>stride</c>-byte element together.
    /// <para>
    /// The stock writer filters such arrays with <c>NativeCompression.FilterDataBeforeWrite</c>
    /// (<c>BinaryWriter.cs:104-109</c>), a P/Invoke into the native <c>PipelinePlugin!filter_strided_data</c> whose
    /// exact transform cannot be read from managed code. Its purpose is compression of strided columns
    /// (<c>IStrideSerializable</c>, R01 §2.1); canonical bytes are only ever produced by <see cref="CanonicalWriter"/>
    /// and consumed by <see cref="CanonicalReader"/>, so the canonical layout defines its own documented transform with
    /// the same purpose and the same length as the input:
    /// </para>
    /// <code>
    /// n = count / stride (whole elements), tail = count % stride
    /// out[j * n + i] = in[i * stride + j]     for 0 ≤ i &lt; n, 0 ≤ j &lt; stride
    /// out[n * stride + t] = in[n * stride + t] for the tail bytes (copied unchanged)
    /// </code>
    /// A stride of 1 or less, or fewer than two whole elements, leaves the bytes unchanged.
    /// </summary>
    internal static class ByteShuffle
    {
        /// <summary>Writes the filtered form of <c>src[srcOffset .. +count]</c> to <c>dst[dstOffset .. +count]</c> (no overlap).</summary>
        internal static void Shuffle(byte[] src, int srcOffset, byte[] dst, int dstOffset, int count, int stride)
        {
            int n = stride > 1 ? count / stride : 0;
            if (n < 2)
            {
                Buffer.BlockCopy(src, srcOffset, dst, dstOffset, count);
                return;
            }
            for (int j = 0; j < stride; j++)
            {
                int s = srcOffset + j;
                int d = dstOffset + j * n;
                for (int i = 0; i < n; i++, s += stride)
                {
                    dst[d + i] = src[s];
                }
            }
            int whole = n * stride;
            Buffer.BlockCopy(src, srcOffset + whole, dst, dstOffset + whole, count - whole);
        }

        /// <summary>The inverse of <see cref="Shuffle"/>: restores <c>count</c> original bytes into <c>dst</c> (no overlap).</summary>
        internal static void Unshuffle(byte[] src, int srcOffset, byte[] dst, int dstOffset, int count, int stride)
        {
            int n = stride > 1 ? count / stride : 0;
            if (n < 2)
            {
                Buffer.BlockCopy(src, srcOffset, dst, dstOffset, count);
                return;
            }
            for (int j = 0; j < stride; j++)
            {
                int s = srcOffset + j * n;
                int d = dstOffset + j;
                for (int i = 0; i < n; i++, d += stride)
                {
                    dst[d] = src[s + i];
                }
            }
            int whole = n * stride;
            Buffer.BlockCopy(src, srcOffset + whole, dst, dstOffset + whole, count - whole);
        }
    }
}
