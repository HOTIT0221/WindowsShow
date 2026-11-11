using System;
using System.Drawing;
using System.Windows.Forms;

namespace WindowsShow
{
    // A text box that records a key combination instead of text.
    internal sealed class HotkeyBox : TextBox
    {
        private Hotkey _hotkey = new Hotkey();

        // Raised only when the user records or clears a hotkey, not when Hotkey is set from code.
        public event EventHandler HotkeyChanged;

        public HotkeyBox()
        {
            ReadOnly = true;
            BackColor = SystemColors.Window;
            ShortcutsEnabled = false;
        }

        public Hotkey Hotkey
        {
            get { return _hotkey; }
            set
            {
                _hotkey = value == null ? new Hotkey() : new Hotkey { Modifiers = value.Modifiers, Key = value.Key };
                Text = _hotkey.ToString();
            }
        }

        public void ResetHotkey()
        {
            SetFromUser(new Hotkey());
        }

        private void SetFromUser(Hotkey hotkey)
        {
            Hotkey = hotkey;
            EventHandler handler = HotkeyChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            Keys mods = keyData & Keys.Modifiers;

            // Keep plain Tab / Shift+Tab / Esc for dialog navigation.
            if ((key == Keys.Tab && (mods == Keys.None || mods == Keys.Shift)) || (key == Keys.Escape && mods == Keys.None))
            {
                return base.ProcessCmdKey(ref msg, keyData);
            }
            if ((key == Keys.Back || key == Keys.Delete) && mods == Keys.None)
            {
                SetFromUser(new Hotkey());
                return true;
            }

            uint modifiers = 0;
            if ((mods & Keys.Control) != 0) modifiers |= Native.MOD_CONTROL;
            if ((mods & Keys.Alt) != 0) modifiers |= Native.MOD_ALT;
            if ((mods & Keys.Shift) != 0) modifiers |= Native.MOD_SHIFT;
            if (Native.IsWinKeyDown()) modifiers |= Native.MOD_WIN;

            bool modifierOnly = key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu
                || key == Keys.LWin || key == Keys.RWin;
            if (modifierOnly)
            {
                Text = Hotkey.Format(modifiers, "");
                return true;
            }

            // A plain letter as a global hotkey would break typing everywhere, so require a modifier (F-keys excepted).
            bool isFunctionKey = key >= Keys.F1 && key <= Keys.F24;
            if (modifiers == 0 && !isFunctionKey)
            {
                Text = "Add Ctrl, Alt, Shift or Win";
                return true;
            }

            SetFromUser(new Hotkey { Modifiers = modifiers, Key = (uint)key });
            return true;
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            Text = _hotkey.ToString();
            base.OnKeyUp(e);
        }

        protected override void OnLeave(EventArgs e)
        {
            Text = _hotkey.ToString();
            base.OnLeave(e);
        }
    }
}
