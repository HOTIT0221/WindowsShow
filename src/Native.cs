using System;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowsShow
{
    internal static class Native
    {
        public const int WM_HOTKEY = 0x0312;
        public const int WM_APP = 0x8000;

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;

        public const int SW_HIDE = 0;
        public const int SW_SHOW = 5;
        public const int SW_SHOWNA = 8;
        public const int SW_RESTORE = 9;

        public const int VK_LWIN = 0x5B;
        public const int VK_RWIN = 0x5C;

        public const int ERROR_ACCESS_DENIED = 5;
        public const int ASFW_ANY = -1;
        public const int DWMWA_CLOAKED = 14;

        public const int GWL_EXSTYLE = -20;
        public const long WS_EX_TOOLWINDOW = 0x00000080;
        public const long WS_EX_APPWINDOW = 0x00040000;
        public const uint GW_OWNER = 4;
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        // Async so a hung target application can never freeze WindowsShow.
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        public const uint EVENT_OBJECT_SHOW = 0x8002;
        public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
        public const int OBJID_WINDOW = 0;
        public const uint GA_ROOT = 2;

        public delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hWnd, int idObject, int idChild,
            uint eventThread, uint eventTime);

        [DllImport("user32.dll")]
        public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback,
            uint processId, uint threadId, uint flags);

        [DllImport("user32.dll")]
        public static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

        [DllImport("kernel32.dll")]
        private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

        public static bool TryGetProcessStartTime(uint processId, out DateTime startUtc)
        {
            startUtc = DateTime.MinValue;
            IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (process == IntPtr.Zero) return false;
            try
            {
                long creation, exit, kernel, user;
                if (!GetProcessTimes(process, out creation, out exit, out kernel, out user)) return false;
                startUtc = DateTime.FromFileTimeUtc(creation);
                return true;
            }
            finally
            {
                CloseHandle(process);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

        // Tick count (ms since boot, wraps) of the last keyboard or mouse input in this session.
        public static uint GetLastInputTick()
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            return GetLastInputInfo(ref info) ? info.dwTime : 0;
        }

        [DllImport("user32.dll")]
        public static extern bool AllowSetForegroundWindow(int processId);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern short GetKeyState(int virtualKey);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint command);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam,
            uint flags, uint timeoutMs, out IntPtr result);

        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
        private static extern IntPtr GetClassLongPtr64(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "GetClassLongW")]
        private static extern uint GetClassLong32(IntPtr hWnd, int index);

        // The icon a window shows in its title bar / taskbar button (e.g. a Chrome web app's own icon).
        public static IntPtr GetWindowIcon(IntPtr hWnd)
        {
            const int WM_GETICON = 0x007F;
            const uint SMTO_ABORTIFHUNG = 0x0002;
            foreach (int type in new[] { 2 /* ICON_SMALL2 */, 0 /* ICON_SMALL */, 1 /* ICON_BIG */ })
            {
                IntPtr icon;
                if (SendMessageTimeout(hWnd, WM_GETICON, new IntPtr(type), IntPtr.Zero, SMTO_ABORTIFHUNG, 100, out icon) != IntPtr.Zero
                    && icon != IntPtr.Zero)
                {
                    return icon;
                }
            }
            IntPtr classIcon = GetClassIcon(hWnd, -34 /* GCLP_HICONSM */);
            return classIcon != IntPtr.Zero ? classIcon : GetClassIcon(hWnd, -14 /* GCLP_HICON */);
        }

        private static IntPtr GetClassIcon(IntPtr hWnd, int index)
        {
            return IntPtr.Size == 8 ? GetClassLongPtr64(hWnd, index) : new IntPtr(GetClassLong32(hWnd, index));
        }

        public static string GetClassName(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        // Works for elevated processes too, unlike Process.MainModule.
        public static string GetProcessPath(uint processId)
        {
            IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (process == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(process, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally
            {
                CloseHandle(process);
            }
        }

        public static string GetTitle(IntPtr hWnd)
        {
            int length = GetWindowTextLength(hWnd);
            if (length <= 0) return string.Empty;
            var sb = new StringBuilder(length + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static uint ProcessIdOf(IntPtr hWnd)
        {
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            return pid;
        }

        public static bool IsCloaked(IntPtr hWnd)
        {
            int cloaked;
            return DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && cloaked != 0;
        }

        public static bool IsWinKeyDown()
        {
            return (GetKeyState(VK_LWIN) & 0x8000) != 0 || (GetKeyState(VK_RWIN) & 0x8000) != 0;
        }
    }
}
