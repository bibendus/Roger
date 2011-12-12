﻿using System;
using System.Collections.Generic;
using System.Threading;
using RabbitMQ.Client;
using Rabbus.Internal;
using Rabbus.Internal.Impl;
using Rabbus.Utilities;

namespace Rabbus
{
    public class DefaultRabbitBus : IRabbitBus
    {
        private readonly IReliableConnection connection;
        private readonly IConsumingProcess consumingProcess;
        private readonly IRabbusLog log;
        private readonly IPublishingProcess publishingProcess;
        private int disposed;

        public DefaultRabbitBus(IConnectionFactory connectionFactory,
                                IConsumerResolver consumerResolver = null,
                                ISupportedMessageTypesResolver supportedMessageTypesResolver = null,
                                IExchangeResolver exchangeResolver = null,
                                IRoutingKeyResolver routingKeyResolver = null,
                                IMessageSerializer serializer = null,
                                IRabbusLog log = null,
                                IIdGenerator idGenerator = null,
                                ISequenceGenerator sequenceGenerator = null,
                                IEnumerable<IMessageFilter> messageFilters = null)
        {
            consumerResolver = consumerResolver.Or(Default.ConsumerResolver);
            supportedMessageTypesResolver = supportedMessageTypesResolver.Or(Default.SupportedMessageTypesResolver);
            exchangeResolver = exchangeResolver.Or(Default.ExchangeResolver);
            routingKeyResolver = routingKeyResolver.Or(Default.RoutingKeyResolver);
            serializer = serializer.Or(Default.Serializer);
            idGenerator = idGenerator.Or(Default.IdGenerator);
            sequenceGenerator = sequenceGenerator.Or(Default.SequenceGenerator);
            messageFilters = messageFilters.Or(Default.Filters);
            this.log = log.Or(Default.Log);

            connection = new ReliableConnection(connectionFactory, this.log);

            publishingProcess = new QueueingPublishingProcess(connection,
                                                              idGenerator,
                                                              sequenceGenerator,
                                                              exchangeResolver,
                                                              routingKeyResolver,
                                                              serializer,
                                                              Default.TypeResolver,
                                                              this.log,
                                                              () => LocalEndpoint,
                                                              TimeSpan.FromSeconds(10));

            consumingProcess = new DefaultConsumingProcess(connection,
                                                           idGenerator,
                                                           exchangeResolver,
                                                           routingKeyResolver,
                                                           serializer,
                                                           Default.TypeResolver, 
                                                           consumerResolver,
                                                           Default.Reflection, 
                                                           supportedMessageTypesResolver,
                                                           messageFilters,
                                                           this.log);

            connection.ConnectionEstabilished += ConnectionEstabilished;
            connection.ConnectionAttemptFailed += ConnectionAttemptFailed;
            connection.UnexpectedShutdown += ConnectionUnexpectedShutdown;
        }

        public event Action Started = delegate { };
        public event Action ConnectionFailure = delegate { };

        public CurrentMessageInformation CurrentMessage
        {
            get { return consumingProcess.CurrentMessage; }
        }

        public RabbusEndpoint LocalEndpoint
        {
            get { return consumingProcess.Endpoint; }
        }

        public TimeSpan ConnectionAttemptInterval
        {
            get { return connection.ConnectionAttemptInterval; }
        }

        public void Start()
        {
            log.Debug("Starting bus");

            connection.Connect();
            publishingProcess.Start();
        }

        public IDisposable AddInstanceSubscription(IConsumer instanceConsumer)
        {
            return consumingProcess.AddInstanceSubscription(instanceConsumer);
        }

        public void Publish(object message)
        {
            publishingProcess.Publish(message);
        }

        public void Request(object message, Action<BasicReturn> basicReturnCallback = null)
        {
            publishingProcess.Request(message, basicReturnCallback);
        }

        public void Send(RabbusEndpoint endpoint, object message, Action<BasicReturn> basicReturnCallback = null)
        {
            publishingProcess.Send(endpoint, message, basicReturnCallback);
        }

        public void PublishMandatory(object message, Action<BasicReturn> basicReturnCallback = null)
        {
            publishingProcess.PublishMandatory(message, basicReturnCallback);
        }

        public void Reply(object message, Action<BasicReturn> basicReturnCallback = null)
        {
            publishingProcess.Reply(message, CurrentMessage, basicReturnCallback);
        }

        public void Consume(object message)
        {
            consumingProcess.Consume(message);
        }

        private void ConnectionEstabilished()
        {
            Started();
            log.Debug("Bus started");
        }

        private void ConnectionAttemptFailed()
        {
            ConnectionFailure();
        }

        private void ConnectionUnexpectedShutdown(ShutdownEventArgs obj)
        {
            ConnectionFailure();
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref disposed, 1, 0) == 1)
                return;

            log.Debug("Disposing bus");

            publishingProcess.Dispose();

            // TODO: beware that currently here order is important as consumer won't stop unless connection is closed
            connection.Dispose();
            consumingProcess.Dispose();
        }
    }
}