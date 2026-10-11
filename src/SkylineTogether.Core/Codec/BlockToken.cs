namespace SkylineTogether.Core.Codec
{
    /// <summary>
    /// A size-prefixed block opened by <see cref="CanonicalWriter.Begin"/> or <see cref="CanonicalReader.Begin()"/>,
    /// the Core counterpart of the game's <c>WriterBlock</c>/<c>ReaderBlock</c> (each a struct with one
    /// <c>m_Position</c> field, <c>Colossal.Serialization.Entities/WriterBlock.cs</c>, <c>ReaderBlock.cs</c>).
    /// <para>
    /// Writer: <see cref="Position"/> is the offset of the 4-byte length placeholder in the target
    /// <see cref="Buffers.ByteWriter"/>. Reader: <see cref="Position"/> is the offset of the block's end, relative to the
    /// start of the record the reader was opened on. The game's <c>NetWriter</c>/<c>NetReader</c> pass the value through
    /// <c>m_Position</c> unchanged; nothing else should interpret it.
    /// </para>
    /// </summary>
    public struct BlockToken
    {
        public int Position;
    }
}
