// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace System.Net.Test.Common
{
    public class Http2LoopbackServer : GenericLoopbackServer, IDisposable
    {
        private Socket _listenSocket;
        private Http2Options _options;
        private Uri _uri;
        private List<Http2LoopbackConnection> _connections = new List<Http2LoopbackConnection>();

        public bool AllowMultipleConnections { get; set; }

        private Http2LoopbackConnection Connection
        {
            get
            {
                RemoveInvalidConnections();
                return _connections[0];
            }
        }

        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        public Uri Address
        {
            get
            {
                var localEndPoint = (IPEndPoint)_listenSocket.LocalEndPoint;
                string host = _options.Address.AddressFamily == AddressFamily.InterNetworkV6 ?
                    $"[{localEndPoint.Address}]" :
                    localEndPoint.Address.ToString();

                string scheme = _options.UseSsl ? "https" : "http";

                _uri = new Uri($"{scheme}://{host}:{localEndPoint.Port}/");

                return _uri;
            }
        }

        public static Http2LoopbackServer CreateServer()
        {
            return new Http2LoopbackServer(new Http2Options());
        }

        public static Http2LoopbackServer CreateServer(Http2Options options)
        {
            return new Http2LoopbackServer(options);
        }

        private Http2LoopbackServer(Http2Options options)
        {
            _options = options;
            _listenSocket = new Socket(_options.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            _listenSocket.Bind(new IPEndPoint(_options.Address, 0));
            _listenSocket.Listen(_options.ListenBacklog);
        }

        private void RemoveInvalidConnections()
        {
            _connections.RemoveAll((c) => c.IsInvalid);
        }

        public async Task<Http2LoopbackConnection> AcceptConnectionAsync()
        {
            RemoveInvalidConnections();

            if (!AllowMultipleConnections && _connections.Count != 0)
            {
                throw new InvalidOperationException("Connection already established. Set `AllowMultipleConnections = true` to bypass.");
            }

            Socket connectionSocket = await _listenSocket.AcceptAsync().ConfigureAwait(false);

            Http2LoopbackConnection connection = new Http2LoopbackConnection(connectionSocket, _options);
            _connections.Add(connection);

            return connection;
        }

        public async Task<Http2LoopbackConnection> EstablishConnectionAsync(params SettingsEntry[] settingsEntries)
        {
            (Http2LoopbackConnection connection, _) = await EstablishConnectionGetSettingsAsync();
            return connection;
        }

        public async Task<(Http2LoopbackConnection, SettingsFrame)> EstablishConnectionGetSettingsAsync(params SettingsEntry[] settingsEntries)
        {
            Http2LoopbackConnection connection = await AcceptConnectionAsync().ConfigureAwait(false);

            // Receive the initial client settings frame.
            Frame receivedFrame = await connection.ReadFrameAsync(Timeout).ConfigureAwait(false);
            Assert.Equal(FrameType.Settings, receivedFrame.Type);
            Assert.Equal(FrameFlags.None, receivedFrame.Flags);
            Assert.Equal(0, receivedFrame.StreamId);

            var clientSettingsFrame = (SettingsFrame)receivedFrame;

            // Receive the initial client window update frame.
            receivedFrame = await connection.ReadFrameAsync(Timeout).ConfigureAwait(false);
            Assert.Equal(FrameType.WindowUpdate, receivedFrame.Type);
            Assert.Equal(FrameFlags.None, receivedFrame.Flags);
            Assert.Equal(0, receivedFrame.StreamId);

            // Send the initial server settings frame.
            SettingsFrame settingsFrame = new SettingsFrame(settingsEntries);
            await connection.WriteFrameAsync(settingsFrame).ConfigureAwait(false);

            // Send the client settings frame ACK.
            Frame settingsAck = new Frame(0, FrameType.Settings, FrameFlags.Ack, 0);
            await connection.WriteFrameAsync(settingsAck).ConfigureAwait(false);

            // The client will send us a SETTINGS ACK eventually, but not necessarily right away.
            connection.ExpectSettingsAck();

            return (connection, clientSettingsFrame);
        }

        public override void Dispose()
        {
            if (_listenSocket != null)
            {
                _listenSocket.Dispose();
                _listenSocket = null;
            }
        }

        //
        // GenericLoopbackServer implementation
        //

        public override async Task<HttpRequestData> HandleRequestAsync(HttpStatusCode statusCode = HttpStatusCode.OK, IList<HttpHeaderData> headers = null, string content = null)
        {
            Http2LoopbackConnection connection = await EstablishConnectionAsync().ConfigureAwait(false);

            (int streamId, HttpRequestData requestData) = await connection.ReadAndParseRequestHeaderAsync().ConfigureAwait(false);

            // We are about to close the connection, after we send the response.
            // So, send a GOAWAY frame now so the client won't inadvertantly try to reuse the connection.
            await connection.SendGoAway(streamId).ConfigureAwait(false);

            if (content == null)
            {
                await connection.SendResponseHeadersAsync(streamId, endStream: true, statusCode, isTrailingHeader: false, headers).ConfigureAwait(false);
            }
            else
            {
                await connection.SendResponseHeadersAsync(streamId, endStream: false, statusCode, isTrailingHeader: false, headers).ConfigureAwait(false);
                await connection.SendResponseBodyAsync(streamId, Encoding.ASCII.GetBytes(content)).ConfigureAwait(false);
            }

            await connection.WaitForConnectionShutdownAsync().ConfigureAwait(false);

            return requestData;
        }

        public async static Task CreateClientAndServerAsync(Func<Uri, Task> clientFunc, Func<Http2LoopbackServer, Task> serverFunc, int timeout = 60_000)
        {
            using (var server = Http2LoopbackServer.CreateServer())
            {
                Task clientTask = clientFunc(server.Address);
                Task serverTask = serverFunc(server);

                await new Task[] { clientTask, serverTask }.WhenAllOrAnyFailed(timeout);
            }
        }
    }

    public class Http2Options
    {
        public IPAddress Address { get; set; } = IPAddress.Loopback;
        public int ListenBacklog { get; set; } = 1;
        public bool UseSsl { get; set; } = PlatformDetection.SupportsAlpn && !Capability.Http2ForceUnencryptedLoopback();
        public SslProtocols SslProtocols { get; set; } = SslProtocols.Tls12;
    }

    public sealed class Http2LoopbackServerFactory : LoopbackServerFactory
    {
        public static readonly Http2LoopbackServerFactory Singleton = new Http2LoopbackServerFactory();

        public static async Task CreateServerAsync(Func<Http2LoopbackServer, Uri, Task> funcAsync, int millisecondsTimeout = 60_000)
        {
            using (var server = Http2LoopbackServer.CreateServer())
            {
                await funcAsync(server, server.Address).TimeoutAfter(millisecondsTimeout).ConfigureAwait(false);
            }
        }

        public override async Task CreateServerAsync(Func<GenericLoopbackServer, Uri, Task> funcAsync, int millisecondsTimeout = 60_000)
        {
            using (var server = Http2LoopbackServer.CreateServer())
            {
                await funcAsync(server, server.Address).TimeoutAfter(millisecondsTimeout).ConfigureAwait(false);
            }
        }

        public override bool IsHttp11 => false;
        public override bool IsHttp2 => true;
    }
}
