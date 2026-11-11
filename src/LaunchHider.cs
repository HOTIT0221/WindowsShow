using System;
using System.Collections.Generic;
using System.IO;

namespace WindowsShow
{
    // "Start hidden": hides the windows a program shows while it starts up, the moment they appear.
    // Only programs started in the last minute count, so new windows of a program that was already
    // running stay visible. Once the user shows the app, that run of the program is left alone.
    internal sealed class LaunchHider : IDisposable
    {
        private static readonly TimeSpan LaunchPeriod = TimeSpan.FromSeconds(60);
        private static readonly string[] ShellClasses = { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

        private readonly AppSettings _settings;
        private readonly WindowHider _hider;
        private readonly Action _windowsChanged;
        private readonly Native.WinEventProc _callback; // kept in a field so it isn't garbage-collected
        private readonly IntPtr _hook;
        private readonly Dictionary<string, DateTime> _shownAt = new Dictionary<string, DateTime>();

        public LaunchHider(AppSettings settings, WindowHider hider, Action windowsChanged)
        {
            _settings = settings;
            _hider = hider;
            _windowsChanged = windowsChanged;
            _callback = OnWindowShown;
            _hook = Native.SetWinEventHook(Native.EVENT_OBJECT_SHOW, Native.EVENT_OBJECT_SHOW, IntPtr.Zero, _callback,
                0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
            HideExistingLaunchWindows();
        }

        // The user showed this app: programs started before now are left visible from here on.
        public void Released(string key)
        {
            _shownAt[key] = DateTime.UtcNow;
        }

        public void ReleasedAll()
        {
            foreach (TargetApp target in _settings.Targets)
            {
                if (target.StartHidden) Released(target.Key);
            }
        }

        // Catches programs that started just before WindowsShow did (e.g. at sign-in).
        private void HideExistingLaunchWindows()
        {
            bool any = false;
            Native.EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (Native.IsWindowVisible(hWnd) && TryHide(hWnd)) any = true;
                return true;
            }, IntPtr.Zero);
            if (any) _windowsChanged();
        }

        private void OnWindowShown(IntPtr hook, uint eventType, IntPtr hWnd, int idObject, int idChild,
            uint eventThread, uint eventTime)
        {
            if (idObject != Native.OBJID_WINDOW || idChild != 0 || hWnd == IntPtr.Zero) return;
            if (TryHide(hWnd)) _windowsChanged();
        }

        private bool TryHide(IntPtr hWnd)
        {
            if (!_settings.Targets.Exists(t => t.StartHidden)) return false;
            if (!IsMainWindowKind(hWnd)) return false;

            uint pid = Native.ProcessIdOf(hWnd);
            DateTime started;
            if (!Native.TryGetProcessStartTime(pid, out started)) return false;
            if (DateTime.UtcNow - started > LaunchPeriod) return false;

            string path = Native.GetProcessPath(pid);
            if (string.IsNullOrEmpty(path)) return false;
            string name = Path.GetFileNameWithoutExtension(path);

            TargetApp target = _settings.Targets.Find(t => t.StartHidden && t.Match == MatchMode.ProcessName
                && string.Equals((t.Value ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase));
            if (target == null) return false;

            DateTime shown;
            if (_shownAt.TryGetValue(target.Key, out shown) && shown >= started) return false;

            return _hider.HideWindow(target.Key, hWnd);
        }

        // Top-level windows you could Alt+Tab to, not tooltips, menus or other short-lived popups.
        private static bool IsMainWindowKind(IntPtr hWnd)
        {
            if (Native.GetAncestor(hWnd, Native.GA_ROOT) != hWnd) return false;
            long exStyle = Native.GetWindowLong(hWnd, Native.GWL_EXSTYLE);
            if ((exStyle & Native.WS_EX_TOOLWINDOW) != 0) return false;
            if (Native.GetWindow(hWnd, Native.GW_OWNER) != IntPtr.Zero && (exStyle & Native.WS_EX_APPWINDOW) == 0) return false;
            return Array.IndexOf(ShellClasses, Native.GetClassName(hWnd)) < 0;
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero) Native.UnhookWinEvent(_hook);
        }
    }
}
