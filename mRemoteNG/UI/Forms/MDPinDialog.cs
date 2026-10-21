using System;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Forms;
using MDRemoteNG.Core;

namespace mRemoteNG.UI.Forms
{
    public sealed class MDPinDialog : Form
    {
        private readonly ProfileStore _store;
        private readonly bool _creating;
        private readonly TextBox _pin = new() { UseSystemPasswordChar = true, MaxLength = 8, Dock = DockStyle.Fill };
        private readonly TextBox _confirmation = new() { UseSystemPasswordChar = true, MaxLength = 8, Dock = DockStyle.Fill };
        private readonly Label _error = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(400, 0), Margin = new Padding(3, 12, 3, 8) };
        public Profile UnlockedProfile { get; private set; }

        public MDPinDialog(ProfileStore store)
        {
            _store = store;
            _creating = !store.Exists;
            Text = _creating ? "Welcome to MDRemoteNG" : "Unlock MDRemoteNG";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = true;
            AutoScaleMode = AutoScaleMode.Font;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var layout = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 400));
            layout.Controls.Add(new Label { Text = _creating ? "Create your startup and network PIN" : "Enter your startup PIN", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold) });
            layout.Controls.Add(new Label { Text = "4–8 characters: letters, numbers and symbols.\nLetters are case insensitive. Spaces are not accepted.", AutoSize = true, Margin = new Padding(3, 10, 3, 14) });
            layout.Controls.Add(new Label { Text = "PIN", AutoSize = true });
            layout.Controls.Add(_pin);
            if (_creating)
            {
                layout.Controls.Add(new Label { Text = "Confirm PIN", AutoSize = true, Margin = new Padding(3, 12, 3, 3) });
                layout.Controls.Add(_confirmation);
                layout.Controls.Add(new Label { Text = "Installs on your local network with the same PIN can exchange the local connection credentials you choose in MDRemoteNG settings.", AutoSize = true, MaximumSize = new Size(395, 0), Margin = new Padding(3, 14, 3, 3) });
            }
            layout.Controls.Add(_error);
            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var accept = new Button { Text = _creating ? "Create PIN" : "Unlock", AutoSize = true };
            accept.Click += OnAccept;
            var cancel = new Button { Text = "Exit", AutoSize = true, DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(accept);
            buttons.Controls.Add(cancel);
            layout.Controls.Add(buttons);
            Controls.Add(layout);
            AcceptButton = accept;
            CancelButton = cancel;
            Shown += (_, _) => _pin.Focus();
            FormClosed += (_, _) => { _pin.Clear(); _confirmation.Clear(); };
        }

        private void OnAccept(object sender, EventArgs e)
        {
            try
            {
                if (_creating && PinPolicy.Normalize(_pin.Text) != PinPolicy.Normalize(_confirmation.Text))
                {
                    _error.Text = "The PINs do not match.";
                    _confirmation.Clear();
                    _confirmation.Focus();
                    return;
                }
                UnlockedProfile = _creating ? _store.Create(_pin.Text) : _store.Unlock(_pin.Text);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (PinLockedException ex)
            {
                _error.Text = "Too many attempts. Try again after " + ex.LockedUntil.ToLocalTime().ToString("T") + ".";
                _pin.Clear();
            }
            catch (UnauthorizedAccessException)
            {
                _error.Text = "The PIN was not accepted. Please try again.";
                _pin.Clear();
                _pin.Focus();
            }
            catch (ArgumentException)
            {
                _error.Text = "Enter 4–8 letters, numbers or symbols without spaces.";
                _pin.Focus();
            }
            catch (Exception ex) when (ex is IOException || ex is CryptographicException || ex is InvalidOperationException)
            {
                _error.Text = "Your protected profile could not be opened. Check access to your Windows user profile. MDRemoteNG remains locked.";
                _pin.Clear();
            }
        }
    }
}
