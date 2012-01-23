using System;

namespace Roger
{
    public class RogerOptions
    {
        public bool NoLocal { get; set; }
        public ushort? PrefetchCount { get; set; }
        public TimeSpan? QueueUnusedTimeout { get; set; }
        public TimeSpan? MessageTimeToLiveOnQueue { get; set; }
        public bool UsePublisherConfirms { get; set; }
        public bool DeduplicationAndResequencing { get; set; }

        /// <summary>
        /// Options to configure the bus
        /// </summary>
        /// <param name="noLocal">If set, messages published by one instance of the bus will not be received by the same instance</param>
        /// <param name="prefetchCount">The number of messages the broker will deliver to the bus before the bus acknowledges them</param>
        /// <param name="queueUnusedTimeout">The timeout after which the queue, if unused, will automatically be deleted</param>
        /// <param name="receivedMessagesTimeToLive">The time to live of the messages received on the bus, after which, if not consumed, they will be deleted</param>
        /// <param name="usePublisherConfirms">Whether to enable publisher confirms mechanism. This improves the chance of messages not being lost but can introduce duplication</param>
        /// <param name="deduplicationAndResequencing">Whether to enable message deduplication and resequencing when consuming messages</param>
        public RogerOptions(bool noLocal = false,
                            ushort? prefetchCount = (ushort)100,
                            TimeSpan? queueUnusedTimeout = null,
                            TimeSpan? receivedMessagesTimeToLive = null,
                            bool usePublisherConfirms = true,
                            bool deduplicationAndResequencing = true)
        {
            NoLocal = noLocal;
            PrefetchCount = prefetchCount;
            QueueUnusedTimeout = queueUnusedTimeout;
            MessageTimeToLiveOnQueue = receivedMessagesTimeToLive;
            UsePublisherConfirms = usePublisherConfirms;
            DeduplicationAndResequencing = deduplicationAndResequencing;
        }
    }
}