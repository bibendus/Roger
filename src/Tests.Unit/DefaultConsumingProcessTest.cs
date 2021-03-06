﻿using MbUnit.Framework;
using NSubstitute;
using RabbitMQ.Client;
using Roger;
using Roger.Internal;
using Roger.Internal.Impl;
using Roger.Messages;

namespace Tests.Unit
{
    [TestFixture]
    public class DefaultConsumingProcessTest
    {
        private IReliableConnection connection;
        private IModelWithConnection model;
        private DefaultConsumingProcess sut;
        private IQueueFactory queueFactory;
        private Aggregator aggregator;

        [SetUp]
        public void Setup()
        {
            connection = Substitute.For<IReliableConnection>();
            model = Substitute.For<IModelWithConnection>();
            connection.CreateModel().Returns(model);

            queueFactory = Substitute.For<IQueueFactory>();
            aggregator = new Aggregator();

            sut = new DefaultConsumingProcess(Substitute.For<IIdGenerator>(),
                                              Substitute.For<IExchangeResolver>(),
                                              Substitute.For<IMessageSerializer>(), 
                                              Substitute.For<IMessageTypeResolver>(),
                                              Substitute.For<IConsumerContainer>(), 
                                              Substitute.For<IMessageFilter>(),
                                              queueFactory,
                                              Substitute.For<IConsumerInvoker>(),
                                              new RogerOptions(), aggregator);
        }

        [Test]
        public void Should_create_queue_when_connection_is_established_for_the_first_time()
        {
            queueFactory.Create(model).Returns(new QueueDeclareOk("someQueue", 1, 1));

            aggregator.Notify(new ConnectionEstablished(connection));

            queueFactory.Received().Create(model);
        }

        [Test]
        [Description(@"Redeclaring queue on connections after the first is required because if a client is disconnected for too long 
and during disconnection his queue is deleted it will have disappeared and need to be redeclared")]
        public void Should_redeclare_queue_with_same_previous_name_upon_following_connections()
        {
            queueFactory.Create(model).Returns(new QueueDeclareOk("someQueue", 1, 1));

            aggregator.Notify(new ConnectionEstablished(connection));
            aggregator.Notify(new ConnectionEstablished(connection));

            queueFactory.Received(1).Create(model);
            queueFactory.Received(1).Create("someQueue", model);
        }

        [Test]
        public void Should_delete_queue_when_bus_is_disposed_of()
        {
            queueFactory.Create(model).Returns(new QueueDeclareOk("someQueue", 1, 1));
            aggregator.Notify(new ConnectionEstablished(connection));

            sut.Dispose();

            model.Received().QueueDelete("someQueue");
        }
    }
}