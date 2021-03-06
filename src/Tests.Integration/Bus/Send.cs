﻿using System.Collections.Generic;
using MbUnit.Framework;
using RabbitMQ.Client;
using Roger;
using Tests.Integration.Bus.SupportClasses;

namespace Tests.Integration.Bus
{
    public class Send : With_default_bus
    {
        protected override void BeforeBusInitialization()
        {
            TestModel.ExchangeDeclare("SendExchange", ExchangeType.Topic, false, true, null);
        }

        [Test]
        public void Should_send_to_specific_endpoint()
        {
            var consumer = new GenericConsumer<SendMessage>();
            Bus.AddInstanceSubscription(consumer);
            Bus.Send(Bus.LocalEndpoint, new SendMessage());

            Assert.IsTrue(consumer.WaitForDelivery());
        }

        [Test]
        public void Should_report_error_if_send_cannot_be_performed()
        {
            var consumer = new GenericConsumer<SendMessage>();
            Bus.AddInstanceSubscription(consumer);

            BasicReturn error = null;
            Bus.Send(new RogerEndpoint("inexistent"), new SendMessage(), reason => error = reason);

            Assert.IsFalse(consumer.WaitForDelivery());
            Assert.IsNotNull(error);
        }

        [Test]
        public void Error_reports_should_be_raised_only_for_the_call_which_triggered_them()
        {
            var consumer = new GenericConsumer<SendMessage>();
            Bus.AddInstanceSubscription(consumer);

            var errors = new SynchronizedCollection<BasicReturn>();

            Bus.Send(new RogerEndpoint("inexistent1"), new SendMessage(), errors.Add);
            Bus.Send(new RogerEndpoint("inexistent2"), new SendMessage(), errors.Add);

            Assert.IsFalse(consumer.WaitForDelivery());
            Assert.AreEqual(2, errors.Count);
        }

        [Test]
        public void Sends_should_contain_reply_endpoint()
        {
            var consumer = new SendCurrentMessageConsumer(Bus);
            Bus.AddInstanceSubscription(consumer);
            Bus.Send(Bus.LocalEndpoint, new SendMessage());

            Assert.IsTrue(consumer.WaitForDelivery());
            Assert.AreEqual(Bus.LocalEndpoint, consumer.CurrentMessage.Endpoint);
        }
    }
}