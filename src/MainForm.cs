using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace WindowsShow
{
    // Step 1: pick apps from the list of running apps. Step 2: set the hotkeys. Step 3: use them.
    internal sealed class MainForm : Form
    {
        private const int HideKeyColumn = 2;
        private const int ShowKeyColumn = 3;
        private const int AutoHideColumn = 4;
        private const int StartHiddenColumn = 5;

        private sealed class WindowRow
        {
            public TargetApp Target;
            public IntPtr Handle;
        }

        // One app and the windows listed under it.
        private sealed class AppGroup
        {
            public TargetApp App;
            public int VisibleWindows;
            public readonly List<WindowRow> Windows = new List<WindowRow>();
        }

        private readonly TrayApp _app;
        private readonly ListView _list;
        private readonly ImageList _icons;
        private readonly Dictionary<string, int> _iconIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly HotkeyBox _hideBox;
        private readonly HotkeyBox _showBox;
        private readonly Label _appLabel;
        private readonly HotkeyBox _appHideBox;
        private readonly HotkeyBox _appShowBox;
        private readonly Button _appHideClear;
        private readonly Button _appShowClear;
        private readonly NumericUpDown _appAutoHide;
        private readonly Label _appAutoHideUnit;
        private readonly CheckBox _appStartHidden;
        private readonly HotkeyBox _trayBox;
        private readonly NumericUpDown _trayMinutes;
        private readonly Label _hotkeyStatus;
        private readonly Timer _refreshTimer;
        private string _conflictMessage;
        private bool _populating;
        private bool _loadingPanel;

        public MainForm(TrayApp app)
        {
            _app = app;
            float scale;
            using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            Text = "WindowsShow";
            Icon = AppIcon.Get();
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(900, 720);
            MinimumSize = new Size(700, 640);

            // Step 1: the app list.
            var refresh = new Button { Text = "&Refresh list", AutoSize = true, Anchor = AnchorStyles.Right };
            refresh.Click += delegate { RefreshList(); };
            var header = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.Controls.Add(Heading("1.  Choose the apps"), 0, 0);
            header.Controls.Add(refresh, 1, 0);

            int iconSize = (int)Math.Round(16 * scale);
            _icons = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(iconSize, iconSize) };
            _icons.Images.Add(SystemIcons.Application);

            _list = new ListView
            {
                View = View.Details,
                CheckBoxes = true,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false,
                SmallImageList = _icons,
                Dock = DockStyle.Fill
            };
            _list.Columns.Add("App / window", (int)(250 * scale));
            _list.Columns.Add("Program", (int)(160 * scale));
            _list.Columns.Add("Hide key", (int)(110 * scale));
            _list.Columns.Add("Show key", (int)(110 * scale));
            _list.Columns.Add("Auto-hide", (int)(75 * scale));
            _list.Columns.Add("Start hidden", (int)(85 * scale));
            _list.Columns.Add("Status", (int)(95 * scale));
            _list.ItemChecked += OnItemChecked;
            _list.SelectedIndexChanged += delegate { if (!_populating) UpdateAppPanel(); };

            var listHint = Hint("Tick an app to hide all its windows, or tick single windows listed under it. "
                + "Ticked rows hide together; click a row to give it its own hotkeys below.");

            // Step 2: hotkeys. Row 1 is for all ticked apps, row 2 for the app selected in the list.
            _hideBox = MakeHotkeyBox(app.Settings.HideHotkey);
            _showBox = MakeHotkeyBox(app.Settings.ShowHotkey);
            _hideBox.HotkeyChanged += delegate { OnGlobalHotkeyChanged(_hideBox, _app.Settings.HideHotkey); };
            _showBox.HotkeyChanged += delegate { OnGlobalHotkeyChanged(_showBox, _app.Settings.ShowHotkey); };

            _appHideBox = MakeHotkeyBox(new Hotkey());
            _appShowBox = MakeHotkeyBox(new Hotkey());
            _appHideBox.HotkeyChanged += delegate { OnAppHotkeyChanged(_appHideBox, true); };
            _appShowBox.HotkeyChanged += delegate { OnAppHotkeyChanged(_appShowBox, false); };
            _appHideClear = ClearButton(_appHideBox);
            _appShowClear = ClearButton(_appShowBox);

            _appLabel = new Label
            {
                AutoSize = false,
                AutoEllipsis = true,
                Width = 220,
                Height = 23,
                TextAlign = ContentAlignment.MiddleLeft,
                Anchor = AnchorStyles.Left
            };

            var keys = new TableLayoutPanel { AutoSize = true, ColumnCount = 7, Margin = new Padding(0) };
            for (int i = 0; i < 7; i++) keys.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            keys.Controls.Add(new Label { Text = "All ticked apps:", AutoSize = false, Width = 220, Height = 23, TextAlign = ContentAlignment.MiddleLeft, Anchor = AnchorStyles.Left }, 0, 0);
            keys.Controls.Add(FieldLabel("Hide:", 0), 1, 0);
            keys.Controls.Add(_hideBox, 2, 0);
            keys.Controls.Add(ClearButton(_hideBox), 3, 0);
            keys.Controls.Add(FieldLabel("Show:", 20), 4, 0);
            keys.Controls.Add(_showBox, 5, 0);
            keys.Controls.Add(ClearButton(_showBox), 6, 0);
            keys.Controls.Add(_appLabel, 0, 1);
            keys.Controls.Add(FieldLabel("Hide:", 0), 1, 1);
            keys.Controls.Add(_appHideBox, 2, 1);
            keys.Controls.Add(_appHideClear, 3, 1);
            keys.Controls.Add(FieldLabel("Show:", 20), 4, 1);
            keys.Controls.Add(_appShowBox, 5, 1);
            keys.Controls.Add(_appShowClear, 6, 1);

            // The selected app's auto-hide time, on its own row under its hotkeys.
            _appAutoHide = new NumericUpDown { Minimum = 0, Maximum = 1440, Width = 70, Anchor = AnchorStyles.Left };
            _appAutoHide.ValueChanged += delegate { OnAutoHideChanged(); };
            _appAutoHideUnit = new Label { Text = "minutes without use  (0 = never)", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };
            var autoHideRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), WrapContents = false };
            autoHideRow.Controls.Add(_appAutoHide);
            autoHideRow.Controls.Add(_appAutoHideUnit);
            _appStartHidden = new CheckBox
            {
                Text = "Start hidden when the program is opened",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(24, 5, 3, 3)
            };
            _appStartHidden.CheckedChanged += delegate { OnStartHiddenChanged(); };
            autoHideRow.Controls.Add(_appStartHidden);
            keys.Controls.Add(FieldLabel("Auto-hide after:", 0), 0, 2);
            keys.Controls.Add(autoHideRow, 1, 2);
            keys.SetColumnSpan(autoHideRow, 6);

            // The tray lock: the icon's own hotkey and how long it stays before disappearing.
            _trayBox = MakeHotkeyBox(app.Settings.TrayHotkey);
            _trayBox.HotkeyChanged += delegate { OnTrayLockChanged(); };
            _trayMinutes = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 1440,
                Width = 70,
                Anchor = AnchorStyles.Left,
                Value = Math.Min(Math.Max(app.Settings.TrayHideMinutes, 0), 1440)
            };
            _trayMinutes.ValueChanged += delegate { OnTrayLockChanged(); };
            var trayMinutesRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), WrapContents = false };
            trayMinutesRow.Controls.Add(_trayMinutes);
            trayMinutesRow.Controls.Add(new Label { Text = "minutes  (0 = never)", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) });
            keys.Controls.Add(new Label { Text = "Tray icon:", AutoSize = false, Width = 220, Height = 23, TextAlign = ContentAlignment.MiddleLeft, Anchor = AnchorStyles.Left, Margin = new Padding(3, 10, 3, 3) }, 0, 3);
            keys.Controls.Add(FieldLabel("Show/hide:", 0), 1, 3);
            keys.Controls.Add(_trayBox, 2, 3);
            keys.Controls.Add(ClearButton(_trayBox), 3, 3);
            keys.Controls.Add(FieldLabel("Hide after:", 20), 4, 3);
            keys.Controls.Add(trayMinutesRow, 5, 3);
            keys.SetColumnSpan(trayMinutesRow, 2);

            var hotkeyHint = Hint("Click a box and press the keys, e.g. Ctrl+Alt+1. Use the same keys for Hide and Show to toggle.\n"
                + "The all-apps Show key brings back every hidden window.\n"
                + "Auto-hide hides the selected app once none of its windows has been in front and used (keyboard or mouse) for that long.\n"
                + "Start hidden hides a program's windows as it opens; its Show key brings them up. (Programs only, not Store apps or single windows.)\n"
                + "Tray icon: it disappears that many minutes after it appears, and the window hotkeys stop working until you press\n"
                + "its Show/hide key again (which also hides it at once). At sign-in it starts hidden.");
            _hotkeyStatus = new Label { AutoSize = true, Margin = new Padding(3, 4, 3, 0) };

            // Step 3: use them.
            var hideNow = new Button { Text = "Hide ticked apps now", AutoSize = true };
            hideNow.Click += delegate { _app.HideSelected(); };
            var showNow = new Button { Text = "Show all hidden apps now", AutoSize = true };
            showNow.Click += delegate { _app.ShowHidden(); };
            var exit = new Button { Text = "Exit (shows all hidden windows)", AutoSize = true, Margin = new Padding(24, 3, 3, 3) };
            exit.Click += delegate { _app.ExitApp(); };
            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            actions.Controls.Add(hideNow);
            actions.Controls.Add(showNow);
            actions.Controls.Add(exit);

            // Step 4: the calculator disguise.
            var codeBox = new TextBox { Width = 120, UseSystemPasswordChar = true, MaxLength = 12, Anchor = AnchorStyles.Left };
            codeBox.KeyPress += (s, e) => { if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar)) e.Handled = true; };
            var codeResult = new Label { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Color.ForestGreen };
            var changeCode = new Button { Text = "Change code", AutoSize = true, Anchor = AnchorStyles.Left };
            changeCode.Click += delegate
            {
                string code = codeBox.Text.Trim();
                if (code.Length == 0)
                {
                    codeResult.ForeColor = Color.Firebrick;
                    codeResult.Text = "Type the new code first (digits only).";
                    return;
                }
                _app.SetUnlockCode(code);
                codeBox.Clear();
                codeResult.ForeColor = Color.ForestGreen;
                codeResult.Text = "✔ Unlock code changed.";
            };
            var codeRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            codeRow.Controls.Add(FieldLabel("New unlock code:", 0));
            codeRow.Controls.Add(codeBox);
            codeRow.Controls.Add(changeCode);
            codeRow.Controls.Add(codeResult);
            var codeHint = Hint("This app opens as a calculator. Type your unlock code and press = (or Enter) to get to this window.");

            var startup = new CheckBox
            {
                Text = "Start WindowsShow automatically when I sign in to Windows",
                AutoSize = true,
                Checked = StartupManager.IsEnabled,
                Margin = new Padding(3, 10, 3, 0)
            };
            startup.CheckedChanged += delegate { _app.SetStartWithWindows(startup.Checked); };

            var footer = Hint("Closing this window locks it again. The app keeps running in the tray as \"Calculator\", "
                + "and your hotkeys keep working while the tray icon is showing.");

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14, 10, 14, 12) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddRow(root, header, false);
            AddRow(root, _list, true);
            AddRow(root, listHint, false);
            AddRow(root, Heading("2.  Set your hotkeys"), false);
            AddRow(root, keys, false);
            AddRow(root, hotkeyHint, false);
            AddRow(root, _hotkeyStatus, false);
            AddRow(root, Heading("3.  Hide and show"), false);
            AddRow(root, actions, false);
            AddRow(root, startup, false);
            AddRow(root, Heading("4.  Calculator disguise"), false);
            AddRow(root, codeHint, false);
            AddRow(root, codeRow, false);
            AddRow(root, footer, false);
            Controls.Add(root);
            ResumeLayout(true);

            // Hiding is asynchronous, so give windows a moment to settle before re-reading their state.
            _refreshTimer = new Timer { Interval = 350 };
            _refreshTimer.Tick += delegate { _refreshTimer.Stop(); RefreshList(); };

            _app.WindowsChanged += OnWindowsChanged;
            _app.HotkeysChanged += UpdateHotkeyStatus;
            Shown += delegate { ActiveControl = _list; };
            Activated += delegate
            {
                RefreshList();
                if (ActiveControl is HotkeyBox) _app.PauseHotkeys(true);
            };
            // Never leave hotkeys paused while the user is working in another app.
            Deactivate += delegate { _app.PauseHotkeys(false); };
            FormClosing += OnFormClosing;

            UpdateAppPanel();
            UpdateHotkeyStatus();
        }

        public void ShowAndActivate()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            RefreshList();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _app.WindowsChanged -= OnWindowsChanged;
                _app.HotkeysChanged -= UpdateHotkeyStatus;
                _refreshTimer.Dispose();
                _icons.Dispose();
            }
            base.Dispose(disposing);
        }

        private TargetApp SelectedApp
        {
            get { return _list.SelectedItems.Count == 0 ? null : (TargetApp)_list.SelectedItems[0].Tag; }
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            Hide();
            _app.MainWindowClosedToTray();
        }

        private void OnWindowsChanged()
        {
            if (!Visible) return;
            _refreshTimer.Stop();
            _refreshTimer.Start();
        }

        private void OnGlobalHotkeyChanged(HotkeyBox box, Hotkey previous)
        {
            string owner = _app.FindHotkeyOwner(box.Hotkey, true, null);
            if (owner != null)
            {
                RejectHotkey(box, previous, owner);
                return;
            }
            _conflictMessage = null;
            _app.SetHotkeys(_hideBox.Hotkey, _showBox.Hotkey);
        }

        private void OnAppHotkeyChanged(HotkeyBox box, bool isHideBox)
        {
            TargetApp selected = SelectedApp;
            if (selected == null) return;

            TargetApp saved = _app.Settings.FindTarget(selected.Key);
            string owner = _app.FindHotkeyOwner(box.Hotkey, false, selected.Key);
            if (owner != null)
            {
                Hotkey previous = saved == null ? new Hotkey() : (isHideBox ? saved.HideHotkey : saved.ShowHotkey);
                RejectHotkey(box, previous, owner);
                return;
            }

            _conflictMessage = null;
            _app.SetAppHotkeys(selected, _appHideBox.Hotkey, _appShowBox.Hotkey);
            UpdateKeyColumns(_list.SelectedItems[0]);
        }

        private void OnTrayLockChanged()
        {
            string owner = _app.FindHotkeyOwner(_trayBox.Hotkey, false, null, true);
            if (owner != null)
            {
                RejectHotkey(_trayBox, _app.Settings.TrayHotkey, owner);
                return;
            }
            _conflictMessage = null;
            _app.SetTrayLock(_trayBox.Hotkey, (int)_trayMinutes.Value);
        }

        private void OnStartHiddenChanged()
        {
            if (_loadingPanel) return;
            TargetApp selected = SelectedApp;
            if (selected == null || selected.Match != MatchMode.ProcessName) return;
            _app.SetAppStartHidden(selected, _appStartHidden.Checked);
            UpdateKeyColumns(_list.SelectedItems[0]);
            UpdateHotkeyStatus();
        }

        private void OnAutoHideChanged()
        {
            if (_loadingPanel) return;
            TargetApp selected = SelectedApp;
            if (selected == null) return;
            _app.SetAppAutoHide(selected, (int)_appAutoHide.Value);
            UpdateKeyColumns(_list.SelectedItems[0]);
            UpdateHotkeyStatus();
        }

        private void RejectHotkey(HotkeyBox box, Hotkey previous, string owner)
        {
            _conflictMessage = box.Hotkey + " is already used by " + owner + ".";
            box.Hotkey = previous;
            UpdateHotkeyStatus();
        }

        private void RefreshList()
        {
            AppSettings settings = _app.Settings;
            WindowFinder.ResolveWindowTargets(settings.Targets);

            var groups = new Dictionary<string, AppGroup>();
            var listedWindowKeys = new HashSet<string>();

            foreach (RunningApp running in WindowFinder.ListRunningApps())
            {
                var group = new AppGroup
                {
                    App = settings.FindTarget(running.Key) ?? running.ToTarget(),
                    VisibleWindows = running.Windows.Count
                };
                if (running.Match == MatchMode.ProcessName)
                {
                    foreach (RunningWindow window in running.Windows)
                    {
                        RunningWindow w = window;
                        TargetApp saved = settings.Targets.Find(t => t.Match == MatchMode.Window
                            && t.Handle == w.Handle.ToInt64() && t.ProcessId == w.ProcessId);
                        if (saved != null) listedWindowKeys.Add(saved.Key);
                        group.Windows.Add(new WindowRow { Target = saved ?? running.ToWindowTarget(w), Handle = w.Handle });
                    }
                }
                groups[running.Key] = group;
            }

            // Saved apps and windows that aren't on screen: either hidden, or closed.
            foreach (TargetApp target in settings.Targets)
            {
                if (target.Match != MatchMode.Window)
                {
                    if (!groups.ContainsKey(target.Key)) groups[target.Key] = new AppGroup { App = target };
                    continue;
                }
                if (listedWindowKeys.Contains(target.Key)) continue;

                string appKey = TargetApp.MakeKey(MatchMode.ProcessName, target.Value);
                AppGroup group;
                if (!groups.TryGetValue(appKey, out group))
                {
                    group = new AppGroup
                    {
                        App = settings.FindTarget(appKey) ?? new TargetApp
                        {
                            Match = MatchMode.ProcessName,
                            Value = target.Value,
                            DisplayName = string.IsNullOrEmpty(target.AppName) ? target.Value : target.AppName,
                            ExePath = target.ExePath
                        }
                    };
                    groups[appKey] = group;
                }
                group.Windows.Add(new WindowRow { Target = target, Handle = WindowFinder.LiveHandle(target) });
            }

            string selectedKey = SelectedApp == null ? null : SelectedApp.Key;

            _populating = true;
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (AppGroup group in groups.Values.OrderBy(g => g.App.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                _list.Items.Add(MakeAppItem(group));
                // A single-window app needs no separate window row, unless that window was saved on its own.
                bool listWindows = group.Windows.Count > 1 || group.Windows.Exists(w => settings.FindTarget(w.Target.Key) != null);
                if (!listWindows) continue;
                foreach (WindowRow window in group.Windows.OrderBy(w => w.Target.DisplayName, StringComparer.CurrentCultureIgnoreCase))
                {
                    _list.Items.Add(MakeWindowItem(window));
                }
            }
            foreach (ListViewItem item in _list.Items)
            {
                if (((TargetApp)item.Tag).Key == selectedKey) item.Selected = true;
            }
            _list.EndUpdate();
            _populating = false;
            UpdateAppPanel();
        }

        private ListViewItem MakeAppItem(AppGroup group)
        {
            TargetApp app = group.App;
            bool anyHidden = _app.Hider.HiddenCount(app.Key) > 0
                || group.Windows.Exists(w => _app.Hider.HiddenCount(w.Target.Key) > 0);
            string status = group.VisibleWindows > 0
                ? (anyHidden ? "Partly hidden" : "Visible")
                : (anyHidden ? "Hidden" : "Not running");

            string program = app.Match == MatchMode.ProcessName ? app.Value + ".exe" : "Store app";
            if (group.VisibleWindows > 1) program += "  (" + group.VisibleWindows + " windows)";
            return MakeItem(app, app.DisplayName, program, status, IconFor(app.ExePath), 0);
        }

        private ListViewItem MakeWindowItem(WindowRow window)
        {
            bool alive = window.Handle != IntPtr.Zero;
            string status;
            if (_app.Hider.HiddenCount(window.Target.Key) > 0 || (alive && !Native.IsWindowVisible(window.Handle))) status = "Hidden";
            else status = alive ? "Visible" : "Closed";

            int icon = alive ? IconForWindow(window.Handle, window.Target.ExePath) : IconFor(window.Target.ExePath);
            return MakeItem(window.Target, window.Target.DisplayName, "window", status, icon, 1);
        }

        private ListViewItem MakeItem(TargetApp target, string text, string program, string status, int imageIndex, int indent)
        {
            TargetApp saved = _app.Settings.FindTarget(target.Key);
            var item = new ListViewItem(text, imageIndex)
            {
                Tag = target,
                Checked = saved != null && saved.Selected,
                IndentCount = indent
            };
            item.SubItems.Add(program);
            item.SubItems.Add("");
            item.SubItems.Add("");
            item.SubItems.Add("");
            item.SubItems.Add("");
            item.SubItems.Add(status);
            if (status == "Not running" || status == "Closed") item.ForeColor = SystemColors.GrayText;
            UpdateKeyColumns(item);
            return item;
        }

        private void UpdateKeyColumns(ListViewItem item)
        {
            TargetApp saved = _app.Settings.FindTarget(((TargetApp)item.Tag).Key);
            string hide = "", show = "";
            if (saved != null && saved.IsToggle)
            {
                hide = show = saved.HideHotkey + " (toggle)";
            }
            else if (saved != null)
            {
                if (!saved.HideHotkey.IsEmpty) hide = saved.HideHotkey.ToString();
                if (!saved.ShowHotkey.IsEmpty) show = saved.ShowHotkey.ToString();
            }
            item.SubItems[HideKeyColumn].Text = hide;
            item.SubItems[ShowKeyColumn].Text = show;
            item.SubItems[AutoHideColumn].Text = saved != null && saved.HasAutoHide ? saved.AutoHideMinutes + " min" : "";
            item.SubItems[StartHiddenColumn].Text = saved != null && saved.StartHidden ? "Yes" : "";
        }

        // Loads the selected app's own hotkeys into the second row of hotkey boxes.
        private void UpdateAppPanel()
        {
            TargetApp selected = SelectedApp;
            bool enabled = selected != null;
            _appLabel.Text = enabled ? "Only " + selected.DisplayName + ":" : "Selected app: (click one above)";
            _appLabel.ForeColor = enabled ? SystemColors.ControlText : SystemColors.GrayText;
            foreach (Control c in new Control[] { _appHideBox, _appShowBox, _appHideClear, _appShowClear, _appAutoHide, _appAutoHideUnit })
            {
                c.Enabled = enabled;
            }

            TargetApp saved = enabled ? _app.Settings.FindTarget(selected.Key) : null;
            _appHideBox.Hotkey = saved == null ? new Hotkey() : saved.HideHotkey;
            _appShowBox.Hotkey = saved == null ? new Hotkey() : saved.ShowHotkey;

            // Start hidden works by program, so it isn't offered for Store apps or single windows.
            _appStartHidden.Enabled = enabled && selected.Match == MatchMode.ProcessName;

            _loadingPanel = true;
            _appAutoHide.Value = saved == null ? 0 : Math.Min(Math.Max(saved.AutoHideMinutes, 0), (int)_appAutoHide.Maximum);
            _appStartHidden.Checked = saved != null && saved.StartHidden;
            _loadingPanel = false;
        }

        private int IconFor(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return 0;
            int index;
            if (_iconIndex.TryGetValue(exePath, out index)) return index;
            index = 0;
            try
            {
                using (Icon icon = Icon.ExtractAssociatedIcon(exePath))
                {
                    if (icon != null)
                    {
                        _icons.Images.Add(icon);
                        index = _icons.Images.Count - 1;
                    }
                }
            }
            catch (Exception)
            {
                // inaccessible file: keep the generic icon
            }
            _iconIndex[exePath] = index;
            return index;
        }

        private int IconForWindow(IntPtr hWnd, string exePath)
        {
            IntPtr hIcon = Native.GetWindowIcon(hWnd);
            if (hIcon == IntPtr.Zero) return IconFor(exePath);

            string key = "hicon:" + hIcon.ToInt64();
            int index;
            if (_iconIndex.TryGetValue(key, out index)) return index;
            try
            {
                // The handle belongs to the other app; ImageList keeps its own copy.
                _icons.Images.Add(Icon.FromHandle(hIcon));
                index = _icons.Images.Count - 1;
            }
            catch (Exception)
            {
                index = IconFor(exePath);
            }
            _iconIndex[key] = index;
            return index;
        }

        private void OnItemChecked(object sender, ItemCheckedEventArgs e)
        {
            if (_populating) return;
            _app.SetTargetSelected((TargetApp)e.Item.Tag, e.Item.Checked);
        }

        private void UpdateHotkeyStatus()
        {
            AppSettings settings = _app.Settings;
            int appsWithKeys = settings.Targets.Count(t => t.HasHotkeys);

            if (_conflictMessage != null)
            {
                _hotkeyStatus.ForeColor = Color.Firebrick;
                _hotkeyStatus.Text = "⚠ " + _conflictMessage + " Choose different keys.";
            }
            else if (_app.HotkeyErrors.Count > 0)
            {
                _hotkeyStatus.ForeColor = Color.Firebrick;
                _hotkeyStatus.Text = "⚠ Already used by another program: " + string.Join(", ", _app.HotkeyErrors)
                    + ". Choose different keys.";
            }
            else if (_app.HotkeysPaused)
            {
                _hotkeyStatus.ForeColor = SystemColors.GrayText;
                _hotkeyStatus.Text = "Recording: press the keys you want.";
            }
            else if (settings.HideHotkey.IsEmpty && settings.ShowHotkey.IsEmpty && appsWithKeys == 0)
            {
                _hotkeyStatus.ForeColor = Color.Firebrick;
                _hotkeyStatus.Text = "No hotkeys set.";
            }
            else
            {
                var parts = new List<string>();
                if (settings.IsToggle) parts.Add(settings.HideHotkey + " hides and shows the ticked apps");
                else
                {
                    if (!settings.HideHotkey.IsEmpty) parts.Add(settings.HideHotkey + " hides the ticked apps");
                    if (!settings.ShowHotkey.IsEmpty) parts.Add(settings.ShowHotkey + " shows all");
                }
                if (appsWithKeys > 0) parts.Add(appsWithKeys + (appsWithKeys == 1 ? " app has" : " apps have") + " its own hotkeys");
                int autoHiding = settings.Targets.Count(t => t.HasAutoHide);
                if (autoHiding > 0) parts.Add(autoHiding + (autoHiding == 1 ? " app auto-hides" : " apps auto-hide"));
                int startHidden = settings.Targets.Count(t => t.StartHidden);
                if (startHidden > 0) parts.Add(startHidden + (startHidden == 1 ? " app starts" : " apps start") + " hidden");
                if (settings.TrayLockEnabled)
                {
                    parts.Add(settings.TrayHotkey + " shows/hides the tray icon, which disappears after "
                        + settings.TrayHideMinutes + " min");
                }
                _hotkeyStatus.ForeColor = Color.ForestGreen;
                _hotkeyStatus.Text = "✔ Active: " + string.Join("; ", parts) + ".";
            }
        }

        private HotkeyBox MakeHotkeyBox(Hotkey hotkey)
        {
            var box = new HotkeyBox { Width = 160, Hotkey = hotkey, Anchor = AnchorStyles.Left };
            box.Enter += delegate { _app.PauseHotkeys(true); };
            box.Leave += delegate { _app.PauseHotkeys(false); };
            return box;
        }

        private static void AddRow(TableLayoutPanel table, Control control, bool fill)
        {
            table.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            table.Controls.Add(control, 0, table.RowCount);
            table.RowCount++;
        }

        private Label Heading(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Font = new Font(Font.FontFamily, Font.Size + 1.5f, FontStyle.Bold),
                Margin = new Padding(0, 12, 3, 6)
            };
        }

        private static Label Hint(string text)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 4, 3, 0) };
        }

        private static Label FieldLabel(string text, int leftMargin)
        {
            return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(leftMargin, 3, 3, 3) };
        }

        private static Button ClearButton(HotkeyBox box)
        {
            var button = new Button { Text = "Clear", AutoSize = true, Anchor = AnchorStyles.Left };
            button.Click += delegate { box.ResetHotkey(); };
            return button;
        }
    }
}
