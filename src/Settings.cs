using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;
using System.Xml.Serialization;

namespace WindowsShow
{
    public enum MatchMode
    {
        ProcessName, // every window of a program
        WindowTitle, // a Store app, told apart by its title
        Window       // one particular window of a program
    }

    public sealed class Hotkey
    {
        public uint Modifiers; // MOD_* flags
        public uint Key;       // virtual-key code

        [XmlIgnore]
        public bool IsEmpty { get { return Key == 0; } }

        public bool SameAs(Hotkey other)
        {
            return other != null && !IsEmpty && Key == other.Key && Modifiers == other.Modifiers;
        }

        public override string ToString()
        {
            return IsEmpty ? "None" : Format(Modifiers, KeyName((Keys)Key));
        }

        public static string Format(uint modifiers, string keyName)
        {
            var sb = new StringBuilder();
            if ((modifiers & Native.MOD_CONTROL) != 0) sb.Append("Ctrl+");
            if ((modifiers & Native.MOD_ALT) != 0) sb.Append("Alt+");
            if ((modifiers & Native.MOD_SHIFT) != 0) sb.Append("Shift+");
            if ((modifiers & Native.MOD_WIN) != 0) sb.Append("Win+");
            sb.Append(keyName);
            return sb.ToString();
        }

        private static string KeyName(Keys key)
        {
            if (key >= Keys.D0 && key <= Keys.D9) return ((char)('0' + (key - Keys.D0))).ToString();
            switch (key)
            {
                case Keys.Oemcomma: return ",";
                case Keys.OemPeriod: return ".";
                case Keys.OemMinus: return "-";
                case Keys.Oemplus: return "=";
                case Keys.OemQuestion: return "/";
                case Keys.OemSemicolon: return ";";
                case Keys.OemQuotes: return "'";
                case Keys.OemOpenBrackets: return "[";
                case Keys.OemCloseBrackets: return "]";
                case Keys.OemPipe: return "\\";
                case Keys.Oemtilde: return "`";
                case Keys.Next: return "PageDown";
                case Keys.Prior: return "PageUp";
                case Keys.Return: return "Enter";
            }
            return key.ToString();
        }
    }

    // An app or window the user ticked or gave hotkeys. Normal apps are identified by process name;
    // Store apps (which all run inside ApplicationFrameHost) by their window title; a single window
    // by its live handle, falling back to its title after the app restarts.
    public sealed class TargetApp
    {
        public MatchMode Match = MatchMode.ProcessName;
        public string Value = "";       // process name without ".exe", or a Store app's window title
        public string DisplayName = "";
        public string ExePath = "";
        public bool Selected = true;              // ticked: hidden by the all-apps hotkeys
        public Hotkey HideHotkey = new Hotkey();  // its own hotkeys (optional)
        public Hotkey ShowHotkey = new Hotkey();

        // Hide automatically after this many minutes without use (in front + keyboard/mouse input). 0: never.
        public int AutoHideMinutes;

        // Hide the windows the program shows when it starts (programs only, not Store apps or single windows).
        public bool StartHidden;

        // Only for MatchMode.Window.
        public string Id = "";
        public string AppName = "";
        public string Title = "";
        public long Handle;
        public uint ProcessId;

        [XmlIgnore]
        public string Key { get { return Match == MatchMode.Window ? "window:" + Id : MakeKey(Match, Value); } }

        [XmlIgnore]
        public bool HasHotkeys { get { return !HideHotkey.IsEmpty || !ShowHotkey.IsEmpty; } }

        [XmlIgnore]
        public bool IsToggle { get { return HideHotkey.SameAs(ShowHotkey); } }

        [XmlIgnore]
        public bool HasAutoHide { get { return AutoHideMinutes > 0; } }

        public static string MakeKey(MatchMode match, string value)
        {
            return (match == MatchMode.ProcessName ? "process:" : "title:") + (value ?? "").Trim().ToLowerInvariant();
        }
    }

    public sealed class AppSettings
    {
        private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(AppSettings));

        public Hotkey HideHotkey = new Hotkey { Modifiers = Native.MOD_CONTROL | Native.MOD_ALT, Key = (uint)Keys.H };
        public Hotkey ShowHotkey = new Hotkey { Modifiers = Native.MOD_CONTROL | Native.MOD_ALT, Key = (uint)Keys.S };
        public List<TargetApp> Targets = new List<TargetApp>();

        // Tray lock: the tray icon disappears this many minutes after it was shown (0: never), and while it's
        // gone only TrayHotkey works; it shows the icon again (or hides it at once) and turns the window hotkeys back on.
        public Hotkey TrayHotkey = new Hotkey { Modifiers = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT, Key = (uint)Keys.C };
        public int TrayHideMinutes;

        // Without a hotkey there would be no way back, so the lock needs both.
        [XmlIgnore]
        public bool TrayLockEnabled
        {
            get { return TrayHideMinutes > 0 && !TrayHotkey.IsEmpty; }
        }

        // The code typed into the calculator (followed by =) to open the real window. Only a hash is stored.
        public string UnlockCodeHash = HashUnlockCode("0221");

        public static string HashUnlockCode(string code)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes("WindowsShow unlock:" + code));
                return BitConverter.ToString(hash).Replace("-", "");
            }
        }

        // The same key for hide and show means "toggle".
        [XmlIgnore]
        public bool IsToggle
        {
            get { return HideHotkey.SameAs(ShowHotkey); }
        }

        public TargetApp FindTarget(string key)
        {
            return Targets.Find(t => t.Key == key);
        }

        public static AppSettings Load()
        {
            string path = AppPaths.SettingsFile;
            if (!File.Exists(path)) return new AppSettings();
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    return (AppSettings)Serializer.Deserialize(stream) ?? new AppSettings();
                }
            }
            catch (Exception)
            {
                // Keep the unreadable file so the user doesn't silently lose their rules.
                try { File.Copy(path, path + ".bak", true); } catch (IOException) { }
                return new AppSettings();
            }
        }

        public void Save()
        {
            Directory.CreateDirectory(AppPaths.Folder);
            string temp = AppPaths.SettingsFile + ".tmp";
            using (var stream = File.Create(temp))
            {
                Serializer.Serialize(stream, this);
            }
            if (File.Exists(AppPaths.SettingsFile)) File.Delete(AppPaths.SettingsFile);
            File.Move(temp, AppPaths.SettingsFile);
        }
    }

    internal static class AppPaths
    {
        public static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsShow");

        public static readonly string SettingsFile = Path.Combine(Folder, "settings.xml");

        // Remembers which windows are hidden, so they can still be shown after a restart or crash.
        public static readonly string StateFile = Path.Combine(Folder, "hidden.txt");
    }
}
