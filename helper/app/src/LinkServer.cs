using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Portaballs.Core;

namespace Portaballs.Helper
{
    /// <summary>
    /// The WebSocket server the game connects to: ws://127.0.0.1:PORT/
    ///
    /// It listens on the loopback address only, so nothing outside this PC can reach it. It is a plain
    /// TCP listener that does the small WebSocket handshake itself, which avoids Windows' HTTP service
    /// (and the administrator rights that can need).
    ///
    /// Any number of listeners may connect. Each gets the hello line first, then every batch. A
    /// listener that cannot keep up loses its OLDEST waiting batches (the decoder counts them as
    /// missed); it never slows the others or the poll loop down.
    ///
    /// Web pages are turned away unless --allow-any-origin is given: browsers always say which page is
    /// asking (the Origin header) and programs do not, so that is how the two are told apart.
    /// </summary>
    internal sealed class LinkServer : ISampleSink, IDisposable
    {
        private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private const int ClientQueueLength = 512;

        private readonly int _requestedPort;
        private readonly bool _allowAnyOrigin;
        private readonly Func<LinkHello> _hello;
        private readonly FileLog _log;

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Channel<Outgoing> _outbox = Channel.CreateUnbounded<Outgoing>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private readonly object _clientsGate = new object();
        private Client[] _clients = new Client[0];
        private TcpListener _listener;
        private long _sequence;
        private long _nextClientId;
        private long _messagesSent;
        private long _bytesSent;
        private long _batchesDropped;

        private struct Outgoing
        {
            public LinkSample[] Samples;
            public int Count;
            public LinkStatus Status;
            public bool IsStatus;
        }

        private sealed class Client
        {
            public long Id;
            public TcpClient Tcp;
            public WebSocket Socket;
            public Channel<byte[]> Queue;
            public string Name;
        }

        public LinkServer(int port, bool allowAnyOrigin, Func<LinkHello> hello, FileLog log)
        {
            _requestedPort = port;
            _allowAnyOrigin = allowAnyOrigin;
            _hello = hello;
            _log = log;
        }

        /// <summary>The port actually bound (differs from the request only when 0 was asked for).</summary>
        public int Port { get; private set; }

        /// <summary>The address actually bound: always 127.0.0.1.</summary>
        public IPAddress BoundAddress { get; private set; }

        public int ClientCount { get { return _clients.Length; } }

        public long MessagesSent { get { return Interlocked.Read(ref _messagesSent); } }

        public long BytesSent { get { return Interlocked.Read(ref _bytesSent); } }

        /// <summary>Batches thrown away because a listener was too slow to take them.</summary>
        public long BatchesDropped { get { return Interlocked.Read(ref _batchesDropped); } }

        /// <summary>Start listening. Throws if the port is taken.</summary>
        public void Start()
        {
            _listener = new TcpListener(IPAddress.Loopback, _requestedPort);
            _listener.Server.ExclusiveAddressUse = true;
            _listener.Start(16);
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BoundAddress = ((IPEndPoint)_listener.LocalEndpoint).Address;
            _ = Task.Run(AcceptLoop);
            _ = Task.Run(EncodeLoop);
        }

        public void PostBatch(LinkSample[] samples, int count)
        {
            if (count <= 0) return;
            Outgoing o = new Outgoing();
            o.Samples = samples;
            o.Count = count;
            _outbox.Writer.TryWrite(o);
        }

        public void PostStatus(LinkStatus status)
        {
            Outgoing o = new Outgoing();
            o.Status = status;
            o.IsStatus = true;
            _outbox.Writer.TryWrite(o);
        }

        // ------------------------------------------------------------------------------------------
        // Turning samples into text, once, and handing the bytes to every listener.
        // ------------------------------------------------------------------------------------------

