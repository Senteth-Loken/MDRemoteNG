using System;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows.Forms;
using MDRemoteNG.Core;
using MDRemoteNG.Host;
using Microsoft.Win32;
using mRemoteNG.App;

namespace mRemoteNG.UI.Forms
{
    public sealed class MDNetworkSettingsDialog : Form
    {
        private readonly ProfileStore _store;
        private readonly Func<Task<HostConfigurationResult>> _configureHost;
        private readonly Func<string, Task<HostConfigurationResult>> _configureCurrentAccount;
        private readonly Func<IWin32Window, string> _requestCurrentAccountPassword;
        private bool _configuringHost;
        private bool _hostCredentialsNeedSave;
        private readonly TextBox _displayName = new() { MaxLength = 64, Dock = DockStyle.Fill };
        private readonly CheckBox _discovery = new() { Text = "Find MDRemoteNG installs on my local network", AutoSize = true };
        private readonly CheckBox _autoConnect = new() { Text = "Open matching peers automatically while MDRemoteNG is unlocked", AutoSize = true };
        private readonly CheckBox _share = new() { Text = "Share a connection to this computer with matching PINs", AutoSize = true };
        private readonly Button _configureHostButton = new() { Text = "Auto-configure this computer", AutoSize = true };
        private readonly Button _configureCurrentAccountButton = new() { Text = "Configure for my Windows account", AutoSize = true };
        private readonly Label _hostStatus = new() { AutoSize = true, MaximumSize = new Size(575, 0), Margin = new Padding(3, 3, 3, 8) };
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
        private readonly Button _save = new() { Text = "Save", AutoSize = true };
        private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        public Profile SavedProfile { get; private set; }
        public event EventHandler HostConfigured;

