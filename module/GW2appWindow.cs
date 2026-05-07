using System;
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

        public enum BackgroundMode { Dark, GameTexture }

        private const int GameTextureAssetId = 155997;

        public const int ContentTopPadding = 6;
        public const int ContentBottomMargin = 5;

        private static readonly Color BlackBg = new Color(0, 0, 0, 215);

        // Multiplier applied to RGB components when sampling the GW2 frame texture for
        // the "Game texture" mode. <1 darkens; 1.0 = original. Alpha is left untouched.
        private const float GameTextureBrightness = 0.6f;

        private BackgroundMode _bgMode;
        private Texture2D _ownedBackground;
        private int _width;
        private int _height;
        // Keeps a reference to the source AsyncTexture2D and a handler so we can rebuild
        // the synthetic background when the asset finishes async loading.
        private AsyncTexture2D _gameTextureSource;
        private EventHandler<ValueChangedEventArgs<Texture2D>> _gameTextureSwapHandler;

        public GW2appWindow(int width, int height, BackgroundMode bgMode = BackgroundMode.Dark)
        {
            _bgMode = bgMode;
            _width = width;
            _height = height;

            this.CanCloseWithEscape = false;

            _ownedBackground = CreateBackground(_width, _height);
            ConstructWindow(_ownedBackground, WindowRegionFor(_width, _height), ContentRegionFor(_width, _height), new Point(_width, _height));
        }

        // Resize the window vertically. Width is fixed.
        public void SetWindowHeight(int newHeight)
        {
            if (newHeight == _height) return;
            _height = newHeight;
            RebuildBackground();
        }

        // Switch background style at runtime (e.g. when the user changes the setting).
        public void SetBackgroundMode(BackgroundMode mode)
        {
            if (mode == _bgMode) return;
            _bgMode = mode;
            RebuildBackground();
        }

        private void RebuildBackground()
        {
            var newBg = CreateBackground(_width, _height);
            var oldBg = _ownedBackground;
            _ownedBackground = newBg;

            ConstructWindow(_ownedBackground, WindowRegionFor(_width, _height), ContentRegionFor(_width, _height), new Point(_width, _height));

            try { oldBg?.Dispose(); } catch { }
        }

        // windowRegion.Y of 5 (instead of 0) shifts where Blish anchors the background draw
        // upward by ~5 px, so the bg fully covers the panel area (which itself is offset
        // ~5 px above where Blish places the bg by default).
        private static Rectangle WindowRegionFor(int w, int h) => new Rectangle(0, 5, w, h - 5);
        private static Rectangle ContentRegionFor(int w, int h) =>
            new Rectangle(0, ContentTopPadding, w, h - ContentBottomMargin);

        // ----- Background generation -----

        private Texture2D CreateBackground(int w, int h)
        {
            // Drop any prior subscription before we choose what to load now.
            DetachGameTextureSubscription();

            if (_bgMode == BackgroundMode.GameTexture)
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
                    _gameTextureSwapHandler = (s, e) => RebuildBackground();
                    src.TextureSwapped += _gameTextureSwapHandler;
                }
            }
            return CreateSolidTexture(w, h, BlackBg);
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
        private static Texture2D CreateClippedFrom(Texture2D src, int w, int h)
        {
            const int srcX = 25;
            const int srcY = 26;

            int copyW = Math.Min(w, src.Width - srcX);
            int copyH = Math.Min(h, src.Height - srcY);
            if (copyW <= 0 || copyH <= 0)
                return CreateSolidTexture(w, h, BlackBg);

            var srcPixels = new Color[copyW * copyH];
            src.GetData(0, new Rectangle(srcX, srcY, copyW, copyH), srcPixels, 0, copyW * copyH);

            var dstPixels = new Color[w * h];
            for (int y = 0; y < copyH; y++)
            {
                int srcRow = y * copyW;
                int dstRow = y * w;
                for (int x = 0; x < copyW; x++)
                {
                    var c = srcPixels[srcRow + x];
                    dstPixels[dstRow + x] = new Color(
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

        // Set the window's emblem to a tinted copy of `source`. Each pixel's RGB is
        // multiplied by `tint` (alpha preserved). The window owns and disposes the
        // resulting texture. Source images should be white silhouettes with alpha for
        // anti-aliasing — a white * tint multiplication reproduces the tint exactly.
        public void SetEmblemTinted(Texture2D source, Color tint)
        {
            if (source == null) return;
            var newEmblem = TintTexture(source, tint);
            var old = _ownedEmblem;
            _ownedEmblem = newEmblem;
            this.Emblem = newEmblem;
            if (old != null) try { old.Dispose(); } catch { }
        }

        private Texture2D _ownedEmblem;

        private static Texture2D TintTexture(Texture2D src, Color tint)
        {
            var pixels = new Color[src.Width * src.Height];
            src.GetData(pixels);
            for (int i = 0; i < pixels.Length; i++)
            {
                var c = pixels[i];
                pixels[i] = new Color(
                    (byte)(c.R * tint.R / 255),
                    (byte)(c.G * tint.G / 255),
                    (byte)(c.B * tint.B / 255),
                    c.A);
            }
            using (var gdc = GameService.Graphics.LendGraphicsDeviceContext())
            {
                var tex = new Texture2D(gdc.GraphicsDevice, src.Width, src.Height);
                tex.SetData(pixels);
                return tex;
            }
        }

        // ----- Title-bar reset overlay (recharge icon + countdown text) -----
        //
        // WindowBase2 doesn't expose a way to inject content next to its built-in
        // Subtitle text, so we paint the overlay ourselves in PaintAfterChildren —
        // which runs after the framework draws title/subtitle, so we sit on top.
        // Position is derived from TitleBarBounds (protected) and the same constants
        // WindowBase2 uses internally to compute the subtitle anchor.

        private const int OverlayIconSize = 16;
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

        public override void PaintAfterChildren(SpriteBatch spriteBatch, Rectangle bounds)
        {
            base.PaintAfterChildren(spriteBatch, bounds);

            if (_rechargeIcon == null || string.IsNullOrEmpty(_countdownText)) return;

            var font = GameService.Content.DefaultFont16;
            int textWidth = (int)font.MeasureString(_countdownText).Width;
            int blockWidth = OverlayIconSize + OverlayGapAfterIcon + textWidth;

            int rightEdgeX = this.Size.X - OverlayRightMargin;
            int iconX = rightEdgeX - blockWidth;
            int iconY = OverlayIconY;

            spriteBatch.DrawOnCtrl(this, _rechargeIcon, new Rectangle(iconX, iconY, OverlayIconSize, OverlayIconSize));

            int textX = iconX + OverlayIconSize + OverlayGapAfterIcon;
            // Text rect taller than icon (font line height ~22). Top-align it with the
            // icon top so the text and icon visually share a row.
            int textY = iconY - 3;
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
