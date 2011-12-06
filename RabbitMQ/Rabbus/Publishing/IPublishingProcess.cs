using System;
using Rabbus.Errors;

namespace Rabbus.Publishing
{
    internal interface IPublishingProcess : IDisposable
    {
        void Start();
        void Publish(object message);
        void Request(object message, Action<BasicReturn> basicReturnCallback);
        void Send(RabbusEndpoint recipient, object message, Action<BasicReturn> basicReturnCallback);
        void PublishMandatory(object message, Action<BasicReturn> basicReturnCallback);
        void Reply(object message, CurrentMessageInformation currentMessage);
    }
}