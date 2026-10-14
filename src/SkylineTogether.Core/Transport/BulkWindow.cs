using System;

namespace SkylineTogether.Core.Transport
{
    /// <summary>
    /// The adaptive bulk window shared by <see cref="PeerOutbox"/> and <see cref="BlobSender"/> (ARCHITECTURE.md#blob-transfer, #outbox-lanes-and-rate-control):
    /// the transport may hold about one second of bulk data at its reported send rate, clamped to the configured
    /// 2-8 MiB. A full second keeps Steam's pipe busy between pumps and across its rate ramp-up (R06 §5.4 suggests a
    /// 2-4 MiB window topped up from the network thread), while the cap always leaves most of the 16 MiB send buffer
    /// to lane R. An unknown rate (0) gives the minimum.
    /// </summary>
    internal static class BulkWindow
    {
        /// <summary>Seconds of bulk data at the current send rate the window aims to cover.</summary>
        internal const double TargetSeconds = 1.0;

        internal static int Compute(int sendRateBytesPerSecond, int minBytes, int maxBytes)
        {
            if (maxBytes < minBytes) maxBytes = minBytes;
            if (sendRateBytesPerSecond <= 0) return minBytes;
            double target = sendRateBytesPerSecond * TargetSeconds;
            if (target <= minBytes) return minBytes;
            if (target >= maxBytes) return maxBytes;
            return (int)Math.Round(target);
        }
    }
}
