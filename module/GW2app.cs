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
using Blish_HUD.Modules;
using Blish_HUD.Modules.Managers;
using Blish_HUD.Settings;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace GW2app
{
    [Export(typeof(Module))]
    public partial class GW2app : Module
    {
        private static readonly Logger Logger = Logger.GetLogger<GW2app>();

        private const int HttpPort = 38473;
        private const int DisplayWidth = 400;
        private const int ProtocolVersion = 1;
        private const int HandshakeTimeoutMs = 5000;

        private const int CloseCodeSuperseded = 4000;
        private const int CloseCodeHandshakeTimeout = 4001;
        private const int CloseCodeProtocolViolation = 4002;

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
            // Note: setting key changed from "backgroundMode" because a previous build had
            // a wider enum and persisted out-of-range integer values that broke the dropdown.
            _backgroundMode = settings.DefineSetting(
                "windowStyle",
                GW2appWindow.BackgroundMode.Dark,
                () => "List window style",
                () => "Changes the background of list windows");

            var internalSettings = settings.AddSubCollection("internal");
            _persistedOpenListsJson = internalSettings.DefineSetting("openLists", "[]");
        }

        protected override async Task LoadAsync()
        {
            _iconTexture = ContentsManager.GetTexture("gw2app-icon.png");
            _cornerSourceTexture = ContentsManager.GetTexture("gw2app-corner.png");
            _logoTexture = ContentsManager.GetTexture("gw2app-logo.png");
            _dotConnectedTexture = ContentsManager.GetTexture("connected.png");
            _dotNotConnectedTexture = ContentsManager.GetTexture("not-connected.png");
            // Prewarm the GW2 window background so it's ready when the user picks
            // "Game texture" in the settings dropdown.
            AsyncTexture2D.FromAssetId(155997);
            CreateCornerIcon();
            RebuildContextMenu();

            if (_backgroundMode != null)
                _backgroundMode.SettingChanged += OnBackgroundModeChanged;

            StartHttpServer();

            await Task.CompletedTask;
        }

        private void OnBackgroundModeChanged(object sender, ValueChangedEventArgs<GW2appWindow.BackgroundMode> e)
        {
            foreach (var entry in _listWindows.Values)
                entry.Window?.SetBackgroundMode(e.NewValue);
            _infoWindow?.SetBackgroundMode(e.NewValue);
        }

        protected override void Update(GameTime gameTime)
        {
            bool catalogChanged = false;
            var dirtyLists = new HashSet<string>();

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
                        case MessageKind.Synced:
                            Logger.Info($"Received synced for [{string.Join(",", msg.SyncedListIds ?? new List<string>())}] (loading was [{string.Join(",", _loadingLists)}])");
                            foreach (var id in msg.SyncedListIds ?? new List<string>())
                            {
                                PruneImagesAfterSynced(id);
                                bool wasLoading = _loadingLists.Remove(id);
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
                    Logger.Warn($"Failed to apply message: {e.Message}");
                }
            }

            if (Interlocked.Exchange(ref _connectionStateDirty, 0) != 0)
                catalogChanged = true;

            if (catalogChanged)
            {
                ReconcileListWindows();
                RebuildContextMenu();
                // Refresh every open window so connection-state-dependent UI (error / spinner)
                // updates even for lists that didn't otherwise change.
                foreach (var id in _listWindows.Keys)
                    dirtyLists.Add(id);
            }

            foreach (var listId in dirtyLists)
                RefreshListWindow(listId);
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
                    if (_loadingLists.Add(list.Id))
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
            e.Completed = entry.Completed;
            e.AutoCompleted = entry.AutoCompleted;

            var chatKey = EntryKey(entry.ListId, entry.Index);
            if (string.IsNullOrEmpty(entry.ChatLink))
                _entryChatLinks.Remove(chatKey);
            else
                _entryChatLinks[chatKey] = entry.ChatLink;

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
                    // at the end of Update — so the new image is rendered with the spinner
                    // already gone in a single atomic update.
                    _pendingEntries.Remove(key);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Failed to decode image for {entry.ListId}[{entry.Index}] ({entry.Mime}): {ex.Message}");
                }
            }

            return true;
        }

        private static string EntryKey(string listId, int index) => listId + ":" + index.ToString();

        // Wipe all per-connection state. Called on disconnect (dropCatalog=true) and on
        // supersede by a new client (dropCatalog=false — keeps windows alive until the
        // new client's first `state` lands). Resetting _lastSubscribedIds is what causes
        // ReconcileListWindows → UpdateSubscriptions to send a fresh `subscribe` to the
        // new client based on currently-open windows.
        private void ResetClientState(bool dropCatalog)
        {
            foreach (var tex in _entryImages.Values)
                try { tex?.Dispose(); } catch { }
            _entryImages.Clear();
            _entryChatLinks.Clear();
            _pendingEntries.Clear();
            _loadingLists.Clear();
            _lastSubscribedIds = new HashSet<string>();
            _restoredFromPersistence = false;
            if (dropCatalog) _catalog = null;
        }

        protected override void Unload()
        {
            // Block UpdateSubscriptions from running during teardown.
            _unloading = true;

            if (_backgroundMode != null)
                _backgroundMode.SettingChanged -= OnBackgroundModeChanged;

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

            foreach (var tex in _entryImages.Values)
                tex?.Dispose();
            _entryImages.Clear();

            _iconTexture?.Dispose();
            _cornerSourceTexture?.Dispose();
            _logoTexture?.Dispose();
            _dotConnectedTexture?.Dispose();
            _dotNotConnectedTexture?.Dispose();
            _dividerTexture?.Dispose();

            GW2appInstance = null;
        }

        // ----- Fields -----

        internal static GW2app GW2appInstance;

        private Texture2D _iconTexture;
        private Texture2D _cornerSourceTexture;
        private Texture2D _logoTexture;
        private Texture2D _dotConnectedTexture;
        private Texture2D _dotNotConnectedTexture;
        private Texture2D _dividerTexture;
        private CornerIcon _cornerIcon;
        private ContextMenuStrip _contextMenuStrip;

        private readonly Dictionary<string, ListWindowEntry> _listWindows
            = new Dictionary<string, ListWindowEntry>();

        private HttpListener _httpListener;
        private CancellationTokenSource _httpCts;

        private readonly object _clientLock = new object();
        private WebSocket _activeClient;
        private CancellationTokenSource _activeClientCts;
        private int _hasActiveConnection;
        private int _connectionStateDirty;

        private StateMessage _catalog;
        private readonly Dictionary<string, Texture2D> _entryImages = new Dictionary<string, Texture2D>();
        // Chat-link strings per (listId, index) — opaque, copied to clipboard on entry-image click.
        private readonly Dictionary<string, string> _entryChatLinks = new Dictionary<string, string>();
        private HashSet<string> _lastSubscribedIds = new HashSet<string>();
        private readonly HashSet<string> _loadingLists = new HashSet<string>();
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
        private SettingEntry<GW2appWindow.BackgroundMode> _backgroundMode;

        private readonly ConcurrentQueue<IncomingMessage> _incomingMessages = new ConcurrentQueue<IncomingMessage>();
    }
}
