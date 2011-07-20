﻿using MbUnit.Framework;

namespace Tests.Bus
{
    public class Manual_consuming : With_default_bus
    {
        [Test]
        public void Consume_message()
        {
            var consumer = new MyConsumer();
            Bus.AddInstanceSubscription(consumer);

            Bus.Consume(new MyMessage());

            Assert.IsNotNull(consumer.Received);
        }
    }
}