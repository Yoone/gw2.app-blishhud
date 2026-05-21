using System;
using Blish_HUD;
using Blish_HUD.Controls;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace GW2app
{
    // Floating hovercard preview. Mirrors the website-side hover card for an
    // entry: dwell on a row long enough and the module sends `open_hover` to
    // the website, which streams back `hover_image` frames that we render
    // here. Sized to the image's native dimensions, scaled by the user's UI
    // scale setting.
    //
    // Subscription model (matches the protocol):
    //   - Dwell timeout       -> open_hover(listId, index)
    //   - Move to other entry -> open_hover(new) implicitly closes prior
    //   - Cursor leaves all   -> close_hover
    //
    // Display states:
    //   - No subscription            : panel hidden
    //   - Subscription with cache hit: show cached texture immediately
    //   - Subscription without cache : show small loading-spinner placeholder
    //   - New frame arrives          : swap to that texture
    //
    // Entrance animation: opacity 0->1 and scale 95%->100%, ease-out-cubic.
    // Mirrors the bits-ui / shadcn popover `fade-in + zoom-in-95` entrance.
    // Triggered when the *image* first appears within a hover session: either
    // the cache hit on open, or the first `hover_image` frame for a cache
    // miss. The spinner shows without animation (it's just a placeholder),
    // and subsequent texture swaps within the same session don't re-animate.
    internal static class HoverCard
    {
        private static readonly Logger Logger = Logger.GetLogger(typeof(HoverCard));

        private const int   HoverDelayMs        = 200;
        private const int   WindowHorizontalGap = 5;   // gap from the list window's edge to the card
        private const int   LoadingPlaceholder  = 64;  // px for the spinner-only state
        private const int   AnimDurationMs     = 180;  // entrance animation length
        private const float AnimStartScale     = 0.95f;

        // Wired from GW2app.LoadAsync. Stored statically so per-row Attach calls
        // don't need to thread the module reference through.
        private static Action<string, int>          _openCallback;
        private static Action                       _closeCallback;
        private static Func<float>                  _uiScaleGetter;
        // Display-pixels per captured-pixel for hover-card textures. Mirrors the
        // ratio entry images use (DisplayWidth / capturedWidth), so a hovercard
        // rendered at e.g. 600px CSS width displays at the same effective scale
        // as the 26rem-wide entry rows.
        private static Func<float>                  _captureScaleGetter;
        private static Func<string, int, Texture2D> _cachedTextureGetter;
        // Whether there is a live WebSocket connection to the website. When
        // false, opening a hover would send open_hover into the void and the
        // spinner would stick to the cursor until MouseLeft (no hover_image
        // can ever arrive). Suppress the open entirely in that case.
        private static Func<bool>                   _isConnectedGetter;

        // Cursor-side hover target (set in MouseEntered, cleared in MouseLeft).
        private static string   _hoveredListId;
        private static int      _hoveredIndex;
        private static bool     _hovered;
        private static DateTime _hoverStartedAt;
        // The hovered control itself, retained mostly for null-checking in
        // MouseLeft. We deliberately don't re-read its AbsoluteBounds in
        // UpdatePosition: the underlying Image gets disposed and recreated
        // on every RefreshListWindow, and the disposed reference returns
        // stale LocalBounds (Parent goes null, AbsoluteBounds falls back).
        private static Control  _hoveredTarget;
        // Vertical center of the entry, captured fresh at every MouseEntered
        // (when the just-mounted Image has correct AbsoluteBounds) and
        // reused for the rest of the session. Sidesteps the disposed-Image
        // stale-bounds problem: when the row image updates the card grows
        // symmetrically around this anchor without snapping around.
        private static int      _anchorEntryCenterY;
        // The owning list window, resolved once at MouseEntered (when the
        // parent chain is intact). Used to dock the card to the window's
        // left or right edge. The window survives list-panel rebuilds, so
        // its AbsoluteBounds stays live for the duration of the hover even
        // when _hoveredTarget gets disposed and recreated underneath.
        private static Control  _hoveredWindow;

        // Active server-side subscription (set after dwell timeout -> open_hover,
        // cleared after MouseLeft -> close_hover). May lag behind the hover target
        // while the user dwells on a new row.
        private static string _openListId;
        private static int    _openIndex;
        private static bool   _hasOpenSub;

        // Display:
        //   _panel        : container parented to SpriteScreen, positioned each tick
        //   _image        : the actual texture display (null while showing the spinner)
        //   _spinner      : the loading-state placeholder (null once an image is set)
        //   _currentTex   : last texture handed to SetImage for the active sub; we
        //                   re-render against this if Refresh* gets called later
        //   _logicalSize  : final/target size of the card (at scale 1.0); the
        //                   animation scales rendered size between AnimStartScale
        //                   and 1.0 of this.
        private static Panel           _panel;
        private static Image           _image;
        private static LoadingSpinner  _spinner;
        private static Texture2D       _currentTex;
        private static Point           _logicalSize;

        // Animation state. `_animPending` is true between OpenSubscription
        // and the first ShowTexture in that session, i.e. the spinner is
        // currently showing and is still waiting for the image-arrived
        // trigger. `_animPhase` says what (if anything) is currently running:
        // Entering = scale 95->100, fade 0->1; Exiting = scale 100->95,
        // fade 1->0; None = static (display final size and full opacity).
        private enum AnimPhase { None, Entering, Exiting }
        private static AnimPhase _animPhase;
        private static bool      _animPending;
        private static DateTime  _animStartedAt;

        // Double-buffer for incoming frames while a texture is already
        // displayed. We keep the previous frame on screen for one more render
        // pass, then swap on the next Tick. Without this, the texture +
        // panel-size changes happen in the same Update as the decode, which
        // can produce a visible flash (texture briefly rendered at the old
        // size, or a GPU upload not yet synced). The very first frame of a
        // session bypasses this and shows immediately so the entrance
        // animation isn't delayed by ~16 ms.
        private static Texture2D _pendingTex;
        private static string    _pendingListId;
        private static int       _pendingIndex;

        public static void Init(
            Action<string, int>          openCallback,
            Action                       closeCallback,
            Func<float>                  uiScaleGetter,
            Func<float>                  captureScaleGetter,
            Func<string, int, Texture2D> cachedTextureGetter,
            Func<bool>                   isConnectedGetter)
        {
            _openCallback        = openCallback;
            _closeCallback       = closeCallback;
            _uiScaleGetter       = uiScaleGetter;
            _captureScaleGetter  = captureScaleGetter;
            _cachedTextureGetter = cachedTextureGetter;
            _isConnectedGetter   = isConnectedGetter;
        }

        public static void Attach(Control hoverTarget, string listId, int index)
        {
            hoverTarget.MouseEntered += (s, e) =>
            {
                // Moving between entries: hide the card so the dwell timer
                // restarts. Blish can fire MouseEntered(new) before
                // MouseLeft(old), so we can't rely on the leave path alone.
                // No exit animation here: the next entry's card should appear
                // promptly after its own dwell, not after the previous one's
                // fade-out has finished.
                if (_hovered && (!Equals(_hoveredListId, listId) || _hoveredIndex != index))
                    HidePanel(animated: false);

                _hoveredListId  = listId;
                _hoveredIndex   = index;
                _hoveredTarget  = hoverTarget;
                // Snapshot live bounds now, while the control is alive and
                // its parent chain is intact. We won't re-read after this:
                // the next list rebuild may dispose this Image and return
                // stale local coords from AbsoluteBounds.
                var bounds = hoverTarget.AbsoluteBounds;
                _anchorEntryCenterY = bounds.Y + bounds.Height / 2;
                _hoveredWindow      = FindOwningWindow(hoverTarget);
                _hovered            = true;
                _hoverStartedAt     = DateTime.UtcNow;
            };
            hoverTarget.MouseLeft += (s, e) =>
            {
                if (!_hovered || !Equals(_hoveredListId, listId) || _hoveredIndex != index) return;
                _hovered       = false;
                _hoveredListId = null;
                _hoveredTarget = null;
                _hoveredWindow = null;
                HidePanel();
                if (_hasOpenSub)
                {
                    _hasOpenSub = false;
                    _openListId = null;
                    _currentTex = null;
                    try { _closeCallback?.Invoke(); } catch (Exception ex) { Logger.Warn($"close_hover send failed: {ex.Message}"); }
                }
            };
        }

        // Called from GW2app.Update once per frame.
        public static void Tick()
        {
            // First, drop the session if the entry we were hovering went
            // away (list window closed, list unsubscribed, etc.). Without
            // this the spinner can stay glued in place when no MouseLeft
            // fires, since the disposed control can't emit one.
            if ((_hovered || _hasOpenSub) && HoverTargetGone())
            {
                Teardown();
                return;
            }

            // Apply any deferred texture swap from a prior tick. Frames stashed
            // for a subscription that's since been superseded are dropped (the
            // texture is still in the website-side cache and a fresh frame
            // will arrive on the next open).
            if (_pendingTex != null)
            {
                if (_hasOpenSub && Equals(_pendingListId, _openListId) && _pendingIndex == _openIndex)
                {
                    ShowTexture(_pendingTex);
                }
                _pendingTex    = null;
                _pendingListId = null;
            }

            if (_hovered)
            {
                bool subscriptionMatchesHover = _hasOpenSub
                    && Equals(_openListId, _hoveredListId)
                    && _openIndex == _hoveredIndex;

                if (!subscriptionMatchesHover &&
                    (DateTime.UtcNow - _hoverStartedAt).TotalMilliseconds >= HoverDelayMs &&
                    (_isConnectedGetter == null || _isConnectedGetter()))
                {
                    OpenSubscription(_hoveredListId, _hoveredIndex);
                }
            }

            if (_panel != null && _panel.Visible)
            {
                ApplyAnimFrame();
                UpdatePosition();
            }
        }

        // Called from GW2app when a hover_image frame arrives. Only updates the
        // display if the frame matches the currently-open subscription; stale
        // frames are quietly ignored.
        public static void SetImage(string listId, int index, Texture2D tex)
        {
            if (!_hasOpenSub) return;
            if (!Equals(_openListId, listId) || _openIndex != index) return;

            if (_currentTex == null)
            {
                // First image of the session: show immediately so the entrance
                // animation isn't delayed by a frame. No flash risk since
                // nothing's displayed yet (just the spinner placeholder).
                ShowTexture(tex);
            }
            else
            {
                // Already displaying a texture: stash for next-tick swap so the
                // current image stays on screen for the rest of this frame.
                _pendingTex    = tex;
                _pendingListId = listId;
                _pendingIndex  = index;
            }
        }

        // Fully resets both the subscription and the cursor-side hover state.
        // Called from GW2app on client disconnect/supersession, and from the
        // in-Tick validity check that catches "the hovered entry's window
        // vanished" cases (list window closed by the user, list unsubscribed,
        // etc.). User has to move the cursor to re-engage, which re-fires
        // MouseEntered with live controls.
        public static void Teardown()
        {
            _hasOpenSub    = false;
            _openListId    = null;
            _currentTex    = null;
            _animPending   = false;
            _pendingTex    = null;
            _pendingListId = null;
            _hovered       = false;
            _hoveredTarget = null;
            _hoveredWindow = null;
            HidePanel(animated: false);
        }

        // Has the entry we were hovering effectively disappeared? True when
        // the cached owning window is no longer in the visible control tree
        // (closed by the user, removed from the subscription set, disposed).
        // Wrapped in try/catch because property access on a disposed Control
        // is implementation-defined and could throw.
        private static bool HoverTargetGone()
        {
            if (_hoveredWindow == null) return true;
            try { return _hoveredWindow.Parent == null || !_hoveredWindow.Visible; }
            catch { return true; }
        }

        // Re-applies the UI scale to the currently-displayed texture, if any.
        // Called from GW2app when the UiScale setting changes.
        public static void RefreshScale()
        {
            if (_panel != null && _panel.Visible && _currentTex != null)
            {
                ApplyImageSize(_currentTex);
                ApplyAnimFrame();
            }
        }

        public static void Dispose()
        {
            HidePanel();
            if (_panel != null) { try { _panel.Dispose(); } catch { } _panel = null; }
            _image = null;
            _spinner = null;
            _currentTex = null;
            _hovered = false;
            _hoveredTarget = null;
            _hoveredWindow = null;
            _hasOpenSub = false;
            _animPhase = AnimPhase.None;
            _animPending = false;
            _pendingTex = null;
            _pendingListId = null;
        }

        private static void OpenSubscription(string listId, int index)
        {
            _openListId  = listId;
            _openIndex   = index;
            _hasOpenSub  = true;
            _currentTex  = null;
            _animPending = true;  // arm the entrance animation for this session
            _animPhase   = AnimPhase.None;
            // Drop any prior deferred frame: it was for the superseded session
            // and shouldn't suddenly land here.
            _pendingTex    = null;
            _pendingListId = null;

            try { _openCallback?.Invoke(listId, index); }
            catch (Exception ex) { Logger.Warn($"open_hover send failed: {ex.Message}"); }

            // Instant display from cache (if any) while we wait for the first
            // live frame; otherwise show the spinner placeholder.
            var cached = _cachedTextureGetter?.Invoke(listId, index);
            if (cached != null) ShowTexture(cached);
            else                ShowSpinner();
        }

        private static void EnsurePanel()
        {
            if (_panel != null) return;
            _panel = new Panel
            {
                Parent = GameService.Graphics.SpriteScreen,
                // Above list windows so the card can spill across them.
                ZIndex = int.MaxValue / 2,
            };
        }

        private static void ShowSpinner()
        {
            EnsurePanel();
            // Tear down any previous image.
            if (_image != null) { try { _image.Dispose(); } catch { } _image = null; }

            int side = (int)Math.Round(LoadingPlaceholder * SafeUiScale);
            _logicalSize = new Point(side, side);

            if (_spinner == null)
            {
                _spinner = new LoadingSpinner { Parent = _panel, Location = Point.Zero };
            }

            _currentTex = null;
            // Spinner is just a placeholder: show it immediately at full size
            // and opacity. The entrance animation is reserved for when the
            // real image lands.
            _panel.Visible = true;
            ApplyAnimFrame();
            UpdatePosition();
        }

        private static void ShowTexture(Texture2D tex)
        {
            if (tex == null) { ShowSpinner(); return; }

            EnsurePanel();
            if (_spinner != null) { try { _spinner.Dispose(); } catch { } _spinner = null; }

            if (_image == null)
            {
                _image = new Image(tex) { Parent = _panel, Location = Point.Zero };
            }
            else
            {
                _image.Texture = tex;
            }

            _currentTex = tex;
            ApplyImageSize(tex);

            // First image-frame of this hover session: trigger the entrance
            // animation. Subsequent frames (live-updating content) swap in
            // place without re-popping.
            if (_animPending)
            {
                _animPending   = false;
                _animPhase     = AnimPhase.Entering;
                _animStartedAt = DateTime.UtcNow;
            }
            _panel.Visible = true;
            ApplyAnimFrame();
            UpdatePosition();
        }

        // Computes the target (unscaled) display size for `tex` and stores it as
        // the logical size. The actual rendered Size is set by ApplyAnimFrame.
        private static void ApplyImageSize(Texture2D tex)
        {
            float scale = _captureScaleGetter != null ? _captureScaleGetter() : SafeUiScale;
            int w = (int)Math.Round(tex.Width  * scale);
            int h = (int)Math.Round(tex.Height * scale);
            // Cap at the screen size so a huge card on a small monitor still fits.
            var screen = GameService.Graphics.SpriteScreen.Size;
            if (w > screen.X) w = screen.X;
            if (h > screen.Y) h = screen.Y;
            _logicalSize = new Point(w, h);
        }

        // Drives the entrance / exit animations. Both ease-out-cubic over
        // AnimDurationMs; Entering scales from AnimStartScale to 1.0 with
        // opacity 0 -> 1, Exiting mirrors that back to the start size and
        // opacity 0. When an Exiting animation completes we actually flip
        // the panel hidden.
        private static void ApplyAnimFrame()
        {
            int w, h;
            float opacity;
            if (_animPhase == AnimPhase.None)
            {
                w = _logicalSize.X;
                h = _logicalSize.Y;
                opacity = 1f;
            }
            else
            {
                double elapsed = (DateTime.UtcNow - _animStartedAt).TotalMilliseconds;
                float t = (float)Math.Min(1.0, elapsed / AnimDurationMs);
                // ease-out cubic, same curve for both directions.
                float eased = 1f - (float)Math.Pow(1.0 - t, 3.0);

                bool entering = _animPhase == AnimPhase.Entering;
                float progress = entering ? eased : (1f - eased);
                float scale    = AnimStartScale + (1f - AnimStartScale) * progress;
                opacity        = progress;
                w = (int)Math.Round(_logicalSize.X * scale);
                h = (int)Math.Round(_logicalSize.Y * scale);

                if (t >= 1f)
                {
                    if (entering)
                    {
                        _animPhase = AnimPhase.None;
                        opacity = 1f;
                        w = _logicalSize.X;
                        h = _logicalSize.Y;
                    }
                    else
                    {
                        _animPhase = AnimPhase.None;
                        if (_panel != null) _panel.Visible = false;
                        // Drop visual state so the next session starts clean.
                        if (_image   != null) { try { _image.Dispose();   } catch { } _image   = null; }
                        if (_spinner != null) { try { _spinner.Dispose(); } catch { } _spinner = null; }
                        _currentTex = null;
                        return; // panel hidden, nothing else to apply
                    }
                }
            }

            _panel.Size    = new Point(w, h);
            _panel.Opacity = opacity;
            if (_image   != null) _image.Size   = new Point(w, h);
            if (_spinner != null) _spinner.Size = new Point(w, h);
        }

        // Hides the panel with a fade-out + zoom-out animation when there's
        // something visible to animate; otherwise (no content, or already
        // exiting) just makes sure it's hidden immediately. `animated=false`
        // forces an immediate hide and is used for inter-entry transitions
        // where the new entry's card should appear without waiting for the
        // previous entry's exit to finish.
        private static void HidePanel(bool animated = true)
        {
            _animPending = false;
            _pendingTex = null;
            _pendingListId = null;

            if (_panel == null || !_panel.Visible)
            {
                _animPhase = AnimPhase.None;
                return;
            }

            if (!animated || _logicalSize.X == 0 || _logicalSize.Y == 0)
            {
                _panel.Visible = false;
                _animPhase = AnimPhase.None;
                if (_image   != null) { try { _image.Dispose();   } catch { } _image   = null; }
                if (_spinner != null) { try { _spinner.Dispose(); } catch { } _spinner = null; }
                _currentTex = null;
                return;
            }

            if (_animPhase == AnimPhase.Exiting) return; // already exiting
            _animPhase = AnimPhase.Exiting;
            _animStartedAt = DateTime.UtcNow;
        }

        private static void UpdatePosition()
        {
            if (_panel == null || _hoveredTarget == null) return;
            var screen = GameService.Graphics.SpriteScreen.Size;
            int w = _panel.Size.X;
            int h = _panel.Size.Y;

            // Vertical: anchored to the entry-center captured at MouseEntered.
            // As _panel.Size.Y changes with new images, the card grows
            // symmetrically around this fixed point.
            int y = _anchorEntryCenterY - h / 2;

            // Horizontal: docked to the cached owning window's edge. Reading
            // bounds from the (live) window directly rather than re-walking
            // from _hoveredTarget, which can be a disposed Image after a
            // list-panel rebuild and would return stale local coords.
            var windowBounds = _hoveredWindow != null
                ? _hoveredWindow.AbsoluteBounds
                : new Rectangle(0, _anchorEntryCenterY, 0, 0);
            // Side choice depends ONLY on which side of the window has more
            // open space, so it stays stable as the card grows or shrinks
            // with incoming frames. If we instead flipped on "card overflows
            // the screen", a wider new image could flip the side mid-session
            // and snap the card across the window.
            int rightSpace = screen.X - windowBounds.Right;
            int leftSpace  = windowBounds.Left;
            int x = rightSpace >= leftSpace
                ? windowBounds.Right + WindowHorizontalGap
                : windowBounds.Left  - WindowHorizontalGap - w;

            // Final clamp in case the window itself sits near the screen edge.
            x = Math.Max(0, Math.Min(screen.X - w, x));
            y = Math.Max(0, Math.Min(screen.Y - h, y));

            _panel.Location = new Point(x, y);
        }

        // Walks up the parent chain until just below SpriteScreen and returns
        // that ancestor. For our list windows that resolves to the
        // GW2appWindow itself, which is what we want to dock against.
        private static Control FindOwningWindow(Control c)
        {
            var screen = GameService.Graphics.SpriteScreen;
            var cur = c;
            while (cur != null && cur.Parent != null && cur.Parent != screen)
                cur = cur.Parent;
            return cur;
        }

        private static float SafeUiScale => _uiScaleGetter != null ? _uiScaleGetter() : 1.0f;
    }
}