        private async Task EncodeLoop()
        {
            StringBuilder sb = new StringBuilder(8192);
            UTF8Encoding utf8 = new UTF8Encoding(false);
            ChannelReader<Outgoing> reader = _outbox.Reader;
            try
            {
                while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
                {
                    Outgoing o;
                    while (reader.TryRead(out o))
                    {
                        Client[] clients = _clients;
                        if (o.IsStatus)
                        {
                            if (clients.Length == 0) continue;
                            sb.Clear();
                            LinkEncoder.AppendStatus(sb, o.Status);
                        }
                        else
                        {
                            // Batches are numbered even when nobody is listening, so a listener can tell
                            // "I joined late" from "I lost some".
                            long sequence = _sequence++;
                            if (clients.Length == 0) continue;
                            sb.Clear();
                            LinkEncoder.AppendBatchHeader(sb, sequence, Clock.Now, o.Count);
                            for (int i = 0; i < o.Count; i++) LinkEncoder.AppendSample(sb, o.Samples[i]);
                        }

                        byte[] bytes = utf8.GetBytes(sb.ToString());
                        for (int i = 0; i < clients.Length; i++) Enqueue(clients[i], bytes, !o.IsStatus);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                _log.Error("Encoder stopped by an error: " + e);
            }
        }

        private void Enqueue(Client client, byte[] bytes, bool isBatch)
        {
            // The queue drops its oldest entry when full; count that so it shows in the log.
            if (isBatch && client.Queue.Reader.Count >= ClientQueueLength) Interlocked.Increment(ref _batchesDropped);
            client.Queue.Writer.TryWrite(bytes);
        }

        // ------------------------------------------------------------------------------------------
        // Listeners coming and going.
        // ------------------------------------------------------------------------------------------

        private async Task AcceptLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient tcp;
                try
                {
                    tcp = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception e)
                {
                    if (_cts.IsCancellationRequested) return;
                    _log.Warn("Accept failed: " + e.Message);
                    continue;
                }
                _ = Task.Run(() => ServeClient(tcp));
            }
        }

