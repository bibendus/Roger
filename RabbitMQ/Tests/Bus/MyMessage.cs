﻿using ProtoBuf;
using Rabbus;

namespace Tests.Bus
{
    [RabbusMessage("TestExchange")]
    [ProtoContract]
    public class MyMessage
    {
        [ProtoMember(1)]
        public int Value { get; set; }
    }
}