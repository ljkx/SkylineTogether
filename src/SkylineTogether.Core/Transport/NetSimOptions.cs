using System.Globalization;

namespace SkylineTogether.Core.Transport
{
    /// <summary>
    /// The impairments <see cref="NetSimTransport"/> applies to what its side sends (ARCHITECTURE.md#transport: loss, latency, jitter,
    /// reordering, duplication, bandwidth cap). The model mirrors a reliable-UDP transport such as Steam Networking
    /// Sockets: unreliable messages are really lost, duplicated and reordered, while reliable lanes stay exactly-once
    /// and ordered and pay for loss with retransmission delay and bandwidth. Rates outside 0..1 are clamped, negative
    /// times count as 0. Changes to a live instance apply to messages sent afterwards.
    /// </summary>
    public sealed class NetSimOptions
    {
        /// <summary>
        /// Probability that one packet (about <see cref="Protocol.Limits.MaxDatagramBytes"/> bytes) is lost. An
        /// unreliable message is lost when any of its packets is; a reliable message's lost packets are retransmitted,
        /// which delays it (and, being ordered, the messages behind it on its lane) by one retransmission timeout per
        /// round and costs their bandwidth again.
        /// </summary>
        public double LossRate;

        /// <summary>Probability that an unreliable message is delivered twice (the reliable layer removes duplicates).</summary>
        public double DuplicateRate;

        /// <summary>Probability that an unreliable message is held back so that later ones overtake it (reliable lanes stay ordered).</summary>
        public double ReorderRate;

        /// <summary>One-way propagation delay in milliseconds.</summary>
        public double LatencyMs;

        /// <summary>Additional delay per message, uniform between 0 and this many milliseconds.</summary>
        public double JitterMs;

        /// <summary>Link capacity in bytes per second; 0 = unlimited. Queued bytes are the connection's pending bytes.</summary>
        public int BandwidthBytesPerSecond;

        /// <summary>Seed of the random decisions (the same calls with the same seed give the same outcome).</summary>
        public int Seed;

        /// <summary>
        /// The simulated send buffer: while this many bytes wait for the bandwidth, sends answer
        /// <see cref="SendResult.Busy"/> (Steam's <c>k_EResultLimitExceeded</c>). 0 = unlimited. Default 16 MiB, the
        /// size the game configures on Steam connections (ARCHITECTURE.md#blob-transfer).
        /// </summary>
        public int SendBufferBytes = 16 << 20;

        /// <summary>
        /// Steam lane priorities (ARCHITECTURE.md#outbox-lanes-and-rate-control): lanes R and U are served before lane B, which only gets leftover
        /// bandwidth. False serves every lane from one queue, like a single connection without lanes.
        /// </summary>
        public bool PrioritizeLanes = true;

        public NetSimOptions Clone()
        {
            return (NetSimOptions)MemberwiseClone();
        }

        public override string ToString()
        {
            var ci = CultureInfo.InvariantCulture;
            return "loss " + LossRate.ToString("0.###", ci) + ", dup " + DuplicateRate.ToString("0.###", ci) + ", reorder "
                   + ReorderRate.ToString("0.###", ci) + ", latency " + LatencyMs.ToString("0.#", ci) + " ms, jitter "
                   + JitterMs.ToString("0.#", ci) + " ms, bandwidth "
                   + (BandwidthBytesPerSecond > 0 ? BandwidthBytesPerSecond.ToString(ci) + " B/s" : "unlimited")
                   + ", seed " + Seed.ToString(ci);
        }
    }
}
