using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace WindowsShow
{
    internal sealed class RunningWindow
    {
        public IntPtr Handle;
        public uint ProcessId;
        public string Title;
    }

    // A running app as shown in the main window: one row per program (or per Store app window).
    internal sealed class RunningApp
    {
        public MatchMode Match;
        public string Value;
        public string DisplayName;
        public string ExePath;
        public readonly List<RunningWindow> Windows = new List<RunningWindow>();

        public string Key { get { return TargetApp.MakeKey(Match, Value); } }

        public TargetApp ToTarget()
        {
            return new TargetApp { Match = Match, Value = Value, DisplayName = DisplayName, ExePath = ExePath ?? "" };
        }

        public TargetApp ToWindowTarget(RunningWindow window)
        {
            return new TargetApp
            {
                Match = MatchMode.Window,
                Id = Guid.NewGuid().ToString("N"),
                Value = Value,
                AppName = DisplayName,
                DisplayName = window.Title,
                Title = window.Title,
                Handle = window.Handle.ToInt64(),
                ProcessId = window.ProcessId,
                ExePath = ExePath ?? ""
            };
        }
    }

    internal static class WindowFinder
    {
        // Store apps (Calculator, Settings, ...) all live in this host process, so they are told apart by title.
        private const string FrameHost = "ApplicationFrameHost";

        // The desktop and taskbar belong to explorer.exe; never hide them along with File Explorer windows.
        private static readonly string[] ShellClasses = { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

        private static readonly uint OwnPid = (uint)Process.GetCurrentProcess().Id;

        private sealed class ProcessInfo
        {
            public string Name = "";
            public string Path = "";
        }

        // Apps that currently show a window you could Alt+Tab to.
        public static List<RunningApp> ListRunningApps()
        {
            var apps = new Dictionary<string, RunningApp>();
            var processes = new Dictionary<uint, ProcessInfo>();

            Native.EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (!IsAppWindow(hWnd)) return true;
                uint pid = Native.ProcessIdOf(hWnd);
                if (pid == OwnPid) return true;
                ProcessInfo info = GetProcessInfo(pid, processes);
                if (info.Name.Length == 0) return true;

                string title = Native.GetTitle(hWnd);
                bool storeApp = string.Equals(info.Name, FrameHost, StringComparison.OrdinalIgnoreCase);
                MatchMode match = storeApp ? MatchMode.WindowTitle : MatchMode.ProcessName;
                string value = storeApp ? title : info.Name;
                string key = TargetApp.MakeKey(match, value);

                RunningApp app;
                if (!apps.TryGetValue(key, out app))
                {
                    app = new RunningApp
                    {
                        Match = match,
                        Value = value,
                        DisplayName = storeApp ? title : Describe(info),
                        ExePath = storeApp ? "" : info.Path
                    };
                    apps[key] = app;
                }
                app.Windows.Add(new RunningWindow { Handle = hWnd, ProcessId = pid, Title = title });
                return true;
            }, IntPtr.Zero);

            return apps.Values.ToList();
        }

        // The window a MatchMode.Window target points at, if it still exists.
        public static IntPtr LiveHandle(TargetApp target)
        {
            var hWnd = new IntPtr(target.Handle);
            return hWnd != IntPtr.Zero && Native.IsWindow(hWnd) && Native.ProcessIdOf(hWnd) == target.ProcessId
                ? hWnd : IntPtr.Zero;
        }

        // Keeps single-window targets pointing at the right window. When a window is gone (for example
        // the app restarted), a window of the same app with the same title takes its place.
        public static void ResolveWindowTargets(IEnumerable<TargetApp> targets)
        {
            var claimed = new HashSet<IntPtr>();
            var lost = new List<TargetApp>();
            foreach (TargetApp target in targets.Where(t => t.Match == MatchMode.Window))
            {
                IntPtr hWnd = LiveHandle(target);
                if (hWnd == IntPtr.Zero)
                {
                    lost.Add(target);
                    continue;
                }
                claimed.Add(hWnd);
                string title = Native.GetTitle(hWnd);
                if (title.Length > 0)
                {
                    target.Title = title;
                    target.DisplayName = title;
                }
            }
            if (lost.Count == 0) return;

            var processes = new Dictionary<uint, ProcessInfo>();
            Native.EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (claimed.Contains(hWnd) || !IsAppWindow(hWnd)) return true;
                uint pid = Native.ProcessIdOf(hWnd);
                string name = GetProcessInfo(pid, processes).Name;
                string title = Native.GetTitle(hWnd);
                TargetApp match = lost.Find(t => t.Title == title && string.Equals(t.Value, name, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    match.Handle = hWnd.ToInt64();
                    match.ProcessId = pid;
                    claimed.Add(hWnd);
                    lost.Remove(match);
                }
                return lost.Count > 0;
            }, IntPtr.Zero);
        }

        // Every visible top-level window of the target, including its popups and tool windows.
        public static List<IntPtr> FindWindowsToHide(TargetApp target)
        {
            var result = new List<IntPtr>();
            if (target.Match == MatchMode.Window) return FindWindowAndPopups(target);

            string value = (target.Value ?? "").Trim();
            if (value.Length == 0) return result;
            var processes = new Dictionary<uint, ProcessInfo>();

            Native.EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (!Native.IsWindowVisible(hWnd) || IsShellWindow(hWnd)) return true;
                uint pid = Native.ProcessIdOf(hWnd);
                if (pid == OwnPid) return true;

                string name = GetProcessInfo(pid, processes).Name;
                bool match = target.Match == MatchMode.ProcessName
                    ? string.Equals(name, value, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(name, FrameHost, StringComparison.OrdinalIgnoreCase)
                      && Native.GetTitle(hWnd).IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
                if (match) result.Add(hWnd);
                return true;
            }, IntPtr.Zero);

            return result;
        }

        private static List<IntPtr> FindWindowAndPopups(TargetApp target)
        {
            var result = new List<IntPtr>();
            IntPtr main = LiveHandle(target);
            if (main == IntPtr.Zero) return result;
            if (Native.IsWindowVisible(main)) result.Add(main);

            // Dialogs and popups owned by the window go with it.
            Native.EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (Native.IsWindowVisible(hWnd) && Native.GetWindow(hWnd, Native.GW_OWNER) == main) result.Add(hWnd);
                return true;
            }, IntPtr.Zero);
            return result;
        }

        // Roughly the windows that appear in Alt+Tab.
        private static bool IsAppWindow(IntPtr hWnd)
        {
            if (!Native.IsWindowVisible(hWnd) || Native.IsCloaked(hWnd) || IsShellWindow(hWnd)) return false;
            if (Native.GetWindowTextLength(hWnd) == 0) return false;
            long exStyle = Native.GetWindowLong(hWnd, Native.GWL_EXSTYLE);
            if ((exStyle & Native.WS_EX_TOOLWINDOW) != 0) return false;
            return Native.GetWindow(hWnd, Native.GW_OWNER) == IntPtr.Zero || (exStyle & Native.WS_EX_APPWINDOW) != 0;
        }

        private static bool IsShellWindow(IntPtr hWnd)
        {
            return Array.IndexOf(ShellClasses, Native.GetClassName(hWnd)) >= 0;
        }

        private static ProcessInfo GetProcessInfo(uint pid, Dictionary<uint, ProcessInfo> cache)
        {
            ProcessInfo info;
            if (cache.TryGetValue(pid, out info)) return info;

            info = new ProcessInfo();
            string path = Native.GetProcessPath(pid);
            if (!string.IsNullOrEmpty(path))
            {
                info.Path = path;
                info.Name = Path.GetFileNameWithoutExtension(path);
            }
            else
            {
                try
                {
                    using (var process = Process.GetProcessById((int)pid)) info.Name = process.ProcessName;
                }
                catch (Exception)
                {
                    // process exited or is inaccessible
                }
            }
            cache[pid] = info;
            return info;
        }

        private static string Describe(ProcessInfo info)
        {
            if (info.Path.Length > 0)
            {
                try
                {
                    string description = FileVersionInfo.GetVersionInfo(info.Path).FileDescription;
                    if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
                }
                catch (Exception)
                {
                    // fall back to the process name
                }
            }
            return info.Name;
        }
    }

    internal sealed class WindowHider
    {
        private sealed class HiddenWindow
        {
            public IntPtr Handle;
            public uint ProcessId; // guards against a recycled window handle
        }

        // Keyed by TargetApp.Key.
        private readonly Dictionary<string, List<HiddenWindow>> _hidden = new Dictionary<string, List<HiddenWindow>>();

        public WindowHider()
        {
            LoadState();
        }

        public int TotalHidden
        {
            get
            {
                Prune();
                return _hidden.Values.Sum(list => list.Count);
            }
        }

        public int HiddenCount(string key)
        {
            Prune();
            List<HiddenWindow> list;
            return _hidden.TryGetValue(key, out list) ? list.Count : 0;
        }

        // Returns how many windows were hidden. "deniedApps" lists apps Windows refused to touch
        // (they run as administrator and WindowsShow does not).
        public int Hide(IEnumerable<TargetApp> targets, out List<string> deniedApps)
        {
            deniedApps = new List<string>();
            int hidden = 0;

            foreach (TargetApp target in targets)
            {
                List<HiddenWindow> list;
                if (!_hidden.TryGetValue(target.Key, out list)) list = new List<HiddenWindow>();

                foreach (IntPtr hWnd in WindowFinder.FindWindowsToHide(target))
                {
                    if (!Native.ShowWindowAsync(hWnd, Native.SW_HIDE))
                    {
                        if (Marshal.GetLastWin32Error() == Native.ERROR_ACCESS_DENIED && !deniedApps.Contains(target.DisplayName))
                        {
                            deniedApps.Add(target.DisplayName);
                        }
                        continue;
                    }
                    hidden++;
                    IntPtr handle = hWnd;
                    if (!list.Exists(w => w.Handle == handle))
                    {
                        list.Add(new HiddenWindow { Handle = hWnd, ProcessId = Native.ProcessIdOf(hWnd) });
                    }
                }

                if (list.Count > 0) _hidden[target.Key] = list;
            }

            SaveState();
            return hidden;
        }

        // Hides one window and remembers it under the given app, as the newest (topmost) of its windows.
        public bool HideWindow(string key, IntPtr hWnd)
        {
            if (!Native.ShowWindowAsync(hWnd, Native.SW_HIDE)) return false;
            List<HiddenWindow> list;
            if (!_hidden.TryGetValue(key, out list))
            {
                list = new List<HiddenWindow>();
                _hidden[key] = list;
            }
            if (!list.Exists(w => w.Handle == hWnd))
            {
                list.Insert(0, new HiddenWindow { Handle = hWnd, ProcessId = Native.ProcessIdOf(hWnd) });
            }
            SaveState();
            return true;
        }

        public int Show(string key)
        {
            List<HiddenWindow> list;
            if (!_hidden.TryGetValue(key, out list)) return 0;
            _hidden.Remove(key);
            list.RemoveAll(w => !IsAlive(w));

            // The list is in the original z-order (topmost first): restore bottom-up, then focus the top one.
            for (int i = list.Count - 1; i >= 1; i--)
            {
                Native.ShowWindowAsync(list[i].Handle, Native.SW_SHOWNA);
            }
            if (list.Count > 0)
            {
                IntPtr top = list[0].Handle;
                Native.ShowWindowAsync(top, Native.IsIconic(top) ? Native.SW_RESTORE : Native.SW_SHOW);
                Native.SetForegroundWindow(top);
            }

            SaveState();
            return list.Count;
        }

        public int ShowAll()
        {
            int shown = 0;
            foreach (string key in _hidden.Keys.ToList()) shown += Show(key);
            return shown;
        }

        private static bool IsAlive(HiddenWindow window)
        {
            return Native.IsWindow(window.Handle) && Native.ProcessIdOf(window.Handle) == window.ProcessId;
        }

        private void Prune()
        {
            foreach (string key in _hidden.Keys.ToList())
            {
                List<HiddenWindow> list = _hidden[key];
                list.RemoveAll(w => !IsAlive(w));
                if (list.Count == 0) _hidden.Remove(key);
            }
        }

        private void LoadState()
        {
            try
            {
                if (!File.Exists(AppPaths.StateFile)) return;
                foreach (string line in File.ReadAllLines(AppPaths.StateFile))
                {
                    string[] parts = line.Split('\t');
                    long handle;
                    uint pid;
                    if (parts.Length != 3
                        || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out handle)
                        || !uint.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out pid))
                    {
                        continue;
                    }

                    var window = new HiddenWindow { Handle = new IntPtr(handle), ProcessId = pid };
                    // Skip windows that closed or were made visible by someone else in the meantime.
                    if (!IsAlive(window) || Native.IsWindowVisible(window.Handle)) continue;

                    List<HiddenWindow> list;
                    if (!_hidden.TryGetValue(parts[0], out list))
                    {
                        list = new List<HiddenWindow>();
                        _hidden[parts[0]] = list;
                    }
                    list.Add(window);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void SaveState()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Folder);
                var lines = new List<string>();
                foreach (KeyValuePair<string, List<HiddenWindow>> entry in _hidden)
                {
                    foreach (HiddenWindow w in entry.Value)
                    {
                        lines.Add(entry.Key.Replace('\t', ' ') + "\t"
                            + w.Handle.ToInt64().ToString(CultureInfo.InvariantCulture) + "\t"
                            + w.ProcessId.ToString(CultureInfo.InvariantCulture));
                    }
                }
                File.WriteAllLines(AppPaths.StateFile, lines);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
