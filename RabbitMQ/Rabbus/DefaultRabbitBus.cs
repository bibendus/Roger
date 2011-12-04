﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Rabbus.Errors;
using Rabbus.GuidGeneration;
using Rabbus.Logging;
using Rabbus.PublishFailureHandling;
using Rabbus.Reflection;
using Rabbus.Resolvers;
using Rabbus.Serialization;
using Rabbus.Utilities;

namespace Rabbus
{
    public class DefaultRabbitBus : IRabbitBus
    {
        private readonly IRoutingKeyResolver routingKeyResolver;
        private readonly ITypeResolver typeResolver;
        private readonly IMessageSerializer serializer;
        private readonly IReflection reflection;
        private readonly IConsumerResolver consumerResolver;
        private readonly ISupportedMessageTypesResolver supportedMessageTypesResolver;
        private readonly IExchangeResolver exchangeResolver;
        private readonly IRabbusLog log;
        private ThreadLocal<IModel> publishModelHolder;
        private readonly IReliableConnection connection;
        private readonly IGuidGenerator guidGenerator;

        public event Action Started = delegate { };
        public event Action ConnectionFailure = delegate { };

        [ThreadStatic]
        private static CurrentMessageInformation _currentMessage;

        public CurrentMessageInformation CurrentMessage { get { return _currentMessage; } }
        public RabbusEndpoint LocalEndpoint { get; private set; }
        public TimeSpan ConnectionAttemptInterval { get { return connection.ConnectionAttemptInterval; } }

        private readonly ConcurrentDictionary<WeakReference, object> instanceConsumers = new ConcurrentDictionary<WeakReference, object>();
        private IModel receivingModel;
        private bool disposed;
        private readonly IPublishFailureHandler publishFailureHandler;
        private QueueingBasicConsumer queueConsumer;

        public DefaultRabbitBus(IConnectionFactory connectionFactory,
                                IConsumerResolver consumerResolver = null,
                                ITypeResolver typeResolver = null,
                                ISupportedMessageTypesResolver supportedMessageTypesResolver = null,
                                IExchangeResolver exchangeResolver = null,
                                IRoutingKeyResolver routingKeyResolver = null,
                                IMessageSerializer serializer = null,
                                IReflection reflection = null,
                                IRabbusLog log = null,
                                IGuidGenerator guidGenerator = null)
        {
            this.consumerResolver = consumerResolver.Or(Default.ConsumerResolver);
            this.typeResolver = typeResolver.Or(Default.TypeResolver);
            this.supportedMessageTypesResolver = supportedMessageTypesResolver.Or(Default.SupportedMessageTypesResolver);
            this.exchangeResolver = exchangeResolver.Or(Default.ExchangeResolver);
            this.reflection = reflection.Or(Default.Reflection);
            this.routingKeyResolver = routingKeyResolver.Or(Default.RoutingKeyResolver);
            this.serializer = serializer.Or(Default.Serializer);
            this.log = log.Or(Default.Log);
            this.guidGenerator = guidGenerator.Or(Default.GuidGenerator);

            publishFailureHandler = new DefaultPublishFailureHandler(this.log);
            connection = new ReliableConnection(connectionFactory, this.log, ConnectionEstabilished);

            connection.ConnectionAttemptFailed += HandleConnectionAttemptFailed;
            connection.UnexpectedShutdown += HandleConnectionUnexpectedShutdown;
        }

        private void HandleConnectionUnexpectedShutdown(ShutdownEventArgs obj)
        {
            ConnectionFailure();
        }

        private void HandleConnectionAttemptFailed()
        {
            ConnectionFailure();            
        }

        private IModel CreatePublishModel()
        {
            var publishModel = connection.CreateModel();

            publishModel.BasicReturn += PublishModelOnBasicReturn;

            return publishModel;
        }

        private void PublishModelOnBasicReturn(IModel model, BasicReturnEventArgs args)
        {
            // beware, this is called on the RabbitMQ client connection thread, therefore we 
            // should use the model parameter rather than the ThreadLocal property. Also we should not block
            log.DebugFormat("Model issued a basic return for message {{we can do better here}} with reply {0} - {1}", args.ReplyCode, args.ReplyText);
            publishFailureHandler.Handle(new PublishFailureReason(new RabbusGuid(args.BasicProperties.MessageId), args.ReplyCode, args.ReplyText));
        }

        public void Start()
        {
            log.Debug("Starting bus");

            connection.Connect();
        }

        private void ConnectionEstabilished()
        {
            publishModelHolder = new ThreadLocal<IModel>(CreatePublishModel);
            receivingModel = connection.CreateModel();

            LocalEndpoint = new RabbusEndpoint(receivingModel.QueueDeclare("", false, true, false, null));

            CreateConsumer();
            ConsumeAsynchronously();

            Started();

            log.Debug("Bus started");
        }

