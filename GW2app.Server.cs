using System;
using System.Collections.Concurrent;
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
                Logger.Info(e, $"Could not bind {prefix}; listening on localhost only.");
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

        // On some Wine/Linux setups reading HttpListenerRequest.IsWebSocketRequest throws
        // TypeInitializationException (native WebSocketProtocolComponent is "Not implemented").
        // A platform that can't detect a WS upgrade can't serve one either, so treat a throw
        // as "not a WebSocket request" and fall through to the HTTP polling path. Probed once.
        private static int _wsDetectionState; // 0 = unknown, 1 = works, 2 = unavailable

        private static bool IsWebSocketRequestSafe(HttpListenerContext ctx)
        {
            if (Volatile.Read(ref _wsDetectionState) == 2) return false;
            try
            {
                bool isWs = ctx.Request.IsWebSocketRequest;
                Volatile.Write(ref _wsDetectionState, 1);
                return isWs;
            }
            catch (Exception e)
            {
                if (Interlocked.Exchange(ref _wsDetectionState, 2) != 2)
                    Logger.Info(e, "WebSocket detection unavailable on this platform; serving HTTP polling only.");
                return false;
            }
        }

        private async Task HandleHttpRequest(HttpListenerContext ctx)
        {
            try
            {
                if (IsWebSocketRequestSafe(ctx))
                {
                    // CORS does not gate WebSockets, so check Origin on the handshake.
                    var wsOrigin = ctx.Request.Headers["Origin"];
                    if (!IsAllowedOrigin(wsOrigin))
                    {
                        Logger.Warn($"Rejecting WS handshake from disallowed origin '{wsOrigin}' ({ctx.Request.RemoteEndPoint})");
                        ctx.Response.StatusCode = 403;
                        ctx.Response.Close();
                        return;
                    }
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

                if (ctx.Request.HttpMethod == "POST" && ctx.Request.Url.AbsolutePath == "/poll")
                {
                    // CORS only hides the response; this handler has side effects,
                    // so gate on Origin server-side like the WS handshake.
                    var pollOrigin = ctx.Request.Headers["Origin"];
                    if (!IsAllowedOrigin(pollOrigin))
                    {
                        Logger.Warn($"Rejecting poll from disallowed origin '{pollOrigin}' ({ctx.Request.RemoteEndPoint})");
                        ctx.Response.StatusCode = 403;
                        ctx.Response.Close();
                        return;
                    }
                    await HandlePoll(ctx);
                    return;
                }

                ctx.Response.StatusCode = 426;
                var msg = Encoding.UTF8.GetBytes("This endpoint expects a WebSocket connection.");
                await ctx.Response.OutputStream.WriteAsync(msg, 0, msg.Length);
                ctx.Response.Close();
            }
            catch (Exception e)
            {
                Logger.Warn(e, "Error handling HTTP request.");
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
            ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            ctx.Response.Headers["Access-Control-Allow-Headers"] = "Upgrade, Connection, Content-Type";
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
        // their LAN; the browser would refuse the private-network access anyway.
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
                Logger.Warn(e, "WS accept failed.");
                return;
            }

            var ws = wsCtx.WebSocket;
            var remote = ctx.Request.RemoteEndPoint;
            Logger.Info($"WS client connected from {remote}");

            WebSocket previous;
            CancellationTokenSource previousCts;
            PollChannel previousPoll;
            lock (_clientLock)
            {
                previous = _activeClient;
                previousCts = _activeClientCts;
                previousPoll = _activePollSession;
                _activeClient = ws;
                _activeClientCts = new CancellationTokenSource();
                _activePollSession = null;
                if (previousPoll != null) { _lastSupersededPollId = previousPoll.SessionId; }
            }
            previousPoll?.MarkSuperseded();
            if (previous != null || previousPoll != null)
            {
                Logger.Info($"Superseding previous {(previous != null ? "WS" : "poll")} client.");
                // Tell the dispatcher to flush per-entry state from the previous client
                // before the new client's `state` is applied. This message is processed
                // FIFO so it lands between the old client's last messages and the new
                // client's first `state`. The old client's receive loop also checks
                // _activeClient before each enqueue (below) to drop in-flight messages
                // racing with this transition.
                _incomingMessages.Enqueue(new IncomingMessage { Kind = MessageKind.ClientReplaced });
                if (previous != null)
                {
                    _ = SupersedePreviousAsync(previous, previousCts);
                }
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
                            Logger.Warn(pe, $"Protocol violation from {remote}.");
                            await CloseWsAsync(ws, CloseCodeProtocolViolation, pe.Message);
                            return;
                        }
                        catch (Exception e)
                        {
                            Logger.Warn(e, $"Failed to parse WS message from {remote}.");
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
                        // message and stop the loop; the new client owns the dispatcher.
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
                Logger.Info(e, $"WS connection from {remote} ended.");
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

        // Retire a superseded client: send our close frame (with code 4000) and let
        // the previous receive loop continue running so it can read the peer's close
        // reply and exit cleanly. Cancelling the receive loop now would force-close the
        // TCP socket before the close frame is flushed, and the browser would only see
        // 1006 (abnormal closure).
        //
        // If the peer never echoes the close frame within 10 s, cancel the receive
        // loop and dispose the socket so we don't leak resources on a dead peer.
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
                Logger.Info(e, "Sending close frame to superseded client failed.");
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

        // Send to the active client whichever transport it uses: queue onto the
        // poll session (drained by its next poll) or write to the WebSocket.
        private async Task<bool> SendToClientAsync(string json)
        {
            WebSocket ws;
            PollChannel poll;
            lock (_clientLock) { ws = _activeClient; poll = _activePollSession; }
            if (poll != null && !poll.Superseded)
            {
                poll.Enqueue(json);
                return true;
            }
            if (ws != null && ws.State == WebSocketState.Open)
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                try
                {
                    await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                    return true;
                }
                catch (Exception e)
                {
                    Logger.Warn(e, "Failed to send to client.");
                }
            }
            return false;
        }

        private async Task SendSubscribeAsync(List<string> listIds)
        {
            var payload = new SubscribeMessage { Type = "subscribe", ListIds = listIds ?? new List<string>() };
            if (await SendToClientAsync(JsonConvert.SerializeObject(payload)))
                Logger.Info($"Sent subscribe with {listIds?.Count ?? 0} list ids.");
        }

        private async Task SendOpenHoverAsync(string listId, int index)
        {
            var payload = new OpenHoverMessage { Type = "open_hover", ListId = listId, Index = index };
            await SendToClientAsync(JsonConvert.SerializeObject(payload));
        }

        private async Task SendCloseHoverAsync()
        {
            var payload = new CloseHoverMessage { Type = "close_hover" };
            await SendToClientAsync(JsonConvert.SerializeObject(payload));
        }

        private async Task SendSetEntryCompletedAsync(string listId, int index, bool completed)
        {
            var payload = new SetEntryCompletedMessage
            {
                Type = "set_entry_completed",
                ListId = listId,
                Index = index,
                Completed = completed,
            };
            if (await SendToClientAsync(JsonConvert.SerializeObject(payload)))
                Logger.Info($"Sent set_entry_completed listId={listId} index={index} completed={completed}");
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
                case "hover_image":
                {
                    var hi = root.ToObject<HoverImageMessage>();
                    if (hi == null || string.IsNullOrEmpty(hi.ListId))
                        throw new ProtocolException("hover_image missing listId");
                    return new IncomingMessage { Kind = MessageKind.HoverImage, HoverImage = hi };
                }
                default:
                    throw new ProtocolException($"unknown message type '{type}'");
            }
        }

        // HTTP polling fallback (POST /poll): inbound messages in the request
        // body, queued outbound in the response, both reusing the WS path's
        // ParseMessage/dispatcher/session queue. The first poll registers (and
        // supersedes any other client); a `close` field or a timeout ends it.
        private async Task HandlePoll(HttpListenerContext ctx)
        {
            string body;
            try
            {
                using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8))
                    body = await reader.ReadToEndAsync();
            }
            catch (Exception e)
            {
                Logger.Warn(e, "Failed to read poll body.");
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                return;
            }

            string session;
            JArray inbound;
            JToken closeTok;
            try
            {
                var root = JObject.Parse(body);
                session = root["session"]?.Value<string>();
                inbound = root["messages"] as JArray ?? new JArray();
                closeTok = root["close"];
            }
            catch (Exception e)
            {
                Logger.Warn(e, "Bad poll request JSON.");
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                return;
            }

            if (string.IsNullOrEmpty(session))
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                return;
            }

            // Client closing this session (page unload / manual disconnect).
            if (closeTok != null && closeTok.Type != JTokenType.Null)
            {
                bool cleared = false;
                lock (_clientLock)
                {
                    if (_activePollSession != null && _activePollSession.SessionId == session)
                    {
                        _activePollSession.MarkSuperseded();
                        _activePollSession = null;
                        _lastSupersededPollId = session;
                        cleared = true;
                    }
                }
                if (cleared) { MarkPollDisconnected(); }
                await WritePollResponse(ctx, null, null);
                return;
            }

            PollChannel channel = null;
            bool returnSuperseded = false;
            bool replacedPrevious = false;
            bool resync = false;
            WebSocket supersededWs = null;
            CancellationTokenSource supersededWsCts = null;
            lock (_clientLock)
            {
                if (_activePollSession != null && _activePollSession.SessionId == session)
                {
                    channel = _activePollSession;
                    channel.LastPollUtc = DateTime.UtcNow;
                }
                else if (session == _lastSupersededPollId)
                {
                    // Stray poll from a session another client already took over.
                    returnSuperseded = true;
                }
                else
                {
                    // New poll client: take the connection from whatever was active.
                    // resync tells a returning session (one we reaped, or after a
                    // module restart) to resend full state so we can rebuild.
                    replacedPrevious = _activeClient != null || _activePollSession != null;
                    resync = true;
                    supersededWs = _activeClient;
                    supersededWsCts = _activeClientCts;
                    _activeClient = null;
                    _activeClientCts = null;
                    if (_activePollSession != null)
                    {
                        _activePollSession.MarkSuperseded();
                        _lastSupersededPollId = _activePollSession.SessionId;
                    }
                    channel = new PollChannel(session);
                    _activePollSession = channel;
                    Interlocked.Exchange(ref _hasActiveConnection, 1);
                    Interlocked.Exchange(ref _connectionStateDirty, 1);
                }
            }

            if (returnSuperseded)
            {
                await WritePollResponse(ctx, null, MakeClose(CloseCodeSuperseded, "superseded"));
                return;
            }
            if (replacedPrevious)
            {
                _incomingMessages.Enqueue(new IncomingMessage { Kind = MessageKind.ClientReplaced });
            }
            if (supersededWs != null)
            {
                _ = SupersedePreviousAsync(supersededWs, supersededWsCts);
            }

            // Ingest inbound website->module messages (same parsing as the WS loop).
            bool superseded = false;
            foreach (var tok in inbound)
            {
                IncomingMessage parsed;
                try { parsed = ParseMessage(tok.ToString(Formatting.None)); }
                catch (Exception e) { Logger.Warn(e, "Skipping bad poll message."); continue; }

                // Ignore anything before this session's first `state` (like the
                // WS invariant, but lenient): a resynced client's stale pre-state
                // messages belong to the old catalogue, so drop rather than close.
                if (!channel.StateSeen)
                {
                    if (parsed.Kind != MessageKind.State) { continue; }
                    channel.StateSeen = true;
                }

                lock (_clientLock) { superseded = !ReferenceEquals(_activePollSession, channel); }
                if (superseded) { break; }
                _incomingMessages.Enqueue(parsed);
            }

            var outMsgs = channel.DrainOutbound();
            await WritePollResponse(ctx, outMsgs, superseded ? MakeClose(CloseCodeSuperseded, "superseded") : null, resync);
        }

        private static JObject MakeClose(int code, string reason)
        {
            return new JObject { ["code"] = code, ["reason"] = reason };
        }

        private static async Task WritePollResponse(HttpListenerContext ctx, List<string> messages, JObject close, bool resync = false)
        {
            var arr = new JArray();
            if (messages != null)
            {
                foreach (var s in messages)
                {
                    try { arr.Add(JToken.Parse(s)); } catch { }
                }
            }
            var root = new JObject { ["messages"] = arr, ["close"] = close ?? (JToken)JValue.CreateNull() };
            if (resync) { root["resync"] = true; }
            var bytes = Encoding.UTF8.GetBytes(root.ToString(Formatting.None));
            try
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            }
            catch (Exception e)
            {
                Logger.Warn(e, "Failed to write poll response.");
            }
            finally
            {
                try { ctx.Response.Close(); } catch { }
            }
        }

        // Poll session ended (close beacon or timeout); mirrors the WS finally.
        private void MarkPollDisconnected()
        {
            Interlocked.Exchange(ref _hasActiveConnection, 0);
            Interlocked.Exchange(ref _connectionStateDirty, 1);
            _incomingMessages.Enqueue(new IncomingMessage { Kind = MessageKind.ConnectionLost });
            _lastSubscribedIds = new HashSet<string>();
            _restoredFromPersistence = false;
        }

        // Per Update tick: a poll client silent past PollSessionTimeout (tab
        // closed without a beacon, or throttled) counts as disconnected.
        private void ReapStalePollSession()
        {
            bool reaped = false;
            lock (_clientLock)
            {
                if (_activePollSession != null &&
                    (DateTime.UtcNow - _activePollSession.LastPollUtc) > PollSessionTimeout)
                {
                    Logger.Info("Poll session timed out; treating as disconnected.");
                    _activePollSession.MarkSuperseded();
                    _activePollSession = null;
                    reaped = true;
                }
            }
            if (reaped) { MarkPollDisconnected(); }
        }

        // A website connected over the polling fallback: its outbound queue
        // (drained by the next poll) plus the last poll time for liveness.
        private sealed class PollChannel
        {
            public string SessionId { get; }
            public DateTime LastPollUtc;
            // Set once this session has sent its first `state` (see HandlePoll).
            public bool StateSeen;
            private volatile bool _superseded;
            private readonly ConcurrentQueue<string> _outbound = new ConcurrentQueue<string>();

            public PollChannel(string sessionId)
            {
                SessionId = sessionId;
                LastPollUtc = DateTime.UtcNow;
            }

            public bool Superseded => _superseded;
            public void MarkSuperseded() { _superseded = true; }
            public void Enqueue(string json) { if (!_superseded) { _outbound.Enqueue(json); } }

            public List<string> DrainOutbound()
            {
                var list = new List<string>();
                while (_outbound.TryDequeue(out var s)) { list.Add(s); }
                return list;
            }
        }
    }
}
