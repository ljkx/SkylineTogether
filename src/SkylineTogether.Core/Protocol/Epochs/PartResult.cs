namespace SkylineTogether.Core.Protocol
{
    /// <summary>Outcome of <see cref="EpochPartAssembler.Accept"/>.</summary>
    public enum PartResult : byte
    {
        /// <summary>The part was buffered (or ignored as a duplicate); the epoch is not complete yet.</summary>
        Incomplete,

        /// <summary>The epoch is complete; the stream holds it.</summary>
        Complete,

        /// <summary>The epoch exceeds <see cref="Limits.MaxEpochTotalBytes"/>: a well-formed but too large epoch, so the
        /// client resyncs (ARCH §12.2). Its parts were dropped; later parts of it report this again.</summary>
        Oversized,

        /// <summary>A malformed part, parts of one epoch that disagree, or too many partial epochs: a protocol violation.</summary>
        Invalid
    }
}
