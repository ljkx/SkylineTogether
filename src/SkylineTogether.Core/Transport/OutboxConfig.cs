using System;
using System.Globalization;

namespace SkylineTogether.Core.Transport
{
    /// <summary>
    /// Windows and limits of a <see cref="PeerOutbox"/> (ARCHITECTURE.md#outbox-lanes-and-rate-control, #live-session, #performance-budgets). The defaults are the design
    /// values; the game layer configures the same <see cref="SendBufferBytes"/> on each Steam connection.
    /// </summary>
    public sealed class OutboxConfig
    {
        /// <summary>The transport's send buffer (Steam <c>k_ESteamNetworkingConfig_SendBufferSize</c>, configured on the
        /// connection, ARCHITECTURE.md#blob-transfer). Steam refuses sends with <c>k_EResultLimitExceeded</c> beyond it.</summary>
        public int SendBufferBytes = 16 << 20;

        /// <summary>Lane R may fill the transport up to <see cref="SendBufferBytes"/> − headroom pending bytes.</summary>
        public int ReliableHeadroomBytes = 1 << 20;

        /// <summary>The adaptive bulk window: lane B is handed over only while the R queue is empty and the transport's
        /// pending bytes stay below it, which always leaves headroom for R (ARCHITECTURE.md#outbox-lanes-and-rate-control).</summary>
        public int BulkWindowMinBytes = 2 << 20, BulkWindowMaxBytes = 8 << 20;

        /// <summary>"Connection too slow for this session": queued bytes (ARCHITECTURE.md#live-session).</summary>
        public long DisconnectQueuedBytes = 64L << 20;

        /// <summary>"Connection too slow for this session": age of the oldest queued message in seconds (ARCHITECTURE.md#live-session).</summary>
        public double DisconnectQueuedSeconds = 60;

        /// <summary>Most pending bytes lane R may leave in the transport.</summary>
        public int ReliableWindowBytes => SendBufferBytes - ReliableHeadroomBytes;

        /// <summary>Throws <see cref="ArgumentException"/> for an inconsistent configuration.</summary>
        public void Validate()
        {
            if (SendBufferBytes <= 0) throw Invalid(nameof(SendBufferBytes), "must be positive");
            if (ReliableHeadroomBytes < 0 || ReliableHeadroomBytes >= SendBufferBytes) throw Invalid(nameof(ReliableHeadroomBytes), "must be between 0 and SendBufferBytes − 1");
            if (BulkWindowMinBytes <= 0) throw Invalid(nameof(BulkWindowMinBytes), "must be positive");
            if (BulkWindowMaxBytes < BulkWindowMinBytes) throw Invalid(nameof(BulkWindowMaxBytes), "must not be below BulkWindowMinBytes");
            if (BulkWindowMaxBytes > SendBufferBytes) throw Invalid(nameof(BulkWindowMaxBytes), "must fit the send buffer");
            if (DisconnectQueuedBytes <= 0) throw Invalid(nameof(DisconnectQueuedBytes), "must be positive");
            if (double.IsNaN(DisconnectQueuedSeconds) || DisconnectQueuedSeconds <= 0) throw Invalid(nameof(DisconnectQueuedSeconds), "must be positive");
        }

        public OutboxConfig Clone()
        {
            return (OutboxConfig)MemberwiseClone();
        }

        public override string ToString()
        {
            var ci = CultureInfo.InvariantCulture;
            return "send buffer " + SendBufferBytes.ToString(ci) + " B, R headroom " + ReliableHeadroomBytes.ToString(ci)
                   + " B, bulk window " + BulkWindowMinBytes.ToString(ci) + "-" + BulkWindowMaxBytes.ToString(ci)
                   + " B, disconnect above " + DisconnectQueuedBytes.ToString(ci) + " B or " + DisconnectQueuedSeconds.ToString("0.###", ci) + " s";
        }

        private static ArgumentException Invalid(string field, string rule)
        {
            return new ArgumentException("OutboxConfig." + field + " " + rule, field);
        }
    }
}
