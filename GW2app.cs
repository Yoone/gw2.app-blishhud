﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Blish_HUD;
using Blish_HUD.Content;
using Blish_HUD.Controls;
using Blish_HUD.Input;
using Blish_HUD.Modules;
using Blish_HUD.Modules.Managers;
using Blish_HUD.Settings;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace GW2app
{
    [Export(typeof(Module))]
    public partial class GW2app : Module
    {
        private static readonly Logger Logger = Logger.GetLogger<GW2app>();

        private const int HttpPort = 38473;
        // Base (100%) entry-image width. Effective DisplayWidth scales with UiScale.
        private const int BaseDisplayWidth = 400;
        private int DisplayWidth => (int)Math.Round(BaseDisplayWidth * UiScale);
        private const int ProtocolVersion = 1;
        private const int HandshakeTimeoutMs = 5000;

        private const int CloseCodeSuperseded = 4000;
        private const int CloseCodeHandshakeTimeout = 4001;
        private const int CloseCodeProtocolViolation = 4002;
        // A poll client silent this long counts as gone (~10 polls at ~2/s).
        private static readonly TimeSpan PollSessionTimeout = TimeSpan.FromSeconds(5);

        internal SettingsManager SettingsManager => this.ModuleParameters.SettingsManager;
        internal ContentsManager ContentsManager => this.ModuleParameters.ContentsManager;
        internal DirectoriesManager DirectoriesManager => this.ModuleParameters.DirectoriesManager;
        internal Gw2ApiManager Gw2ApiManager => this.ModuleParameters.Gw2ApiManager;

        [ImportingConstructor]
        public GW2app([Import("ModuleParameters")] ModuleParameters moduleParameters) : base(moduleParameters)
        {
            GW2appInstance = this;
        }

        protected override void DefineSettings(SettingCollection settings)
        {
            // Sub-collections render as labeled panels in Blish's auto-generated settings
            // view. We use them to group related options visually.
            var appearance = settings.AddSubCollection("appearance", true, () => "Appearance");
            _windowTheme = appearance.DefineSetting(
                "windowTheme",
                GW2appWindow.WindowTheme.Game,
                () => "Window theme",
                () => "Changes the background of windows");
            _showAccountName = appearance.DefineSetting(
                "showAccountName",
                true,
                () => "Show GW2 account name in list header",
                () => "Hides the account-name subtitle on the list windows when off.");
            _showCopyWaypointsButton = appearance.DefineSetting(
                "showCopyWaypointsButton",
                true,
                () => "Show \"Copy waypoints\" button in lists",
                () => "Hides the bottom-of-list button used to copy chat-link waypoints.");
            // Integer percentage so the slider snaps to whole percent values.
            _bgOpacityPct = appearance.DefineSetting(
                "bgOpacityPct",
                85,
                () => "Window background opacity",
                () => "How opaque the window background is. Lower is more see-through.");
            _bgOpacityPct.SetRange(75, 100);

            // Integer percentage so the slider snaps to whole percent values. Setting key
            // bumped from "uiScale" because the type changed (float -> int) and old
            // persisted values would not deserialize.
            var sizing = settings.AddSubCollection("sizing", true, () => "Sizing");
            _uiScalePct = sizing.DefineSetting(
                "uiScalePct",
                100,
                () => "List scale",
                () => "Scales list window dimensions and entry images");
            _uiScalePct.SetRange(75, 125);

            var controls = settings.AddSubCollection("controls", true, () => "Controls");
            // Defaults to unbound (Keys.None) to avoid colliding with GW2/Blish keybinds;
            // enabled and hooked in Initialize.
            _toggleListsKeybind = controls.DefineSetting(
                "toggleListsVisibility",
                new KeyBinding(Keys.None),
                () => "Show/hide all lists",
                () => "Hides or restores every open list window. Purely visual: hidden lists stay connected and reappear instantly.");

            var internalSettings = settings.AddSubCollection("internal");
            _persistedOpenListsJson = internalSettings.DefineSetting("openLists", "[]");
            // Hidden setting: max waypoints per chat-link copy chunk. 1-15. At 15
            // (MaxWaypointsPerMessage) the chunker switches to character-count mode
            // (<= GW2ChatMaxLength = 199 per chunk). Mirrors the website.
            _maxWaypointsPerCopy = internalSettings.DefineSetting("maxWaypointsPerCopy", MaxWaypointsPerMessage);
        }

        // GW2 chat input character limit and "max" position on the slider.
        internal const int Gw2ChatMaxLength = 199;
        internal const int MaxWaypointsPerMessage = 15;

        // Custom view that wraps Blish's auto-generated SettingsView and appends a
        // "Reset scale to 100%" button after the slider. The slider is defined last in
        // DefineSettings so the button sits directly below it.
        public override Blish_HUD.Graphics.UI.IView GetSettingsView()
        {
            return new GW2appSettingsView(
                _windowTheme,
                _bgOpacityPct,
                _showAccountName,
                _showCopyWaypointsButton,
                _uiScalePct,
                _toggleListsKeybind,
                onResetScale: () => { if (_uiScalePct != null) _uiScalePct.Value = 100; });
        }

        private float UiScale => (_uiScalePct?.Value ?? 100) / 100f;
        private float BgOpacity => (_bgOpacityPct?.Value ?? 85) / 100f;

        // True when a website client is currently connected and the socket is open.
        // Used to gate hover-card opens: with no live client we can't actually
        // receive `hover_image` frames, so showing the spinner just glues a stuck
        // loader to the cursor.
        private bool IsClientConnected
        {
            get
            {
                WebSocket ws;
                PollChannel poll;
                lock (_clientLock) { ws = _activeClient; poll = _activePollSession; }
                if (ws != null && ws.State == WebSocketState.Open) { return true; }
                return poll != null && !poll.Superseded;
            }
        }

        // Display-pixels per captured-pixel ratio. Entry images apply
        // `DisplayWidth / tex.Width` per ScaledSize; hover-card images use the
        // same factor so they render at the same effective scale. All entry
        // captures share the same source CSS width (26rem), so any cached
        // entry texture is a valid reference. Falls back to UiScale when no
        // entry image is cached (e.g. nothing has rendered yet).
        private float CaptureScale
        {
            get
            {
                foreach (var tex in _entryImages.Values)
                {
                    if (tex != null && tex.Width > 0)
                        return (float)DisplayWidth / tex.Width;
                }
                return UiScale;
            }
        }
        private bool ShowAccountName => _showAccountName?.Value ?? true;
        private bool ShowCopyWaypointsButton => _showCopyWaypointsButton?.Value ?? true;

        // The corner icon is created here (main thread) rather than in LoadAsync
        // (background thread). A CornerIcon registers itself with the overlay's
        // top-bar icon strip on construction, and doing that off-thread leaves it
        // missing from the bar on a fresh install until Blish restarts.
        protected override void Initialize()
        {
            _iconTexture = ContentsManager.GetTexture("gw2app-icon.png");
            CreateCornerIcon();
            RebuildContextMenu();

            // KeyBindings default to disabled; enable it. Activated fires on the update
            // thread, so the handler can touch UI directly.
            if (_toggleListsKeybind?.Value != null)
            {
                _toggleListsKeybind.Value.Enabled = true;
                _toggleListsKeybind.Value.Activated += OnToggleListsKeybind;
            }
        }

        protected override async Task LoadAsync()
        {
            _rechargeTexture = ContentsManager.GetTexture("recharge.png");
            _cornerSourceTexture = ContentsManager.GetTexture("gw2app-corner.png");
            _logoTexture = ContentsManager.GetTexture("gw2app-logo.png");
            _dotConnectedTexture = ContentsManager.GetTexture("connected.png");
            _dotNotConnectedTexture = ContentsManager.GetTexture("not-connected.png");
            // Corner-icon variant shown while lists are hidden. Guard against the error
            // texture GetTexture returns for a missing file, so a bad asset just keeps the
            // normal icon instead of a pink placeholder.
            var hiddenIcon = ContentsManager.GetTexture("gw2app-icon-lists-hidden.png");
            if (hiddenIcon != null && hiddenIcon != ContentService.Textures.Error)
                _iconHiddenTexture = hiddenIcon;
            // Prewarm the GW2 window background so it's ready when the user picks
            // "Game texture" in the settings dropdown.
            AsyncTexture2D.FromAssetId(155997);

            if (_windowTheme != null)
                _windowTheme.SettingChanged += OnWindowThemeChanged;
            if (_bgOpacityPct != null)
                _bgOpacityPct.SettingChanged += OnBgOpacityChanged;
            if (_uiScalePct != null)
                _uiScalePct.SettingChanged += OnUiScaleChanged;
            if (_showAccountName != null)
                _showAccountName.SettingChanged += OnShowAccountNameChanged;
            if (_showCopyWaypointsButton != null)
                _showCopyWaypointsButton.SettingChanged += OnShowCopyWaypointsButtonChanged;

            StartHttpServer();

            HoverCard.Init(
                openCallback:        (lid, idx) => { _ = SendOpenHoverAsync(lid, idx); },
                closeCallback:       ()         => { _ = SendCloseHoverAsync(); },
                uiScaleGetter:       () => UiScale,
                captureScaleGetter:  () => CaptureScale,
                cachedTextureGetter: (lid, idx) => _hoverImageCache.TryGetValue(EntryKey(lid, idx), out var t) ? t : null,
                isConnectedGetter:   () => IsClientConnected);

            await Task.CompletedTask;
        }

        private void OnBgOpacityChanged(object sender, ValueChangedEventArgs<int> e)
        {
            float opacity = e.NewValue / 100f;
            foreach (var entry in _listWindows.Values)
                entry.Window?.SetBackgroundOpacity(opacity);
            _infoWindow?.SetBackgroundOpacity(opacity);
        }

        private void OnWindowThemeChanged(object sender, ValueChangedEventArgs<GW2appWindow.WindowTheme> e)
        {
            foreach (var entry in _listWindows.Values)
                entry.Window?.SetWindowTheme(e.NewValue);
            _infoWindow?.SetWindowTheme(e.NewValue);

            // Action buttons (Hide completed, Copy waypoints, Back) swap between
            // StandardButton and Label-as-button per theme; re-render so they
            // pick up the new style.
            foreach (var id in _listWindows.Keys.ToList())
                RefreshListWindow(id);
        }

        // Apply a new UI scale in-place: resize each open window, switch its title mode,
        // re-tint the emblem at the new scale, and re-render the panel children. We
        // intentionally do NOT dispose+reopen; that would fire Disposed handlers which
        // momentarily empty the subscription set, causing the website to re-stream every
        // image (loading spinner + re-decode). In-place updates reuse cached textures.
        private void OnUiScaleChanged(object sender, ValueChangedEventArgs<int> e)
        {
            bool compact = UiScale < 1.0f;
            foreach (var kvp in _listWindows)
            {
                var entry = kvp.Value;
                var list = _catalog?.Lists?.FirstOrDefault(l => l.Id == kvp.Key);

                entry.Window.SetCompactTitle(compact);
                // SetWindowSize fires LayoutRefreshed which syncs the panel + scrollbar.
                entry.Window.SetWindowSize(WindowWidthFor(list), WindowMaxHeight);
                if (list != null)
                {
                    entry.Window.Title = TitleFor(list);
                    entry.Window.Subtitle = SubtitleFor(list);
                    entry.Window.SetEmblemTinted(_cornerSourceTexture, EmblemTintFor(list), UiScale);
                }
                RefreshListWindow(kvp.Key);
            }
            // Re-size any currently-displayed hover card to the new scale.
            HoverCard.RefreshScale();
        }

        // Subtitle is account-name driven; toggling the setting just refreshes subtitles.
        private void OnShowAccountNameChanged(object sender, ValueChangedEventArgs<bool> e)
        {
            foreach (var kvp in _listWindows)
            {
                var list = _catalog?.Lists?.FirstOrDefault(l => l.Id == kvp.Key);
                if (list == null) continue;
                kvp.Value.Window.Subtitle = SubtitleFor(list);
            }
        }

        // Footer button visibility / copy-mode entry depends on this setting.
        // Force re-render of every open list so it appears or disappears immediately.
        private void OnShowCopyWaypointsButtonChanged(object sender, ValueChangedEventArgs<bool> e)
        {
            // If turning off mid-copy-mode, exit copy mode for all lists first.
            if (!e.NewValue) _copyModeListIds.Clear();
            foreach (var id in _listWindows.Keys.ToList())
                RefreshListWindow(id);
        }

        protected override void Update(GameTime gameTime)
        {
            bool catalogChanged = false;
            var dirtyLists = new HashSet<string>();

            ReapStalePollSession();

            while (_incomingMessages.TryDequeue(out var msg))
            {
                try
                {
                    switch (msg.Kind)
                    {
                        case MessageKind.State:
                            if (ApplyState(msg.State, dirtyLists))
                                catalogChanged = true;
                            break;
                        case MessageKind.Entry:
                            if (ApplyEntry(msg.Entry))
                                dirtyLists.Add(msg.Entry.ListId);
                            break;
                        case MessageKind.HoverImage:
                            ApplyHoverImage(msg.HoverImage);
                            break;
                        case MessageKind.Synced:
                            Logger.Info($"Received synced for [{string.Join(",", msg.SyncedListIds ?? new List<string>())}] (loading was [{string.Join(",", _loadingLists)}])");
                            foreach (var id in msg.SyncedListIds ?? new List<string>())
                            {
                                PruneImagesAfterSynced(id);
                                bool wasLoading = _loadingLists.Contains(id);
                                MarkLoaded(id);
                                Logger.Info($"Synced: cleared loading={wasLoading} for {id}");
                                dirtyLists.Add(id);
                            }
                            break;
                        case MessageKind.ConnectionLost:
                            // Drop the catalog entirely on disconnect so ReconcileListWindows
                            // closes all open list windows (their persisted IDs auto-reopen on
                            // the next connection's first state).
                            ResetClientState(dropCatalog: true);
                            catalogChanged = true;
                            break;
                        case MessageKind.ClientReplaced:
                            // Keep the catalog so list windows survive until the new client's
                            // first `state` arrives; only flush per-entry caches and the
                            // subscription set so the new client gets a fresh `subscribe`.
                            ResetClientState(dropCatalog: false);
                            catalogChanged = true;
                            break;
                    }
                }
                catch (Exception e)
                {
                    Logger.Warn(e, "Failed to apply message.");
                }
            }

            if (Interlocked.Exchange(ref _connectionStateDirty, 0) != 0)
                catalogChanged = true;

            // Refresh subtitles once per UTC minute so the reset countdown ticks down.
            // Cheap when nothing changed (we only assign Subtitle if the string differs).
            int currentMinuteEpoch = (int)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute);
            if (_lastCountdownMinute != currentMinuteEpoch)
            {
                _lastCountdownMinute = currentMinuteEpoch;
                RefreshOpenWindowCountdowns();
            }

            if (catalogChanged)
            {
                ReconcileListWindows();
                RebuildContextMenu();
                // Refresh every open window so connection-state-dependent UI (error / spinner)
                // updates even for lists that didn't otherwise change.
                foreach (var id in _listWindows.Keys)
                    dirtyLists.Add(id);
            }

            // Menu rebuild requested by a toggle. Skip if catalogChanged already rebuilt it this tick.
            if (_contextMenuRebuildPending)
            {
                _contextMenuRebuildPending = false;
                if (!catalogChanged) RebuildContextMenu();
            }

            // Loading-timeout sweep. Lists that have been waiting > LoadingTimeoutSeconds
            // without a `synced` flip to the failed state and surface a Retry button.
            if (_loadingLists.Count > 0)
            {
                var nowUtc = DateTime.UtcNow;
                foreach (var listId in _loadingLists.ToList())
                {
                    if (_loadingFailures.Contains(listId)) continue;
                    if (!_loadingStartTimes.TryGetValue(listId, out var start)) continue;
                    if ((nowUtc - start).TotalSeconds > LoadingTimeoutSeconds)
                    {
                        _loadingFailures.Add(listId);
                        Logger.Warn($"Loading timeout for list {listId} after {LoadingTimeoutSeconds}s; surfacing Retry.");
                        dirtyLists.Add(listId);
                    }
                }
            }

            foreach (var listId in dirtyLists)
                RefreshListWindow(listId);

            // Deferred UI-initiated refreshes (e.g. slider drag inside the copy-mode
            // panel). Processed here so the firing control isn't disposed mid-event.
            if (_deferredRefreshes.Count > 0)
            {
                foreach (var listId in _deferredRefreshes.ToList())
                    RefreshListWindow(listId);
                _deferredRefreshes.Clear();
            }

            HoverCard.Tick();
        }

        private bool ApplyState(StateMessage state, HashSet<string> dirtyLists)
        {
            var oldCatalog = _catalog;
            _catalog = state;

            // First state for this connection: try to restore previously open list windows.
            if (!_restoredFromPersistence)
            {
                _restoredFromPersistence = true;
                RestorePersistedOpenLists();
            }

            // Per protocol: imagery is NOT pruned here. It's pruned when `synced` arrives.
            // For each subscribed list whose entries materially changed (count or flags),
            // mark it as loading so the UI shows a spinner until the client's `synced` confirms
            // the bulk re-image is complete.
            foreach (var list in state.Lists ?? new List<ListDto>())
            {
                if (string.IsNullOrEmpty(list.Id)) continue;
                dirtyLists.Add(list.Id);

                if (_lastSubscribedIds.Contains(list.Id) && EntriesChanged(oldCatalog, list.Id, list))
                {
                    bool wasLoading = _loadingLists.Contains(list.Id);
                    MarkLoading(list.Id);
                    if (!wasLoading)
                        Logger.Info($"Loading: marked {list.Id} as loading (state changed)");
                }
            }

            return true;
        }

        private static bool EntriesChanged(StateMessage oldState, string listId, ListDto newList)
        {
            var oldList = oldState?.Lists?.FirstOrDefault(l => l.Id == listId);
            if (oldList == null) return true; // list newly appeared
            var oldEntries = oldList.Entries ?? new List<EntryDto>();
            var newEntries = newList.Entries ?? new List<EntryDto>();
            if (oldEntries.Count != newEntries.Count) return true;
            for (int i = 0; i < oldEntries.Count; i++)
            {
                if (oldEntries[i].Completed     != newEntries[i].Completed ||
                    oldEntries[i].AutoCompleted != newEntries[i].AutoCompleted)
                    return true;
            }
            return false;
        }

        // Per protocol: when `synced` arrives, drop cached images whose index is now
        // out of range (entries were removed), and drop all images for lists no longer
        // in the catalog.
        private void PruneImagesAfterSynced(string listId)
        {
            var list = _catalog?.Lists?.FirstOrDefault(l => l.Id == listId);
            int validCount = list?.Entries?.Count ?? 0;

            var prefix = listId + ":";
            var keysToRemove = new List<string>();
            foreach (var key in _entryImages.Keys)
            {
                if (!key.StartsWith(prefix)) continue;
                if (!int.TryParse(key.Substring(prefix.Length), out int idx)) continue;
                if (idx >= validCount) keysToRemove.Add(key);
            }

            foreach (var key in keysToRemove)
            {
                if (_entryImages.TryGetValue(key, out var tex))
                    tex?.Dispose();
                _entryImages.Remove(key);
                _entryChatLinks.Remove(key);
                _entryLinks.Remove(key);
            }
        }

        private bool ApplyEntry(EntryMessage entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.ListId)) return false;
            if (_catalog?.Lists == null) return false;

            var list = _catalog.Lists.FirstOrDefault(l => l.Id == entry.ListId);
            if (list == null || list.Entries == null) return false;
            if (entry.Index < 0 || entry.Index >= list.Entries.Count) return false;

            var e = list.Entries[entry.Index];
            // Guard against an empty payload so we never wipe the last known name.
            if (!string.IsNullOrEmpty(entry.Name)) e.Name = entry.Name;
            e.Completed = entry.Completed;
            e.AutoCompleted = entry.AutoCompleted;
            e.HasHoverCard = entry.HasHoverCard;

            var chatKey = EntryKey(entry.ListId, entry.Index);
            if (string.IsNullOrEmpty(entry.ChatLink))
                _entryChatLinks.Remove(chatKey);
            else
                _entryChatLinks[chatKey] = entry.ChatLink;

            if (string.IsNullOrEmpty(entry.Link))
                _entryLinks.Remove(chatKey);
            else
                _entryLinks[chatKey] = entry.Link;

            if (!string.IsNullOrEmpty(entry.ImageB64))
            {
                try
                {
                    var bytes = Convert.FromBase64String(entry.ImageB64);
                    Texture2D newTex;
                    using (var ms = new MemoryStream(bytes))
                    using (var gdc = GameService.Graphics.LendGraphicsDeviceContext())
                    {
                        newTex = Texture2D.FromStream(gdc.GraphicsDevice, ms);
                        PremultiplyAlpha(newTex);
                    }

                    var key = EntryKey(entry.ListId, entry.Index);
                    if (_entryImages.TryGetValue(key, out var oldTex))
                        oldTex?.Dispose();
                    _entryImages[key] = newTex;

                    // Image just arrived: if this entry was pending a user toggle, clear it.
                    // The cache update above + pending removal here happen before the refresh
                    // at the end of Update, so the new image is rendered with the spinner
                    // already gone in a single atomic update.
                    _pendingEntries.Remove(key);

                    // A new row image is the website's signal that underlying data changed,
                    // so the cached hover card is stale. Drop it; next hover re-streams.
                    if (_hoverImageCache.TryGetValue(key, out var staleHover))
                    {
                        try { staleHover?.Dispose(); } catch { }
                        _hoverImageCache.Remove(key);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, $"Failed to decode image for {entry.ListId}[{entry.Index}] ({entry.Mime}).");
                }
            }

            return true;
        }

        private void ApplyHoverImage(HoverImageMessage hi)
        {
            if (hi == null || string.IsNullOrEmpty(hi.ListId) || string.IsNullOrEmpty(hi.ImageB64)) return;

            Texture2D newTex;
            try
            {
                var bytes = Convert.FromBase64String(hi.ImageB64);
                using (var ms = new MemoryStream(bytes))
                using (var gdc = GameService.Graphics.LendGraphicsDeviceContext())
                {
                    newTex = Texture2D.FromStream(gdc.GraphicsDevice, ms);
                    PremultiplyAlpha(newTex);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"Failed to decode hover_image for {hi.ListId}[{hi.Index}] ({hi.Mime}).");
                return;
            }

            var key = EntryKey(hi.ListId, hi.Index);
            _hoverImageCache.TryGetValue(key, out var oldTex);
            _hoverImageCache[key] = newTex;
            // Hand off to HoverCard before disposing oldTex so it can swap to newTex
            // first (HoverCard may have been displaying oldTex).
            HoverCard.SetImage(hi.ListId, hi.Index, newTex);
            if (oldTex != null) { try { oldTex.Dispose(); } catch { } }
        }

        private static string EntryKey(string listId, int index) => listId + ":" + index.ToString();

        // Wraps _loadingLists mutations so the loading-start timestamps and failure
        // markers stay in sync. Use these instead of mutating the sets directly.
        private void MarkLoading(string listId)
        {
            _loadingLists.Add(listId);
            _loadingStartTimes[listId] = DateTime.UtcNow;
            _loadingFailures.Remove(listId);
        }

        private void MarkLoaded(string listId)
        {
            _loadingLists.Remove(listId);
            _loadingStartTimes.Remove(listId);
            _loadingFailures.Remove(listId);
        }

        // Wipe all per-connection state. Called on disconnect (dropCatalog=true) and on
        // supersede by a new client (dropCatalog=false: keeps windows alive until the
        // new client's first `state` lands). Resetting _lastSubscribedIds is what causes
        // ReconcileListWindows → UpdateSubscriptions to send a fresh `subscribe` to the
        // new client based on currently-open windows.
        private void ResetClientState(bool dropCatalog)
        {
            HoverCard.Teardown();

            foreach (var tex in _entryImages.Values)
                try { tex?.Dispose(); } catch { }
            _entryImages.Clear();
            _entryChatLinks.Clear();
            _entryLinks.Clear();
            foreach (var tex in _hoverImageCache.Values)
                try { tex?.Dispose(); } catch { }
            _hoverImageCache.Clear();
            _pendingEntries.Clear();
            _loadingLists.Clear();
            _loadingStartTimes.Clear();
            _loadingFailures.Clear();
            _copyModeListIds.Clear();
            _lastSubscribedIds = new HashSet<string>();
            _restoredFromPersistence = false;
            if (dropCatalog) _catalog = null;
        }

        protected override void Unload()
        {
            // Block UpdateSubscriptions from running during teardown.
            _unloading = true;

            if (_windowTheme != null)
                _windowTheme.SettingChanged -= OnWindowThemeChanged;
            if (_bgOpacityPct != null)
                _bgOpacityPct.SettingChanged -= OnBgOpacityChanged;
            if (_uiScalePct != null)
                _uiScalePct.SettingChanged -= OnUiScaleChanged;
            if (_showAccountName != null)
                _showAccountName.SettingChanged -= OnShowAccountNameChanged;
            if (_showCopyWaypointsButton != null)
                _showCopyWaypointsButton.SettingChanged -= OnShowCopyWaypointsButtonChanged;
            if (_toggleListsKeybind?.Value != null)
                _toggleListsKeybind.Value.Activated -= OnToggleListsKeybind;

            try { _httpCts?.Cancel(); } catch { }
            try { _httpListener?.Stop(); } catch { }
            try { _httpListener?.Close(); } catch { }
            _httpListener = null;
            _httpCts?.Dispose();
            _httpCts = null;

            WebSocket activeClient;
            lock (_clientLock)
            {
                activeClient = _activeClient;
                _activeClient = null;
                try { _activeClientCts?.Cancel(); } catch { }
                try { _activeClientCts?.Dispose(); } catch { }
                _activeClientCts = null;
                _activePollSession?.MarkSuperseded();
                _activePollSession = null;
            }
            if (activeClient != null)
            {
                try { activeClient.Dispose(); } catch { }
            }

            foreach (var entry in _listWindows.Values.ToList())
            {
                try { entry.Window?.Dispose(); } catch { }
            }
            _listWindows.Clear();

            _contextMenuStrip?.Dispose();
            _cornerIcon?.Dispose();
            _infoWindow?.Dispose();
            HoverCard.Dispose();

            foreach (var tex in _entryImages.Values)
                tex?.Dispose();
            _entryImages.Clear();

            foreach (var tex in _hoverImageCache.Values)
                try { tex?.Dispose(); } catch { }
            _hoverImageCache.Clear();

            _iconTexture?.Dispose();
            _iconHiddenTexture?.Dispose();
            _cornerSourceTexture?.Dispose();
            _logoTexture?.Dispose();
            _dotConnectedTexture?.Dispose();
            _dotNotConnectedTexture?.Dispose();
            _dividerTexture?.Dispose();
            _rechargeTexture?.Dispose();

            GW2appInstance = null;
        }

        // ----- Fields -----

        internal static GW2app GW2appInstance;

        private Texture2D _iconTexture;
        // Corner-icon variant shown while lists are hidden. Null when the asset isn't bundled.
        private Texture2D _iconHiddenTexture;
        private Texture2D _cornerSourceTexture;
        private Texture2D _logoTexture;
        private Texture2D _dotConnectedTexture;
        private Texture2D _dotNotConnectedTexture;
        private Texture2D _dividerTexture;
        private Texture2D _rechargeTexture;
        // Last UTC-minute for which we refreshed window subtitles (drives reset
        // countdown updates). Initialized so the first Update tick refreshes immediately.
        private int _lastCountdownMinute = -1;
        private CornerIcon _cornerIcon;
        private ContextMenuStrip _contextMenuStrip;

        private readonly Dictionary<string, ListWindowEntry> _listWindows
            = new Dictionary<string, ListWindowEntry>();

        private HttpListener _httpListener;
        private CancellationTokenSource _httpCts;

        private readonly object _clientLock = new object();
        private WebSocket _activeClient;
        private CancellationTokenSource _activeClientCts;
        // Active polling client, if any. At most one of _activeClient / _activePollSession
        // is set; either kind supersedes the other.
        private PollChannel _activePollSession;
        // Most recently superseded poll session; its last in-flight poll gets a 4000 close
        // instead of re-registering and stealing the connection back.
        private string _lastSupersededPollId;
        private int _hasActiveConnection;
        private int _connectionStateDirty;

        private StateMessage _catalog;
        private readonly Dictionary<string, Texture2D> _entryImages = new Dictionary<string, Texture2D>();
        // Chat-link strings per (listId, index). Opaque, copied to clipboard on entry-image click.
        private readonly Dictionary<string, string> _entryChatLinks = new Dictionary<string, string>();
        // External URL per (listId, index), currently only set for custom entries. Opened
        // in the default browser on entry-image click when no chat_link is present.
        private readonly Dictionary<string, string> _entryLinks = new Dictionary<string, string>();
        // Last hover-card PNG per (listId, index). Populated as `hover_image` frames arrive
        // while a subscription is open; consulted on a fresh hover so the previously-rendered
        // card flashes up instantly while we wait for the new live frame. Invalidated when a
        // new row image arrives for the same key (underlying data changed).
        private readonly Dictionary<string, Texture2D> _hoverImageCache = new Dictionary<string, Texture2D>();
        private HashSet<string> _lastSubscribedIds = new HashSet<string>();
        private readonly HashSet<string> _loadingLists = new HashSet<string>();
        // Per-list timestamp of when loading started; drives the timeout check.
        private readonly Dictionary<string, DateTime> _loadingStartTimes = new Dictionary<string, DateTime>();
        // List IDs whose loading exceeded LoadingTimeoutSeconds without a `synced`.
        // RefreshListWindow renders the failed-state UI for these instead of the spinner.
        private readonly HashSet<string> _loadingFailures = new HashSet<string>();
        // Lists exceeding this without a `synced` are surfaced as "Failed to load" with a Retry button.
        private const int LoadingTimeoutSeconds = 20;
        // Entry keys (EntryKey(listId, index)) we sent set_entry_completed for, awaiting
        // a fresh image from the website confirming the change. Cleared as soon as a new
        // image arrives (in ApplyEntry).
        private readonly HashSet<string> _pendingEntries = new HashSet<string>();

        // List IDs whose "Completed" section is currently collapsed in GROUP_COMPLETED mode.
        // Default = expanded (not in the set). Per-session only.
        private readonly HashSet<string> _completedSectionCollapsed = new HashSet<string>();
        private bool _restoredFromPersistence;
        private bool _unloading;
        private SettingEntry<string> _persistedOpenListsJson;
        private SettingEntry<int> _maxWaypointsPerCopy;
        // List ids currently in "copy waypoints" mode (panel shows slider + chunks).
        private readonly HashSet<string> _copyModeListIds = new HashSet<string>();
        // Lists scheduled for refresh on the next Update tick. Used to defer refreshes
        // out of UI event handlers (e.g. slider ValueChanged) so we don't dispose the
        // very control that fired the event.
        private readonly HashSet<string> _deferredRefreshes = new HashSet<string>();
        // Set by ToggleActiveListsVisibility, processed in Update so the menu is never
        // rebuilt (disposing its items) from inside a menu item's own Click.
        private bool _contextMenuRebuildPending;
        private SettingEntry<GW2appWindow.WindowTheme> _windowTheme;
        private SettingEntry<int> _bgOpacityPct;
        private SettingEntry<int> _uiScalePct;
        private SettingEntry<bool> _showAccountName;
        private SettingEntry<bool> _showCopyWaypointsButton;
        private SettingEntry<KeyBinding> _toggleListsKeybind;

        // List ids hidden by the show/hide toggle. In-memory only, never persisted, so an
        // accidental hide never survives a restart or reconnect.
        private readonly HashSet<string> _peekHiddenIds = new HashSet<string>();
        // Set while the toggle drives visibility so the Shown/Hidden handlers skip their
        // persist + resubscribe side effects (a peek is visual only).
        private bool _suppressListVisibilityHandlers;

        private readonly ConcurrentQueue<IncomingMessage> _incomingMessages = new ConcurrentQueue<IncomingMessage>();
    }
}
