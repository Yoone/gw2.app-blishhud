﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Blish_HUD;
using Blish_HUD.Content;
using Blish_HUD.Controls;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Color = Microsoft.Xna.Framework.Color;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace GW2app
{
    public partial class GW2app
    {
        private void CreateCornerIcon()
        {
            _cornerIcon = new CornerIcon()
            {
                Icon = _iconTexture,
                BasicTooltipText = "GW2.app (Not connected)",
                Priority = 1645843523,
                Parent = GameService.Graphics.SpriteScreen
            };

            _contextMenuStrip = new ContextMenuStrip();
            _cornerIcon.Menu = _contextMenuStrip;
            // Left click opens the info window. Right click is wired automatically by Blish
            // via the Menu property and shows the lists menu.
            _cornerIcon.Click += (s, e) => OpenInfoWindow();
        }

        // ----- Info window (instructions + connection status) -----

        private GW2appWindow _infoWindow;
        private Label _infoStatusLabel;
        private Image _infoStatusDot;
        private int _infoStatusRowY;
        private Label _infoConnectedHint1;
        private Label _infoConnectedHint2;

        private void OpenInfoWindow()
        {
            if (_infoWindow == null)
                BuildInfoWindow();

            _infoWindow.ToggleWindow();
        }

        private void BuildInfoWindow()
        {
            _infoWindow = new GW2appWindow(430, 460, _windowTheme?.Value ?? GW2appWindow.WindowTheme.Game)
            {
                Parent = GameService.Graphics.SpriteScreen,
                Title = "Connect GW2.app",
                Subtitle = "",
                Location = new Point(300, 120),
                SavesPosition = true,
                Id = $"{nameof(GW2app)}_InfoWindow",
                CanCloseWithEscape = true,
            };
            _infoWindow.SetEmblemTinted(_cornerSourceTexture, Color.White);
            _infoWindow.Disposed += (s, e) =>
            {
                _infoWindow = null;
                _infoStatusLabel = null;
                _infoStatusDot = null;
            };

            // Logo, centered horizontally. Scaled to ~2/3 of the source dimensions.
            int srcW = _logoTexture?.Width ?? 200;
            int srcH = _logoTexture?.Height ?? 108;
            int logoW = srcW * 2 / 3;
            int logoH = srcH * 2 / 3;
            const int contentTopOffset = 54; // 24 base + 30 push-down
            new Image(_logoTexture)
            {
                Size = new Point(logoW, logoH),
                Location = new Point((_infoWindow.ContentRegion.Width - logoW) / 2, contentTopOffset),
                Parent = _infoWindow,
            };

            int y = contentTopOffset + logoH + 28; // 18 base + 10 extra below logo

            // Two-line instructions. Separate Labels (no WrapText) to avoid Blish's
            // wrap-with-center StackOverflow bug we hit before.
            new Label()
            {
                Text = "Visit gw2.app/blish in a web browser",
                TextColor = Color.LightGray,
                Font = GameService.Content.DefaultFont16,
                Size = new Point(_infoWindow.ContentRegion.Width, 22),
                Location = new Point(0, y),
                HorizontalAlignment = HorizontalAlignment.Center,
                Parent = _infoWindow,
            };
            y += 22;
            new Label()
            {
                Text = "and click Connect to send your lists in-game.",
                TextColor = Color.LightGray,
                Font = GameService.Content.DefaultFont16,
                Size = new Point(_infoWindow.ContentRegion.Width, 22),
                Location = new Point(0, y),
                HorizontalAlignment = HorizontalAlignment.Center,
                Parent = _infoWindow,
            };
            y += 28;

            const int btnW = 200;
            const int btnH = 30;
            var openBtn = new StandardButton()
            {
                Text = "Open gw2.app/blish",
                Width = btnW,
                Height = btnH,
                Location = new Point((_infoWindow.ContentRegion.Width - btnW) / 2, y),
                Parent = _infoWindow,
            };
            openBtn.Click += (s, e) =>
            {
                try { System.Diagnostics.Process.Start("https://gw2.app/blish"); }
                catch (Exception ex) { Logger.Warn($"Failed to open browser: {ex.Message}"); }
            };
            y += btnH + 18;

            // Status row: dot icon + label, centered. Dot textures are 32x32 with the
            // visible dot ~1/4 the size (~12px) centered in the texture; we layout
            // against the visible dot, not the texture bounds.
            _infoStatusRowY = y;
            _infoStatusDot = new Image(_dotConnectedTexture)
            {
                Size = new Point(DotIconSize, DotIconSize),
                Location = new Point(0, _infoStatusRowY),
                Parent = _infoWindow,
            };
            _infoStatusLabel = new Label()
            {
                Text = "",
                Font = GameService.Content.DefaultFont16,
                AutoSizeWidth = true,
                AutoSizeHeight = true,
                Location = new Point(0, _infoStatusRowY),
                Parent = _infoWindow,
            };

            // Hint shown only when connected.
            int hintY = _infoStatusRowY + DotIconSize + 14;
            _infoConnectedHint1 = new Label()
            {
                Text = "Right click the GW2.app icon at the top",
                TextColor = Color.LightGray,
                Font = GameService.Content.DefaultFont16,
                Size = new Point(_infoWindow.ContentRegion.Width, 22),
                Location = new Point(0, hintY),
                HorizontalAlignment = HorizontalAlignment.Center,
                Parent = _infoWindow,
            };
            _infoConnectedHint2 = new Label()
            {
                Text = "to open the list(s) you want to track.",
                TextColor = Color.LightGray,
                Font = GameService.Content.DefaultFont16,
                Size = new Point(_infoWindow.ContentRegion.Width, 22),
                Location = new Point(0, hintY + 22),
                HorizontalAlignment = HorizontalAlignment.Center,
                Parent = _infoWindow,
            };

            RefreshInfoStatus();
        }

        private const int DotIconSize = 32;     // texture dimensions
        private const int DotVisibleSize = 12;  // approximate visible dot diameter
        private const int DotTextGap = 6;       // visual gap between dot edge and text
        private const int DotVerticalNudge = 2; // shift dot down a touch to align with text baseline

        private void RefreshInfoStatus()
        {
            if (_infoWindow == null || _infoStatusLabel == null || _infoStatusDot == null) return;

            bool connected = _hasActiveConnection != 0;
            int listCount = _catalog?.Lists?.Count ?? 0;

            _infoStatusDot.Texture = connected ? _dotConnectedTexture : _dotNotConnectedTexture;
            _infoStatusLabel.Text = connected
                ? ("Connected (" + listCount + (listCount == 1 ? " list)" : " lists)"))
                : "Not connected";
            _infoStatusLabel.TextColor = connected
                ? new Color(0x32, 0xcd, 0x32) // green
                : new Color(0xdc, 0x14, 0x3c); // crimson

            // Center the visual unit (dot + gap + text) using the dot's visible width.
            int visualWidth = DotVisibleSize + DotTextGap + _infoStatusLabel.Width;
            int leftX = (_infoWindow.ContentRegion.Width - visualWidth) / 2;

            int dotIconX = leftX - (DotIconSize - DotVisibleSize) / 2;
            int textX = leftX + DotVisibleSize + DotTextGap;

            _infoStatusDot.Location = new Point(dotIconX, _infoStatusRowY + DotVerticalNudge);
            int labelY = _infoStatusRowY + (DotIconSize - _infoStatusLabel.Height) / 2;
            _infoStatusLabel.Location = new Point(textX, labelY);

            if (_infoConnectedHint1 != null) _infoConnectedHint1.Visible = connected;
            if (_infoConnectedHint2 != null) _infoConnectedHint2.Visible = connected;
        }

        private void RebuildContextMenu()
        {
            if (_contextMenuStrip == null) return;

            foreach (var child in _contextMenuStrip.Children.ToList())
                child.Dispose();

            bool connected = _hasActiveConnection != 0;
            if (_cornerIcon != null)
            {
                if (connected)
                {
                    int listCount = _catalog?.Lists?.Count ?? 0;
                    _cornerIcon.BasicTooltipText = "GW2.app (Connected, " + listCount + (listCount == 1 ? " list)" : " lists)");
                }
                else
                {
                    _cornerIcon.BasicTooltipText = "GW2.app (Not connected)";
                }
            }
            RefreshInfoStatus();

            var lists = _catalog?.Lists;

            if (!connected)
            {
                var item = _contextMenuStrip.AddMenuItem("(Not connected: no lists available)");
                item.Enabled = false;
                return;
            }

            if (lists == null || lists.Count == 0)
            {
                var item = _contextMenuStrip.AddMenuItem("(no lists)");
                item.Enabled = false;
                return;
            }

            // Group lists by account name. Each account gets a disabled header item
            // followed by its lists, indented. Accounts and lists are each sorted
            // alphabetically (case-insensitive). Lists with no account name are
            // bucketed under "" and rendered first, without a header.
            var byAccount = new SortedDictionary<string, List<ListDto>>(StringComparer.OrdinalIgnoreCase);
            foreach (var list in lists)
            {
                if (list == null || string.IsNullOrEmpty(list.Id)) continue;
                string acct = null;
                try { acct = list.Settings?["gw2AccountName"]?.Value<string>(); }
                catch { /* malformed settings */ }
                var key = acct ?? "";
                if (!byAccount.TryGetValue(key, out var bucket))
                {
                    bucket = new List<ListDto>();
                    byAccount[key] = bucket;
                }
                bucket.Add(list);
            }

            int addedCount = 0;
            foreach (var kvp in byAccount)
            {
                if (!string.IsNullOrEmpty(kvp.Key))
                {
                    var header = _contextMenuStrip.AddMenuItem(kvp.Key);
                    header.Enabled = false;
                }
                foreach (var list in kvp.Value.OrderBy(l => l.Name ?? l.Id, StringComparer.OrdinalIgnoreCase))
                {
                    var listId = list.Id;
                    var name = list.Name ?? list.Id;
                    var label = string.IsNullOrEmpty(kvp.Key) ? name : "   " + name;
                    var item = _contextMenuStrip.AddMenuItem(label);
                    item.Click += (s, e) => OpenListWindow(listId);
                    addedCount++;
                }
            }

            // All lists were filtered out (e.g. missing/empty id). Surface a placeholder
            // so the right-click menu still opens; otherwise ContextMenuStrip refuses
            // to display when it has zero children.
            if (addedCount == 0)
            {
                var item = _contextMenuStrip.AddMenuItem("(no usable lists)");
                item.Enabled = false;
            }
        }

        private void OpenListWindow(string listId)
        {
            if (_listWindows.TryGetValue(listId, out var existing))
            {
                if (!existing.Window.Visible) existing.Window.Show();
                return;
            }

            var list = _catalog?.Lists?.FirstOrDefault(l => l.Id == listId);
            if (list == null) return;

            bool compact = UiScale < 1.0f;
            int initialWidth = WindowWidthFor(list);
            var window = new GW2appWindow(initialWidth, WindowMaxHeight, _windowTheme?.Value ?? GW2appWindow.WindowTheme.Game, compactTitle: compact)
            {
                Parent = GameService.Graphics.SpriteScreen,
                Title = TitleFor(list),
                Subtitle = SubtitleFor(list),
                Location = new Point(300, 300),
                SavesPosition = true,
                // Vertical-only resize via the bottom-right handle. Width is locked by
                // GW2appWindow.HandleWindowResize. SavesSize persists the user's height
                // across sessions (Blish writes once on mouse release).
                CanResize = true,
                SavesSize = true,
                Id = $"{nameof(GW2app)}_List_{listId}",
            };
            window.SetEmblemTinted(_cornerSourceTexture, EmblemTintFor(list), UiScale);
            window.SetResetCountdownOverlay(_rechargeTexture, ResetCountdownFor(list));

            var panel = new Panel()
            {
                Location = new Point(0, 0),
                Size = new Point(window.ContentRegion.Width, window.ContentRegion.Height),
                CanScroll = true,
                ShowBorder = true,
                Parent = window,
            };

            // Footer panel sits below the main panel. Initially zero-height; sized by
            // ResizePanelToWindow when entry.FooterReserve > 0 (e.g. list has chat_links).
            var footerPanel = new Panel()
            {
                Location = new Point(0, window.ContentRegion.Height),
                Size = new Point(window.ContentRegion.Width, 0),
                Parent = window,
            };

            var entry = new ListWindowEntry { Window = window, Panel = panel, FooterPanel = footerPanel, ListId = listId };

            // Single hook: GW2appWindow.LayoutRefreshed fires after every recalc
            // (constructor, SetWindowHeight, SetWindowSize, user drag, bg-mode change,
            // game-texture async swap), so panel + scrollbar stay in sync from one place.
            window.LayoutRefreshed += (s, e) => ResizePanelToWindow(entry);

            EventHandler<EventArgs> onHidden = (s, e) =>
            {
                // User clicked X. Remove from auto-open set.
                RemovePersisted(listId);
                UpdateSubscriptions();
            };
            EventHandler<EventArgs> onShown = (s, e) =>
            {
                // Window opened (new or reopened from menu). Add to auto-open set.
                AddPersisted(listId);
                UpdateSubscriptions();
            };
            EventHandler<EventArgs> onDisposed = null;
            onDisposed = (s, e) =>
            {
                window.Hidden -= onHidden;
                window.Shown -= onShown;
                window.Disposed -= onDisposed;
                _listWindows.Remove(listId);
                // Programmatic dispose (disconnect / unload). Do NOT touch persisted set.
                UpdateSubscriptions();
            };
            window.Hidden += onHidden;
            window.Shown += onShown;
            window.Disposed += onDisposed;

            _listWindows[listId] = entry;

            window.Show();
            RefreshListWindow(listId);
        }

        // Update the title-bar countdown overlay on every open list window. Called once
        // per UTC minute so the displayed countdown ticks down without re-rendering the
        // entire entries panel.
        private void RefreshOpenWindowCountdowns()
        {
            if (_catalog?.Lists == null) return;
            foreach (var kvp in _listWindows)
            {
                var list = _catalog.Lists.FirstOrDefault(l => l.Id == kvp.Key);
                if (list == null) continue;
                kvp.Value.Window.SetResetCountdownOverlay(_rechargeTexture, ResetCountdownFor(list));
            }
        }

        // Reconcile list windows against the latest catalog. On disconnect, all windows
        // are programmatically disposed (which fires Disposed, NOT Hidden) so the persisted
        // open set is unaffected and lists will auto-restore on reconnect.
        private void ReconcileListWindows()
        {
            bool connected = _hasActiveConnection != 0;

            if (!connected)
            {
                foreach (var id in _listWindows.Keys.ToList())
                {
                    if (_listWindows.TryGetValue(id, out var entry))
                        try { entry.Window.Dispose(); } catch { }
                }
                UpdateSubscriptions();
                return;
            }

            if (_catalog?.Lists != null)
            {
                var validIds = new HashSet<string>();
                foreach (var l in _catalog.Lists)
                    if (l != null && !string.IsNullOrEmpty(l.Id)) validIds.Add(l.Id);

                var toClose = _listWindows.Keys.Where(id => !validIds.Contains(id)).ToList();
                foreach (var id in toClose)
                {
                    if (_listWindows.TryGetValue(id, out var entry))
                    {
                        try { entry.Window.Dispose(); } catch { }
                    }
                }

                // Refresh title/subtitle/emblem of still-open windows in case the list metadata changed.
                foreach (var kvp in _listWindows)
                {
                    var listId = kvp.Key;
                    var list = _catalog.Lists.FirstOrDefault(l => l.Id == listId);
                    if (list != null)
                    {
                        var newTitle = TitleFor(list);
                        var newSubtitle = SubtitleFor(list);
                        if (kvp.Value.Window.Title != newTitle)
                            kvp.Value.Window.Title = newTitle;
                        if (kvp.Value.Window.Subtitle != newSubtitle)
                            kvp.Value.Window.Subtitle = newSubtitle;
                        // Re-tint emblem in case settings.color changed.
                        kvp.Value.Window.SetEmblemTinted(_cornerSourceTexture, EmblemTintFor(list), UiScale);
                        // Refresh the title-bar countdown overlay in case settings.reset changed.
                        kvp.Value.Window.SetResetCountdownOverlay(_rechargeTexture, ResetCountdownFor(list));
                    }
                }
            }

            UpdateSubscriptions();
        }

        private void RefreshListWindow(string listId)
        {
            if (!_listWindows.TryGetValue(listId, out var entry)) return;
            if (entry.Panel == null) return;

            foreach (var child in entry.Panel.Children.ToList())
                child.Dispose();
            if (entry.FooterPanel != null)
                foreach (var child in entry.FooterPanel.Children.ToList())
                    child.Dispose();
            entry.FooterReserve = 0;
            entry.RerenderCopyChunks = null;

            // Reset to resizable; the loading / failed branches turn this off again
            // since the window is sized to fit a fixed-height view in those states.
            entry.Window.CanResize = true;

            // Resolve the list early so loading-progress can show "X / Y".
            var list = _catalog?.Lists?.FirstOrDefault(l => l.Id == listId);

            // Failed: timed out waiting for `synced`. Show error + Retry.
            if (_loadingFailures.Contains(listId))
            {
                entry.Panel.CanScroll = false;
                _copyModeListIds.Remove(listId);
                RenderLoadingFailed(entry, listId);
                return;
            }

            // Loading: spinner + "Loading X / Y" below.
            if (_loadingLists.Contains(listId))
            {
                int totalForLoading = list?.Entries?.Count ?? 0;
                int loadedCount = 0;
                if (totalForLoading > 0)
                {
                    for (int i = 0; i < totalForLoading; i++)
                        if (_entryImages.ContainsKey(EntryKey(listId, i))) loadedCount++;
                }

                // Defensive: every image is cached but `synced` never arrived. Bail out
                // of loading state and render entries normally. The PruneImagesAfterSynced
                // step is a no-op when entry count is unchanged, so this is safe.
                if (totalForLoading > 0 && loadedCount >= totalForLoading)
                {
                    Logger.Info($"All {totalForLoading} images cached for list {listId} but no `synced` received; auto-clearing loading state.");
                    MarkLoaded(listId);
                    // fall through to entries rendering
                }
                else
                {
                    entry.Panel.CanScroll = false;
                    _copyModeListIds.Remove(listId);
                    RenderLoadingProgress(entry, loadedCount, totalForLoading);
                    return;
                }
            }

            if (list == null) return;
            int total = list.Entries?.Count ?? 0;

            if (total == 0)
            {
                entry.Panel.CanScroll = false;
                _copyModeListIds.Remove(listId);
                ResizeWindowAndPanel(entry, WindowBaseHeight);
                new Label()
                {
                    Text = "Empty list",
                    TextColor = Color.LightGray,
                    Font = GameService.Content.DefaultFont18,
                    Size = new Point(entry.Panel.Width, entry.Panel.Height),
                    Location = new Point(0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Middle,
                    Parent = entry.Panel,
                };
                return;
            }

            // Collect individual waypoint codes for this list (entry order, then
            // space-split since one chat_link may bundle multiple codes; PSNA carries
            // ~4 vendor waypoints per entry). When entry_type is present we restrict
            // to waypoint-bearing types ("location", "dailypsna", "vendoritem") per
            // protocol; entries without entry_type are accepted as a fallback for
            // older clients.
            var chatLinks = new List<string>();
            for (int i = 0; i < total; i++)
            {
                var t = list.Entries[i]?.EntryType;
                bool waypointEligible = string.IsNullOrEmpty(t)
                    || t == "location" || t == "dailypsna" || t == "vendoritem";
                if (!waypointEligible) continue;
                if (_entryChatLinks.TryGetValue(EntryKey(listId, i), out var cl) && !string.IsNullOrEmpty(cl))
                    chatLinks.AddRange(cl.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            }
            bool footerEnabled = ShowCopyWaypointsButton;
            bool hasChatLinks = chatLinks.Count > 0;
            bool inCopyMode = hasChatLinks && footerEnabled && _copyModeListIds.Contains(listId);
            if (!hasChatLinks || !footerEnabled) _copyModeListIds.Remove(listId);

            // Reserve footer space when there's a footer button to render.
            if (hasChatLinks && footerEnabled)
            {
                entry.FooterReserve = FooterReserveTotal;
                RenderFooter(entry, listId, chatLinks.Count, inCopyMode);
            }

            if (inCopyMode)
            {
                int copyContentHeight = RenderCopyMode(entry, listId, chatLinks);
                int copyTarget = copyContentHeight + WindowVerticalChrome + entry.FooterReserve;
                copyTarget = Math.Min(WindowMaxHeight, Math.Max(WindowBaseHeight, copyTarget));
                entry.Window.MaxAllowedHeight = Math.Min(GW2appWindow.MaxResizeHeight, copyTarget);
                ResizeWindowAndPanel(entry, copyTarget);
                return;
            }

            entry.Panel.CanScroll = true;
            int y = ImagesTopMargin;

            string sortMode = SortEntriesFor(list);
            bool groupCompleted = string.Equals(sortMode, "GROUP_COMPLETED", StringComparison.OrdinalIgnoreCase);
            bool noCheckbox = list.IsLootBag;

            // Tracks whether any entry has been rendered in the current section. Drives
            // divider drawing (we draw a 1px line BEFORE every rendered entry except the
            // first one in a section). Reset between sections so the completed section
            // doesn't get a leading divider from the uncompleted section.
            bool sectionHasContent = false;

            if (!groupCompleted)
            {
                for (int i = 0; i < total; i++)
                    RenderEntry(entry, listId, list, i, ref y, ref sectionHasContent, noCheckbox);
            }
            else
            {
                // Pass 1: uncompleted entries, in original order.
                for (int i = 0; i < total; i++)
                {
                    var e = list.Entries[i];
                    if (e == null || e.Completed || e.AutoCompleted) continue;
                    RenderEntry(entry, listId, list, i, ref y, ref sectionHasContent, noCheckbox);
                }

                // Count completed entries to render the header (and section, if expanded).
                int completedCount = 0;
                for (int i = 0; i < total; i++)
                {
                    var e = list.Entries[i];
                    if (e != null && (e.Completed || e.AutoCompleted)) completedCount++;
                }

                if (completedCount > 0)
                {
                    bool collapsed = _completedSectionCollapsed.Contains(listId);
                    var btnText = (collapsed ? "Show completed (" : "Hide completed (") + completedCount + ")";
                    y += 6;
                    int btnWidth  = ActionButtonWidth;
                    int btnHeight = ActionButtonHeight;
                    // Use ContentRegion.Width so the centering matches the footer button
                    // (whose panel has no border, hence ContentRegion.X = 0). Children's
                    // Location is relative to the parent's ContentRegion.
                    int btnX = Math.Max(0, (entry.Panel.ContentRegion.Width - btnWidth) / 2);
                    var capturedListId = listId;
                    ActionButton.Create(
                        entry.Panel, btnText, new Point(btnX, y), btnWidth, btnHeight,
                        UiScale, CurrentTheme,
                        onClick: () =>
                        {
                            if (!_completedSectionCollapsed.Add(capturedListId))
                                _completedSectionCollapsed.Remove(capturedListId);
                            RefreshListWindow(capturedListId);
                        });
                    y += btnHeight + 6;

                    if (!collapsed)
                    {
                        sectionHasContent = false; // restart divider tracking for this section
                        for (int i = 0; i < total; i++)
                        {
                            var e = list.Entries[i];
                            if (e == null || !(e.Completed || e.AutoCompleted)) continue;
                            RenderEntry(entry, listId, list, i, ref y, ref sectionHasContent, noCheckbox);
                        }
                    }
                }
            }

            // Sync window width to the loot-bag flag (drops the checkbox column when
            // is_loot_bag is true). Width may have been set differently at OpenListWindow
            // time if the catalog state was empty/stale.
            int desiredWidth = WindowWidthFor(list);
            if (entry.Window.Size.X != desiredWidth)
                entry.Window.SetWindowSize(desiredWidth, entry.Window.Size.Y);

            // Auto-fit (default rendered height) caps at WindowMaxHeight; the user-drag
            // cap is separate (min(content+chrome, MaxResizeHeight)) so users can grow
            // the window past the default but never beyond actual content or 1200.
            int contentHeight = y + ImagesBottomMargin;
            int contentBased = contentHeight + WindowVerticalChrome + entry.FooterReserve;
            int fitHeight = Math.Min(WindowMaxHeight, Math.Max(WindowBaseHeight, contentBased));
            int dragCap   = Math.Min(GW2appWindow.MaxResizeHeight, Math.Max(WindowBaseHeight, contentBased));
            entry.Window.MaxAllowedHeight = dragCap;

            // Honor the user's manual height (capped at content) across re-renders.
            int targetHeight = entry.Window.UserPreferredHeight.HasValue
                ? Math.Min(entry.Window.UserPreferredHeight.Value, dragCap)
                : fitHeight;
            ResizeWindowAndPanel(entry, targetHeight);
        }

        // Tooltip text cap. Long chat_links (e.g. PSNA waypoints carry 4-6
        // codes separated by spaces) would otherwise render an unreadably
        // wide tooltip. Truncate to TooltipMaxChars including the ellipsis.
        private const int TooltipMaxChars = 40;

        private static string TruncateTooltipText(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= TooltipMaxChars) return s;
            return s.Substring(0, TooltipMaxChars - 1) + "…";
        }

        // Renders one entry's checkbox + image (+ pending overlay) into the panel and
        // advances `y`. Skips entries with no cached image. Draws a 1px divider above
        // the entry when the section already has at least one rendered entry, and sets
        // sectionHasContent to true. When `noCheckbox` is true (loot-bag list), the
        // checkbox is omitted and the image starts at the panel's left edge.
        private void RenderEntry(ListWindowEntry entry, string listId, ListDto list, int index, ref int y, ref bool sectionHasContent, bool noCheckbox)
        {
            var key = EntryKey(listId, index);
            if (!_entryImages.TryGetValue(key, out var tex) || tex == null) return;

            if (sectionHasContent)
                DrawDivider(entry.Panel, ref y, noCheckbox);
            sectionHasContent = true;

            var size = ScaledSize(tex);
            var entryDto = list.Entries[index];
            bool isPending = _pendingEntries.Contains(key);

            int imageX = noCheckbox ? 0 : CheckboxColumnWidth;

            if (!noCheckbox)
            {
                bool autoChecked = entryDto != null && entryDto.AutoCompleted;
                bool isChecked = autoChecked || (entryDto != null && entryDto.Completed);
                var checkbox = new Checkbox()
                {
                    Location = new Point(CheckboxLeftMargin, y + (size.Y - CheckboxSize) / 2),
                    Size = new Point(CheckboxSize, CheckboxSize),
                    Checked = isChecked,
                    Enabled = !autoChecked && !isPending,
                    Parent = entry.Panel,
                };
                if (!autoChecked && !isPending)
                {
                    var capturedListId = listId;
                    int capturedIndex = index;
                    checkbox.CheckedChanged += (s, e) => OnCheckboxToggled(checkbox, capturedListId, capturedIndex);
                }
            }

            var image = new Image(tex)
            {
                Location = new Point(imageX, y),
                Size = size,
                Tint = isPending ? PendingTint : Color.White,
                Parent = entry.Panel,
            };

            if (_entryChatLinks.TryGetValue(key, out var chatLink) && !string.IsNullOrEmpty(chatLink))
            {
                image.BasicTooltipText = TruncateTooltipText("Click to copy: " + chatLink);
                var capturedLink = chatLink;
                image.Click += (s, e) => CopyChatLinkToClipboard(capturedLink);
            }
            else if (_entryLinks.TryGetValue(key, out var url) && !string.IsNullOrEmpty(url))
            {
                image.BasicTooltipText = TruncateTooltipText("Click to open " + url);
                var capturedUrl = url;
                image.Click += (s, e) => OpenUrlInBrowser(capturedUrl);
            }

            // Hovercard: only attach for entries the website actually exposes one for.
            // (per protocol: `has_hover_card === true` in the latest state).
            if (entryDto != null && entryDto.HasHoverCard)
            {
                HoverCard.Attach(image, listId, index);
            }

            if (isPending)
            {
                const int pendingSpinnerSize = 32;
                new LoadingSpinner()
                {
                    Location = new Point(
                        imageX + (size.X - pendingSpinnerSize) / 2,
                        y + (size.Y - pendingSpinnerSize) / 2),
                    Size = new Point(pendingSpinnerSize, pendingSpinnerSize),
                    Parent = entry.Panel,
                };
            }

            y += size.Y;
        }

        // 1px line in #151921, drawn between consecutive entries within a section.
        // Spans from the panel's left edge (just before the checkbox) to the right edge
        // of the entry image. No padding; sits flush against both images.
        private const int DividerLeftX = 0;
        private int DividerRightX(bool noCheckbox) => (noCheckbox ? 0 : CheckboxColumnWidth) + DisplayWidth;
        // White at 15% opacity, premultiplied (Blish's SpriteBatch expects premultiplied alpha).
        private static readonly Color DividerColor = new Color(38, 38, 38, 38);

        private void DrawDivider(Container parent, ref int y, bool noCheckbox)
        {
            EnsureDividerTexture();
            new Image(_dividerTexture)
            {
                Location = new Point(DividerLeftX, y),
                Size = new Point(DividerRightX(noCheckbox) - DividerLeftX, 1),
                Parent = parent,
            };
            y += 1;
        }

        private void EnsureDividerTexture()
        {
            if (_dividerTexture != null) return;
            using (var gdc = GameService.Graphics.LendGraphicsDeviceContext())
            {
                _dividerTexture = new Texture2D(gdc.GraphicsDevice, 1, 1);
                _dividerTexture.SetData(new[] { DividerColor });
            }
        }

        private static string SortEntriesFor(ListDto list)
        {
            try { return list?.Settings?["sortEntries"]?.Value<string>() ?? "STATIC"; }
            catch { return "STATIC"; }
        }

        // Reflection handle to Panel's private scrollbar field. Used to fix a Blish layout
        // ordering bug: Panel updates the scrollbar's Height synchronously when its own Size
        // changes, but at that moment Panel.ContentRegion is still stale (recomputed only on
        // the next layout pass). The scrollbar ends up sized for the old ContentRegion. We
        // force a layout recompute, then rewrite the scrollbar height with the fresh value.
        private static readonly FieldInfo _panelScrollbarField =
            typeof(Panel).GetField("_panelScrollbar", BindingFlags.NonPublic | BindingFlags.Instance);

        // StandardButton inherits LabelBase, whose _font is protected. We need to swap
        // it for a smaller font in compact mode and there is no public Font property
        // on StandardButton.
        private static readonly FieldInfo _labelBaseFontField =
            typeof(LabelBase).GetField("_font", BindingFlags.NonPublic | BindingFlags.Instance);

        // (Removed CenterButtonText reflection helper: StandardButton already places
        // text at width/2 - textWidth/2 with Left alignment, which IS visually centered.
        // Setting Center put text in the right half of those bounds, skewing right.)

        private GW2appWindow.WindowTheme CurrentTheme =>
            _windowTheme?.Value ?? GW2appWindow.WindowTheme.Game;

        // Resize the window. The panel is auto-synced via the window's LayoutRefreshed
        // event (subscribed once in OpenListWindow), so we only need to nudge the
        // panel here when SetWindowHeight short-circuited (newHeight == current).
        private static void ResizeWindowAndPanel(ListWindowEntry entry, int newHeight)
        {
            entry.Window.SetWindowHeight(newHeight);
            ResizePanelToWindow(entry);
        }

        // Sync the panel's size + scrollbar to the window's current content region,
        // reserving FooterReserve px below for the footer panel.
        // Wired to GW2appWindow.LayoutRefreshed so user drags update the panel live.
        private static void ResizePanelToWindow(ListWindowEntry entry)
        {
            if (entry.Panel == null) return;
            int fullW = entry.Window.ContentRegion.Width;
            int fullH = entry.Window.ContentRegion.Height;
            int footer = Math.Max(0, entry.FooterReserve);
            int panelH = Math.Max(0, fullH - footer);

            entry.Panel.Size = new Point(fullW, panelH);
            entry.Panel.RecalculateLayout();

            if (_panelScrollbarField?.GetValue(entry.Panel) is Scrollbar sb)
                sb.Height = entry.Panel.ContentRegion.Height - 20;

            if (entry.FooterPanel != null)
            {
                entry.FooterPanel.Location = new Point(0, panelH);
                entry.FooterPanel.Size = new Point(fullW, footer);
            }
        }

        // ---- Footer + waypoint copy mode ----

        // Standard dimensions delegated to ActionButton.cs (the in-panel action
        // button styling lives there since it varies by background style).
        private int ActionButtonWidth  => ActionButton.WidthFor(UiScale);
        private int ActionButtonHeight => ActionButton.HeightFor(UiScale);

        // Vertical offset of the footer button within the footer panel. Negative
        // values let it sit slightly above the panel's top edge.
        private const int FooterTopMargin   = -4;
        private int FooterReserveTotal      => FooterTopMargin + ActionButtonHeight;

        // ---- Loading / failure UIs ----
        //
        // Both views anchor their content to the panel's top with LoadingTopPad of
        // breathing room, then leave LoadingBottomPad below the last element. Window
        // height is derived from the actual content so the layout has no implicit
        // padding via vertical centering.
        private const int LoadingTopPad     = 10;
        private const int LoadingBottomPad  = 20;
        private const int LoadingTextGap    = 0;  // gap between spinner and text
        private const int FailedTextHeight  = 18;
        private const int FailedButtonGap   = 8;

        private void RenderLoadingProgress(ListWindowEntry entry, int loadedCount, int total)
        {
            int spinnerSize = (int)Math.Round(64 * UiScale);
            int contentHeight = LoadingTopPad + spinnerSize + LoadingTextGap + FailedTextHeight + LoadingBottomPad;
            int windowHeight = contentHeight + WindowVerticalChrome;
            entry.Window.CanResize = false;
            entry.Window.MaxAllowedHeight = windowHeight;
            ResizeWindowAndPanel(entry, windowHeight);

            int sx = Math.Max(0, (entry.Panel.Width - spinnerSize) / 2);
            new LoadingSpinner()
            {
                Location = new Point(sx, LoadingTopPad),
                Size = new Point(spinnerSize, spinnerSize),
                Parent = entry.Panel,
            };

            string text = total > 0 ? "Loading " + loadedCount + " / " + total : "Loading...";
            new Label()
            {
                Text = text,
                Font = GameService.Content.DefaultFont14,
                TextColor = Color.LightGray,
                Size = new Point(entry.Panel.Width, FailedTextHeight),
                Location = new Point(0, LoadingTopPad + spinnerSize + LoadingTextGap),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Middle,
                Parent = entry.Panel,
            };
        }

        // "Failed to load list" in red + a Retry button below it.
        private void RenderLoadingFailed(ListWindowEntry entry, string listId)
        {
            int btnW = ActionButtonWidth;
            int btnH = ActionButtonHeight;
            int contentHeight = LoadingTopPad + FailedTextHeight + FailedButtonGap + btnH + LoadingBottomPad;
            int windowHeight = contentHeight + WindowVerticalChrome;
            entry.Window.CanResize = false;
            entry.Window.MaxAllowedHeight = windowHeight;
            ResizeWindowAndPanel(entry, windowHeight);

            new Label()
            {
                Text = "Failed to load list",
                Font = GameService.Content.DefaultFont14,
                TextColor = Color.Red,
                Size = new Point(entry.Panel.Width, FailedTextHeight),
                Location = new Point(0, LoadingTopPad),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Middle,
                Parent = entry.Panel,
            };

            int btnX = Math.Max(0, (entry.Panel.ContentRegion.Width - btnW) / 2);
            int btnY = LoadingTopPad + FailedTextHeight + FailedButtonGap;
            var capturedListId = listId;
            ActionButton.Create(
                entry.Panel, "Retry", new Point(btnX, btnY), btnW, btnH,
                UiScale, CurrentTheme,
                onClick: () => RetryLoadList(capturedListId));
        }

        // Resub trick: send a subscribe without listId, then a normal subscribe with it.
        // Server treats the second one as "newly subscribed" and re-streams imagery +
        // sends `synced`. No protocol change required.
        private void RetryLoadList(string listId)
        {
            // Reset local state for this list.
            _loadingFailures.Remove(listId);
            _pendingEntries.RemoveWhere(k => k.StartsWith(listId + ":"));
            // Drop any partial imagery we have so the new stream replaces fresh.
            var prefix = listId + ":";
            var toRemove = _entryImages.Keys.Where(k => k.StartsWith(prefix)).ToList();
            foreach (var k in toRemove)
            {
                if (_entryImages.TryGetValue(k, out var tex)) try { tex?.Dispose(); } catch { }
                _entryImages.Remove(k);
            }
            MarkLoading(listId);

            // Step 1: send subscribe excluding listId so server drops it.
            if (_lastSubscribedIds.Contains(listId))
            {
                var without = new HashSet<string>(_lastSubscribedIds);
                without.Remove(listId);
                _ = SendSubscribeAsync(without.ToList());
                _lastSubscribedIds = without;
            }
            // Step 2: re-add and send. Server sees this as a fresh subscription -> re-stream.
            var withAgain = new HashSet<string>(_lastSubscribedIds) { listId };
            _ = SendSubscribeAsync(withAgain.ToList());
            _lastSubscribedIds = withAgain;

            RefreshListWindow(listId);
        }

        private void RenderFooter(ListWindowEntry entry, string listId, int chatLinkCount, bool inCopyMode)
        {
            if (entry.FooterPanel == null) return;

            string text = inCopyMode ? "Back" : "Copy waypoints";
            int btnW = ActionButtonWidth;
            int btnH = ActionButtonHeight;
            // Center horizontally within the footer panel.
            int btnX = Math.Max(0, (entry.FooterPanel.Width - btnW) / 2);
            int btnY = FooterTopMargin;

            var capturedListId = listId;
            ActionButton.Create(
                entry.FooterPanel, text, new Point(btnX, btnY), btnW, btnH,
                UiScale, CurrentTheme,
                onClick: () =>
                {
                    if (_copyModeListIds.Contains(capturedListId))
                        _copyModeListIds.Remove(capturedListId);
                    else
                        _copyModeListIds.Add(capturedListId);
                    RefreshListWindow(capturedListId);
                });
        }

        // Lays out the copy-waypoints panel: slider + chunk buttons. Returns the
        // rendered content height (used by the caller to size the window).
        // Stores entry.RerenderCopyChunks so the slider's ValueChanged can update only
        // the chunk buttons (leaving the slider alive; disposing it mid-drag dead-ends
        // the user's interaction).
        private int RenderCopyMode(ListWindowEntry entry, string listId, List<string> chatLinks)
        {
            entry.Panel.CanScroll = true;

            int max = (_maxWaypointsPerCopy?.Value ?? GW2app.MaxWaypointsPerMessage);
            max = Math.Max(1, Math.Min(GW2app.MaxWaypointsPerMessage, max));

            const int sidePad           = 20; // visual left padding
            const int rightReserve      = 25; // right padding incl. scrollbar overlap reserve
            const int topPad            = 20; // space above the header
            const int bottomPad         = 20; // space below the last chunk button
            const int sliderToChunksGap = 10; // extra space between slider and first chunk
            const int chunkH            = 30;
            const int chunkGap          = 4;
            int innerWidth = Math.Max(0, entry.Panel.Width - sidePad - rightReserve);
            int y = topPad;

            // Header row: "Max waypoints per message: 8" / "Max"
            var headerLabel = new Label()
            {
                Text          = LabelForMaxWaypoints(max),
                Font          = GameService.Content.DefaultFont14,
                TextColor     = Color.LightGray,
                AutoSizeWidth = true,
                Location      = new Point(sidePad, y),
                Parent        = entry.Panel,
            };
            y += 18;

            // Slider 1..15.
            const int sliderH = 16;
            var slider = new TrackBar()
            {
                MinValue  = 1,
                MaxValue  = GW2app.MaxWaypointsPerMessage,
                Value     = max,
                SmallStep = true,
                Width     = innerWidth,
                Height    = sliderH,
                Location  = new Point(sidePad, y),
                Parent    = entry.Panel,
            };
            y += sliderH + sliderToChunksGap;
            int chunksStartY = y;

            // Track chunk buttons so the re-render delegate can dispose only those.
            var chunkButtons = new List<StandardButton>();

            // Render chunks at the current max. Returns the total content height
            // including ImagesBottomMargin so callers can size the window.
            int RenderChunks(int currentMax)
            {
                foreach (var b in chunkButtons) try { b.Dispose(); } catch { }
                chunkButtons.Clear();

                int yy = chunksStartY;
                int idx = 1;
                foreach (var chunk in ChunkChatLinks(chatLinks, currentMax))
                {
                    string codes = string.Join(" ", chunk);
                    string label = "Copy group " + idx + " (" + chunk.Count + ")";
                    var chunkBtn = new StandardButton()
                    {
                        Text     = label,
                        Width    = innerWidth,
                        Height   = chunkH,
                        Location = new Point(sidePad, yy),
                        Parent   = entry.Panel,
                    };
                    if (UiScale < 1.0f && _labelBaseFontField != null)
                    {
                        try { _labelBaseFontField.SetValue(chunkBtn, GameService.Content.DefaultFont12); } catch { }
                        chunkBtn.Invalidate();
                    }
                    var capturedCodes = codes;
                    chunkBtn.Click += (s, e) => CopyChatLinkToClipboard(capturedCodes);
                    chunkButtons.Add(chunkBtn);
                    yy += chunkH + chunkGap;
                    idx++;
                }
                return yy + bottomPad;
            }

            int contentHeight = RenderChunks(max);

            // Re-render delegate: invoked from slider.ValueChanged. Updates the header
            // text, replaces only the chunk buttons, and resizes the window to fit.
            entry.RerenderCopyChunks = () =>
            {
                int v = (_maxWaypointsPerCopy?.Value ?? GW2app.MaxWaypointsPerMessage);
                v = Math.Max(1, Math.Min(GW2app.MaxWaypointsPerMessage, v));
                headerLabel.Text = LabelForMaxWaypoints(v);
                int newContentH = RenderChunks(v);
                int target = newContentH + WindowVerticalChrome + entry.FooterReserve;
                target = Math.Min(WindowMaxHeight, Math.Max(WindowBaseHeight, target));
                entry.Window.MaxAllowedHeight = Math.Min(GW2appWindow.MaxResizeHeight, target);
                ResizeWindowAndPanel(entry, target);
            };

            slider.ValueChanged += (s, e) =>
            {
                int v = Math.Max(1, Math.Min(GW2app.MaxWaypointsPerMessage, (int)Math.Round(e.Value)));
                if (_maxWaypointsPerCopy != null && _maxWaypointsPerCopy.Value != v)
                    _maxWaypointsPerCopy.Value = v;
                entry.RerenderCopyChunks?.Invoke();
            };

            return contentHeight;
        }

        private static string LabelForMaxWaypoints(int max) =>
            "Max waypoints per message: " + (max == GW2app.MaxWaypointsPerMessage ? "Max" : max.ToString());

        // Mirrors ui/src/lib/components/locations/copy-locations.svelte:chunkLocations.
        // At maxPerMsg == MaxWaypointsPerMessage (15): chunk by GW2_CHAT_MAX_LENGTH chars.
        // Otherwise: chunk by entry count.
        private static List<List<string>> ChunkChatLinks(List<string> links, int maxPerMsg)
        {
            var result = new List<List<string>>();
            if (links == null || links.Count == 0) return result;

            var current = new List<string>();
            string currentJoined = "";
            foreach (var link in links)
            {
                string sep = currentJoined.Length > 0 ? " " : "";
                string test = currentJoined + sep + link;
                bool maxReached = maxPerMsg == GW2app.MaxWaypointsPerMessage
                    ? test.Length > GW2app.Gw2ChatMaxLength
                    : current.Count >= maxPerMsg;

                if (!maxReached)
                {
                    current.Add(link);
                    currentJoined = test;
                }
                else
                {
                    if (current.Count > 0) result.Add(current);
                    current = new List<string> { link };
                    currentJoined = link;
                }
            }
            if (current.Count > 0) result.Add(current);
            return result;
        }

        // Base character budgets at UiScale = 1.0. Lists with reset=NEVER get a more
        // generous limit since the right side of the title bar is empty (no countdown
        // overlay). The compact-mode bonus differs per text element because the font
        // swap is different (title: Font32 -> Font18, subtitle: Font16 -> Font14).
        private const int BaseTitleMaxChars = 12;
        private const int BaseTitleMaxCharsWithCountdown = BaseTitleMaxChars - 2;
        private const int BaseSubtitleMaxChars = 15;
        private const int BaseSubtitleMaxCharsWithCountdown = 13;
        private const float CompactTitleBonus    = 1.5f;  // per Font32 -> Font18 width ratio
        private const float CompactSubtitleBonus = 1.15f; // per Font16 -> Font14 width ratio
        // Subtitle char count scales 1.5x faster with UiScale than title; a longer
        // subtitle is more visually intrusive, so it should shrink faster as the window
        // gets smaller (and grow faster as it gets larger).
        private const float SubtitleScaleSpeed = 1.5f;

        // Window sizing.
        // Width = fixed-chrome + scaled image. Fixed = checkbox column (CheckboxColumnWidth)
        // + room on the right for the scrollbar. The image (DisplayWidth) is scaled by
        // UiScale; the scrollbar / checkbox sizes stay constant.
        private const int FixedRightChrome = 20; // scrollbar + right padding
        private int WindowWidth => CheckboxColumnWidth + DisplayWidth + FixedRightChrome;
        // Loot-bag lists have no checkboxes, so the checkbox column is dropped entirely.
        private int LootBagWindowWidth => DisplayWidth + FixedRightChrome;
        private int WindowWidthFor(ListDto list) => (list?.IsLootBag ?? false) ? LootBagWindowWidth : WindowWidth;
        private const int CheckboxSize = 22;
        private const int CheckboxLeftMargin = 8;  // space between panel edge and checkbox
        // Image stays at the same panel-x as before; reduce the visual gap between
        // checkbox and image by pushing the checkbox right.
        private const int CheckboxColumnWidth = 30; // image starts here in panel-x

        // Tint applied to entry images when an entry is pending a server confirmation
        // after the user toggled its checkbox. Multiplies image RGB → darkens.
        private static readonly Color PendingTint = new Color(110, 110, 110);
        // Heights scale linearly with UiScale (no fixed chrome to subtract).
        private const int BaseWindowMaxHeight = 440;
        private const int BaseWindowBaseHeight = 130;
        private int WindowMaxHeight => (int)Math.Round(BaseWindowMaxHeight * UiScale);
        private int WindowBaseHeight => (int)Math.Round(BaseWindowBaseHeight * UiScale); // shown while loading or empty
        private const int ImagesTopMargin = 0;      // space above first image inside the panel
        private const int ImagesBottomMargin = 10;  // matching space after last image
        // Approx vertical chrome per window: title bar + content top/bottom paddings + panel border.
        // Used to convert (sum of image heights) to (target window height).
        // Title bar (40) + top padding (6) - titlebar vertical offset (11) + bottom margin (5)
        // + panel ShowBorder insets top+bottom (7+7) = 54. Tuned empirically to 48 to remove
        // the extra space at the bottom of windows.
        private const int WindowVerticalChrome = 40;

        private string TitleFor(ListDto list)
        {
            int @base = string.IsNullOrEmpty(ResetCountdownFor(list)) ? BaseTitleMaxChars : BaseTitleMaxCharsWithCountdown;
            float multiplier = UiScale * (UiScale < 1.0f ? CompactTitleBonus : 1.0f);
            return Truncate(list?.Name ?? list?.Id ?? "", Math.Max(1, (int)Math.Round(@base * multiplier)));
        }

        // Subtitle char budget shrinks/grows faster than the title (SubtitleScaleSpeed
        // amplifies the deviation from 1.0) and uses a smaller compact bonus because
        // the subtitle font swap is gentler.
        private int SubtitleCharBudget(int @base)
        {
            float scale = 1.0f + (UiScale - 1.0f) * SubtitleScaleSpeed;
            float multiplier = scale * (UiScale < 1.0f ? CompactSubtitleBonus : 1.0f);
            return Math.Max(1, (int)Math.Round(@base * multiplier));
        }

        // Maps list.settings.color (string like "pink", "blue", ...) to the same hex
        // values the website uses (see ui/src/lib/listColors.ts). Default white = no
        // tint visible (the source is white).
        private static Color EmblemTintFor(ListDto list)
        {
            string colorName = null;
            try { colorName = list?.Settings?["color"]?.Value<string>(); }
            catch { /* malformed settings */ }
            if (string.IsNullOrEmpty(colorName)) return Color.White;

            switch (colorName.ToLowerInvariant())
            {
                case "pink":   return new Color(0xc2, 0x19, 0x5d);
                case "orange": return new Color(0xb0, 0x5c, 0x20);
                case "yellow": return new Color(0xcb, 0x8b, 0x00);
                case "green":  return new Color(0x1c, 0x91, 0x41);
                case "blue":   return new Color(0x58, 0x65, 0xf2);
                case "purple": return new Color(0x8f, 0x00, 0xfe);
                default:       return Color.White;
            }
        }

        private string SubtitleFor(ListDto list)
        {
            // Account-name display is opt-out via the "Show GW2 account name in list
            // header" setting.
            if (!ShowAccountName) return "";
            string acct = null;
            try { acct = list?.Settings?["gw2AccountName"]?.Value<string>(); }
            catch { /* malformed settings; fall through */ }
            int @base = string.IsNullOrEmpty(ResetCountdownFor(list)) ? BaseSubtitleMaxChars : BaseSubtitleMaxCharsWithCountdown;
            return TruncateAccountName(acct ?? "", SubtitleCharBudget(@base));
        }

        // Live countdown until the list's next reset, mirroring the website's
        // ui/src/lib/components/timers/countdown.svelte:
        //   - DAILY: reset at 00:00 UTC. Returns "Xh", "Xh YY", or "X mins" until the
        //     next midnight UTC.
        //   - WEEKLY: reset Monday 07:30 UTC. If <1 day away returns hours/minutes;
        //     otherwise "X days" (ceiling).
        //   - NEVER / unknown: returns null (no countdown shown).
        private static string ResetCountdownFor(ListDto list)
        {
            string reset = null;
            try { reset = list?.Settings?["reset"]?.Value<string>(); }
            catch { /* malformed settings */ }
            if (string.IsNullOrEmpty(reset)) return null;

            var now = DateTime.UtcNow;
            switch (reset.ToUpperInvariant())
            {
                case "DAILY":
                {
                    // Minutes until next midnight UTC. If we're exactly at midnight,
                    // diff is 0; same edge case as the web client (settles within a
                    // minute). The +1440%1440 dance keeps the value non-negative.
                    int nowMins = now.Hour * 60 + now.Minute;
                    int diffMinutes = (1440 - nowMins) % 1440;
                    return FormatCountdownDuration(diffMinutes);
                }
                case "WEEKLY":
                {
                    // Monday 07:30 UTC. Pin today at 07:30 UTC then advance to the next
                    // Monday occurrence.
                    var resetAt = new DateTime(now.Year, now.Month, now.Day, 7, 30, 0, DateTimeKind.Utc);
                    int dow = (int)resetAt.DayOfWeek; // Sunday=0, Monday=1, ...
                    if (dow != 1 || resetAt <= now)
                    {
                        int dayDiff = ((1 - dow + 7) % 7);
                        if (dayDiff == 0) dayDiff = 7;
                        resetAt = resetAt.AddDays(dayDiff);
                    }
                    var span = resetAt - now;
                    if (span.TotalDays < 1.0)
                    {
                        int nowMins = now.Hour * 60 + now.Minute;
                        int resetMins = resetAt.Hour * 60 + resetAt.Minute;
                        int diffMinutes = ((resetMins - nowMins) + 1440) % 1440;
                        return FormatCountdownDuration(diffMinutes);
                    }
                    int totalHours = (int)Math.Floor(span.TotalHours);
                    int days  = totalHours / 24;
                    int hours = totalHours % 24;
                    return hours > 0 ? days + "d " + hours + "h" : days + "d";
                }
                default:
                    return null; // NEVER or unknown
            }
        }

        // Compact duration: "5h30" / "5h" / "45m". Days are rendered as "3d" by the caller.
        private static string FormatCountdownDuration(int totalMinutes)
        {
            int hours = totalMinutes / 60;
            int mins  = totalMinutes % 60;
            if (hours > 0 && mins > 0) return hours + "h" + mins.ToString("D2");
            if (hours > 0)             return hours + "h";
            return mins + "m";
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length <= max) return s;
            return s.Substring(0, Math.Max(0, max - 1)) + "…";
        }

        // Truncate "<name>.<suffix>" by shortening only the name part and keeping the suffix
        // intact, e.g. "The Legendary Guy.1234" with max=15 -> "The Lege...1234".
        private static string TruncateAccountName(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";

            int dotIdx = s.LastIndexOf('.');
            if (dotIdx < 0) return Truncate(s, max);

            string suffix = s.Substring(dotIdx + 1);
            int allowedNameLen = max - 3 - suffix.Length; // 3 for "..."
            if (allowedNameLen <= 0) return Truncate(s, max);

            string namePart = s.Substring(0, dotIdx);
            if (namePart.Length <= allowedNameLen) return s;

            return namePart.Substring(0, allowedNameLen) + "..." + suffix;
        }

        private Point ScaledSize(Texture2D tex)
        {
            if (tex == null || tex.Width <= 0) return new Point(DisplayWidth, 0);
            int scaledHeight = (int)Math.Round(tex.Height * (double)DisplayWidth / tex.Width);
            return new Point(DisplayWidth, scaledHeight);
        }

        // Computes the current subscription set from open windows and re-sends if changed.
        // Persistence is independent (handled by AddPersisted / RemovePersisted hooked to
        // the window's Shown / Hidden events).
        private void UpdateSubscriptions()
        {
            if (_unloading) return;

            var subs = new HashSet<string>();
            foreach (var kvp in _listWindows)
            {
                if (kvp.Value.Window != null && kvp.Value.Window.Visible)
                    subs.Add(kvp.Key);
            }

            if (subs.SetEquals(_lastSubscribedIds)) return;

            // Lists newly entering the sub set start in loading state.
            foreach (var id in subs)
            {
                if (!_lastSubscribedIds.Contains(id))
                    MarkLoading(id);
            }
            // Lists leaving the sub set are no longer loading.
            foreach (var id in _lastSubscribedIds)
            {
                if (!subs.Contains(id))
                    MarkLoaded(id);
            }

            _lastSubscribedIds = subs;
            _ = SendSubscribeAsync(subs.ToList());
        }

        private void RestorePersistedOpenLists()
        {
            if (_persistedOpenListsJson == null)
            {
                Logger.Info("Restore: persisted setting is null; skipping.");
                return;
            }
            if (_catalog?.Lists == null)
            {
                Logger.Info("Restore: catalog has no lists yet; skipping.");
                return;
            }

            var raw = _persistedOpenListsJson.Value ?? "[]";
            List<string> persisted;
            try { persisted = JsonConvert.DeserializeObject<List<string>>(raw) ?? new List<string>(); }
            catch (Exception e)
            {
                Logger.Warn($"Restore: failed to parse persisted JSON ({raw}): {e.Message}");
                persisted = new List<string>();
            }

            var availableIds = new HashSet<string>(
                _catalog.Lists.Where(l => l != null && !string.IsNullOrEmpty(l.Id)).Select(l => l.Id));

            Logger.Info($"Restore: persisted=[{string.Join(",", persisted)}] available=[{string.Join(",", availableIds)}]");

            int opened = 0;
            foreach (var id in persisted)
            {
                if (availableIds.Contains(id) && !_listWindows.ContainsKey(id))
                {
                    OpenListWindow(id);
                    opened++;
                }
            }
            Logger.Info($"Restore: opened {opened} list window(s).");
        }

        // Notify the website that the user toggled a checkbox in-game. Marks the entry as
        // pending: the checkbox is disabled, the image is darkened, and a small spinner is
        // overlaid in the middle of the image. Pending state clears when the website sends
        // a fresh entry message for this position (handled in ApplyEntry).
        private void OnCheckboxToggled(Checkbox checkbox, string listId, int index)
        {
            // Loot-bag lists are read-only per protocol; checkboxes shouldn't render
            // for them, but guard here too.
            var list = _catalog?.Lists?.FirstOrDefault(l => l.Id == listId);
            if (list != null && list.IsLootBag) return;

            _pendingEntries.Add(EntryKey(listId, index));
            _ = SendSetEntryCompletedAsync(listId, index, checkbox.Checked);
            RefreshListWindow(listId);
        }

        private HashSet<string> LoadPersistedSet()
        {
            if (_persistedOpenListsJson == null) return new HashSet<string>();
            try
            {
                var list = JsonConvert.DeserializeObject<List<string>>(_persistedOpenListsJson.Value ?? "[]")
                           ?? new List<string>();
                return new HashSet<string>(list);
            }
            catch { return new HashSet<string>(); }
        }

        private void WritePersistedSet(HashSet<string> set)
        {
            if (_persistedOpenListsJson == null) return;
            try
            {
                var json = JsonConvert.SerializeObject(set.ToList());
                _persistedOpenListsJson.Value = json;
                Logger.Info($"Persisted open lists = {json}");
            }
            catch (Exception e) { Logger.Warn($"Failed to persist open lists: {e.Message}"); }
        }

        private void AddPersisted(string listId)
        {
            var set = LoadPersistedSet();
            if (set.Add(listId)) WritePersistedSet(set);
        }

        private void RemovePersisted(string listId)
        {
            var set = LoadPersistedSet();
            if (set.Remove(listId)) WritePersistedSet(set);
        }

        // Copy a chat link to the Windows clipboard via Blish's async clipboard helper, then
        // pop a screen notification telling the user to paste it in chat.
        private async void CopyChatLinkToClipboard(string chatLink)
        {
            if (string.IsNullOrEmpty(chatLink)) return;
            try
            {
                var ok = await ClipboardUtil.WindowsClipboardService.SetTextAsync(chatLink);
                if (!ok)
                {
                    Logger.Warn($"Clipboard set returned false for chat link.");
                    return;
                }
                ScreenNotification.ShowNotification(
                    "Copied! Paste into chat to use.",
                    ScreenNotification.NotificationType.Info);
            }
            catch (Exception e)
            {
                Logger.Warn($"Failed to copy chat link: {e.Message}");
            }
        }

        // Open an external URL in the user's default browser. Used for custom-entry `link`
        // values; the website restricts these to http(s) at submit time.
        private void OpenUrlInBrowser(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception e)
            {
                Logger.Warn($"Failed to open URL '{url}': {e.Message}");
            }
        }

        // Texture2D.FromStream loads PNG/JPEG with straight alpha, but Blish's SpriteBatch
        // expects premultiplied alpha. Without this fix, semi-transparent pixels render with
        // washed-out / wrong colors.
        private static void PremultiplyAlpha(Texture2D tex)
        {
            if (tex == null) return;
            var data = new Color[tex.Width * tex.Height];
            tex.GetData(data);
            for (int i = 0; i < data.Length; i++)
            {
                var c = data[i];
                if (c.A == 255) continue;
                if (c.A == 0) { data[i] = Color.Transparent; continue; }
                data[i] = new Color(
                    (byte)(c.R * c.A / 255),
                    (byte)(c.G * c.A / 255),
                    (byte)(c.B * c.A / 255),
                    c.A);
            }
            tex.SetData(data);
        }
    }
}
