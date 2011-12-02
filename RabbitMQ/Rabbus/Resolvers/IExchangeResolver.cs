﻿using System;

namespace Rabbus.Resolvers
{
    /// <summary>
    /// Implementors should provide an exchange name given the type of the message
    /// </summary>
    public interface IExchangeResolver
    {
        /// <summary>
        /// Resolves the name of the exchange a message of type <paramref name="messageType"/> will be published on
        /// </summary>
        /// <param name="messageType">The type of the message</param>
        /// <returns>The name of the exchange</returns>
        string Resolve(Type messageType);
    }
}