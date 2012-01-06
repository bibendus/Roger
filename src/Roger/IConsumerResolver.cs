﻿using System;
using System.Collections.Generic;

namespace Roger
{
    public interface IConsumerResolver
    {
        IEnumerable<IConsumer> Resolve(Type messageType);
        void Release(IEnumerable<IConsumer> consumers);
        ISet<Type> GetAllSupportedMessageTypes();
    }
}