using System;

namespace SkylineTogether.Core
{
    /// <summary>
    /// The only exception a decoder of network or file input may throw (buffer overruns, invalid values, size limits,
    /// corrupt compressed data). Fuzz tests assert that nothing else escapes (IMPLEMENTATION-PLAN §4). A malformed
    /// message disconnects the sending peer on the host and closes the session on a client (ARCH §12.2, §14.3).
    /// </summary>
    public sealed class DecodeException : Exception
    {
        public DecodeException(string message) : base(message)
        {
        }

        public DecodeException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
