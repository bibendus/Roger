﻿using System;
using System.Collections.Generic;
using System.Text;
using ZMQ;

namespace ZeroMQExtensions
{
    public interface ISocket : IDisposable
    {
        PollItem CreatePollItem(IOMultiPlex events);
        PollItem CreatePollItem(IOMultiPlex events, System.Net.Sockets.Socket sysSocket);
        void SetSockOpt(SocketOpt option, ulong value);
        void SetSockOpt(SocketOpt option, byte[] value);
        void SetSockOpt(SocketOpt option, int value);
        void SetSockOpt(SocketOpt option, long value);
        object GetSockOpt(SocketOpt option);
        void Bind(string addr);
        void Bind(Transport transport, string addr, uint port);
        void Bind(Transport transport, string addr);
        void Connect(string addr);
        void Forward(Socket destination);
        byte[] Recv(params SendRecvOpt[] flags);
        byte[] Recv();
        byte[] Recv(int timeout);
        string Recv(Encoding encoding);
        string Recv(Encoding encoding, int timeout);
        string Recv(Encoding encoding, params SendRecvOpt[] flags);
        Queue<byte[]> RecvAll();
        Queue<byte[]> RecvAll(params SendRecvOpt[] flags);
        Queue<string> RecvAll(Encoding encoding);
        Queue<string> RecvAll(Encoding encoding, params SendRecvOpt[] flags);
        void Send(byte[] message, params SendRecvOpt[] flags);
        void Send(byte[] message);
        void Send(string message, Encoding encoding);
        void Send();
        void SendMore();
        void SendMore(byte[] message);
        void SendMore(string message, Encoding encoding);
        void SendMore(string message, Encoding encoding, params SendRecvOpt[] flags);
        void Send(string message, Encoding encoding, params SendRecvOpt[] flags);
        string IdentityToString(Encoding encoding);
        void StringToIdentity(string identity, Encoding encoding);
        
        byte[] Identity { get; set; }
        ulong HWM { get; set; }
        bool RcvMore { get; }
        long Swap { get; set; }
        ulong Affinity { get; set; }
        long Rate { get; set; }
        long RecoveryIvl { get; set; }
        long MCastLoop { get; set; }
        ulong SndBuf { get; set; }
        ulong RcvBuf { get; set; }
        int Linger { get; set; }
        int ReconnectIvl { get; set; }
        int Backlog { get; set; }
        IntPtr FD { get; }
        IOMultiPlex[] Events { get; }
        string Address { get; }
        event PollHandler PollInHandler;
        event PollHandler PollOutHandler;
        event PollHandler PollErrHandler;
    }
}