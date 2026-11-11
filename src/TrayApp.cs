using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace WindowsShow
{
    // Hidden top-level window that receives WM_HOTKEY and the "open window" signal from a second instance.
    internal sealed class MessageWindow : NativeWindow, IDisposable
    {
        public const int WM_OPEN_SETTINGS = Native.WM_APP + 1;

        public event Action<int> HotkeyPressed;
        public event Action OpenSettingsRequested;

        public MessageWindow()
        {
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                Action<int> handler = HotkeyPressed;
                if (handler != null) handler(m.WParam.ToInt32());
                return;
            }
            if (m.Msg == WM_OPEN_SETTINGS)
            {
                Action handler = OpenSettingsRequested;
                if (handler != null) handler();
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            DestroyHandle();
        }
    }

    // Owns the settings, the hotkeys and the hidden windows; the main window is just a view onto it.
    internal sealed class TrayApp : ApplicationContext
    {
        private enum HotkeyAction { Hide, Show, Toggle, TrayToggle }

        private sealed class Binding
        {
            public string TargetKey; // null: the all-apps hotkeys
            public HotkeyAction Action;
        }

        private readonly MessageWindow _window;
        private readonly NotifyIcon _tray;
        private readonly WindowHider _hider;
        private readonly AutoHideMonitor _autoHide;
        private readonly LaunchHider _launchHider;
        private readonly AppSettings _settings;
        private readonly RegisteredWaitHandle _openWait;
        private readonly Dictionary<int, Binding> _bindings = new Dictionary<int, Binding>();
        private readonly List<string> _hotkeyErrors = new List<string>();
        private MainForm _mainForm;
        private CalculatorForm _calculator;
        private bool _hotkeysPaused;

        // Tray lock: while the icon is hidden, only the tray hotkey is registered.
        private readonly System.Windows.Forms.Timer _trayTimer = new System.Windows.Forms.Timer();
        private bool _trayHidden;
        private bool _lockPending; // time ran out while the main window was open

        public event Action HotkeysChanged;
        public event Action WindowsChanged;

        public TrayApp(EventWaitHandle openWindowEvent, bool startInTray)
        {
            _settings = AppSettings.Load();
            _hider = new WindowHider();
            _autoHide = new AutoHideMonitor(_settings, _hider, OnWindowsChanged);

            _window = new MessageWindow();
            _window.HotkeyPressed += OnHotkey;
            _window.OpenSettingsRequested += OpenCalculator;

            var menu = new ContextMenuStrip();
            menu.Opening += delegate { PopulateMenu(menu); };
            PopulateMenu(menu);

            _tray = new NotifyIcon { Icon = AppIcon.Get(), ContextMenuStrip = menu, Visible = false };
            _trayTimer.Tick += delegate { _trayTimer.Stop(); OnTrayTimeUp(); };
            _launchHider = new LaunchHider(_settings, _hider, OnWindowsChanged);
            _tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) OpenCalculator(); };

            IntPtr hWnd = _window.Handle;
            _openWait = ThreadPool.RegisterWaitForSingleObject(openWindowEvent,
                delegate { Native.PostMessage(hWnd, MessageWindow.WM_OPEN_SETTINGS, IntPtr.Zero, IntPtr.Zero); },
                null, Timeout.Infinite, false);

            // Started at sign-in: begin locked. Started by hand: the owner is here, so begin unlocked.
            _trayHidden = startInTray && _settings.TrayLockEnabled;
            RegisterHotkeys();
            ApplyTrayState();
            UpdateTrayText();
            if (!startInTray) OpenCalculator();
        }

        public AppSettings Settings { get { return _settings; } }
        public WindowHider Hider { get { return _hider; } }
        public IList<string> HotkeyErrors { get { return _hotkeyErrors; } }
        public bool HotkeysPaused { get { return _hotkeysPaused; } }

        public void HideSelected()
        {
            // Stays silent when there's nothing to do: notifications would give away what this app is.
            List<TargetApp> ticked = _settings.Targets.FindAll(t => t.Selected);
            if (ticked.Count > 0) HideTargets(ticked);
        }

        public void HideApp(TargetApp target)
        {
            HideTargets(new List<TargetApp> { target });
        }

        public void ShowHidden()
        {
            _launchHider.ReleasedAll();
            _hider.ShowAll();
            OnWindowsChanged();
        }

        public void ShowApp(TargetApp target)
        {
            _launchHider.Released(target.Key);
            _hider.Show(target.Key);
            OnWindowsChanged();
        }

        private void HideTargets(List<TargetApp> targets)
        {
            WindowFinder.ResolveWindowTargets(_settings.Targets);
            List<string> denied;
            _hider.Hide(targets, out denied);
            if (denied.Count > 0)
            {
                Notify(string.Join(", ", denied) + " runs as administrator and can't be hidden. "
                    + "Install with install.ps1 to fix this.", ToolTipIcon.Warning);
            }
            OnWindowsChanged();
        }

        // Called by the calculator when the unlock code is entered.
        public void Unlock()
        {
            if (_calculator != null) _calculator.HideAndReset();
            if (_trayHidden) ShowTray(); // the unlock code proves it's the owner
            OpenMainWindow();
        }

        public bool TrayHidden { get { return _trayHidden; } }

        public void SetTrayLock(Hotkey hotkey, int minutes)
        {
            _settings.TrayHotkey = hotkey;
            _settings.TrayHideMinutes = Math.Max(0, minutes);
            Save();
            if (!_settings.TrayLockEnabled)
            {
                _trayHidden = false;
                _lockPending = false;
            }
            RegisterHotkeys();
            ApplyTrayState(); // restarts the countdown with the new time
        }

        private void ShowTray()
        {
            _trayHidden = false;
            _lockPending = false;
            RegisterHotkeys();
            ApplyTrayState();
        }

        // Hides the icon and turns off the window hotkeys. Windows stay as they are.
        private void HideTray()
        {
            _lockPending = false;
            if (!_settings.TrayLockEnabled) return;
            if (_calculator != null && _calculator.Visible) _calculator.HideAndReset();
            if (_mainForm != null && _mainForm.Visible) _mainForm.Hide();
            _hotkeysPaused = false;
            _trayHidden = true;
            RegisterHotkeys();
            ApplyTrayState();
        }

        private void OnTrayTimeUp()
        {
            // Don't pull the window away while it's being used; lock as soon as it's closed.
            if (_mainForm != null && _mainForm.Visible)
            {
                _lockPending = true;
                return;
            }
            HideTray();
        }

        private void ApplyTrayState()
        {
            _tray.Visible = !_trayHidden;
            _trayTimer.Stop();
            if (!_trayHidden && _settings.TrayLockEnabled)
            {
                _trayTimer.Interval = _settings.TrayHideMinutes * 60000;
                _trayTimer.Start();
            }
        }

        public void SetUnlockCode(string code)
        {
            _settings.UnlockCodeHash = AppSettings.HashUnlockCode(code);
            Save();
        }

        public void ExitApp()
        {
            ExitThread();
        }

        public void SetTargetSelected(TargetApp target, bool selected)
        {
            TargetApp existing = _settings.FindTarget(target.Key);
            if (selected)
            {
                if (existing == null)
                {
                    target.Selected = true;
                    _settings.Targets.Add(target);
                }
                else
                {
                    existing.Selected = true;
                }
            }
            else if (existing != null)
            {
                existing.Selected = false;
                RemoveIfUnused(existing);
            }
            Save();
            UpdateTrayText();
        }

        // The all-apps hotkeys.
        public void SetHotkeys(Hotkey hide, Hotkey show)
        {
            _settings.HideHotkey = hide;
            _settings.ShowHotkey = show;
            Save();
            RegisterHotkeys();
        }

        // One app's own hotkeys. Giving an unticked app hotkeys keeps it unticked.
        public void SetAppHotkeys(TargetApp target, Hotkey hide, Hotkey show)
        {
            TargetApp existing = _settings.FindTarget(target.Key);
            if (existing == null)
            {
                if (hide.IsEmpty && show.IsEmpty) return;
                target.Selected = false;
                _settings.Targets.Add(target);
                existing = target;
            }
            existing.HideHotkey = hide;
            existing.ShowHotkey = show;
            RemoveIfUnused(existing);
            Save();
            RegisterHotkeys();
        }

        // One app's auto-hide time in minutes (0: never). Like hotkeys, it keeps an unticked app unticked.
        public void SetAppAutoHide(TargetApp target, int minutes)
        {
            TargetApp existing = _settings.FindTarget(target.Key);
            if (existing == null)
            {
                if (minutes <= 0) return;
                target.Selected = false;
                _settings.Targets.Add(target);
                existing = target;
            }
            existing.AutoHideMinutes = Math.Max(0, minutes);
            _autoHide.Reset(existing.Key);
            RemoveIfUnused(existing);
            Save();
        }

        // One program's "start hidden" option. Like hotkeys, it keeps an unticked app unticked.
        public void SetAppStartHidden(TargetApp target, bool startHidden)
        {
            TargetApp existing = _settings.FindTarget(target.Key);
            if (existing == null)
            {
                if (!startHidden) return;
                target.Selected = false;
                _settings.Targets.Add(target);
                existing = target;
            }
            existing.StartHidden = startHidden;
            RemoveIfUnused(existing);
            Save();
        }

        // Returns who already uses the hotkey (the all-apps keys or an app's name), or null if it's free.
        public string FindHotkeyOwner(Hotkey hotkey, bool skipGlobal, string skipTargetKey, bool skipTray = false)
        {
            if (hotkey.IsEmpty) return null;
            if (!skipTray && _settings.TrayHotkey.SameAs(hotkey)) return "the tray icon hotkey";
            if (!skipGlobal && (_settings.HideHotkey.SameAs(hotkey) || _settings.ShowHotkey.SameAs(hotkey)))
            {
                return "the all-apps hotkeys";
            }
            foreach (TargetApp t in _settings.Targets)
            {
                if (t.Key == skipTargetKey) continue;
                if (t.HideHotkey.SameAs(hotkey) || t.ShowHotkey.SameAs(hotkey)) return t.DisplayName;
            }
            return null;
        }

        // Forget an app that is neither ticked nor has hotkeys or auto-hide, and don't leave it hidden.
        private void RemoveIfUnused(TargetApp target)
        {
            if (target.Selected || target.HasHotkeys || target.HasAutoHide || target.StartHidden) return;
            _settings.Targets.Remove(target);
            if (_hider.Show(target.Key) > 0) OnWindowsChanged();
        }

        // Global hotkeys are released while a hotkey box has focus, so the keys can be recorded.
        public void PauseHotkeys(bool pause)
        {
            if (_hotkeysPaused == pause) return;
            _hotkeysPaused = pause;
            RegisterHotkeys();
        }

        public void SetStartWithWindows(bool enabled)
        {
            try
            {
                StartupManager.SetEnabled(enabled);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not change the startup setting:\n" + ex.Message, "WindowsShow",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public void MainWindowClosedToTray()
        {
            PauseHotkeys(false);
            if (_lockPending) HideTray();
        }

        // Everything a passer-by can reach (tray icon, second launch) opens the calculator, never the real window.
        private void OpenCalculator()
        {
            if (_mainForm != null && _mainForm.Visible)
            {
                _mainForm.ShowAndActivate(); // already unlocked
                return;
            }
            if (_calculator == null) _calculator = new CalculatorForm(this);
            _calculator.ShowAndActivate();
        }

        private void OpenMainWindow()
        {
            if (_mainForm == null) _mainForm = new MainForm(this);
            _mainForm.ShowAndActivate();
        }

        // Deliberately no Exit here: exiting shows every hidden window, so it's only offered once unlocked.
        private void PopulateMenu(ContextMenuStrip menu)
        {
            menu.Items.Clear();
            var open = new ToolStripMenuItem("Open", null, delegate { OpenCalculator(); });
            open.Font = new Font(open.Font, FontStyle.Bold);
            menu.Items.Add(open);
        }

        private void OnHotkey(int id)
        {
            Binding binding;
            if (!_bindings.TryGetValue(id, out binding)) return;

            if (binding.Action == HotkeyAction.TrayToggle)
            {
                if (_trayHidden) ShowTray();
                else HideTray();
                return;
            }
            if (_trayHidden) return; // window hotkeys are off while the icon is hidden

            if (binding.TargetKey == null)
            {
                bool show = binding.Action == HotkeyAction.Show
                    || (binding.Action == HotkeyAction.Toggle && _hider.TotalHidden > 0);
                if (show) ShowHidden();
                else HideSelected();
                return;
            }

            TargetApp target = _settings.FindTarget(binding.TargetKey);
            if (target == null) return;
            bool showApp = binding.Action == HotkeyAction.Show
                || (binding.Action == HotkeyAction.Toggle && _hider.HiddenCount(target.Key) > 0);
            if (showApp) ShowApp(target);
            else HideApp(target);
        }

        private void RegisterHotkeys()
        {
            foreach (int id in _bindings.Keys) Native.UnregisterHotKey(_window.Handle, id);
            _bindings.Clear();
            _hotkeyErrors.Clear();

            if (!_hotkeysPaused)
            {
                int nextId = 1;
                bool trayKeyWorks = _settings.TrayLockEnabled
                    && TryRegister(nextId++, _settings.TrayHotkey, null, HotkeyAction.TrayToggle, "tray icon");

                // Never stay hidden without a working way back.
                if (_trayHidden && !trayKeyWorks)
                {
                    _trayHidden = false;
                    ApplyTrayState();
                }

                if (!_trayHidden)
                {
                    RegisterPair(ref nextId, null, "all apps", _settings.HideHotkey, _settings.ShowHotkey, _settings.IsToggle);
                    foreach (TargetApp target in _settings.Targets)
                    {
                        if (target.HasHotkeys)
                        {
                            RegisterPair(ref nextId, target.Key, target.DisplayName, target.HideHotkey, target.ShowHotkey, target.IsToggle);
                        }
                    }
                }

                if (_hotkeyErrors.Count > 0 && (_mainForm == null || !_mainForm.Visible))
                {
                    Notify("Shortcut already used by another program: " + string.Join(", ", _hotkeyErrors) + ".",
                        ToolTipIcon.Warning);
                }
            }

            Action handler = HotkeysChanged;
            if (handler != null) handler();
        }

        private void RegisterPair(ref int nextId, string targetKey, string owner, Hotkey hide, Hotkey show, bool toggle)
        {
            if (toggle)
            {
                TryRegister(nextId++, hide, targetKey, HotkeyAction.Toggle, owner);
                return;
            }
            if (!hide.IsEmpty) TryRegister(nextId++, hide, targetKey, HotkeyAction.Hide, owner);
            if (!show.IsEmpty) TryRegister(nextId++, show, targetKey, HotkeyAction.Show, owner);
        }

        private bool TryRegister(int id, Hotkey hotkey, string targetKey, HotkeyAction action, string owner)
        {
            if (Native.RegisterHotKey(_window.Handle, id, hotkey.Modifiers | Native.MOD_NOREPEAT, hotkey.Key))
            {
                _bindings[id] = new Binding { TargetKey = targetKey, Action = action };
                return true;
            }
            _hotkeyErrors.Add(hotkey + " (" + owner + ")");
            return false;
        }

        private void Save()
        {
            try
            {
                _settings.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not save settings:\n" + ex.Message, "WindowsShow",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnWindowsChanged()
        {
            UpdateTrayText();
            Action handler = WindowsChanged;
            if (handler != null) handler();
        }

        private void UpdateTrayText()
        {
            _tray.Text = "Calculator";
        }

        private void Notify(string text, ToolTipIcon icon)
        {
            _tray.ShowBalloonTip(4000, "Calculator", text, icon);
        }

        protected override void ExitThreadCore()
        {
            if (_mainForm != null) _mainForm.Dispose();
            if (_calculator != null) _calculator.Dispose();
            _autoHide.Dispose();
            _launchHider.Dispose();
            _trayTimer.Dispose();
            foreach (int id in _bindings.Keys) Native.UnregisterHotKey(_window.Handle, id);
            // Never leave windows stranded in a hidden state when the app quits.
            _hider.ShowAll();
            Save(); // keeps window targets' latest handles and titles
            _openWait.Unregister(null);
            _tray.Visible = false;
            _tray.Dispose();
            _window.Dispose();
            base.ExitThreadCore();
        }
    }

    internal static class StartupManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "WindowsShow";

        public static bool IsEnabled
        {
            get
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    return key != null && key.GetValue(ValueName) != null;
                }
            }
        }

        public static void SetEnabled(bool enabled)
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\" /tray");
                else key.DeleteValue(ValueName, false);
            }
        }
    }

    internal static class AppIcon
    {
        private static Icon _icon;

        public static Icon Get()
        {
            if (_icon == null) _icon = Create(32);
            return _icon;
        }

        // A calculator: dark body, light display, a grid of keys with one blue "=" key.
        private static Icon Create(int size)
        {
            float s = size / 32f;
            using (var bmp = new Bitmap(size, size))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (var body = new SolidBrush(Color.FromArgb(58, 58, 64)))
                    {
                        g.FillRectangle(body, 5 * s, 1 * s, 22 * s, 30 * s);
                    }
                    using (var display = new SolidBrush(Color.FromArgb(214, 232, 248)))
                    {
                        g.FillRectangle(display, 8 * s, 4 * s, 16 * s, 7 * s);
                    }
                    using (var key = new SolidBrush(Color.FromArgb(225, 225, 230)))
                    using (var accent = new SolidBrush(Color.FromArgb(0, 120, 215)))
                    {
                        for (int row = 0; row < 3; row++)
                        {
                            for (int col = 0; col < 3; col++)
                            {
                                bool isEquals = row == 2 && col == 2;
                                g.FillRectangle(isEquals ? accent : key, (8 + col * 6) * s, (14 + row * 5.5f) * s, 4 * s, 4 * s);
                            }
                        }
                    }
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }
}