        public MDNetworkSettingsDialog(ProfileStore store, Profile profile, Func<Task<HostConfigurationResult>> configureHost = null,
            Func<string, Task<HostConfigurationResult>> configureCurrentAccount = null,
            Func<IWin32Window, string> requestCurrentAccountPassword = null)
        {
            _store = store;
            _configureHost = configureHost ?? (() => HostSetupClient.ConfigureAsync(profile.InstanceId));
            _configureCurrentAccount = configureCurrentAccount ?? (password => HostSetupClient.ConfigureCurrentAccountAsync(profile.InstanceId, password));
            _requestCurrentAccountPassword = requestCurrentAccountPassword ?? PromptForWindowsPassword;
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
            _configureHostButton.Click += async (_, _) => await ConfigureHostAsync();
            AddWide(layout, _configureHostButton);
            AddWide(layout, new Label { Text = "Auto-configure prepares Remote Desktop, creates a dedicated local non-administrator account with a generated password, and immediately saves it for matching PIN holders on your local network. Windows will ask for administrator approval.", AutoSize = true, MaximumSize = new Size(575, 0), Margin = new Padding(3, 6, 3, 8) });
            _configureCurrentAccountButton.Click += async (_, _) => await ConfigureCurrentAccountHostAsync();
            AddWide(layout, _configureCurrentAccountButton);
            AddWide(layout, new Label { Text = "Alternatively, use your current Windows account and its password. Matching PIN holders will receive remote access with that account's existing permissions. A Windows Hello PIN is not its password.", AutoSize = true, MaximumSize = new Size(575, 0), Margin = new Padding(3, 6, 3, 8) });
            AddWide(layout, _hostStatus);
            _protocol.Items.AddRange(new object[] { "RDP", "SSH", "VNC" });
            AddRow(layout, "Protocol", _protocol);
            AddRow(layout, "Port", _port);
            AddRow(layout, "Username", _username);
            AddRow(layout, "Domain (RDP)", _domain);
            AddRow(layout, "Password", _password);
            AddWide(layout, new Label { Text = "You can also enter an existing RDP, SSH or VNC account manually; its server must already be enabled. Windows cannot reveal your existing account password. These credentials apply only to this computer.", AutoSize = true, MaximumSize = new Size(575, 0), Margin = new Padding(3, 4, 3, 12) });
            AddWide(layout, _changePin);
            AddRow(layout, "Current PIN", _currentPin);
            AddRow(layout, "New PIN", _newPin);
            AddRow(layout, "Confirm new PIN", _confirmPin);
            AddWide(layout, new Label { Text = "4–8 letters, numbers or symbols, case insensitive, without spaces.", AutoSize = true, Margin = new Padding(3, 3, 3, 12) });
            AddWide(layout, _error);
            scroll.Controls.Add(layout);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(12), FlowDirection = FlowDirection.RightToLeft };
            _save.Click += OnSave;
            buttons.Controls.Add(_save);
            buttons.Controls.Add(_cancel);
            Controls.Add(scroll);
            Controls.Add(buttons);
            AcceptButton = _save;
            CancelButton = _cancel;
            _displayName.Text = profile.DisplayName;
            _discovery.Checked = profile.DiscoveryEnabled;
            _autoConnect.Checked = profile.AutoConnect;
            _share.Checked = profile.SharedConnection != null;
            _protocol.SelectedItem = profile.SharedConnection?.Protocol ?? "RDP";
            _port.Value = Math.Clamp(profile.SharedConnection?.Port ?? LocalRdpPort(), 1, 65535);
            _username.Text = profile.SharedConnection?.Username ?? Environment.UserName;
            _domain.Text = profile.SharedConnection?.Domain ?? Environment.UserDomainName;
            _password.Text = profile.SharedConnection?.Password ?? "";
            _share.CheckedChanged += (_, _) => UpdateEnabled();
            _changePin.CheckedChanged += (_, _) => UpdateEnabled();
            _protocol.SelectedIndexChanged += (_, _) => _port.Value = _protocol.Text switch { "SSH" => 22, "VNC" => 5900, _ => 3389 };
            UpdateEnabled();
            FormClosing += (_, e) => { if (_configuringHost) e.Cancel = true; };
            FormClosed += (_, _) => { _password.Clear(); _currentPin.Clear(); _newPin.Clear(); _confirmPin.Clear(); };
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // At larger DPI/font sizes keep the footer on the selected monitor;
            // the settings body already scrolls when the content is taller.
            Rectangle workArea = Screen.FromControl(this).WorkingArea;
            MaximumSize = new Size(Math.Max(1, workArea.Width - 24), Math.Max(1, workArea.Height - 24));
            Size = new Size(Math.Min(Width, MaximumSize.Width), Math.Min(Height, MaximumSize.Height));
        }

        private static string PromptForWindowsPassword(IWin32Window owner)
        {
            using var identity = WindowsIdentity.GetCurrent();
            using var prompt = new MDWindowsAccountPasswordDialog(identity.Name);
            return prompt.ShowDialog(owner) == DialogResult.OK ? prompt.Password : null;
        }

