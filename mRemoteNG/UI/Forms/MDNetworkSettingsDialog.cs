using System;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Forms;
using MDRemoteNG.Core;

namespace mRemoteNG.UI.Forms
{
    public sealed class MDNetworkSettingsDialog : Form
    {
        private readonly ProfileStore _store;
        private readonly TextBox _displayName = new() { MaxLength = 64, Dock = DockStyle.Fill };
        private readonly CheckBox _discovery = new() { Text = "Find MDRemoteNG installs on my local network", AutoSize = true };
        private readonly CheckBox _autoConnect = new() { Text = "Open matching peers automatically while MDRemoteNG is unlocked", AutoSize = true };
        private readonly CheckBox _share = new() { Text = "Share a connection to this computer with matching PINs", AutoSize = true };
        private readonly ComboBox _protocol = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 3389, Dock = DockStyle.Fill };
        private readonly TextBox _username = new() { MaxLength = 256, Dock = DockStyle.Fill };
        private readonly TextBox _domain = new() { MaxLength = 256, Dock = DockStyle.Fill };
        private readonly TextBox _password = new() { UseSystemPasswordChar = true, MaxLength = 1024, Dock = DockStyle.Fill };
        private readonly CheckBox _changePin = new() { Text = "Change startup and network PIN", AutoSize = true };
        private readonly TextBox _currentPin = new() { UseSystemPasswordChar = true, MaxLength = 8, Dock = DockStyle.Fill };
        private readonly TextBox _newPin = new() { UseSystemPasswordChar = true, MaxLength = 8, Dock = DockStyle.Fill };
        private readonly TextBox _confirmPin = new() { UseSystemPasswordChar = true, MaxLength = 8, Dock = DockStyle.Fill };
        private readonly Label _error = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(560, 0) };
        public Profile SavedProfile { get; private set; }

        public MDNetworkSettingsDialog(ProfileStore store, Profile profile)
        {
            _store = store;
            Text = "MDRemoteNG PIN and local network";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(650, 690);
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            var layout = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(18), ColumnCount = 2 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddRow(layout, "Computer name", _displayName);
            AddWide(layout, _discovery);
            AddWide(layout, _autoConnect);
            AddWide(layout, new Label { Text = "The helper continues discovery after you close the window. Only matching PINs receive connection credentials. PINs and credentials are protected for your Windows account.", AutoSize = true, MaximumSize = new Size(575, 0), Margin = new Padding(3, 8, 3, 12) });
            AddWide(layout, _share);
            AddWide(layout, new Label { Text = "Enter an account that may access this computer. Its RDP, SSH or VNC server must already be enabled and allowed through the firewall. These credentials are shared with every matching PIN holder on the local network.", AutoSize = true, MaximumSize = new Size(575, 0), Margin = new Padding(3, 6, 3, 12) });
            _protocol.Items.AddRange(new object[] { "RDP", "SSH", "VNC" });
            AddRow(layout, "Protocol", _protocol);
            AddRow(layout, "Port", _port);
            AddRow(layout, "Username", _username);
            AddRow(layout, "Domain (RDP)", _domain);
            AddRow(layout, "Password", _password);
            AddWide(layout, new Label { Text = "Credentials apply only to this computer. They do not include your saved connection tree.", AutoSize = true, MaximumSize = new Size(575, 0), Margin = new Padding(3, 4, 3, 12) });
            AddWide(layout, _changePin);
            AddRow(layout, "Current PIN", _currentPin);
            AddRow(layout, "New PIN", _newPin);
            AddRow(layout, "Confirm new PIN", _confirmPin);
            AddWide(layout, new Label { Text = "4–8 letters, numbers or symbols, case insensitive, without spaces.", AutoSize = true, Margin = new Padding(3, 3, 3, 12) });
            AddWide(layout, _error);
            scroll.Controls.Add(layout);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(12), FlowDirection = FlowDirection.RightToLeft };
            var save = new Button { Text = "Save", AutoSize = true };
            save.Click += OnSave;
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);
            Controls.Add(scroll);
            Controls.Add(buttons);
            AcceptButton = save;
            CancelButton = cancel;
            _displayName.Text = profile.DisplayName;
            _discovery.Checked = profile.DiscoveryEnabled;
            _autoConnect.Checked = profile.AutoConnect;
            _share.Checked = profile.SharedConnection != null;
            _protocol.SelectedItem = profile.SharedConnection?.Protocol ?? "RDP";
            _port.Value = Math.Clamp(profile.SharedConnection?.Port ?? 3389, 1, 65535);
            _username.Text = profile.SharedConnection?.Username ?? "";
            _domain.Text = profile.SharedConnection?.Domain ?? "";
            _password.Text = profile.SharedConnection?.Password ?? "";
            _share.CheckedChanged += (_, _) => UpdateEnabled();
            _changePin.CheckedChanged += (_, _) => UpdateEnabled();
            _protocol.SelectedIndexChanged += (_, _) => _port.Value = _protocol.Text switch { "SSH" => 22, "VNC" => 5900, _ => 3389 };
            UpdateEnabled();
            FormClosed += (_, _) => { _password.Clear(); _currentPin.Clear(); _newPin.Clear(); _confirmPin.Clear(); };
        }

        private static void AddWide(TableLayoutPanel panel, Control control)
        {
            int row = panel.RowCount++;
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.Controls.Add(control, 0, row);
            panel.SetColumnSpan(control, 2);
        }

        private static void AddRow(TableLayoutPanel panel, string text, Control control)
        {
            int row = panel.RowCount++;
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.Controls.Add(new Label { Text = text, AutoSize = true, Margin = new Padding(3, 7, 3, 7) }, 0, row);
            control.Margin = new Padding(3, 4, 3, 4);
            panel.Controls.Add(control, 1, row);
        }

        private void UpdateEnabled()
        {
            foreach (Control control in new Control[] { _protocol, _port, _username, _domain, _password }) control.Enabled = _share.Checked;
            foreach (Control control in new Control[] { _currentPin, _newPin, _confirmPin }) control.Enabled = _changePin.Checked;
        }

        private void OnSave(object sender, EventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_displayName.Text)) throw new ArgumentException("Enter a computer name.");
                if (_share.Checked && string.IsNullOrWhiteSpace(_username.Text) && _protocol.Text != "VNC")
                    throw new ArgumentException("Enter the account name for this computer.");
                if (_changePin.Checked && PinPolicy.Normalize(_newPin.Text) != PinPolicy.Normalize(_confirmPin.Text))
                    throw new ArgumentException("The new PINs do not match.");

                var profile = _store.Load();
                profile.DisplayName = _displayName.Text.Trim();
                profile.DiscoveryEnabled = _discovery.Checked;
                profile.AutoConnect = _autoConnect.Checked;
                profile.SharedConnection = _share.Checked ? new SharedConnection
                {
                    Protocol = _protocol.Text,
                    Port = (int)_port.Value,
                    Username = _username.Text.Trim(),
                    Domain = _domain.Text.Trim(),
                    Password = _password.Text
                } : null;
                SavedProfile = _store.UpdateProfile(profile,
                    _changePin.Checked ? _currentPin.Text : null,
                    _changePin.Checked ? _newPin.Text : null);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (PinLockedException ex)
            {
                _error.Text = "PIN changes are locked until " + ex.LockedUntil.ToLocalTime().ToString("T") + ".";
                _currentPin.Clear();
            }
            catch (UnauthorizedAccessException)
            {
                _error.Text = "The current PIN was not accepted, or the profile could not be saved.";
                _currentPin.Clear();
            }
            catch (ArgumentException ex) { _error.Text = ex.Message; }
            catch (Exception ex) when (ex is IOException || ex is CryptographicException || ex is InvalidOperationException)
            {
                _error.Text = "The protected profile could not be saved. Check your Windows profile permissions and try again.";
            }
        }
    }
}
