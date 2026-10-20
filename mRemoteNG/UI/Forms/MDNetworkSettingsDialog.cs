using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
        private readonly Func<string, Task<HostConfigurationResult>> _configureProtocolHost;
        private readonly Func<string, bool, Task<HostConfigurationResult>> _configureProtocolHostWithAccess;
        private readonly Func<string, string, Task<HostConfigurationResult>> _configureProtocolCurrentAccount;
        private readonly Func<IWin32Window, string> _requestCurrentAccountPassword;
        private readonly Dictionary<string, SharedConnection> _connectionDrafts = new(StringComparer.Ordinal);
        private readonly HashSet<string> _sharedProtocols = new(StringComparer.Ordinal);
        private readonly HashSet<string> _editedProtocols = new(StringComparer.Ordinal);
        private string _activeProtocol;
        private bool _loadingProtocol;
        private bool _clearAllConnections;
        private VncHostSettings _pendingVncHost;
        private bool _configuringHost;
        private bool _hostCredentialsNeedSave;
        private readonly TextBox _displayName = new() { MaxLength = 64, Dock = DockStyle.Fill };
        private readonly CheckBox _discovery = new() { Text = "Find MDRemoteNG installs on my local network", AutoSize = true };
        private readonly CheckBox _autoConnect = new() { Text = "Open matching peers automatically while MDRemoteNG is unlocked", AutoSize = true };
        private readonly CheckBox _share = new() { Text = "Share this protocol with matching PINs", AutoSize = true };
        private readonly Label _sharedSummary = new() { AutoSize = true, MaximumSize = new Size(350, 0), Margin = new Padding(3, 6, 3, 6) };
        private readonly Button _stopSharing = new() { Text = "Stop sharing all connections", AutoSize = true };
        private readonly Button _configureHostButton = new() { Text = "Auto-configure this computer", AutoSize = true };
        private readonly CheckBox _sshAdministrator = new() { Text = "Create a dedicated administrator account for SSH", AutoSize = true };
        private readonly Label _sshAdministratorWarning = new() { Text = "Anyone on this local network with the matching PIN can receive administrator credentials. Switching back or stopping sharing does not remove the administrator account or revoke credentials already shared.", AutoSize = true, MaximumSize = new Size(575, 0), ForeColor = Color.Firebrick, Margin = new Padding(3, 3, 3, 8) };
        private readonly Button _configureCurrentAccountButton = new() { Text = "Configure for my Windows account", AutoSize = true };
        private readonly Label _hostDescription = new() { AutoSize = true, MaximumSize = new Size(575, 0), Margin = new Padding(3, 6, 3, 8) };
        private readonly Label _currentAccountDescription = new() { AutoSize = true, MaximumSize = new Size(575, 0), Margin = new Padding(3, 6, 3, 8) };
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
            Func<IWin32Window, string> requestCurrentAccountPassword = null,
            Func<string, Task<HostConfigurationResult>> configureProtocolHost = null,
            Func<string, string, Task<HostConfigurationResult>> configureProtocolCurrentAccount = null,
            Func<string, bool, Task<HostConfigurationResult>> configureProtocolHostWithAccess = null)
        {
            _store = store;
            _configureProtocolHost = configureProtocolHost ?? (configureHost != null
                ? _ => configureHost() : protocol => HostSetupClient.ConfigureAsync(profile.InstanceId, protocol));
            _configureProtocolHostWithAccess = configureProtocolHostWithAccess ?? (configureProtocolHost != null || configureHost != null
                ? (protocol, administratorAccess) => administratorAccess
                    ? Task.FromException<HostConfigurationResult>(new HostSetupException("This host setup provider does not support administrator SSH accounts."))
                    : _configureProtocolHost(protocol)
                : (protocol, administratorAccess) => HostSetupClient.ConfigureAsync(profile.InstanceId, protocol, administratorAccess));
            _configureProtocolCurrentAccount = configureProtocolCurrentAccount ?? (configureCurrentAccount != null
                ? (_, password) => configureCurrentAccount(password)
                : (protocol, password) => HostSetupClient.ConfigureCurrentAccountAsync(profile.InstanceId, protocol, password));
            _requestCurrentAccountPassword = requestCurrentAccountPassword ?? PromptForWindowsPassword;
            foreach (SharedConnection connection in SharedConnectionSet.GetConnections(profile.SharedConnection, profile.SharedConnections))
            {
                _connectionDrafts.Add(connection.Protocol, CopyConnection(connection));
                _sharedProtocols.Add(connection.Protocol);
            }
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
            _protocol.Items.AddRange(new object[] { "RDP", "SSH", "VNC" });
            AddRow(layout, "Protocol to configure", _protocol);
            AddWide(layout, _share);
            _stopSharing.Click += OnStopSharing;
            var sharing = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
            sharing.Controls.Add(_sharedSummary);
            sharing.Controls.Add(_stopSharing);
            AddWide(layout, sharing);
            _sshAdministrator.CheckedChanged += (_, _) => UpdateDedicatedHostChoice();
            AddWide(layout, _sshAdministrator);
            AddWide(layout, _sshAdministratorWarning);
            _configureHostButton.Click += async (_, _) => await ConfigureHostAsync();
            AddWide(layout, _configureHostButton);
            AddWide(layout, _hostDescription);
            _configureCurrentAccountButton.Click += async (_, _) => await ConfigureCurrentAccountHostAsync();
            AddWide(layout, _configureCurrentAccountButton);
            AddWide(layout, _currentAccountDescription);
            AddWide(layout, _hostStatus);
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
            _protocol.SelectedItem = profile.SharedConnection?.Protocol ?? "RDP";
            LoadProtocol(_protocol.Text);
            _share.CheckedChanged += (_, _) => { RememberProtocol(); UpdateEnabled(); };
            _changePin.CheckedChanged += (_, _) => UpdateEnabled();
            _protocol.SelectedIndexChanged += (_, _) =>
            {
                RememberProtocol();
                _error.Text = "";
                _hostStatus.ForeColor = SystemColors.ControlText;
                _hostStatus.Text = _hostCredentialsNeedSave ? "Configured connection details still need to be saved. Click Save to retry." : "";
                LoadProtocol(_protocol.Text);
            };
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

        private static SharedConnection CopyConnection(SharedConnection connection) => new()
        {
            Protocol = connection.Protocol, Port = connection.Port, Username = connection.Username,
            Domain = connection.Domain, Password = connection.Password,
            CertificateFingerprint = connection.CertificateFingerprint,
            SshHostKeyFingerprints = connection.SshHostKeyFingerprints.ToList()
        };

        private void RememberProtocol()
        {
            if (_loadingProtocol || _activeProtocol == null) return;
            var connection = _connectionDrafts.TryGetValue(_activeProtocol, out var existing)
                ? CopyConnection(existing) : new SharedConnection { Protocol = _activeProtocol };
            connection.Port = (int)_port.Value;
            connection.Username = _username.Text;
            connection.Domain = _domain.Text;
            connection.Password = _password.Text;
            _connectionDrafts[_activeProtocol] = connection;
            if (_share.Checked) _sharedProtocols.Add(_activeProtocol);
            else _sharedProtocols.Remove(_activeProtocol);
            _editedProtocols.Add(_activeProtocol);
        }

        private void LoadProtocol(string protocol)
        {
            _loadingProtocol = true;
            try
            {
                _activeProtocol = protocol;
                _connectionDrafts.TryGetValue(protocol, out var connection);
                _port.Value = connection?.Port ?? (protocol switch { "SSH" => 22, "VNC" => 5900, _ => LocalRdpPort() });
                _username.Text = connection?.Username ?? (protocol == "VNC" ? "" : Environment.UserName);
                _domain.Text = connection?.Domain ?? (protocol == "RDP" ? Environment.UserDomainName : "");
                _password.Text = connection?.Password ?? "";
                _share.Checked = _sharedProtocols.Contains(protocol);
                if (protocol != "SSH") _sshAdministrator.Checked = false;
                UpdateDedicatedHostChoice();
                _currentAccountDescription.Text = protocol == "VNC"
                    ? "VNC shares the existing desktop using credentials generated by MDRemoteNG. It does not use your Windows account password."
                    : "Alternatively, use your current Windows account and its password. Matching PIN holders receive access with that account's existing permissions. A Windows Hello PIN is not its password.";
            }
            finally { _loadingProtocol = false; }
            UpdateEnabled();
        }

        private void UpdateDedicatedHostChoice()
        {
            string protocol = _protocol.Text;
            bool administratorAccess = protocol == "SSH" && _sshAdministrator.Checked;
            _sshAdministrator.Visible = protocol == "SSH";
            _sshAdministratorWarning.Visible = administratorAccess;
            _configureHostButton.Text = protocol switch
            {
                "SSH" when administratorAccess => "Configure administrator SSH access",
                "SSH" => "Configure SSH terminal access",
                "VNC" => "Configure VNC desktop sharing",
                _ => "Auto-configure Remote Desktop"
            };
            _hostDescription.Text = protocol == "VNC"
                ? "Prepares sharing of this computer's existing desktop with a generated VNC password. Matching PIN holders can control the desktop. The connection is saved immediately. Windows may ask for administrator approval."
                : "Prepares " + (protocol == "SSH" ? "SSH terminal access" : "Remote Desktop") + ", creates a dedicated local " +
                  (administratorAccess ? "administrator" : "non-administrator") + " account with a generated password, and immediately saves it for matching PIN holders. Windows will ask for administrator approval.";
        }

        private void OnStopSharing(object sender, EventArgs e)
        {
            if (_configuringHost) return;
            RememberProtocol();
            _clearAllConnections = true;
            _sharedProtocols.Clear();
            _loadingProtocol = true;
            try { _share.Checked = false; }
            finally { _loadingProtocol = false; }
            _hostStatus.ForeColor = SystemColors.ControlText;
            _hostStatus.Text = "All connection sharing will stop when you click Save. Cancel keeps your saved sharing settings.";
            UpdateEnabled();
        }

        private Task ConfigureHostAsync()
        {
            string protocol = _protocol.Text;
            bool administratorAccess = protocol == "SSH" && _sshAdministrator.Checked;
            return ConfigureHostCoreAsync(() => _configureProtocolHostWithAccess(protocol, administratorAccess),
                administratorAccess ? "Administrator SSH host configured and shared. The generated administrator credentials are already saved." :
                    "Host configured and shared. The generated connection details are already saved.", administratorAccess);
        }

        private async Task ConfigureCurrentAccountHostAsync()
        {
            if (_configuringHost) return;
            if (_protocol.Text == "VNC") return;
            string protocol = _protocol.Text;
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
                await ConfigureHostCoreAsync(() => _configureProtocolCurrentAccount(protocol, password),
                    "Host configured for your Windows account and shared. Its existing permissions apply; the connection details are already saved.");
            }
            catch (Exception)
            {
                ShowHostError("Windows account setup could not be started. Your connection details have not changed.");
            }
            finally { password = null; }
        }

        private async Task ConfigureHostCoreAsync(Func<Task<HostConfigurationResult>> configure, string successMessage, bool administratorAccess = false)
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
                if (result.AdministratorAccess != administratorAccess || (result.AdministratorAccess && result.Protocol != "SSH"))
                    throw new HostSetupException("Host setup returned an unexpected account access level. Your connection details have not changed.");
                var connection = new SharedConnection
                {
                    Protocol = result.Protocol,
                    Port = result.Port,
                    Username = result.Username,
                    Domain = result.Domain,
                    Password = result.Password,
                    CertificateFingerprint = result.CertificateFingerprint,
                    SshHostKeyFingerprints = result.SshHostKeyFingerprints.ToList()
                };
                connection.Validate();
                if ((connection.Protocol != "VNC" && string.IsNullOrWhiteSpace(connection.Username)) || string.IsNullOrEmpty(connection.Password))
                    throw new InvalidDataException("Host setup returned invalid credentials.");
                if (connection.Protocol == "SSH" && connection.SshHostKeyFingerprints.Count == 0)
                    throw new InvalidDataException("Host setup returned no SSH host-key pins.");
                VncHostSettings vncHost = null;
                if (connection.Protocol == "VNC")
                {
                    vncHost = new VncHostSettings { CertificatePfx = result.VncCertificatePfx.ToArray() };
                    vncHost.Validate();
                    if (string.IsNullOrEmpty(connection.CertificateFingerprint)) throw new InvalidDataException("Host setup returned no VNC certificate pin.");
                    using var certificate = X509CertificateLoader.LoadPkcs12(vncHost.CertificatePfx, "", X509KeyStorageFlags.EphemeralKeySet);
                    if (!certificate.HasPrivateKey || !string.Equals(certificate.GetCertHashString(HashAlgorithmName.SHA256),
                            connection.CertificateFingerprint, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Host setup returned a mismatched VNC certificate.");
                }

                // Apply the result together only after validation. Failed or cancelled
                // setup keeps the user's existing connection details intact.
                RememberProtocol();
                _connectionDrafts[connection.Protocol] = connection;
                _sharedProtocols.Add(connection.Protocol);
                _protocol.SelectedItem = connection.Protocol;
                LoadProtocol(connection.Protocol);
                _editedProtocols.Add(connection.Protocol);
                if (vncHost != null) _pendingVncHost = vncHost;
                _discovery.Checked = true;
                _hostCredentialsNeedSave = true;
                try
                {
                    // Host setup can rotate an app-managed account password. Persist it
                    // immediately so closing the settings dialog cannot lose that password.
                    Profile profile = _store.Load();
                    SharedConnectionSet.Upsert(profile, connection);
                    if (vncHost != null) profile.VncHost = vncHost;
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
                ShowHostError("Host setup could not be completed. Check the selected server's requirements and try again with administrator approval. Your connection details have not changed.");
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
            _protocol.Enabled = !_configuringHost;
            _sshAdministrator.Enabled = !_configuringHost && _protocol.Text == "SSH";
            foreach (Control control in new Control[] { _port, _password }) control.Enabled = _share.Checked && !_configuringHost;
            _username.Enabled = _share.Checked && _protocol.Text != "VNC" && !_configuringHost;
            _domain.Enabled = _share.Checked && _protocol.Text == "RDP" && !_configuringHost;
            _configureCurrentAccountButton.Enabled = !_configuringHost && _protocol.Text != "VNC";
            _stopSharing.Enabled = !_configuringHost && _sharedProtocols.Count > 0;
            _sharedSummary.Text = "Protocols selected: " + (_sharedProtocols.Count == 0 ? "none" :
                string.Join(", ", new[] { "RDP", "SSH", "VNC" }.Where(_sharedProtocols.Contains))) + ". Save applies manual changes.";
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
                RememberProtocol();
                foreach (string protocol in _editedProtocols.Where(_sharedProtocols.Contains))
                    if (protocol != "VNC" && string.IsNullOrWhiteSpace(_connectionDrafts[protocol].Username))
                        throw new ArgumentException("Enter the account name for " + protocol + ".");

                var profile = _store.Load();
                profile.DisplayName = _displayName.Text.Trim();
                profile.DiscoveryEnabled = _discovery.Checked;
                profile.AutoConnect = _autoConnect.Checked;
                if (_clearAllConnections) SharedConnectionSet.Clear(profile);
                foreach (string protocol in _editedProtocols.Where(protocol => protocol != _activeProtocol).Append(_activeProtocol))
                {
                    if (_sharedProtocols.Contains(protocol))
                    {
                        SharedConnection connection = CopyConnection(_connectionDrafts[protocol]);
                        connection.Username = connection.Username.Trim();
                        connection.Domain = connection.Domain.Trim();
                        SharedConnectionSet.Upsert(profile, connection);
                    }
                    else SharedConnectionSet.Remove(profile, protocol);
                }
                if (_pendingVncHost != null) profile.VncHost = _pendingVncHost;
                if (!SharedConnectionSet.GetConnections(profile.SharedConnection, profile.SharedConnections)
                    .Any(connection => connection.Protocol == "VNC" && !string.IsNullOrEmpty(connection.CertificateFingerprint)))
                    profile.VncHost = null;
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
