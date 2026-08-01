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
            // Capture this listener/token so the loop only ever touches its own
            // instance; RestartHttpListener can stop this one without the old
            // loop racing onto the replacement.
            var listener = _httpListener;
            var token = _httpCts.Token;
            Task.Run(() => HttpListenLoop(listener, token));
            Logger.Info($"GW2.app HTTP listener started on port {HttpPort}");
        }

        private async Task HttpListenLoop(HttpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync();
                }
                catch (Exception e)
                {
                    // Stop only when this listener is actually gone (shutdown, or
                    // a RestartHttpListener that replaced it). A one-off failure
                    // to accept, which Wine's HttpApi does raise, used to end the
                    // loop and leave the module running with nothing serving HTTP
                    // until the next game restart.
                    if (ct.IsCancellationRequested || !listener.IsListening)
                    {
                        break;
                    }
                    Logger.Warn(e, "Failed to accept an HTTP request; still listening.");
                    await Task.Delay(250);
                    continue;
                }

                _ = Task.Run(() => HandleHttpRequest(ctx));
            }
        }

        // Cooldown so a transport that keeps re-wedging can't thrash the listener.
        private static readonly TimeSpan ListenerRestartCooldown = TimeSpan.FromSeconds(5);
        private DateTime _lastListenerRestartUtc = DateTime.MinValue;

        // Wine's HttpListener can wedge its underlying request queue under load
        // (large hover-image poll bodies are the likeliest trigger) with NO
        // exception surfacing to managed code: requests just stop completing and
        // the poll session times out. The listener still reports IsListening, so
        // nothing detects it and it never recovers on its own. Recreating the
        // listener clears the native state, which is exactly what a manual module
        // restart does. Guarded by a cooldown and by _unloading.
        private void RestartHttpListener()
        {
            if (_unloading) { return; }
            var now = DateTime.UtcNow;
            if (now - _lastListenerRestartUtc < ListenerRestartCooldown) { return; }
            _lastListenerRestartUtc = now;

            Logger.Info("Recreating the HTTP listener to recover a wedged connection.");
            try { _httpCts?.Cancel(); } catch { }
            try { _httpListener?.Stop(); } catch { }
            try { _httpListener?.Close(); } catch { }
            _httpListener = null;
            try { _httpCts?.Dispose(); } catch { }
            _httpCts = null;
            try
            {
                StartHttpServer();
            }
            catch (Exception e)
            {
                Logger.Warn(e, "Failed to recreate the HTTP listener.");
            }
        }

        // Per Update tick: if the listener has died outright (its loop broke, or
        // a recreate failed) revive it. The wedge itself keeps IsListening true,
        // so the poll-timeout reap is what catches that case; this only backstops
        // an actually-dead listener so HTTP is never left permanently silent.
        private void EnsureHttpListenerAlive()
        {
            if (_unloading) { return; }
            if (_httpListener == null || !_httpListener.IsListening)
            {
                RestartHttpListener();
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

        // Finish a response, with the body length declared up front. Without a
        // length the listener uses chunked transfer encoding, which Wine's
        // HttpApi cannot complete: disposing the response stream aborts the
        // request instead (SEHException out of HttpCancelHttpRequest), so the
        // browser gets no bytes at all and reports an empty response. Closing
        // can still throw once the request is gone, which is not actionable,
        // hence the swallow.
        //
        // Each response also ends its connection. Wine's listener stops
        // serving a connection after it has answered a few requests on it,
        // and the browser then gets empty responses for everything it sends
        // on that socket until it gives up on it. One connection per request
        // is a negligible cost over loopback and takes the reuse, and the
        // whole failure mode with it, off the table.
        private static void CloseResponse(HttpListenerContext ctx, int status, string contentType = null, byte[] body = null)
        {
            try
            {
                ctx.Response.StatusCode = status;
                ctx.Response.KeepAlive = false;
                if (contentType != null) { ctx.Response.ContentType = contentType; }
                ctx.Response.ContentLength64 = body?.Length ?? 0;
                if (body != null && body.Length > 0)
                {
                    ctx.Response.OutputStream.Write(body, 0, body.Length);
                }
                ctx.Response.Close();
                Logger.Debug($"HTTP response {status} sent ({body?.Length ?? 0} bytes).");
            }
            catch (Exception e)
            {
                Logger.Debug(e, $"Writing HTTP response {status} ({body?.Length ?? 0} bytes) failed.");
                try { ctx.Response.Abort(); } catch { }
            }
        }

        private async Task HandleHttpRequest(HttpListenerContext ctx)
        {
            // Logged before any branching so a request that dies in the
            // listener below managed code (Wine aborting a large or slow poll
            // body) is still visible as an accepted request with its declared
            // size, distinguishing it from one that reached a handler.
            Logger.Debug($"HTTP {ctx.Request.HttpMethod} {ctx.Request.Url?.AbsolutePath} " +
                $"len={ctx.Request.ContentLength64} from {ctx.Request.RemoteEndPoint}");
            try
            {
                if (IsWebSocketRequestSafe(ctx))
                {
                    // CORS does not gate WebSockets, so check Origin on the handshake.
                    var wsOrigin = ctx.Request.Headers["Origin"];
                    if (!IsAllowedOrigin(wsOrigin))
                    {
                        Logger.Warn($"Rejecting WS handshake from disallowed origin '{wsOrigin}' ({ctx.Request.RemoteEndPoint})");
                        CloseResponse(ctx, 403);
                        return;
                    }
                    await HandleWebSocket(ctx);
                    return;
                }

                ApplyCorsHeaders(ctx);

                if (ctx.Request.HttpMethod == "OPTIONS")
                {
                    CloseResponse(ctx, 204);
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
                        CloseResponse(ctx, 403);
                        return;
                    }
                    await HandlePoll(ctx);
                    return;
                }

                // Also where a WebSocket upgrade lands on a platform whose
                // listener cannot detect (so cannot serve) one: the browser
                // gets a prompt failure and the client falls back to polling.
                CloseResponse(ctx, 426, "text/plain; charset=utf-8",
                    Encoding.UTF8.GetBytes("This endpoint expects a WebSocket connection."));
            }
            catch (Exception e)
            {
                Logger.Warn(e, "Error handling HTTP request.");
                CloseResponse(ctx, 500);
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
            // Without this the browser's default (5s) makes it re-run the
            // preflight throughout a polling session.
            ctx.Response.Headers["Access-Control-Max-Age"] = "86400";
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
                    // Accept any client protocol from 1 to what we implement. The
                    // client sends 1 for backward compatibility with older modules
                    // (which reject anything but 1) and opts into newer features
                    // off our advertised serverProtocol, not off what it sends.
                    if (proto < 1 || proto > ProtocolVersion) throw new ProtocolException($"unsupported protocol version {proto}");
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
                CloseResponse(ctx, 400);
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
                CloseResponse(ctx, 400);
                return;
            }

            if (string.IsNullOrEmpty(session))
            {
                CloseResponse(ctx, 400);
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
                WritePollResponse(ctx, null, null);
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
                WritePollResponse(ctx, null, MakeClose(CloseCodeSuperseded, "superseded"));
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
                // Reassemble a fragmented message. The client splits a message
                // too big for the listener's per-body limit (Wine's http.sys
                // drops an oversized body) into ordered slices; each arrives as
                // {"__frag":{id,seq,final},"data":"<slice>"}. AcceptFragment
                // returns the full message JSON once the final slice lands, or
                // null while more are pending.
                string messageJson;
                if (tok is JObject fobj && fobj["__frag"] != null)
                {
                    var reassembled = channel.AcceptFragment(fobj);
                    if (reassembled == null) { continue; }
                    messageJson = reassembled;
                }
                else
                {
                    messageJson = tok.ToString(Formatting.None);
                }

                IncomingMessage parsed;
                try { parsed = ParseMessage(messageJson); }
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
            WritePollResponse(ctx, outMsgs, superseded ? MakeClose(CloseCodeSuperseded, "superseded") : null, resync);
        }

        private static JObject MakeClose(int code, string reason)
        {
            return new JObject { ["code"] = code, ["reason"] = reason };
        }

        private static void WritePollResponse(HttpListenerContext ctx, List<string> messages, JObject close, bool resync = false)
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
            // Advertise the version we implement so the client can opt into
            // post-v1 features (e.g. fragmentation). Absent to older clients that
            // ignore it, and absent from older modules so the client stays on v1.
            root["serverProtocol"] = ProtocolVersion;
            if (resync) { root["resync"] = true; }
            var bytes = Encoding.UTF8.GetBytes(root.ToString(Formatting.None));
            CloseResponse(ctx, 200, "application/json", bytes);
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
            if (reaped)
            {
                MarkPollDisconnected();
                // A live poll client polls ~2/s, so a timeout means delivery
                // wedged or the client vanished. Recreate the listener either
                // way: it clears a wedge (what a manual module restart does) and
                // is harmless when the client is simply gone.
                RestartHttpListener();
            }
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

            // Reassembly state for the one fragmented message that can be in
            // flight at a time (the poll lane is serial, so fragments arrive in
            // order). A new id, or any out-of-order seq, discards the partial.
            private string _fragId;
            private int _fragNextSeq;
            private StringBuilder _fragData;

            public PollChannel(string sessionId)
            {
                SessionId = sessionId;
                LastPollUtc = DateTime.UtcNow;
            }

            public bool Superseded => _superseded;
            public void MarkSuperseded() { _superseded = true; }
            public void Enqueue(string json) { if (!_superseded) { _outbound.Enqueue(json); } }

            // Feed one {"__frag":{id,seq,final},"data":...} slice. Returns the
            // reassembled message JSON once the final slice completes an in-order
            // set, else null. A superseded frame (new id mid-stream) or a gap
            // just drops the partial and waits for a fresh id at seq 0.
            public string AcceptFragment(JObject msg)
            {
                var f = msg["__frag"] as JObject;
                if (f == null) { return null; }
                var id = f["id"]?.Value<string>();
                if (string.IsNullOrEmpty(id)) { return null; }
                int seq = f["seq"]?.Value<int>() ?? -1;
                bool final = f["final"]?.Value<bool>() ?? false;
                var data = msg["data"]?.Value<string>() ?? "";

                if (id != _fragId)
                {
                    if (seq != 0) { _fragId = null; return null; }
                    _fragId = id;
                    _fragNextSeq = 0;
                    _fragData = new StringBuilder();
                }
                if (seq != _fragNextSeq)
                {
                    _fragId = null;
                    _fragData = null;
                    return null;
                }
                _fragData.Append(data);
                _fragNextSeq++;
                if (!final) { return null; }

                var result = _fragData.ToString();
                _fragId = null;
                _fragData = null;
                return result;
            }

            public List<string> DrainOutbound()
            {
                var list = new List<string>();
                while (_outbound.TryDequeue(out var s)) { list.Add(s); }
                return list;
            }
        }
    }
}