        private async Task ServeClient(TcpClient tcp)
        {
            Client client = null;
            try
            {
                tcp.NoDelay = true;   // small messages must go out at once, not wait to be bundled
                NetworkStream stream = tcp.GetStream();

                string refusal = "bad request";
                string origin = null;
                string key = null;
                HandshakeResult request = await ReadHandshake(stream).ConfigureAwait(false);
                if (request != null) key = Evaluate(request, out refusal, out origin);

                if (key == null)
                {
                    string status = refusal == "not a websocket request" ? "426 Upgrade Required"
                                  : refusal == "origin not allowed" ? "403 Forbidden" : "400 Bad Request";
                    string body = "Portaballs helper (WebSocket only): " + refusal + "\n";
                    byte[] no = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 " + status + "\r\nContent-Type: text/plain\r\nConnection: close\r\nContent-Length: " +
                        body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n" + body);
                    try { await stream.WriteAsync(no, 0, no.Length, _cts.Token).ConfigureAwait(false); } catch (Exception) { }
                    _log.Info("Turned a connection away: " + refusal + (origin != null ? " (origin " + origin + ")" : "") + ".");
                    tcp.Close();
                    return;
                }

                byte[] yes = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + AcceptKey(key) + "\r\n\r\n");
                await stream.WriteAsync(yes, 0, yes.Length, _cts.Token).ConfigureAwait(false);

                client = new Client();
                client.Id = Interlocked.Increment(ref _nextClientId);
                client.Tcp = tcp;
                client.Name = "listener " + client.Id.ToString(CultureInfo.InvariantCulture) + (origin != null ? " (origin " + origin + ")" : "");
                client.Socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(20) });
                client.Queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(ClientQueueLength)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                });

                // Hello goes into the queue before the listener can receive anything else.
                StringBuilder sb = new StringBuilder(96);
                LinkEncoder.AppendHello(sb, _hello());
                client.Queue.Writer.TryWrite(Encoding.UTF8.GetBytes(sb.ToString()));
                AddClient(client);
                _log.Info("Connected: " + client.Name + ". Listeners now: " + ClientCount.ToString(CultureInfo.InvariantCulture) + ".");

                Task send = SendLoop(client);
                Task receive = ReceiveLoop(client);
                await Task.WhenAny(send, receive).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                if (!_cts.IsCancellationRequested) _log.Info("A connection ended with an error: " + e.Message);
            }
            finally
            {
                if (client != null)
                {
                    RemoveClient(client);
                    client.Queue.Writer.TryComplete();
                    try { client.Socket.Abort(); } catch (Exception) { }
                    try { client.Socket.Dispose(); } catch (Exception) { }
                    if (!_cts.IsCancellationRequested)
                    {
                        _log.Info("Disconnected: " + client.Name + ". Listeners now: " + ClientCount.ToString(CultureInfo.InvariantCulture) + ".");
                    }
                }
                try { tcp.Close(); } catch (Exception) { }
            }
        }

        private async Task SendLoop(Client client)
        {
            ChannelReader<byte[]> reader = client.Queue.Reader;
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                byte[] bytes;
                while (reader.TryRead(out bytes))
                {
                    await client.Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref _messagesSent);
                    Interlocked.Add(ref _bytesSent, bytes.Length);
                }
            }
        }

        /// <summary>The helper has nothing to hear, but reading is what notices a listener leaving.</summary>
        private async Task ReceiveLoop(Client client)
        {
            byte[] buffer = new byte[1024];
            while (client.Socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await client.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    try
                    {
                        using (CancellationTokenSource brief = new CancellationTokenSource(1000))
                        {
                            await client.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", brief.Token).ConfigureAwait(false);
                        }
                    }
                    catch (Exception)
                    {
                    }
                    return;
                }
            }
        }

        private void AddClient(Client client)
        {
            lock (_clientsGate)
            {
                Client[] bigger = new Client[_clients.Length + 1];
                Array.Copy(_clients, bigger, _clients.Length);
                bigger[_clients.Length] = client;
                _clients = bigger;
            }
        }

        private void RemoveClient(Client client)
        {
            lock (_clientsGate)
            {
                int at = Array.IndexOf(_clients, client);
                if (at < 0) return;
                Client[] smaller = new Client[_clients.Length - 1];
                Array.Copy(_clients, 0, smaller, 0, at);
                Array.Copy(_clients, at + 1, smaller, at, _clients.Length - at - 1);
                _clients = smaller;
            }
        }

        // ------------------------------------------------------------------------------------------
        // The WebSocket handshake (RFC 6455, section 4.2).
        // ------------------------------------------------------------------------------------------

        private sealed class HandshakeResult
        {
            public string Method;
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private async Task<HandshakeResult> ReadHandshake(NetworkStream stream)
        {
            byte[] buffer = new byte[8192];
            int used = 0;
            using (CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
            {
                limit.CancelAfter(5000);
                while (used < buffer.Length)
                {
                    int n = await stream.ReadAsync(buffer, used, buffer.Length - used, limit.Token).ConfigureAwait(false);
                    if (n <= 0) return null;
                    used += n;
                    if (EndsHeaders(buffer, used)) break;
                }
            }
            if (!EndsHeaders(buffer, used)) return null;

            string text = Encoding.ASCII.GetString(buffer, 0, used);
            string[] lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length < 1) return null;
            string[] request = lines[0].Split(' ');
            if (request.Length < 3) return null;

            HandshakeResult result = new HandshakeResult();
            result.Method = request[0];
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                result.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }
            return result;
        }

        private static bool EndsHeaders(byte[] buffer, int used)
        {
            for (int i = 3; i < used; i++)
            {
                if (buffer[i - 3] == 13 && buffer[i - 2] == 10 && buffer[i - 1] == 13 && buffer[i] == 10) return true;
            }
            return false;
        }

        /// <summary>Returns the client's key when the request is acceptable, otherwise null and why.</summary>
        private string Evaluate(HandshakeResult h, out string refusal, out string origin)
        {
            refusal = null;
            h.Headers.TryGetValue("Origin", out origin);

            string upgrade, key;
            h.Headers.TryGetValue("Upgrade", out upgrade);
            h.Headers.TryGetValue("Sec-WebSocket-Key", out key);
            if (h.Method != "GET" || upgrade == null || upgrade.IndexOf("websocket", StringComparison.OrdinalIgnoreCase) < 0 || string.IsNullOrEmpty(key))
            {
                refusal = "not a websocket request";
                return null;
            }
            if (!_allowAnyOrigin && !OriginIsLocal(origin))
            {
                refusal = "origin not allowed";
                return null;
            }
            return key;
        }

        /// <summary>No Origin at all (a program, not a browser), or a page served from this PC.</summary>
        internal static bool OriginIsLocal(string origin)
        {
            if (string.IsNullOrEmpty(origin)) return true;
            Uri uri;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out uri)) return false;
            string host = uri.Host;
            return host == "127.0.0.1" || host == "[::1]" || host == "::1"
                || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
        }

        internal static string AcceptKey(string clientKey)
        {
            using (SHA1 sha = SHA1.Create())
            {
                return Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(clientKey.Trim() + WebSocketGuid)));
            }
        }

        public void Dispose()
        {
            if (_cts.IsCancellationRequested) return;
            _cts.Cancel();
            try { if (_listener != null) _listener.Stop(); } catch (Exception) { }
            Client[] clients = _clients;
            for (int i = 0; i < clients.Length; i++)
            {
                try { clients[i].Socket.Abort(); } catch (Exception) { }
                try { clients[i].Tcp.Close(); } catch (Exception) { }
            }
            _outbox.Writer.TryComplete();
        }
    }
}