        private IModel PublishModel { get { return publishModelHolder.Value; } }

        private void CreateConsumer()
        {
            CreateBindings(consumerResolver.GetAllSupportedMessageTypes());

            queueConsumer = new QueueingBasicConsumer(receivingModel);
            receivingModel.BasicConsume(LocalEndpoint.Queue, false, queueConsumer);
        }

        private void CreateBindings(ISet<Type> messageTypes)
        {
            // Here we allow eventual duplicate bindings if this method is called multiple times which result
            // in queues being bound to the same exchange with the same arguments
            // http://www.rabbitmq.com/amqp-0-9-1-reference.html#queue.bind

            var allExchanges = new HashSet<string>();

            log.Debug("Performing pub/sub bindings");

            foreach (var messageType in messageTypes.ExceptReplies())
            {
                var exchange = exchangeResolver.Resolve(messageType);
                var routingKey = routingKeyResolver.Resolve(messageType);

                log.DebugFormat("Binding queue {0} to exchange {1} with routing key {2}", LocalEndpoint, exchange, routingKey);

                receivingModel.QueueBind(LocalEndpoint.Queue, exchange, routingKey);

                allExchanges.Add(exchange);
            }

            log.Debug("Performing private messages bindings");

            foreach (var exchange in allExchanges)
            {
                log.DebugFormat("Binding queue {0} to exchange {1} with quete name as routing key", LocalEndpoint, exchange);

                receivingModel.QueueBind(LocalEndpoint.Queue, exchange, LocalEndpoint.Queue);
            }
        }

        private void ConsumeAsynchronously()
        {
            Task.Factory.StartNew(ConsumeSynchronously, TaskCreationOptions.LongRunning);
        }

        private void ConsumeSynchronously()
        {
            foreach (var message in BlockingDequeue(queueConsumer.Queue))
            {
                SetCurrentMessageAndInvokeConsumers(message);
                queueConsumer.Model.BasicAck(message.DeliveryTag, false);
            }
        }

        private IEnumerable<CurrentMessageInformation> BlockingDequeue(IEnumerable queue)
        {
            return queue.OfType<BasicDeliverEventArgs>().Select(CreateMessage);
        }

        private CurrentMessageInformation CreateMessage(BasicDeliverEventArgs args)
        {
            var properties = args.BasicProperties;
            var messageType = typeResolver.ResolveType(properties.Type);

            return new CurrentMessageInformation
                   {
                       MessageId = new RabbusGuid(properties.MessageId),
                       MessageType = messageType,
                       Endpoint = new RabbusEndpoint(properties.ReplyTo),
                       CorrelationId = string.IsNullOrWhiteSpace(properties.CorrelationId) ? RabbusGuid.Empty : new RabbusGuid(properties.CorrelationId),
                       DeliveryTag = args.DeliveryTag,
                       Exchange = args.Exchange,
                       Body = serializer.Deserialize(messageType, args.Body)
                   };
        }

        private void SetCurrentMessageAndInvokeConsumers(CurrentMessageInformation message)
        {
            _currentMessage = message;

            var consumers = ResolveConsumers(_currentMessage.MessageType);

            var localInstanceConsumers = consumers.Item1.ToArray();
            var defaultConsumers = consumers.Item2.ToArray();

            log.DebugFormat("Found {0} standard consumers and {1} instance consumers for message {2}",
                            defaultConsumers.Length,
                            localInstanceConsumers.Length,
                            _currentMessage.MessageType);

            var allConsumers = localInstanceConsumers.Concat(defaultConsumers);

            foreach (var c in allConsumers)
            {
                log.DebugFormat("Invoking Consume method on consumer {0} for message {1}",
                                c.GetType(),
                                _currentMessage.MessageType);

                reflection.InvokeConsume(c, _currentMessage.Body);
            }

            consumerResolver.Release(defaultConsumers);
        }

        public IDisposable AddInstanceSubscription(IConsumer consumer)
        {
            var consumerReference = new WeakReference(consumer);
            instanceConsumers.TryAdd(consumerReference, null);

            CreateBindings(GetSupportedMessageTypes(consumer));

            // TODO: queue bindings are not removed, no problem unless we start adding too many instance subscriptions
            return RemoveInstanceConsumer(consumerReference);
        }

        private IDisposable RemoveInstanceConsumer(WeakReference consumerReference)
        {
            return new DisposableAction(() =>
            {
                object _;
                instanceConsumers.TryRemove(consumerReference, out _);
            });
        }

