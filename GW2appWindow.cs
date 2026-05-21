using System;
using System.ComponentModel;
using Blish_HUD;
using Blish_HUD.Content;
using Blish_HUD.Controls;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace GW2app
{
    // Custom WindowBase2 subclass with:
    //   - synthetic (or game-clipped) background texture sized to the window (no overflow)
    //   - auto-computed content region from window size
    //   - SetWindowHeight() to dynamically resize based on content
    //   - ESC closing disabled (lists must be closed via the X button)
    internal class GW2appWindow : WindowBase2
    {
        private static readonly Logger Logger = Logger.GetLogger<GW2appWindow>();

        // [Description] is read by Humanizer (used by Blish's enum dropdown) and is also
        // honored on the round-trip back, so the friendly labels persist correctly.
        public enum WindowTheme
        {
            [Description("Game (default)")] Game,
            [Description("GW2.app")]        GW2app,
            [Description("Black")]          Black,
        }

        private const int GameTextureAssetId = 155997;

        public const int ContentTopPadding = 6;
        public const int ContentBottomMargin = 5;

        private static readonly Color BlackBg = new Color(0, 0, 0, 215);
        // WindowTheme.GW2app: a dark blue-gray (#1c212b) matching the website's panel color.
        private static readonly Color DarkBg  = new Color(0x1c, 0x21, 0x2b, 215);

        // Multiplier applied to RGB components when sampling the GW2 frame texture for
        // the "Game texture" mode. <1 darkens; 1.0 = original. Alpha is left untouched.
        private const float GameTextureBrightness = 0.6f;

        private WindowTheme _theme;
        private Texture2D _ownedBackground;
        private int _width;
        private int _height;
        // Keeps a reference to the source AsyncTexture2D and a handler so we can rebuild
        // the synthetic background when the asset finishes async loading.
        private AsyncTexture2D _gameTextureSource;
        private EventHandler<ValueChangedEventArgs<Texture2D>> _gameTextureSwapHandler;

        // When compactTitle = true, we hide Blish's built-in DefaultFont32 title text and
        // paint our own using DefaultFont18. Title and Subtitle setters are shadowed so
        // base.Title stays empty (skipping Blish's title-text pass) while external
        // callers continue to read/write Title and Subtitle as usual.
        private bool _compactTitle;
        private string _customTitle = "";
        private string _customSubtitle = "";

        public GW2appWindow(int width, int height, WindowTheme theme = WindowTheme.Game, bool compactTitle = false)
        {
            _theme = theme;
            _width = width;
            _height = height;
            _compactTitle = compactTitle;

            // Clear Blish's default "No Title" placeholder up front when in compact
            // mode; otherwise it shows in the title bar (in DefaultFont32) until the
            // shadowed setter is called, which only writes to our custom storage.
            if (_compactTitle)
            {
                base.Title = "";
                base.Subtitle = "";
            }

            this.CanCloseWithEscape = false;

            // Single entry point for layout. See Recalculate().
            Recalculate();
        }

        // Resize the window vertically. Width is fixed.
        public void SetWindowHeight(int newHeight)
        {
            if (newHeight == _height) return;
            _height = newHeight;
            Recalculate();
        }

        // Resize both width and height. Used when the UI scale changes; avoids the
        // dispose/reopen path that would re-trigger subscribe and a server re-image.
        public void SetWindowSize(int newWidth, int newHeight)
        {
            if (newWidth == _width && newHeight == _height) return;
            _width = newWidth;
            _height = newHeight;
            Recalculate();
        }

        // ----- User resize support -----
        //
        // Set CanResize=true/SavesSize=true on the window externally; the user can then
        // drag the bottom-right handle. We lock the width via HandleWindowResize and
        // remember whether the size was set by the user (drag or a saved-size restore on
        // Show()) via _userResized so RefreshListWindow stops auto-fitting the height.

        public const int MinResizeHeight = 120;
        public const int MaxResizeHeight = 1200;

        private bool _userResized;
        // Set externally if the caller wants auto-fit to take over again (e.g. after
        // double-clicking the resize handle, or programmatically resetting).
        public bool UserResized
        {
            get => _userResized;
            set { _userResized = value; if (!value) UserPreferredHeight = null; }
        }

        // Saved height the user dragged to. RefreshListWindow uses this to keep the
        // window at the user's preferred size (capped by content) across re-renders.
        public int? UserPreferredHeight { get; private set; }

        // Upper cap for user-initiated resize: the window can't be dragged beyond this.
        // Set externally to the current "fit" height (content + chrome), so users can
        // never grow the window past what the entries need.
        public int MaxAllowedHeight { get; set; } = MaxResizeHeight;

        protected override Point HandleWindowResize(Point newSize)
        {
            // Width locked. Height clamped between MinResizeHeight and MaxAllowedHeight
            // (which is set per-refresh to the current content cap).
            int max = Math.Max(MinResizeHeight, Math.Min(MaxResizeHeight, MaxAllowedHeight));
            int h = Math.Max(MinResizeHeight, Math.Min(max, newSize.Y));
            return new Point(this.Size.X, h);
        }

        protected override void OnResized(ResizedEventArgs e)
        {
            base.OnResized(e);

            // Sync HEIGHT only. Width is treated as "intended": set by the constructor
            // or SetWindowSize, never by Blish. Otherwise SavesSize would carry over a
            // stale width across UI-scale changes (e.g. user was at 75%, restored at
            // 100%, window opens at the smaller width and clips entries on the right).
            _height = this.Size.Y;

            if (_recalculating) return;

            // External path: user drag, double-click reset, or SavesSize restore.
            _userResized = true;
            UserPreferredHeight = this.Size.Y;

            // Recalculate refreshes ratios/ContentRegion AND forces this.Size back to
            // (_width, _height) via ConstructWindow, so a saved width that doesn't
            // match the current scale's width is silently corrected.
            Recalculate();
        }

        // Toggle compact-title mode at runtime. Migrates the current Title/Subtitle
        // strings between Blish's storage (base) and our shadow storage so the visible
        // text doesn't disappear when switching modes.
        public void SetCompactTitle(bool compact)
        {
            if (compact == _compactTitle) return;
            if (compact)
            {
                _customTitle = base.Title ?? "";
                _customSubtitle = base.Subtitle ?? "";
                base.Title = "";
                base.Subtitle = "";
            }
            else
            {
                base.Title = _customTitle;
                base.Subtitle = _customSubtitle;
                _customTitle = "";
                _customSubtitle = "";
            }
            _compactTitle = compact;
        }

        // Switch background style at runtime (e.g. when the user changes the setting).
        public void SetWindowTheme(WindowTheme theme)
        {
            if (theme == _theme) return;
            _theme = theme;
            Recalculate();
        }

        // Single entry point for refreshing the window's geometry. Anything that wants
        // the layout to update (constructor, SetWindowHeight/SetWindowSize, user resize
        // via OnResized, background mode change, async game-texture swap) calls this.
        // Subscribers (typically the entry panel) should listen to LayoutRefreshed
        // rather than Blish's Resized event, because Resized only fires when Size
        // actually changes (and ConstructWindow's `Size = ...` is often a no-op).
        public event EventHandler LayoutRefreshed;
        private bool _recalculating;

        private void Recalculate()
        {
            if (_recalculating) return; // guard re-entry from ConstructWindow → Size → OnResized
            _recalculating = true;
            try
            {
                var newBg = CreateBackground(_width, _height);
                var oldBg = _ownedBackground;
                _ownedBackground = newBg;

                ConstructWindow(_ownedBackground, WindowRegionFor(_width, _height), ContentRegionFor(_width, _height), new Point(_width, _height));

                // ConstructWindow sets ContentRegion to the raw passed value (which
                // is taller than the actual content area). Re-apply the Height-Y-margin
                // formula OnResized would use, so ContentRegion is always consistent
                // even when ConstructWindow's `Size = ...` was a no-op.
                int marginX = this.WindowRegion.Right  - this.WindowRelativeContentRegion.Right;
                int marginY = this.WindowRegion.Bottom - this.WindowRelativeContentRegion.Bottom;
                this.ContentRegion = new Rectangle(
                    this.ContentRegion.X,
                    this.ContentRegion.Y,
                    this.Width  - this.ContentRegion.X - marginX,
                    this.Height - this.ContentRegion.Y - marginY);

                try { oldBg?.Dispose(); } catch { }

                LayoutRefreshed?.Invoke(this, EventArgs.Empty);
            }
            finally
            {
                _recalculating = false;
            }
        }

        private static Rectangle WindowRegionFor(int w, int h) => new Rectangle(0, 0, w, h);
        private static Rectangle ContentRegionFor(int w, int h) =>
            new Rectangle(0, ContentTopPadding, w, h - ContentBottomMargin);

        // ----- Background generation -----

        private Texture2D CreateBackground(int w, int h)
        {
            // Drop any prior subscription before we choose what to load now.
            DetachGameTextureSubscription();

            if (_theme == WindowTheme.Game)
            {
                var src = AsyncTexture2D.FromAssetId(GameTextureAssetId);
                if (src != null && src.HasSwapped)
                {
                    try { return CreateClippedFrom(src.Texture, w, h); }
                    catch (Exception e) { Logger.Warn($"Clip failed: {e.Message}"); }
                }

                // Asset hasn't finished loading. Subscribe so we rebuild when it does.
                if (src != null)
                {
                    _gameTextureSource = src;
                    _gameTextureSwapHandler = (s, e) => Recalculate();
                    src.TextureSwapped += _gameTextureSwapHandler;
                }
                // Fall through to a solid black bg until the asset arrives.
                return CreateSolidTexture(w, h, BlackBg);
            }
            return CreateSolidTexture(w, h, _theme == WindowTheme.GW2app ? DarkBg : BlackBg);
        }

        private void DetachGameTextureSubscription()
        {
            if (_gameTextureSource != null && _gameTextureSwapHandler != null)
            {
                try { _gameTextureSource.TextureSwapped -= _gameTextureSwapHandler; } catch { }
            }
            _gameTextureSource = null;
            _gameTextureSwapHandler = null;
        }

        private static Texture2D CreateSolidTexture(int w, int h, Color c)
        {
            var pixels = new Color[w * h];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = c;
            using (var gdc = GameService.Graphics.LendGraphicsDeviceContext())
            {
                var tex = new Texture2D(gdc.GraphicsDevice, w, h);
                tex.SetData(pixels);
                return tex;
            }
        }

        // Copy a w*h region from src, anchored at the asset's inner-frame top-left
        // (~(25, 26) for the standard GW2 window background) so the title-bar art
        // and left/top frame are preserved. The right/bottom edges of the source frame
        // are cropped away when our window is narrower/shorter than the asset.
        // Cached opaque bounding-box of the source asset's interior. The asset
        // has an alpha gradient at all four edges (the window's built-in fade);
        // sampling outside this box introduces transparency in the destination.
        // Found once via an alpha-channel scan and reused across resizes.
        private static bool      _srcOpaqueBoundsKnown;
        private static Rectangle _srcOpaqueBounds;
        private const byte       OpaqueAlphaThreshold = 240;

        private static Texture2D CreateClippedFrom(Texture2D src, int w, int h)
        {
            // Skip the leftmost / topmost decorative pixels of the source.
            const int srcX = 25;
            const int srcY = 26;

            int srcW = src.Width  - srcX;
            int srcH = src.Height - srcY;
            if (srcW <= 0 || srcH <= 0)
                return CreateSolidTexture(w, h, BlackBg);

            var srcPixels = new Color[srcW * srcH];
            src.GetData(0, new Rectangle(srcX, srcY, srcW, srcH), srcPixels, 0, srcW * srcH);

            if (!_srcOpaqueBoundsKnown)
            {
                _srcOpaqueBounds = FindOpaqueBounds(srcPixels, srcW, srcH);
                _srcOpaqueBoundsKnown = true;
            }
            // Sample only the opaque interior, not the full source rect.
            // Stretching this over the destination fills every pixel without
            // any of the source's edge alpha bleeding in.
            int opX = _srcOpaqueBounds.X;
            int opY = _srcOpaqueBounds.Y;
            int opW = _srcOpaqueBounds.Width;
            int opH = _srcOpaqueBounds.Height;

            var dstPixels = new Color[w * h];
            for (int dy = 0; dy < h; dy++)
            {
                int sy = opY + (int)((dy * (long)opH) / h);
                if (sy >= opY + opH) sy = opY + opH - 1;
                int srcRow = sy * srcW;
                int dstRow = dy * w;
                for (int dx = 0; dx < w; dx++)
                {
                    int sx = opX + (int)((dx * (long)opW) / w);
                    if (sx >= opX + opW) sx = opX + opW - 1;
                    var c = srcPixels[srcRow + sx];
                    dstPixels[dstRow + dx] = new Color(
                        (byte)(c.R * GameTextureBrightness),
                        (byte)(c.G * GameTextureBrightness),
                        (byte)(c.B * GameTextureBrightness),
                        c.A);
                }
            }

            using (var gdc = GameService.Graphics.LendGraphicsDeviceContext())
            {
                var tex = new Texture2D(gdc.GraphicsDevice, w, h);
                tex.SetData(dstPixels);
                return tex;
            }
        }

        // Bounding box of pixels with alpha >= OpaqueAlphaThreshold. Falls back
        // to the full input rectangle if none qualify (which would mean the
        // source has no opaque pixels at all, in which case there's nothing
        // smarter to do).
        private static Rectangle FindOpaqueBounds(Color[] pixels, int w, int h)
        {
            int top = h, bottom = -1, left = w, right = -1;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    if (pixels[row + x].A < OpaqueAlphaThreshold) continue;
                    if (y < top)    top = y;
                    if (y > bottom) bottom = y;
                    if (x < left)   left = x;
                    if (x > right)  right = x;
                }
            }
            if (bottom < top || right < left)
                return new Rectangle(0, 0, w, h);
            return new Rectangle(left, top, right - left + 1, bottom - top + 1);
        }

        // Set the window's emblem to a tinted copy of `source`. Each pixel's RGB is
        // multiplied by `tint` (alpha preserved). When scale < 1.0 the result is
        // bilinearly downsampled. Blish positions the emblem from its texture's
        // Width/Height, so a smaller texture renders smaller without further setup.
        // The window owns and disposes the resulting texture. Source images should be
        // white silhouettes with alpha for anti-aliasing.
        public void SetEmblemTinted(Texture2D source, Color tint, float scale = 1.0f)
        {
            if (source == null) return;
            var newEmblem = TintTexture(source, tint, scale);
            var old = _ownedEmblem;
            _ownedEmblem = newEmblem;
            this.Emblem = newEmblem;
            if (old != null) try { old.Dispose(); } catch { }
        }

        private Texture2D _ownedEmblem;

        private static Texture2D TintTexture(Texture2D src, Color tint, float scale)
        {
            int dstW = Math.Max(1, (int)Math.Round(src.Width  * scale));
            int dstH = Math.Max(1, (int)Math.Round(src.Height * scale));

            var srcPixels = new Color[src.Width * src.Height];
            src.GetData(srcPixels);
            var dstPixels = new Color[dstW * dstH];

            if (dstW == src.Width && dstH == src.Height)
            {
                // Same size: just tint in place.
                for (int i = 0; i < srcPixels.Length; i++)
                {
                    var c = srcPixels[i];
                    dstPixels[i] = new Color(
                        (byte)(c.R * tint.R / 255),
                        (byte)(c.G * tint.G / 255),
                        (byte)(c.B * tint.B / 255),
                        c.A);
                }
            }
            else
            {
                // Bilinear downsample + tint. For each dest pixel we sample 4 source
                // pixels and interpolate, then apply the tint multiplication.
                float invScaleX = (float)src.Width  / dstW;
                float invScaleY = (float)src.Height / dstH;
                for (int y = 0; y < dstH; y++)
                {
                    float sy = (y + 0.5f) * invScaleY - 0.5f;
                    int y0 = Math.Max(0, (int)Math.Floor(sy));
                    int y1 = Math.Min(src.Height - 1, y0 + 1);
                    float dy = sy - y0;
                    for (int x = 0; x < dstW; x++)
                    {
                        float sx = (x + 0.5f) * invScaleX - 0.5f;
                        int x0 = Math.Max(0, (int)Math.Floor(sx));
                        int x1 = Math.Min(src.Width - 1, x0 + 1);
                        float dx = sx - x0;
                        var c00 = srcPixels[y0 * src.Width + x0];
                        var c10 = srcPixels[y0 * src.Width + x1];
                        var c01 = srcPixels[y1 * src.Width + x0];
                        var c11 = srcPixels[y1 * src.Width + x1];
                        float wR = (1 - dx) * (1 - dy) * c00.R + dx * (1 - dy) * c10.R + (1 - dx) * dy * c01.R + dx * dy * c11.R;
                        float wG = (1 - dx) * (1 - dy) * c00.G + dx * (1 - dy) * c10.G + (1 - dx) * dy * c01.G + dx * dy * c11.G;
                        float wB = (1 - dx) * (1 - dy) * c00.B + dx * (1 - dy) * c10.B + (1 - dx) * dy * c01.B + dx * dy * c11.B;
                        float wA = (1 - dx) * (1 - dy) * c00.A + dx * (1 - dy) * c10.A + (1 - dx) * dy * c01.A + dx * dy * c11.A;
                        dstPixels[y * dstW + x] = new Color(
                            (byte)(wR * tint.R / 255),
                            (byte)(wG * tint.G / 255),
                            (byte)(wB * tint.B / 255),
                            (byte)wA);
                    }
                }
            }
            using (var gdc = GameService.Graphics.LendGraphicsDeviceContext())
            {
                var tex = new Texture2D(gdc.GraphicsDevice, dstW, dstH);
                tex.SetData(dstPixels);
                return tex;
            }
        }

        // ----- Title-bar reset overlay (recharge icon + countdown text) -----
        //
        // WindowBase2 doesn't expose a way to inject content next to its built-in
        // Subtitle text, so we paint the overlay ourselves in PaintAfterChildren,
        // which runs after the framework draws title/subtitle, so we sit on top.
        // Position is derived from TitleBarBounds (protected) and the same constants
        // WindowBase2 uses internally to compute the subtitle anchor.

        // Icon and font default to "normal" sizes; in compact-title mode (UiScale < 1)
        // they shrink to match the smaller subtitle font (Font14 vs Font16). Above
        // 100% scale we leave them at the normal size, no upward growth.
        private const int OverlayIconSize        = 16;
        private const int OverlayIconSizeCompact = 14;
        private const int OverlayGapAfterIcon = 4;
        // Right margin: distance from the window's right edge to the right edge of the
        // overlay block. Sized to clear the X button (~32 px wide + ~16 px margin).
        private const int OverlayRightMargin = 50;
        // Y position of the icon within the title bar. Bumped well below 0 because
        // the title bar texture extends both above and below y=0 and the visible bar
        // sits low in that range.
        private const int OverlayIconY = 13;

        private Texture2D _rechargeIcon;
        private string _countdownText;

        public void SetResetCountdownOverlay(Texture2D icon, string countdown)
        {
            _rechargeIcon = icon;
            _countdownText = countdown;
        }

        // Shadowed Title/Subtitle. In compact mode we hold the strings here and leave
        // base.Title / base.Subtitle empty so Blish skips its built-in title-text pass.
        // In normal mode we transparently forward to base.
        public new string Title
        {
            get => _compactTitle ? _customTitle : base.Title;
            set
            {
                if (_compactTitle) _customTitle = value ?? "";
                else base.Title = value;
            }
        }

        public new string Subtitle
        {
            get => _compactTitle ? _customSubtitle : base.Subtitle;
            set
            {
                if (_compactTitle) _customSubtitle = value ?? "";
                else base.Subtitle = value;
            }
        }

        public override void PaintBeforeChildren(SpriteBatch spriteBatch, Rectangle bounds)
        {
            base.PaintBeforeChildren(spriteBatch, bounds);

            // Blish's BackgroundDestinationBounds drift below the window when
            // ratios go stale across resizes. Our bg is opaque (the asset's
            // opaque interior nearest-neighbor scaled), so that drift became
            // visible as extra colour past the panel. Re-draw the bg over the
            // exact ContentRegion to cover the overflow with a perfectly
            // sized copy of the texture; the area outside ContentRegion is
            // already painted by base (titlebar, frame, corners).
            if (_ownedBackground != null && this.ContentRegion.Width > 0 && this.ContentRegion.Height > 0)
            {
                spriteBatch.DrawOnCtrl(this, _ownedBackground, this.ContentRegion);
            }
        }

        public override void PaintAfterChildren(SpriteBatch spriteBatch, Rectangle bounds)
        {
            base.PaintAfterChildren(spriteBatch, bounds);

            // Compact-mode title/subtitle: draw what Blish skipped.
            if (_compactTitle)
            {
                int leftBarX = this.TitleBarBounds.X - 2;   // STANDARD_LEFTTITLEBAR_HORIZONTAL_OFFSET
                int leftBarY = this.TitleBarBounds.Y - 11;  // STANDARD_TITLEBAR_VERTICAL_OFFSET
                const int titleAnchorX = 80;                // STANDARD_TITLEOFFSET (with emblem)

                int titleWidth = 0;
                if (!string.IsNullOrWhiteSpace(_customTitle))
                {
                    var titleFont = GameService.Content.DefaultFont18;
                    titleWidth = (int)titleFont.MeasureString(_customTitle).Width;
                    // Vertically aligned with where Blish's DefaultFont32 baseline would
                    // have sat. Bumped 10px below half-difference centering for visual balance.
                    int titleY = leftBarY + (30 - 22) / 2 + 12;
                    spriteBatch.DrawStringOnCtrl(
                        this, _customTitle, titleFont,
                        new Rectangle(leftBarX + titleAnchorX, titleY, titleWidth + 4, 28),
                        ContentService.Colors.ColonialWhite);
                }

                if (!string.IsNullOrWhiteSpace(_customSubtitle) && titleWidth > 0)
                {
                    var subFont = GameService.Content.DefaultFont14;
                    int subWidth = (int)subFont.MeasureString(_customSubtitle).Width;
                    int subX = leftBarX + titleAnchorX + titleWidth + 12;
                    // Anchor to the title's Y so the subtitle sits on the same baseline
                    // (small +offset because Font14 is shorter than Font18, leaving the
                    // top edge slightly lower for visual baseline alignment).
                    int titleY = leftBarY + (30 - 22) / 2 + 12;
                    int subY = titleY + 6;
                    spriteBatch.DrawStringOnCtrl(
                        this, _customSubtitle, subFont,
                        new Rectangle(subX, subY, subWidth + 4, 20),
                        Color.White);
                }
            }

            if (_rechargeIcon == null || string.IsNullOrEmpty(_countdownText)) return;

            // In compact-title mode, match the subtitle's smaller font + a smaller icon.
            int  iconSize = _compactTitle ? OverlayIconSizeCompact : OverlayIconSize;
            var  font     = _compactTitle ? GameService.Content.DefaultFont14
                                          : GameService.Content.DefaultFont16;
            int textWidth = (int)font.MeasureString(_countdownText).Width;
            int blockWidth = iconSize + OverlayGapAfterIcon + textWidth;

            int rightEdgeX = this.Size.X - OverlayRightMargin;
            int iconX = rightEdgeX - blockWidth;
            int iconY = OverlayIconY + (OverlayIconSize - iconSize) / 2; // keep vertical center

            spriteBatch.DrawOnCtrl(this, _rechargeIcon, new Rectangle(iconX, iconY, iconSize, iconSize));

            int textX = iconX + iconSize + OverlayGapAfterIcon;
            // Text rect taller than icon (font line height ~22). Top-align it with the
            // icon top so the text and icon visually share a row. Compact mode lifts
            // the text 2px more so it visually aligns with the smaller subtitle baseline.
            int textY = iconY - (_compactTitle ? 5 : 3);
            spriteBatch.DrawStringOnCtrl(this, _countdownText, font, new Rectangle(textX, textY, textWidth + 2, 22), Color.White);
        }

        protected override void DisposeControl()
        {
            DetachGameTextureSubscription();
            try { _ownedBackground?.Dispose(); } catch { }
            try { _ownedEmblem?.Dispose(); } catch { }
            base.DisposeControl();
        }
    }
}
