using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Helios.Buffers;
using Helios.Exceptions;
using Helios.Serialization;
using Helios.Topology;
using Helios.Util.Concurrency;

namespace Helios.Net.Connections
{
    public class TcpConnection : UnstreamedConnectionBase
    {
        protected TcpClient _client;

        public TcpConnection(NetworkEventLoop eventLoop, INode node, TimeSpan timeout, IMessageEncoder encoder, IMessageDecoder decoder, IByteBufAllocator allocator, int bufferSize = NetworkConstants.DEFAULT_BUFFER_SIZE)
            : base(eventLoop, node, timeout, encoder, decoder, allocator, bufferSize)
        {
            InitClient();
        }

        public TcpConnection(NetworkEventLoop eventLoop, INode node, IMessageEncoder encoder, IMessageDecoder decoder, IByteBufAllocator allocator, int bufferSize = NetworkConstants.DEFAULT_BUFFER_SIZE)
            : base(eventLoop, node, encoder, decoder, allocator, bufferSize)
        {
            InitClient();
        }

        public TcpConnection(TcpClient client, int bufferSize = NetworkConstants.DEFAULT_BUFFER_SIZE)
            : base(bufferSize)
        {
            InitClient(client);
        }

        public TcpConnection(TcpClient client, IMessageEncoder encoder, IMessageDecoder decoder, IByteBufAllocator allocator, int bufferSize = NetworkConstants.DEFAULT_BUFFER_SIZE)
            : base(bufferSize)
        {
            InitClient(client);
            Encoder = encoder;
            Decoder = decoder;
            Allocator = allocator;
        }

        public override TransportType Transport { get { return TransportType.Tcp; } }

        public override bool Blocking
        {
            get { return _client.Client.Blocking; }
            set { _client.Client.Blocking = value; }
        }

        public bool NoDelay
        {
            get { return _client.NoDelay; }
            set { _client.NoDelay = value; }
        }

        public int Linger
        {
            get { return _client.LingerState.Enabled ? _client.LingerState.LingerTime : 0; }
            set { _client.LingerState = new LingerOption(value > 0, value); }
        }

        public int SendBufferSize
        {
            get { return _client.SendBufferSize; }
            set { _client.SendBufferSize = value; }
        }

        public int ReceiveBufferSize
        {
            get { return _client.ReceiveBufferSize; }
            set { _client.ReceiveBufferSize = value; }
        }

        public bool ReuseAddress
        {
            get { return !_client.ExclusiveAddressUse; }
            set { _client.ExclusiveAddressUse = !value; }
        }

        public bool KeepAlive
        {
            get { return ((int)_client.Client.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive) == 1); }
            set { _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, value ? 1 : 0); }
        }

        public override bool IsOpen()
        {
            if (_client == null) return false;
            return _client.Connected;
        }

        public override int Available
        {
            get
            {
                if (!IsOpen()) return 0;
                return _client.Available;
            }
        }

#if NET35 || NET40
        public override Task<bool> OpenAsync()
        {
            CheckWasDisposed();

            return TaskRunner.Run<bool>(() =>
            {
                Open();
                return true;
            });
        }

#else
        public override async Task<bool> OpenAsync()
        {
            CheckWasDisposed();

            if (IsOpen()) return await Task.Run(() => true);

            if (RemoteHost == null || RemoteHost.Host == null)
            {
                throw new HeliosConnectionException(ExceptionType.NotOpen, "Cannot open a connection to a null Node or null Node.Host");
            }

            if (RemoteHost.Port <= 0)
            {
                throw new HeliosConnectionException(ExceptionType.NotOpen, "Cannot open a connection to an invalid port");
            }

            if (_client == null)
                InitClient();


            return await _client.ConnectAsync(RemoteHost.Host, RemoteHost.Port)
                .ContinueWith(x =>
                {
                    var result = x.IsCompleted && !x.IsFaulted && !x.IsCanceled;
                    if (result)
                    {
                        SetLocal(_client);
                        InvokeConnectIfNotNull(RemoteHost);
                    }
                    return result;
                },
                    TaskContinuationOptions.AttachedToParent | TaskContinuationOptions.ExecuteSynchronously);
        }
