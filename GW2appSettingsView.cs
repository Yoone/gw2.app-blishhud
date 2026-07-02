using System;
using System.Reflection;
using Blish_HUD;
using Blish_HUD.Controls;
using Blish_HUD.Graphics.UI;
using Blish_HUD.Settings;
using Microsoft.Xna.Framework;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;

namespace GW2app
{
    // Custom settings view: two columns (Appearance | Sizing) plus a brand-colored
    // "Open gw2.app/blish" button at the bottom. We bypass Blish's auto-generated
    // SettingsView entirely to control layout precisely (compactness, narrower
    // dropdown, custom slider readout, etc.).
    internal class GW2appSettingsView : View
    {
        private static readonly Logger Logger = Logger.GetLogger<GW2appSettingsView>();

        private const int LeftPad      = 15;
        private const int ColumnGap    = 30;
        private const int TitleTopPad  = 8;
        private const int SectionGap   = 28;   // vertical space below section title before content
        private const int DropdownWidth = 140; // ~50% of Blish's default
        private const int TopPad        = 8;

        private readonly SettingEntry<GW2appWindow.WindowTheme> _windowTheme;
        private readonly SettingEntry<bool> _showAccountName;
        private readonly SettingEntry<bool> _showCopyWaypointsButton;
        private readonly SettingEntry<int>  _uiScalePct;
        private readonly Action _onResetScale;

        // Subscriptions kept so we can detach in Unload.
        private EventHandler<ValueChangedEventArgs<int>>  _scaleChangedHandler;
        private EventHandler<ValueChangedEventArgs<bool>> _accountChangedHandler;
        private EventHandler<ValueChangedEventArgs<bool>> _copyBtnChangedHandler;
        private EventHandler<ValueChangedEventArgs<GW2appWindow.WindowTheme>> _themeChangedHandler;

        public GW2appSettingsView(
            SettingEntry<GW2appWindow.WindowTheme> windowTheme,
            SettingEntry<bool>                        showAccountName,
            SettingEntry<bool>                        showCopyWaypointsButton,
            SettingEntry<int>                         uiScalePct,
            Action                                    onResetScale)
        {
            _windowTheme             = windowTheme;
            _showAccountName         = showAccountName;
            _showCopyWaypointsButton = showCopyWaypointsButton;
            _uiScalePct              = uiScalePct;
            _onResetScale            = onResetScale;
        }