        private ISet<Type> GetSupportedMessageTypes(IConsumer consumer)
        {
            return supportedMessageTypesResolver.Resolve(consumer.GetType());
        }

        public void Publish(object message)
        {
            var messageType = message.GetType();
            var properties = CreateProperties(messageType);

            PublishModel.BasicPublish(exchangeResolver.Resolve(messageType),
                                      routingKeyResolver.Resolve(messageType),
                                      properties,
                                      serializer.Serialize(message));
        }

        private IBasicProperties CreateProperties(Type messageType)
        {
            var properties = PublishModel.CreateBasicProperties();

            properties.MessageId = guidGenerator.Next();
            properties.Type = typeResolver.Unresolve(messageType);
            properties.ReplyTo = LocalEndpoint.Queue;
            properties.ContentType = serializer.ContentType;
            
            return properties;
        }

        public void Request(object message)
        {
            Request(message, Nop);
        }

        public void Request(object message, Action<PublishFailureReason> requestFailure)
        {
            var properties = CreateProperties(message.GetType());
            properties.CorrelationId = guidGenerator.Next();

            PublishMandatoryInternal(message, properties, requestFailure);

            log.DebugFormat("Issued request with message {0}", message.GetType());
        }

        public void Send(RabbusEndpoint endpoint, object message)
        {
            Send(endpoint, message, Nop);
        }

        public void Send(RabbusEndpoint endpoint, object message, Action<PublishFailureReason> publishFailureCallback)
        {
            var properties = CreateProperties(message.GetType());

            PublishMandatoryInternal(message, properties, publishFailureCallback, endpoint.Queue);
        }

        public void PublishMandatory(object message, Action<PublishFailureReason> publishFailureCallback)
        {
            var properties = CreateProperties(message.GetType());

            PublishMandatoryInternal(message, properties, publishFailureCallback);
        }

        private void PublishMandatoryInternal(object message,
                                              IBasicProperties properties,
                                              Action<PublishFailureReason> publishFailureCallback)
        {
            PublishMandatoryInternal(message, properties, publishFailureCallback, routingKeyResolver.Resolve(message.GetType()));
        }

        private void PublishMandatoryInternal(object message,
                                              IBasicProperties properties,
                                              Action<PublishFailureReason> publishFailureCallback,
                                              string routingKey)
        {
            publishFailureHandler.Subscribe(new RabbusGuid(properties.MessageId), publishFailureCallback);

            PublishModel.BasicPublish(exchangeResolver.Resolve(message.GetType()),
                                      routingKey,
                                      true,
                                      false,
                                      properties,
                                      serializer.Serialize(message));
        }

        public void Reply(object message)
        {
            EnsureRequestContext();

            ValidateReplyMessage(message);

            var properties = CreateProperties(message.GetType());
            properties.CorrelationId = CurrentMessage.CorrelationId.ToString();

            // reply on the same exchange of the request message
            PublishModel.BasicPublish(exchangeResolver.Resolve(CurrentMessage.MessageType),
                                      CurrentMessage.Endpoint,
                                      properties,
                                      serializer.Serialize(message));
        }

        private void ValidateReplyMessage(object message)
        {
            if (!message.GetType().IsDefined(typeof (RabbusReplyAttribute), false))
            {
                log.Error("Reply method called with a reply message not decorated woth the right attribute");
                throw new InvalidOperationException(ErrorMessages.ReplyMessageNotAReply);
            }
        }

        private void EnsureRequestContext()
        {
            if (CurrentMessage == null ||
                CurrentMessage.Endpoint.IsEmpty() ||
                CurrentMessage.CorrelationId.IsEmpty)
            {
                log.Error("Reply method called out of the context of a message handling request");
                throw new InvalidOperationException(ErrorMessages.ReplyInvokedOutOfRequestContext);
            }
        }

        private Tuple<IEnumerable<IConsumer>, IEnumerable<IConsumer>> ResolveConsumers(Type messageType)
        {
            return Tuple.Create(InstanceConsumers(messageType), consumerResolver.Resolve(messageType));
        }

        private IEnumerable<IConsumer> InstanceConsumers(Type messageType)
        {
            return instanceConsumers.Keys
                .Where(r => r.IsAlive)
                .Select(r => r.Target)
                .Cast<IConsumer>()
                .Where(c => GetSupportedMessageTypes(c).Contains(messageType));
        }

        public void Consume(object message)
        {
            SetCurrentMessageAndInvokeConsumers(new CurrentMessageInformation
            {
                MessageId = guidGenerator.Next(),
                Body = message,
                MessageType = message.GetType()
            });
        }

        private static void Nop<T>(T input)
        {}

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            log.Debug("Disposing bus");
            connection.Dispose();
        }
    }
}