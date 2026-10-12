namespace SkylineTogether.Core.Transport
{
    /// <summary>What a <see cref="NetSimTransport"/> did to the traffic it sent (all its connections), for tests and logs.</summary>
    public struct NetSimCounters
    {
        /// <summary>Reliable messages accepted by <see cref="IConnection.Send"/>.</summary>
        public long ReliableMessages;

        /// <summary>Unreliable messages accepted by <see cref="IConnection.Send"/> (before loss).</summary>
        public long UnreliableMessages;

        /// <summary>Unreliable messages lost (<see cref="NetSimOptions.LossRate"/>).</summary>
        public long UnreliableLost;

        /// <summary>Unreliable messages dropped because the link was saturated (Steam's "no delay" semantics).</summary>
        public long UnreliableSaturationDrops;

        /// <summary>Unreliable messages delivered twice.</summary>
        public long UnreliableDuplicated;

        /// <summary>Unreliable messages held back so that later ones overtake them.</summary>
        public long UnreliableReordered;

        /// <summary>Packets of reliable messages that had to be sent again.</summary>
        public long RetransmittedPackets;

        /// <summary>Sends refused with <see cref="SendResult.Busy"/> by <see cref="NetSimTransport.BusyRate"/>.</summary>
        public long BusyRateRefusals;

        /// <summary>Sends refused with <see cref="SendResult.Busy"/> because the simulated send buffer was full.</summary>
        public long SendBufferRefusals;

        /// <summary>Messages (including duplicates) handed to the inner transport after their delay.</summary>
        public long DeliveredMessages;

        public long DeliveredBytes;

        /// <summary>Times the inner transport refused a due reliable message (it is retried).</summary>
        public long InnerBusyRetries;
    }
}
