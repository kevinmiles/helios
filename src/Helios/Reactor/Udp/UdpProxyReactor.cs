﻿using System;
using System.Net;
using System.Net.Sockets;
using Helios.Exceptions;
using Helios.Net;
using Helios.Ops;
using Helios.Reactor.Response;
using Helios.Topology;


namespace Helios.Reactor.Udp
{
    public class UdpProxyReactor : ProxyReactorBase<IPEndPoint>
    {
        protected EndPoint RemoteEndPoint;

        public UdpProxyReactor(IPAddress localAddress, int localPort, NetworkEventLoop eventLoop, int bufferSize = NetworkConstants.DEFAULT_BUFFER_SIZE) 
            : base(localAddress, localPort, eventLoop, SocketType.Dgram, ProtocolType.Udp, bufferSize)
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
        }

        public override bool IsActive { get; protected set; }

        public override void Configure(IConnectionConfig config)
        {
            if (config.HasOption<int>("receiveBufferSize"))
                Listener.ReceiveBufferSize = config.GetOption<int>("receiveBufferSize");
            if (config.HasOption<int>("sendBufferSize"))
                Listener.SendBufferSize = config.GetOption<int>("sendBufferSize");
            if(config.HasOption<bool>("reuseAddress"))
                Listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, config.GetOption<bool>("reuseAddress"));
            if (config.HasOption<bool>("keepAlive"))
                Listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, config.GetOption<bool>("keepAlive"));
        }

        protected override void StartInternal()
        {
            IsActive = true;
            Listener.BeginReceiveFrom(Buffer, 0, Buffer.Length, SocketFlags.None, ref RemoteEndPoint, ReceiveCallback, Listener);
        }

        private void ReceiveCallback(IAsyncResult ar)
        {
            var socket = (Socket) ar.AsyncState;
            try
            {
                var received = socket.EndReceiveFrom(ar, ref RemoteEndPoint);
                var dataBuff = new byte[received];
                Array.Copy(Buffer, dataBuff, received);

                var remoteAddress = (IPEndPoint)RemoteEndPoint;
                ReactorResponseChannel adapter;
                if (NodeMap.ContainsKey(remoteAddress))
                {
                    adapter = SocketMap[NodeMap[remoteAddress]];
                }
                else
                {
                    adapter = new ReactorProxyResponseChannel(this, socket, remoteAddress, EventLoop);
                    NodeMap.Add(remoteAddress, adapter.RemoteHost);
                    SocketMap.Add(adapter.RemoteHost, adapter);
                    NodeConnected(adapter.RemoteHost, adapter);
                }

                var networkData = new NetworkData() { Buffer = dataBuff, Length = received, RemoteHost = adapter.RemoteHost };
                socket.BeginReceiveFrom(Buffer, 0, Buffer.Length, SocketFlags.None, ref RemoteEndPoint, ReceiveCallback, socket); //receive more messages
                ReceivedData(networkData, adapter);
            }
            catch (SocketException ex)
            {
                var node =  NodeBuilder.FromEndpoint((IPEndPoint)RemoteEndPoint);
                CloseConnection(node, ex);
            }
        }

        public override void Send(byte[] message, INode responseAddress)
        {
            Listener.BeginSendTo(message, 0, message.Length, SocketFlags.None, responseAddress.ToEndPoint(), SendCallback, Listener);
        }

        private void SendCallback(IAsyncResult ar)
        {
            var socket = (Socket)ar.AsyncState;
            try
            {
                socket.EndSendTo(ar);
            }
            catch (SocketException ex) //node disconnected
            {
                var node = NodeMap[(IPEndPoint)socket.RemoteEndPoint];
                CloseConnection(node, ex);
            }
        }

        internal override void CloseConnection(INode remoteHost)
        {
            CloseConnection(remoteHost, null);
        }

        internal override void CloseConnection(INode remoteHost, Exception reason)
        {
            //NO-OP (no connections in UDP)
            try
            {
                NodeDisconnected(remoteHost, new HeliosConnectionException(ExceptionType.Closed, reason));
            }
            finally
            {
                NodeMap.Remove(remoteHost.ToEndPoint());
                SocketMap.Remove(remoteHost);
            }
        }

        protected override void StopInternal()
        {
            //NO-OP
        }

        #region IDisposable Members

        public override void Dispose(bool disposing)
        {
            if (!WasDisposed && disposing && Listener != null)
            {
                Stop();
                Listener.Dispose();
            }
            IsActive = false;
            WasDisposed = true;
        }

        #endregion

        
    }
}