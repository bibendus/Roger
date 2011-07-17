﻿using System;

namespace Rabbus
{
    public class DefaultRoutingKeyGenerationStrategy : IRoutingKeyGenerationStrategy
    {
        public string GetRoutingKey<T>() where T : class
        {
            return GetRoutingKey(typeof(T));
        }

        public string GetRoutingKey(Type messageType)
        {
            return messageType.FullName;
        }
    }
}