#endif

        public override void Configure(IHeliosConfig config)
        {
            if (config.HasOption<int>("receiveBufferSize"))
                ReceiveBufferSize = config.GetOption<int>("receiveBufferSize");
            if (config.HasOption<int>("sendBufferSize"))
                SendBufferSize = config.GetOption<int>("sendBufferSize");
            if (config.HasOption<bool>("reuseAddress"))
                ReuseAddress = config.GetOption<bool>("reuseAddress");
            if (config.HasOption<bool>("tcpNoDelay"))
                NoDelay = config.GetOption<bool>("tcpNoDelay");
            if (config.HasOption<bool>("keepAlive"))
                KeepAlive = config.GetOption<bool>("keepAlive");
            if (config.HasOption<bool>("linger") && config.GetOption<bool>("linger"))
                Linger = 10;
            else
                Linger = 0;
            if (config.HasOption<TimeSpan>("connectTimeout"))
                Timeout = config.GetOption<TimeSpan>("connectTimeout");
        }

        public override void Open()
        {
            CheckWasDisposed();

            if (IsOpen()) return;

            if (RemoteHost == null || RemoteHost.Host == null)
            {
                throw new HeliosConnectionException(ExceptionType.NotOpen, "Cannot open a connection to a null Node or null Node.Host");
            }

            if (RemoteHost.Port <= 0)
            {
                throw new HeliosConnectionException(ExceptionType.NotOpen, "Cannot open a connection to an invalid port");
            }

            if (_client == null)
                InitClient();

            var ar = _client.BeginConnect(RemoteHost.Host, RemoteHost.Port, null, null);
            if (ar.AsyncWaitHandle.WaitOne(Timeout))
            {
                try
                {
                    _client.EndConnect(ar);
                }
                catch (SocketException ex)
                {
                    throw new HeliosConnectionException(ExceptionType.NotOpen, ex);
                }
            }
            else
            {
                _client.Close();
                throw new HeliosConnectionException(ExceptionType.TimedOut, "Timed out on connect");
            }
            SetLocal(_client);
            InvokeConnectIfNotNull(RemoteHost);
        }

        protected override void BeginReceiveInternal()
        {
            var receiveState = CreateNetworkState(_client.Client, RemoteHost);
            _client.Client.BeginReceive(receiveState.RawBuffer, 0, receiveState.RawBuffer.Length, SocketFlags.None, ReceiveCallback, receiveState);
        }


        public override void Close(Exception reason)
        {
            InvokeDisconnectIfNotNull(RemoteHost, new HeliosConnectionException(ExceptionType.Closed, reason));

            if (_client == null || WasDisposed || !IsOpen())
                return;

            _client.Close();
            EventLoop.Shutdown(TimeSpan.FromSeconds(2));
            _client = null;
        }

        public override void Close()
        {
            Close(null);
        }

        protected override void SendInternal(byte[] buffer, int index, int length, INode destination)
        {
            try
            {
                if (WasDisposed || !_client.Connected)
                {
                    Close();
                    return;
                }

                var buf = Allocator.Buffer(length);
                buf.WriteBytes(buffer, index, length);
                List<IByteBuf> encodedMessages;
                Encoder.Encode(this, buf, out encodedMessages);
                foreach (var message in encodedMessages)
                {
                    var bytesToSend = message.ToArray();
                    var bytesSent = 0;
                    while (bytesSent < bytesToSend.Length)
                    {
                        bytesSent += _client.Client.Send(bytesToSend, bytesSent, bytesToSend.Length - bytesSent,
                            SocketFlags.None);
                    }

                }
            }
            catch (SocketException ex)
            {
                Close(ex);
            }
            catch (Exception ex)
            {
                InvokeErrorIfNotNull(ex);
            }
        }

        #region IDisposable Members

        protected override void Dispose(bool disposing)
        {
            if (!WasDisposed)
            {
                if (disposing)
                {
                    if (_client != null)
                    {
                        ((IDisposable)_client).Dispose();
                        _client = null;
                        EventLoop.Dispose();
                    }
                }
            }
            WasDisposed = true;
        }

        #endregion

        private void InitClient(TcpClient client)
        {
            _client = client;
            _client.NoDelay = true;
            _client.ReceiveTimeout = Timeout.Seconds;
            _client.SendTimeout = Timeout.Seconds;
            _client.ReceiveBufferSize = BufferSize;
            var ipAddress = (IPEndPoint)_client.Client.RemoteEndPoint;
            RemoteHost = Binding = NodeBuilder.FromEndpoint(ipAddress);
            Local = NodeBuilder.FromEndpoint((IPEndPoint)_client.Client.LocalEndPoint);
        }

        private void InitClient()
        {
            _client = new TcpClient()
            {
                ReceiveTimeout = Timeout.Seconds,
                SendTimeout = Timeout.Seconds,
                Client = { NoDelay = true },
                ReceiveBufferSize = BufferSize
            };
            RemoteHost = Binding;
        }

        /// <summary>
        /// After a TCP connection is successfully established, set the value of the local node
        /// to whatever port / IP was assigned.
        /// </summary>
        protected void SetLocal(TcpClient client)
        {
            var localEndpoint = (IPEndPoint)client.Client.LocalEndPoint;
            Local = NodeBuilder.FromEndpoint(localEndpoint);
        }
    }
}