        private static int LocalRdpPort()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp");
                return key?.GetValue("PortNumber") is int port && port is >= 1 and <= 65535 ? port : 3389;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is IOException)
            {
                return 3389;
            }
        }

        private Task ConfigureHostAsync() => ConfigureHostCoreAsync(_configureHost,
            "Host configured and shared. The generated connection details are already saved.");

        private async Task ConfigureCurrentAccountHostAsync()
        {
            if (_configuringHost) return;
            string password = null;
            try
            {
                _error.Text = "";
                password = _requestCurrentAccountPassword(this);
                if (password == null)
                {
                    _hostStatus.ForeColor = SystemColors.ControlText;
                    _hostStatus.Text = "Windows account setup was cancelled. Your connection details have not changed.";
                    return;
                }
                if (password.Length == 0)
                {
                    ShowHostError("Enter this Windows account's password. A Windows Hello PIN cannot be used for this setup.");
                    return;
                }
                await ConfigureHostCoreAsync(() => _configureCurrentAccount(password),
                    "Host configured for your Windows account and shared. Its existing permissions apply; the connection details are already saved.");
            }
            catch (Exception)
            {
                ShowHostError("Windows account setup could not be started. Your connection details have not changed.");
            }
            finally { password = null; }
        }

        private async Task ConfigureHostCoreAsync(Func<Task<HostConfigurationResult>> configure, string successMessage)
        {
            if (_configuringHost) return;
            _configuringHost = true;
            _error.Text = "";
            _hostStatus.ForeColor = SystemColors.ControlText;
            _hostStatus.Text = "Preparing this computer. Complete the Windows administrator prompt if it appears…";
            UseWaitCursor = true;
            UpdateEnabled();
            try
            {
                HostConfigurationResult result = await configure();
                if (result == null) throw new InvalidDataException("Host setup returned no credentials.");
                var connection = new SharedConnection
                {
                    Protocol = result.Protocol,
                    Port = result.Port,
                    Username = result.Username,
                    Domain = result.Domain,
                    Password = result.Password
                };
                connection.Validate();
                if (connection.Protocol != "RDP" || string.IsNullOrWhiteSpace(connection.Username) || string.IsNullOrEmpty(connection.Password))
                    throw new InvalidDataException("Host setup returned invalid credentials.");

                // Apply the result together only after validation. Failed or cancelled
                // setup keeps the user's existing connection details intact.
                _protocol.SelectedItem = connection.Protocol;
                _port.Value = connection.Port;
                _username.Text = connection.Username;
                _domain.Text = connection.Domain;
                _password.Text = connection.Password;
                _share.Checked = true;
                _discovery.Checked = true;
                _hostCredentialsNeedSave = true;
                try
                {
                    // Host setup can rotate an app-managed account password. Persist it
                    // immediately so closing the settings dialog cannot lose that password.
                    Profile profile = _store.Load();
                    profile.SharedConnection = connection;
                    profile.DiscoveryEnabled = true;
                    SavedProfile = _store.UpdateProfile(profile);
                    _hostCredentialsNeedSave = false;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is CryptographicException || ex is InvalidOperationException)
                {
                    ShowHostError("Windows is configured, but the connection details could not be saved. Click Save to retry. The details are still in the fields below; you can run host setup again later if needed.");
                    return;
                }
                _hostStatus.ForeColor = Color.ForestGreen;
                _hostStatus.Text = successMessage +
                    (string.IsNullOrWhiteSpace(result.StatusMessage) ? "" : " " + result.StatusMessage);
                try { HostConfigured?.Invoke(this, EventArgs.Empty); }
                catch (Exception)
                {
                    _hostStatus.ForeColor = Color.DarkOrange;
                    _hostStatus.Text = "Host configured and saved. Reopen local network settings if discovery has not updated.";
                }
            }
            catch (OperationCanceledException)
            {
                _hostStatus.Text = "Host setup was cancelled. Your connection details have not changed.";
            }
            catch (HostSetupException ex)
            {
                ShowHostError(ex.Message);
            }
            catch (Exception)
            {
                ShowHostError("Host setup could not be completed. Check that this Windows edition supports Remote Desktop hosting and try again with administrator approval. Your connection details have not changed.");
            }
            finally
            {
                _configuringHost = false;
                UseWaitCursor = false;
                UpdateEnabled();
            }
        }

        private void ShowHostError(string message)
        {
            _error.Text = message;
            _hostStatus.ForeColor = Color.Firebrick;
            _hostStatus.Text = message;
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
            foreach (Control control in new Control[] { _displayName, _discovery, _autoConnect, _share, _changePin, _configureHostButton, _configureCurrentAccountButton, _save, _cancel })
                control.Enabled = !_configuringHost;
            foreach (Control control in new Control[] { _protocol, _port, _username, _domain, _password }) control.Enabled = _share.Checked && !_configuringHost;
            foreach (Control control in new Control[] { _currentPin, _newPin, _confirmPin }) control.Enabled = _changePin.Checked && !_configuringHost;
        }

        private void OnSave(object sender, EventArgs e)
        {
            if (_configuringHost) return;
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
                bool notifyHost = _hostCredentialsNeedSave;
                _hostCredentialsNeedSave = false;
                if (notifyHost)
                    try { HostConfigured?.Invoke(this, EventArgs.Empty); }
                    catch (Exception) { System.Diagnostics.Debug.WriteLine("Host settings notification failed."); }
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
