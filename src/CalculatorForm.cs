using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace WindowsShow
{
    // A working calculator that the app shows first. Typing the unlock code and pressing = opens the real window.
    internal sealed class CalculatorForm : Form
    {
        private static readonly Color Background = Color.FromArgb(243, 243, 243);
        private static readonly Color NumberKey = Color.White;
        private static readonly Color FunctionKey = Color.FromArgb(249, 249, 249);
        private static readonly Color KeyBorder = Color.FromArgb(229, 229, 229);
        private static readonly Color Accent = Color.FromArgb(0, 103, 192);
        private const int MaxDigits = 16;

        private readonly TrayApp _app;
        private readonly Label _expression;
        private readonly Label _display;

        private decimal _accumulator;
        private string _pendingOp;    // "+", "-", "*", "/" or null
        private string _entry = "0";  // the number on the display
        private bool _entryFresh = true;
        private string _typed = "";   // keys typed for the current entry, checked against the unlock code
        private bool _error;

        // Not focusable, so Enter always means "=" instead of re-clicking the last button.
        private sealed class KeyButton : Button
        {
            public KeyButton()
            {
                SetStyle(ControlStyles.Selectable, false);
            }
        }

        public CalculatorForm(TrayApp app)
        {
            _app = app;

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F);
            Text = "Calculator";
            Icon = AppIcon.Get();
            BackColor = Background;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(320, 480);
            KeyPreview = true;

            _expression = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.BottomRight,
                ForeColor = Color.FromArgb(96, 96, 96),
                Font = new Font("Segoe UI", 10F),
                Margin = new Padding(0)
            };
            _display = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                Font = new Font("Segoe UI Semibold", 30F),
                Margin = new Padding(0)
            };

            var keys = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 6, Margin = new Padding(0) };
            for (int i = 0; i < 4; i++) keys.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            for (int i = 0; i < 6; i++) keys.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 6));

            string[,] layout =
            {
                { "%", "CE", "C", "BS" },
                { "INV", "SQR", "SQRT", "/" },
                { "7", "8", "9", "*" },
                { "4", "5", "6", "-" },
                { "1", "2", "3", "+" },
                { "NEG", "0", ".", "=" }
            };
            for (int row = 0; row < 6; row++)
            {
                for (int col = 0; col < 4; col++)
                {
                    keys.Controls.Add(MakeKey(layout[row, col]), col, row);
                }
            }

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(6, 4, 6, 6) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Controls.Add(_expression, 0, 0);
            root.Controls.Add(_display, 0, 1);
            root.Controls.Add(keys, 0, 2);
            Controls.Add(root);
            ResumeLayout(true);

            FormClosing += OnFormClosing;
            UpdateDisplay();
        }

        public void ShowAndActivate()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        public void HideAndReset()
        {
            Hide();
            ClearAll();
            UpdateDisplay();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            HideAndReset();
        }

        private Button MakeKey(string token)
        {
            bool isNumber = token.Length == 1 && (char.IsDigit(token[0]) || token == ".");
            bool isEquals = token == "=";
            var button = new KeyButton
            {
                Text = Label(token),
                Tag = token,
                Dock = DockStyle.Fill,
                Margin = new Padding(2),
                FlatStyle = FlatStyle.Flat,
                BackColor = isEquals ? Accent : (isNumber ? NumberKey : FunctionKey),
                ForeColor = isEquals ? Color.White : Color.Black,
                Font = isNumber || isEquals ? new Font("Segoe UI Semibold", 14F) : new Font("Segoe UI", 12F),
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderColor = isEquals ? Accent : KeyBorder;
            button.Click += delegate { Press(token); };
            return button;
        }

        private static string Label(string token)
        {
            switch (token)
            {
                case "BS": return "⌫";
                case "INV": return "1/x";
                case "SQR": return "x²";
                case "SQRT": return "√x";
                case "/": return "÷";
                case "*": return "×";
                case "-": return "−";
                case "NEG": return "+/−";
                default: return token;
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Enter: Press("="); return true;
                case Keys.Back: Press("BS"); return true;
                case Keys.Escape: Press("C"); return true;
                case Keys.Delete: Press("CE"); return true;
                case Keys.F9: Press("NEG"); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            char c = e.KeyChar;
            string token = null;
            if (c >= '0' && c <= '9') token = c.ToString();
            else if (c == '.' || c == ',') token = ".";
            else if ("+-*/%=".IndexOf(c) >= 0) token = c.ToString();
            else if (c == '@') token = "SQRT";
            if (token != null)
            {
                Press(token);
                e.Handled = true;
            }
            base.OnKeyPress(e);
        }

        private void Press(string token)
        {
            if (_error) ClearAll();
            try
            {
                switch (token)
                {
                    case ".": InputPoint(); break;
                    case "+":
                    case "-":
                    case "*":
                    case "/": InputOperator(token); break;
                    case "=": InputEquals(); break;
                    case "C": ClearAll(); break;
                    case "CE": _entry = "0"; _entryFresh = true; _typed = ""; break;
                    case "BS": Backspace(); break;
                    case "NEG": Negate(); break;
                    case "%": SetResult(_pendingOp != null ? _accumulator * Value / 100 : Value / 100); break;
                    case "INV":
                        if (Value == 0) ShowError("Cannot divide by zero");
                        else SetResult(1 / Value);
                        break;
                    case "SQR": SetResult(Value * Value); break;
                    case "SQRT":
                        if (Value < 0) ShowError("Invalid input");
                        else SetResult((decimal)Math.Sqrt((double)Value));
                        break;
                    default: InputDigit(token); break;
                }
            }
            catch (OverflowException)
            {
                ShowError("Overflow");
            }
            UpdateDisplay();
        }

        private decimal Value
        {
            get { return decimal.Parse(_entry, NumberStyles.Number, CultureInfo.InvariantCulture); }
        }

        private void InputDigit(string digit)
        {
            if (_entryFresh)
            {
                _entry = "0";
                _typed = "";
                _entryFresh = false;
            }
            if (CountDigits(_entry) >= MaxDigits) return;
            _typed += digit;
            if (_entry == "0") _entry = digit;
            else if (_entry == "-0") _entry = "-" + digit;
            else _entry += digit;
        }

        private void InputPoint()
        {
            if (_entryFresh)
            {
                _entry = "0";
                _typed = "";
                _entryFresh = false;
            }
            if (_entry.IndexOf('.') < 0) _entry += ".";
            _typed += ".";
        }

        private void InputOperator(string op)
        {
            if (_pendingOp != null && !_entryFresh)
            {
                if (!Compute()) return; // chained: "2 + 3 +" shows 5
            }
            else if (_pendingOp == null)
            {
                _accumulator = Value;
            }
            _pendingOp = op;
            _expression.Text = Format(_accumulator) + " " + Label(op);
            _entry = Format(_accumulator);
            _entryFresh = true;
            _typed = "";
        }

        private void InputEquals()
        {
            if (_pendingOp == null && _typed.Length > 0 && AppSettings.HashUnlockCode(_typed) == _app.Settings.UnlockCodeHash)
            {
                ClearAll();
                _app.Unlock();
                return;
            }

            if (_pendingOp == null)
            {
                _expression.Text = Format(Value) + " =";
            }
            else
            {
                string expression = Format(_accumulator) + " " + Label(_pendingOp) + " " + Format(Value) + " =";
                if (!Compute()) return;
                _expression.Text = expression;
                _entry = Format(_accumulator);
                _pendingOp = null;
            }
            _entryFresh = true;
            _typed = "";
        }

        private bool Compute()
        {
            decimal b = Value;
            switch (_pendingOp)
            {
                case "+": _accumulator += b; break;
                case "-": _accumulator -= b; break;
                case "*": _accumulator *= b; break;
                case "/":
                    if (b == 0)
                    {
                        ShowError("Cannot divide by zero");
                        return false;
                    }
                    _accumulator /= b;
                    break;
            }
            return true;
        }

        private void Backspace()
        {
            if (_entryFresh) return;
            _entry = _entry.Length > 1 ? _entry.Substring(0, _entry.Length - 1) : "0";
            if (_entry == "-" || _entry == "") _entry = "0";
            if (_typed.Length > 0) _typed = _typed.Substring(0, _typed.Length - 1);
        }

        private void Negate()
        {
            if (Value == 0) return;
            _entry = _entry.StartsWith("-") ? _entry.Substring(1) : "-" + _entry;
            _typed = "";
        }

        private void SetResult(decimal value)
        {
            _entry = Format(value);
            _entryFresh = true;
            _typed = "";
        }

        private void ClearAll()
        {
            _accumulator = 0;
            _pendingOp = null;
            _entry = "0";
            _entryFresh = true;
            _typed = "";
            _error = false;
            _expression.Text = "";
        }

        private void ShowError(string message)
        {
            _error = true;
            _pendingOp = null;
            _expression.Text = "";
            _display.Text = message;
        }

        private void UpdateDisplay()
        {
            string text = _error ? _display.Text : _entry;
            float size = text.Length <= 10 ? 30F : (text.Length <= 14 ? 23F : 17F);
            if (_display.Font.Size != size) _display.Font = new Font("Segoe UI Semibold", size);
            _display.Text = text;
        }

        private static string Format(decimal value)
        {
            return value.ToString("G" + MaxDigits, CultureInfo.InvariantCulture);
        }

        private static int CountDigits(string number)
        {
            int count = 0;
            foreach (char c in number) if (char.IsDigit(c)) count++;
            return count;
        }
    }
}
