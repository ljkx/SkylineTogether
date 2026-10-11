namespace SkylineTogether.Core.Fields
{
    /// <summary>What <see cref="BlockDelta"/>'s <c>BuildDelta</c> produced, for the warm-join log line and the diagnostics.</summary>
    public struct BlockDeltaStats
    {
        /// <summary>Length of the source file.</summary>
        public long SourceBytes;

        /// <summary>Chunks the source was cut into.</summary>
        public int SourceBlocks;

        /// <summary>Source chunks the receiver already holds.</summary>
        public int MatchedBlocks;

        /// <summary>Bytes the receiver copies from its own file.</summary>
        public long CopiedBytes;

        /// <summary>Bytes carried in the delta itself.</summary>
        public long LiteralBytes;

        /// <summary>Total size of the delta stream.</summary>
        public long DeltaBytes;
    }
}
