using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace WindowsShow
{
    // Hides an app's windows once they have gone unused for its auto-hide time. An app counts as used
    // while one of its windows is in front and the keyboard or mouse is being used. The countdown runs
    // while you're in another app or away from the PC, and starts over whenever its windows come back.
    // Hiding is silent, like everything else a passer-by could notice.
    internal sealed class AutoHideMonitor : IDisposable
    {
        private const int CheckIntervalMs = 2000;

        private readonly AppSettings _settings;
        private readonly WindowHider _hider;
        private readonly Action _windowsChanged;
        private readonly Timer _timer;
        private readonly Dictionary<string, DateTime> _lastUsed = new Dictionary<string, DateTime>();
        private uint _lastInputTick;

        public AutoHideMonitor(AppSettings settings, WindowHider hider, Action windowsChanged)
        {
            _settings = settings;
            _hider = hider;
            _windowsChanged = windowsChanged;
            _lastInputTick = Native.GetLastInputTick();

            _timer = new Timer { Interval = CheckIntervalMs };
            _timer.Tick += delegate { Check(); };
            _timer.Start();
        }

        // Starts an app's countdown over, e.g. after its auto-hide time was changed.
        public void Reset(string key)
        {
            _lastUsed.Remove(key);
        }

        private void Check()
        {
            uint inputTick = Native.GetLastInputTick();
            bool hadInput = inputTick != _lastInputTick;
            _lastInputTick = inputTick;

            List<TargetApp> targets = _settings.Targets.FindAll(t => t.HasAutoHide);
            var activeKeys = new HashSet<string>(targets.Select(t => t.Key));
            foreach (string key in _lastUsed.Keys.ToList())
            {
                if (!activeKeys.Contains(key)) _lastUsed.Remove(key);
            }
            if (targets.Count == 0) return;

            WindowFinder.ResolveWindowTargets(_settings.Targets);
            IntPtr foreground = Native.GetForegroundWindow();
            DateTime now = DateTime.UtcNow;
            var expired = new List<TargetApp>();

            foreach (TargetApp target in targets)
            {
                List<IntPtr> windows = WindowFinder.FindWindowsToHide(target);
                if (windows.Count == 0)
                {
                    // Hidden or closed: the countdown starts fresh once a window is visible again.
                    _lastUsed.Remove(target.Key);
                    continue;
                }

                DateTime lastUsed;
                if (!_lastUsed.TryGetValue(target.Key, out lastUsed) || (hadInput && windows.Contains(foreground)))
                {
                    _lastUsed[target.Key] = now;
                    continue;
                }
                if (now - lastUsed >= TimeSpan.FromMinutes(target.AutoHideMinutes)) expired.Add(target);
            }

            if (expired.Count == 0) return;
            List<string> denied; // apps running as administrator; retried after another full timeout
            _hider.Hide(expired, out denied);
            foreach (TargetApp target in expired) _lastUsed.Remove(target.Key);
            _windowsChanged();
        }

        public void Dispose()
        {
            _timer.Stop();
            _timer.Dispose();
        }
    }
}