        protected override void Build(Container buildPanel)
        {
            int totalWidth  = Math.Max(420, buildPanel.Width - LeftPad * 2);
            int columnWidth = (totalWidth - ColumnGap) / 2;
            int leftX  = LeftPad;
            int rightX = LeftPad + columnWidth + ColumnGap;

            // ===================== APPEARANCE COLUMN =====================
            new Label
            {
                Text          = "Appearance",
                Font          = GameService.Content.DefaultFont18,
                TextColor     = Color.White,
                AutoSizeWidth = true,
                Location      = new Point(leftX, TitleTopPad),
                Parent        = buildPanel,
            };

            int ly = TitleTopPad + SectionGap;

            // Window background: text label on the left, dropdown anchored at right of column.
            new Label
            {
                Text          = "Window background",
                Font          = GameService.Content.DefaultFont14,
                TextColor     = Color.LightGray,
                AutoSizeWidth = true,
                Location      = new Point(leftX, ly + 7),
                Parent        = buildPanel,
            };
            var bgDropdown = new Dropdown
            {
                Width    = DropdownWidth,
                Location = new Point(leftX + columnWidth - DropdownWidth, ly),
                Parent   = buildPanel,
            };
            // Explicit order: Game (default), Dark, Black.
            var bgOrder = new[]
            {
                GW2appWindow.WindowTheme.Game,
                GW2appWindow.WindowTheme.GW2app,
                GW2appWindow.WindowTheme.Black,
            };
            foreach (var v in bgOrder)
                bgDropdown.Items.Add(EnumLabel(v));
            bgDropdown.SelectedItem = EnumLabel(_windowTheme.Value);
            bgDropdown.ValueChanged += (s, e) =>
            {
                if (TryParseEnum<GW2appWindow.WindowTheme>(e.CurrentValue, out var v))
                    _windowTheme.Value = v;
            };
            ly += 36;

            var acctCheckbox = new Checkbox
            {
                Text     = "Show GW2 account name in list header",
                Checked  = _showAccountName.Value,
                Location = new Point(leftX, ly + 4),
                Parent   = buildPanel,
            };
            acctCheckbox.CheckedChanged += (s, e) => _showAccountName.Value = e.Checked;
            ly += 30;

            var copyBtnCheckbox = new Checkbox
            {
                Text     = "Show \"Copy waypoints\" button in lists",
                Checked  = _showCopyWaypointsButton.Value,
                Location = new Point(leftX, ly + 4),
                Parent   = buildPanel,
            };
            copyBtnCheckbox.CheckedChanged += (s, e) => _showCopyWaypointsButton.Value = e.Checked;
            ly += 30;
            int leftBottom = ly;

            // ===================== SIZING COLUMN =====================
            new Label
            {
                Text          = "Sizing",
                Font          = GameService.Content.DefaultFont18,
                TextColor     = Color.White,
                AutoSizeWidth = true,
                Location      = new Point(rightX, TitleTopPad),
                Parent        = buildPanel,
            };

            int ry = TitleTopPad + SectionGap;

            var scaleLabel = new Label
            {
                Text          = FormatScale(_uiScalePct.Value),
                Font          = GameService.Content.DefaultFont14,
                TextColor     = Color.LightGray,
                AutoSizeWidth = true,
                Location      = new Point(rightX, ry),
                Parent        = buildPanel,
            };
            ry += 22;

            var trackBar = new TrackBar
            {
                MinValue  = 75,
                MaxValue  = 125,
                Value     = _uiScalePct.Value,
                SmallStep = true,
                Width     = columnWidth - 16,
                Height    = 16,
                Location  = new Point(rightX, ry),
                Parent    = buildPanel,
            };
            trackBar.ValueChanged += (s, e) => _uiScalePct.Value = (int)Math.Round(e.Value);
            ry += 24;

            var resetBtn = new StandardButton
            {
                Text     = "Reset to 100%",
                Width    = 130,
                Height   = 26,
                Location = new Point(rightX, ry + 6),
                Parent   = buildPanel,
            };
            resetBtn.Click += (s, e) => _onResetScale();
            int rightBottom = ry + 6 + 26;

            // ===================== BOTTOM: brand button =====================
            int bottomY = Math.Max(leftBottom, rightBottom) + 24;

            var brandColor = new Color(0xff, 0x7b, 0xc6);
            var brandHover = new Color(0xe5, 0x6e, 0xb2); // ~10% darker than brandColor
            var openSiteBtn = new Label
            {
                Text                = "Open gw2.app/blish",
                Font                = GameService.Content.DefaultFont16,
                TextColor           = new Color(0x14, 0x04, 0x0d),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Middle,
                Width               = 200,
                Height              = 30,
                Location            = new Point((buildPanel.Width - 200) / 2, bottomY),
                BackgroundColor     = brandColor,
                Parent              = buildPanel,
            };
            openSiteBtn.MouseEntered += (s, e) => openSiteBtn.BackgroundColor = brandHover;
            openSiteBtn.MouseLeft    += (s, e) => openSiteBtn.BackgroundColor = brandColor;
            openSiteBtn.Click += (s, e) =>
            {
                try { System.Diagnostics.Process.Start("https://gw2.app/blish"); }
                catch (Exception ex) { Logger.Warn(ex, "Failed to open browser."); }
            };

            // ===================== Live setting -> control sync =====================
            _scaleChangedHandler = (s, e) =>
            {
                scaleLabel.Text = FormatScale(e.NewValue);
                if ((int)Math.Round(trackBar.Value) != e.NewValue) trackBar.Value = e.NewValue;
            };
            _uiScalePct.SettingChanged += _scaleChangedHandler;

            _accountChangedHandler = (s, e) =>
            {
                if (acctCheckbox.Checked != e.NewValue) acctCheckbox.Checked = e.NewValue;
            };
            _showAccountName.SettingChanged += _accountChangedHandler;

            _copyBtnChangedHandler = (s, e) =>
            {
                if (copyBtnCheckbox.Checked != e.NewValue) copyBtnCheckbox.Checked = e.NewValue;
            };
            _showCopyWaypointsButton.SettingChanged += _copyBtnChangedHandler;

            _themeChangedHandler = (s, e) =>
            {
                var label = EnumLabel(e.NewValue);
                if (bgDropdown.SelectedItem != label) bgDropdown.SelectedItem = label;
            };
            _windowTheme.SettingChanged += _themeChangedHandler;
        }

        protected override void Unload()
        {
            if (_scaleChangedHandler   != null && _uiScalePct              != null) _uiScalePct.SettingChanged              -= _scaleChangedHandler;
            if (_accountChangedHandler != null && _showAccountName         != null) _showAccountName.SettingChanged         -= _accountChangedHandler;
            if (_copyBtnChangedHandler != null && _showCopyWaypointsButton != null) _showCopyWaypointsButton.SettingChanged -= _copyBtnChangedHandler;
            if (_themeChangedHandler   != null && _windowTheme             != null) _windowTheme.SettingChanged             -= _themeChangedHandler;
        }

        private static string FormatScale(int pct) => "List UI scale: " + pct + "%";

        // Reads the [Description] attribute on an enum value, falling back to the
        // member name. Lets us reuse the labels declared on WindowTheme without
        // a Humanizer dependency.
        private static string EnumLabel<T>(T value) where T : Enum
        {
            var member = typeof(T).GetMember(value.ToString());
            if (member.Length > 0)
            {
                var attr = member[0].GetCustomAttribute<DescriptionAttribute>();
                if (attr != null) return attr.Description;
            }
            return value.ToString();
        }

        // Inverse of EnumLabel: matches the dropdown selection back to an enum value.
        private static bool TryParseEnum<T>(string label, out T value) where T : struct, Enum
        {
            foreach (T v in Enum.GetValues(typeof(T)))
            {
                if (EnumLabel(v) == label) { value = v; return true; }
            }
            value = default;
            return false;
        }
    }
}
