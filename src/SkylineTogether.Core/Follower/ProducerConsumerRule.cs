namespace SkylineTogether.Core.Follower
{
    /// <summary>
    /// One producer/consumer pair of ARCH §6.3 (R11 §4): <see cref="Consumer"/> drains a queue that every system of
    /// <see cref="Producers"/> writes without checking the consumer's <c>Enabled</c> flag, so the consumer may be off only
    /// while those producers are off too (a disabled consumer leaks persistent or <c>TempJob</c> queues). Checked by
    /// <see cref="GatePlanner"/>.
    /// </summary>
    internal sealed class ProducerConsumerRule
    {
        public readonly string Consumer;
        public readonly string[] Producers;

        /// <summary>The research reference and the kind of container, quoted in violation messages.</summary>
        public readonly string Evidence;

        public ProducerConsumerRule(string consumer, string evidence, params string[] producers)
        {
            Consumer = consumer;
            Evidence = evidence;
            Producers = producers;
        }

        public override string ToString() => Consumer + " requires off: " + string.Join(", ", Producers);
    }
}
