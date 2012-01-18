using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Common.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Roger.Internal.Impl
{
    internal class QueueingPublishingProcess : IPublishingProcess
    {
        private readonly IReliableConnection connection;
        private readonly ILog log = LogManager.GetCurrentClassLogger();
        private int disposed;
        private Task publishTask;
        private readonly BlockingCollection<IDeliveryFactory> publishingQueue = new BlockingCollection<IDeliveryFactory>();
        private readonly CancellationTokenSource tokenSource = new CancellationTokenSource();
        private readonly ManualResetEventSlim publishEnabled = new ManualResetEventSlim(false);
        private IModel model;
        private readonly IIdGenerator idGenerator;
        private readonly ISequenceGenerator sequenceGenerator;
        private readonly IExchangeResolver exchangeResolver;
        private readonly IRoutingKeyResolver routingKeyResolver;
        private readonly IMessageSerializer serializer;
        private readonly ITypeResolver typeResolver;
        private readonly Func<RogerEndpoint> getEndpoint;
        private readonly IPublishModule modules;

        internal QueueingPublishingProcess(IReliableConnection connection,
                                           IIdGenerator idGenerator,
                                           ISequenceGenerator sequenceGenerator,
                                           IExchangeResolver exchangeResolver,
                                           IMessageSerializer serializer,
                                           ITypeResolver typeResolver,
                                           Func<RogerEndpoint> getEndpoint,
                                           IPublishModule modules)
        {
            this.connection = connection;
            this.idGenerator = idGenerator;
            this.sequenceGenerator = sequenceGenerator;
            this.exchangeResolver = exchangeResolver;
            routingKeyResolver = new DefaultRoutingKeyResolver();
            this.serializer = serializer;
            this.typeResolver = typeResolver;
            this.getEndpoint = getEndpoint;
            this.modules = modules;

            connection.ConnectionEstabilished += ConnectionOnConnectionEstabilished;
            connection.UnexpectedShutdown += ConnectionOnUnexpectedShutdown;

            modules.Initialize(this);
        }

        private void ConnectionOnConnectionEstabilished()
        {
            model = connection.CreateModel();
            modules.BeforePublishEnabled(model);

            EnablePublishing();
        }

        private void EnablePublishing()
        {
            publishEnabled.Set();
            log.Debug("Publishing is enabled");
        }

        private void ConnectionOnUnexpectedShutdown(ShutdownEventArgs shutdownEventArgs)
        {
            DisablePublishing();

            modules.AfterPublishDisabled(model);
        }

        private void DisablePublishing()
        {
            log.Warn("Disabling publishing due to unexpected connection shutdown");
            publishEnabled.Reset();
        }

        public void Start()
        {
            publishTask = Task.Factory.StartNew(() =>
            {
                try
                {
                    foreach (var factory in publishingQueue.GetConsumingEnumerable(tokenSource.Token))
                    {
                        try
                        {
                            publishEnabled.Wait(tokenSource.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            // operation canceled while waiting for publish to be enabled, just break out of the loop
                            break;
                        }
                        
                        var delivery = factory.Create(model, idGenerator, typeResolver, serializer, sequenceGenerator);

                        log.Debug("Executing delivery");

                        try
                        {
                            delivery.Execute(model, getEndpoint(), modules);
                        }
                         /* 
                         * we may experience a newtork problem even before the connection notifies its own shutdown
                         * but it's safer not to disable publishing to avoid the risk of deadlocking
                         * Instead we catch the exception and hopefully will republish these messages
                         */
                        catch (AlreadyClosedException e)
                        {
                            log.Error("Model was already closed when trying to publish on it", e);
                        }
                        catch (IOException e)
                        {
                            log.Error("IO error when trying to publish", e);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // operation canceled while iterating over the queue, do nothing and let task complete
                }
                catch (ObjectDisposedException)
                {
                    log.Error("Publishing queue was disposed while iterating over it, this is not supposed to be happening");
                }
            }, TaskCreationOptions.LongRunning);
        }

        public void Process(IDeliveryFactory factory)
        {
            Enqueue(factory);
        }

        private void Enqueue(IDeliveryFactory factory)
        {
            try
            {
                log.Debug("Enqueuing delivery");
                publishingQueue.Add(factory);
            }
            catch (ObjectDisposedException)
            {
                log.Error("Could not enqueue delivery as publishing queue has been disposed of already");
            }
            catch (InvalidOperationException e)
            {
                log.Error("Could not enqueue delivery", e);
            }
        }

        public void Publish(object message, bool persistent)
        {
            var messageType = message.GetType();

            Enqueue(new PublishFactory(messageType,
                                       Exchange(messageType),
                                       RoutingKey(messageType),
                                       Serialize(message),
                                       persistent));
        }

        public void Request(object message, Action<BasicReturn> basicReturnCallback, bool persistent)
        {
            var messageType = message.GetType();

            Enqueue(new RequestFactory(messageType,
                                       Exchange(messageType),
                                       RoutingKey(messageType),
                                       Serialize(message),
                                       persistent,
                                       basicReturnCallback));
        }

        public void Send(RogerEndpoint recipient, object message, Action<BasicReturn> basicReturnCallback, bool persistent)
        {
            var messageType = message.GetType();

            Enqueue(new SendFactory(messageType,
                                    Exchange(messageType),
                                    recipient,
                                    Serialize(message),
                                    basicReturnCallback,
                                    persistent));
        }

        public void PublishMandatory(object message, Action<BasicReturn> basicReturnCallback, bool persistent)
        {
            var messageType = message.GetType();

            Enqueue(new PublishMandatoryFactory(messageType,
                                                Exchange(messageType),
                                                RoutingKey(messageType),
                                                Serialize(message),
                                                basicReturnCallback,
                                                persistent));
        }

        public void Reply(object message, CurrentMessageInformation request, Action<BasicReturn> basicReturnCallback, bool persistent)
        {
            EnsureRequestContext(request);

            Enqueue(new ReplyFactory(message.GetType(),
                                     Exchange(request.MessageType),
                                     request,
                                     Serialize(message),
                                     basicReturnCallback,
                                     persistent));
        }

        private void EnsureRequestContext(CurrentMessageInformation currentMessage)
        {
            if (currentMessage == null ||
                currentMessage.Endpoint.IsEmpty ||
                currentMessage.CorrelationId.IsEmpty)
            {
                log.Error("Reply method called out of the context of a message handling request");
                throw new InvalidOperationException(ErrorMessages.ReplyInvokedOutOfRequestContext);
            }
        }

        private string Exchange(Type messageType)
        {
            return exchangeResolver.Resolve(messageType);
        }

        private string RoutingKey(Type messageType)
        {
            return routingKeyResolver.Resolve(messageType);
        }

        private byte[] Serialize(object message)
        {
            return serializer.Serialize(message);
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref disposed, 1, 0) == 1)
                return;

            publishingQueue.CompleteAdding();
            tokenSource.Cancel();
            publishTask.Wait();

            publishingQueue.Dispose();
            tokenSource.Dispose();
            publishTask.Dispose();
        }
    }
}