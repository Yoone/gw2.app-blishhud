using System;
using System.Reflection;
using Blish_HUD;
using Blish_HUD.Content;
using Blish_HUD.Controls;
using Microsoft.Xna.Framework;

namespace GW2app
{
    // Single source of truth for the standard "action button" used inside list
    // windows (Hide completed, Copy waypoints, Back, etc.). The exact control type
    // depends on the active window theme:
    //
    //   Game            -> StandardButton (native GW2-look, with compact-mode font swap)
    //   GW2app / Black  -> Label-as-button with custom bg/text colors and a hover state
    //
    // Width/height scale with UiScale; callers should re-create the button on
    // theme changes so the right control type is used.
    internal static class ActionButton
    {
        // Base dimensions at UiScale = 1.0.
        public const int BaseWidth  = 180;
        public const int BaseHeight = 26;

        public static int WidthFor(float uiScale)  => (int)Math.Round(BaseWidth  * uiScale);
        public static int HeightFor(float uiScale) => (int)Math.Round(BaseHeight * uiScale);

        // Label-as-button palette (used for GW2app / Black themes).
        public static readonly Color LabelBg        = new Color(0x42, 0x44, 0x59);
        public static readonly Color LabelBgHover   = new Color(0xbd, 0x94, 0xfa);
        public static readonly Color LabelText      = new Color(0xfa, 0xfa, 0xfa);
        public static readonly Color LabelTextHover = new Color(0x0d, 0x09, 0x16);

        // Reflection: StandardButton inherits LabelBase whose _font field is
        // protected with no public setter. We swap fonts for compact mode.
        private static readonly FieldInfo _labelBaseFontField =
            typeof(LabelBase).GetField("_font", BindingFlags.NonPublic | BindingFlags.Instance);

        public static Control Create(
            Container                   parent,
            string                      text,
            Point                       location,
            int                         width,
            int                         height,
            float                       uiScale,
            GW2appWindow.WindowTheme    theme,
            Action                      onClick)
        {
            if (theme == GW2appWindow.WindowTheme.Game)
            {
                var btn = new StandardButton()
                {
                    Text     = text,
                    Width    = width,
                    Height   = height,
                    Location = location,
                    Parent   = parent,
                };
                if (uiScale < 1.0f && _labelBaseFontField != null)
                {
                    try { _labelBaseFontField.SetValue(btn, GameService.Content.DefaultFont12); } catch { }
                    // Reflection bypasses Blish's SetProperty, so the layout was
                    // measured against the old font. Force a re-layout.
                    btn.Invalidate();
                }
                btn.Click += (s, e) => onClick();
                return btn;
            }

            // GW2app / Black: a Label with explicit colors. Label exposes TextColor
            // (LabelBase) and BackgroundColor (Control) as real settable properties,
            // so no reflection is needed. Hover swaps to a brighter accent.
            var lbl = new Label()
            {
                Text                = text,
                Font                = uiScale < 1.0f ? GameService.Content.DefaultFont12 : GameService.Content.DefaultFont14,
                TextColor           = LabelText,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Middle,
                Width               = width,
                Height              = height,
                Location            = location,
                BackgroundColor     = LabelBg,
                Parent              = parent,
            };
            lbl.MouseEntered += (s, e) => { lbl.BackgroundColor = LabelBgHover; lbl.TextColor = LabelTextHover; };
            lbl.MouseLeft    += (s, e) => { lbl.BackgroundColor = LabelBg;       lbl.TextColor = LabelText;      };
            lbl.Click        += (s, e) => onClick();
            return lbl;
        }
    }
}
