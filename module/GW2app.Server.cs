using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Blish_HUD;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GW2app
{
    public partial class GW2app
    {
        private void StartHttpServer()
        {
            string prefix = $"http://+:{HttpPort}/";
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add(prefix);
            try
            {
                _httpListener.Start();
            }
            catch (HttpListenerException e)
            {
                Logger.Info($"Could not bind {prefix} ({e.Message}); listening on localhost only.");
                _httpListener = new HttpListener();
                _httpListener.Prefixes.Add($"http://localhost:{HttpPort}/");
                _httpListener.Start();
            }

            _httpCts = new CancellationTokenSource();
            Task.Run(() => HttpListenLoop(_httpCts.Token));
            Logger.Info($"GW2.app HTTP listener started on port {HttpPort}");
        }

        private async Task HttpListenLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _httpListener != null && _httpListener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _httpListener.GetContextAsync();
                }
                catch (Exception)
                {
                    break;
                }

                _ = Task.Run(() => HandleHttpRequest(ctx));
            }
        }

        private async Task HandleHttpRequest(HttpListenerContext ctx)
        {
            try
            {
                if (ctx.Request.IsWebSocketRequest)
                {
                    await HandleWebSocket(ctx);
                    return;
                }

                ApplyCorsHeaders(ctx);

                if (ctx.Request.HttpMethod == "OPTIONS")
                {
                    ctx.Response.StatusCode = 204;
                    ctx.Response.Close();
                    return;
                }

                ctx.Response.StatusCode = 426;
                var msg = Encoding.UTF8.GetBytes("This endpoint expects a WebSocket connection.");
                await ctx.Response.OutputStream.WriteAsync(msg, 0, msg.Length);
                ctx.Response.Close();
            }
            catch (Exception e)
            {
                Logger.Warn($"Error handling HTTP request: {e.Message}");
                try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
            }
        }

        // Echoes back the request Origin if it's localhost, gw2.app, or a subdomain of gw2.app.
        // For other origins, no Allow-Origin header is sent and the browser blocks the response.
        // Allow-Private-Network is required because the module listens on a loopback address but
        // pages on https://gw2.app are "public" from the browser's PNA perspective.
        private static void ApplyCorsHeaders(HttpListenerContext ctx)
        {
            var origin = ctx.Request.Headers["Origin"];
            if (IsAllowedOrigin(origin))
            {
                ctx.Response.Headers["Access-Control-Allow-Origin"] = origin;
                ctx.Response.Headers["Vary"] = "Origin";
            }
            ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, OPTIONS";
            ctx.Response.Headers["Access-Control-Allow-Headers"] = "Upgrade, Connection";
            ctx.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
        }

        private static bool IsAllowedOrigin(string origin)
        {
            if (string.IsNullOrEmpty(origin)) return false;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
            var host = uri.Host;
            if (string.IsNullOrEmpty(host)) return false;
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
            if (host.Equals("gw2.app", StringComparison.OrdinalIgnoreCase)) return true;
            if (host.EndsWith(".gw2.app", StringComparison.OrdinalIgnoreCase)) return true;
            if (System.Net.IPAddress.TryParse(host, out var ip) && IsLoopbackOrPrivateIp(ip)) return true;
            return false;
        }

        // Loopback (127/8, ::1) and RFC1918 private ranges (10/8, 172.16/12, 192.168/16),
        // plus IPv6 unique-local (fc00::/7) and link-local (fe80::/10). Public IPs are not
        // accepted as a serving host even if a user happens to point a public DNS name at
        // their LAN — the browser would refuse the private-network access anyway.
        private static bool IsLoopbackOrPrivateIp(System.Net.IPAddress ip)
        {
            if (System.Net.IPAddress.IsLoopback(ip)) return true;

            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                if (b[0] == 10) return true;                                  // 10.0.0.0/8
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;     // 172.16.0.0/12
                if (b[0] == 192 && b[1] == 168) return true;                  // 192.168.0.0/16
                if (b[0] == 169 && b[1] == 254) return true;                  // 169.254.0.0/16 link-local
                return false;
            }
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                if (ip.IsIPv6LinkLocal) return true;
                var b = ip.GetAddressBytes();
                if ((b[0] & 0xfe) == 0xfc) return true;                       // fc00::/7 unique-local
                return false;
            }
            return false;
        }

        private async Task HandleWebSocket(HttpListenerContext ctx)
        {
            HttpListenerWebSocketContext wsCtx;
            try
            {
                wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null);
            }
            catch (Exception e)
            {
                Logger.Warn($"WS accept failed: {e.Message}");
                return;
            }

            var ws = wsCtx.WebSocket;
            var remote = ctx.Request.RemoteEndPoint;
            Logger.Info($"WS client connected from {remote}");

            WebSocket previous;
            CancellationTokenSource previousCts;
            lock (_clientLock)
            {
                previous = _activeClient;
                previousCts = _activeClientCts;
                _activeClient = ws;
                _activeClientCts = new CancellationTokenSource();
            }
            if (previous != null)
            {
                Logger.Info($"Superseding previous WS client.");
                // Tell the dispatcher to flush per-entry state from the previous client
                // before the new client's `state` is applied. This message is processed
                // FIFO so it lands between the old client's last messages and the new
                // client's first `state`. The old client's receive loop also checks
                // _activeClient before each enqueue (below) to drop in-flight messages
                // racing with this transition.
                _incomingMessages.Enqueue(new IncomingMessage { Kind = MessageKind.ClientReplaced });
                _ = SupersedePreviousAsync(previous, previousCts);
            }

            Interlocked.Exchange(ref _hasActiveConnection, 1);
            Interlocked.Exchange(ref _connectionStateDirty, 1);

            var handshakeReceived = new TaskCompletionSource<bool>();
            var localCts = new CancellationTokenSource();
            CancellationTokenSource clientCts;
            lock (_clientLock) { clientCts = _activeClientCts; }

            _ = Task.Run(async () =>
            {
                try
                {
                    var done = await Task.WhenAny(handshakeReceived.Task, Task.Delay(HandshakeTimeoutMs, localCts.Token));
                    if (done != handshakeReceived.Task)
                    {
                        Logger.Info($"Handshake timeout; closing WS from {remote}");
                        await CloseWsAsync(ws, CloseCodeHandshakeTimeout, "handshake timeout");
                    }
                }
                catch { }
            });

            var buffer = new byte[64 * 1024];
            bool stateSeen = false;
            try
            {
                while (ws.State == WebSocketState.Open && !clientCts.IsCancellationRequested)
                {
                    using (var ms = new MemoryStream())
                    {
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), clientCts.Token);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
                                return;
                            }
                            ms.Write(buffer, 0, result.Count);
                        } while (!result.EndOfMessage);

                        if (result.MessageType != WebSocketMessageType.Text)
                        {
                            Logger.Warn($"Unexpected non-text WS frame from {remote}; closing.");
                            await CloseWsAsync(ws, CloseCodeProtocolViolation, "expected text frames");
                            return;
                        }

                        var text = Encoding.UTF8.GetString(ms.ToArray());
                        IncomingMessage parsed;
                        try
                        {
                            parsed = ParseMessage(text);
                            if (!stateSeen && parsed.Kind != MessageKind.State)
                                throw new ProtocolException("first message must be 'state'");
                        }
                        catch (ProtocolException pe)
                        {
                            Logger.Warn($"Protocol violation from {remote}: {pe.Message}");
                            await CloseWsAsync(ws, CloseCodeProtocolViolation, pe.Message);
                            return;
                        }
                        catch (Exception e)
                        {
                            Logger.Warn($"Failed to parse WS message from {remote}: {e.Message}");
                            await CloseWsAsync(ws, CloseCodeProtocolViolation, "bad json");
                            return;
                        }

                        if (parsed.Kind == MessageKind.State)
                        {
                            if (!stateSeen)
                            {
                                stateSeen = true;
                                handshakeReceived.TrySetResult(true);
                            }
                        }

                        // If we've been superseded between ReceiveAsync and now, drop the
                        // message and stop the loop — the new client owns the dispatcher.
                        bool stillActive;
                        lock (_clientLock) { stillActive = _activeClient == ws; }
                        if (!stillActive) return;

                        _incomingMessages.Enqueue(parsed);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                Logger.Info($"WS connection from {remote} ended: {e.Message}");
            }
            finally
            {
                handshakeReceived.TrySetResult(false);
                try { localCts.Cancel(); } catch { }
                localCts.Dispose();

                bool wasActive = false;
                lock (_clientLock)
                {
                    if (_activeClient == ws)
                    {
                        _activeClient = null;
                        try { _activeClientCts?.Dispose(); } catch { }
                        _activeClientCts = null;
                        wasActive = true;
                    }
                }
                if (wasActive)
                {
                    Interlocked.Exchange(ref _hasActiveConnection, 0);
                    Interlocked.Exchange(ref _connectionStateDirty, 1);
                    _incomingMessages.Enqueue(new IncomingMessage { Kind = MessageKind.ConnectionLost });
                    _lastSubscribedIds = new HashSet<string>();
                    _restoredFromPersistence = false;
                }

                try { ws.Dispose(); } catch { }
                Logger.Info($"WS from {remote} closed.");
            }
        }

        // Politely retire a superseded client: send our close frame (with code 4000) and
        // let the previous receive loop continue running so it can read the peer's close
        // reply and exit cleanly. Cancelling the receive loop now would force-close the
        // TCP socket before the close frame is flushed, and the browser would only see
        // 1006 (abnormal closure).
        //
        // Safety net: if the peer never echoes the close frame within 10 s, cancel the
        // receive loop and dispose the socket so we don't leak resources on a dead peer.
        private async Task SupersedePreviousAsync(WebSocket previous, CancellationTokenSource previousCts)
        {
            try
            {
                using (var sendCts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                {
                    await previous.CloseOutputAsync(
                        (WebSocketCloseStatus)CloseCodeSuperseded, "superseded", sendCts.Token);
                }
            }
            catch (Exception e)
            {
                Logger.Info($"Sending close frame to superseded client failed: {e.Message}");
            }

            _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ =>
            {
                try { previousCts?.Cancel(); } catch { }
                try { previousCts?.Dispose(); } catch { }
                try { previous.Dispose(); } catch { }
            });
        }

        private static async Task CloseWsAsync(WebSocket ws, int code, string reason)
        {
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                {
                    await ws.CloseAsync((WebSocketCloseStatus)code, reason ?? "", cts.Token);
                }
            }
            catch { }
            try { ws.Dispose(); } catch { }
        }

        private async Task SendSubscribeAsync(List<string> listIds)
        {
            WebSocket ws;
            lock (_clientLock) { ws = _activeClient; }
            if (ws == null || ws.State != WebSocketState.Open) return;

            var payload = new SubscribeMessage { Type = "subscribe", ListIds = listIds ?? new List<string>() };
            var json = JsonConvert.SerializeObject(payload);
            var bytes = Encoding.UTF8.GetBytes(json);
            try
            {
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                Logger.Info($"Sent subscribe with {listIds?.Count ?? 0} list ids.");
            }
            catch (Exception e)
            {
                Logger.Warn($"Failed to send subscribe: {e.Message}");
            }
        }

        private async Task SendSetEntryCompletedAsync(string listId, int index, bool completed)
        {
            WebSocket ws;
            lock (_clientLock) { ws = _activeClient; }
            if (ws == null || ws.State != WebSocketState.Open) return;

            var payload = new SetEntryCompletedMessage
            {
                Type = "set_entry_completed",
                ListId = listId,
                Index = index,
                Completed = completed,
            };
            var json = JsonConvert.SerializeObject(payload);
            var bytes = Encoding.UTF8.GetBytes(json);
            try
            {
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                Logger.Info($"Sent set_entry_completed listId={listId} index={index} completed={completed}");
            }
            catch (Exception e)
            {
                Logger.Warn($"Failed to send set_entry_completed: {e.Message}");
            }
        }

        private static IncomingMessage ParseMessage(string text)
        {
            JObject root;
            try { root = JObject.Parse(text); }
            catch (Exception e) { throw new ProtocolException("invalid json: " + e.Message); }

            var typeTok = root["type"];
            if (typeTok == null) throw new ProtocolException("missing 'type' field");
            var type = typeTok.Value<string>();

            switch (type)
            {
                case "state":
                {
                    var protoTok = root["protocol"];
                    if (protoTok == null) throw new ProtocolException("state missing 'protocol' field");
                    int proto = protoTok.Value<int>();
                    if (proto != ProtocolVersion) throw new ProtocolException($"unsupported protocol version {proto}");
                    var state = root.ToObject<StateMessage>() ?? new StateMessage();
                    return new IncomingMessage { Kind = MessageKind.State, State = state };
                }
                case "entry":
                {
                    var entry = root.ToObject<EntryMessage>();
                    if (entry == null || string.IsNullOrEmpty(entry.ListId))
                        throw new ProtocolException("entry missing listId");
                    return new IncomingMessage { Kind = MessageKind.Entry, Entry = entry };
                }
                case "synced":
                {
                    var ids = root["listIds"]?.ToObject<List<string>>() ?? new List<string>();
                    return new IncomingMessage { Kind = MessageKind.Synced, SyncedListIds = ids };
                }
                default:
                    throw new ProtocolException($"unknown message type '{type}'");
            }
        }
    }
}
