using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MDRemoteNG.Discovery;
using mRemoteNG.App;
using mRemoteNG.Connection;
using mRemoteNG.Connection.Protocol;
using mRemoteNG.Messages;

namespace mRemoteNG.UI.Forms
{
    public partial class FrmMain
    {
        private MDPeerController _mdPeers;

        private void InitializeMdDiscovery()
        {
            if (_mdPeers != null || !MDRemoteNgSession.IsUnlocked) return;
            _mdPeers = new MDPeerController(this, msMain);
            FormClosed += (_, _) => _mdPeers.Dispose();
            _mdPeers.Start();
        }
    }

    /// <summary>Only unlocked desktop sessions may turn helper results into connections.</summary>
    internal sealed class MDPeerController : IDisposable
    {
        private readonly Form _owner;
        private readonly ToolStripMenuItem _menu = new("&Local peers");
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };
        private readonly CancellationTokenSource _stop = new();
        private readonly Dictionary<Guid, ConnectionInfo> _connections = new();
        private readonly HashSet<Guid> _attempted = new();
        private bool _polling;
        private bool _disposed;
        private DateTimeOffset _lastFailure;

        public MDPeerController(Form owner, MenuStrip menu)
        {
            _owner = owner;
            menu.Items.Add(_menu);
            _timer.Tick += async (_, _) => await PollAsync();
            MDRemoteNgSession.SettingsChanged += SettingsChanged;
        }

        public void Start()
        {
            StartHelper();
            _timer.Start();
            _ = PollAsync();
        }

        private void SettingsChanged(object sender, EventArgs args)
        {
            _attempted.Clear();
            _connections.Clear();
            RenderMenu(Array.Empty<DiscoveredPeer>());
            StartHelper();
            _ = PollAsync();
        }

        private void StartHelper()
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Discovery", "MDRemoteNG.Discovery.exe");
                var enabled = MDRemoteNgSession.Current?.DiscoveryEnabled == true;
                DiscoveryClient.SetAutoStart(path, enabled);
                if (enabled) DiscoveryClient.EnsureRunning(path);
            }
            catch (Exception ex) { ReportFailure(ex); }
        }

        private async Task PollAsync()
        {
            if (_polling || _disposed) return;
            _polling = true;
            try
            {
                var profile = MDRemoteNgSession.Current;
                IReadOnlyList<DiscoveredPeer> peers = profile?.DiscoveryEnabled == true
                    ? await DiscoveryClient.GetPeersAsync(_stop.Token)
                    : Array.Empty<DiscoveredPeer>();
                if (_disposed) return;
                // A settings change invalidates any response that was already in flight.
                if (!ReferenceEquals(profile, MDRemoteNgSession.Current)) return;
                foreach (Guid id in _attempted.Except(peers.Select(p => p.InstanceId)).ToArray())
                {
                    _attempted.Remove(id);
                    _connections.Remove(id);
                }
                RenderMenu(peers);
                if (profile?.AutoConnect == true)
                    foreach (var peer in peers.Where(p => p.SharedConnection != null))
                        if (_owner.Enabled && !Application.OpenForms.Cast<Form>().Any(f => f.Modal) &&
                            _attempted.Add(peer.InstanceId)) OpenPeer(peer);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!_disposed)
                {
                    RenderMenu(Array.Empty<DiscoveredPeer>(), "Discovery unavailable");
                    ReportFailure(ex);
                    StartHelper();
                }
            }
            finally { _polling = false; }
        }

        private void RenderMenu(IReadOnlyList<DiscoveredPeer> peers, string emptyText = "Waiting for matching PIN peers…")
        {
            var displayedProfile = MDRemoteNgSession.Current;
            var previous = _menu.DropDownItems.Cast<ToolStripItem>().ToArray();
            _menu.DropDownItems.Clear();
            foreach (var item in previous) item.Dispose();
            _menu.Text = $"&Local peers ({peers.Count})";
            var settings = new ToolStripMenuItem("PIN && local discovery settings…");
            settings.Click += (_, _) => MDRemoteNgSession.ShowSettings(_owner);
            _menu.DropDownItems.Add(settings);
            _menu.DropDownItems.Add(new ToolStripSeparator());
            if (peers.Count == 0)
            {
                string label = MDRemoteNgSession.Current?.DiscoveryEnabled == true ? emptyText : "Local discovery disabled";
                _menu.DropDownItems.Add(new ToolStripMenuItem(label) { Enabled = false });
            }
            foreach (var peer in peers.OrderBy(p => p.DisplayName))
            {
                var item = new ToolStripMenuItem($"{peer.DisplayName.Replace("&", "&&")} ({peer.Address})" +
                    (peer.SharedConnection == null ? " — no endpoint shared" : "")) { Enabled = peer.SharedConnection != null };
                item.Click += (_, _) =>
                {
                    if (ReferenceEquals(displayedProfile, MDRemoteNgSession.Current)) OpenPeer(peer);
                };
                _menu.DropDownItems.Add(item);
            }
        }

        private void OpenPeer(DiscoveredPeer peer)
        {
            if (_disposed || !MDRemoteNgSession.IsUnlocked || MDRemoteNgSession.Current?.DiscoveryEnabled != true) return;
            var shared = peer.SharedConnection;
            if (shared == null) return;
            shared.Validate();
            if (!_connections.TryGetValue(peer.InstanceId, out var connection))
            {
                connection = new ConnectionInfo("mdpeer-" + peer.InstanceId.ToString("N")) { IsQuickConnect = true };
                connection.Inheritance.EverythingInherited = false;
                _connections.Add(peer.InstanceId, connection);
            }
            connection.Name = peer.DisplayName;
            connection.Hostname = peer.Address;
            connection.Protocol = shared.Protocol switch { "RDP" => ProtocolType.RDP, "SSH" => ProtocolType.SSH2, "VNC" => ProtocolType.VNC, _ => throw new InvalidDataException("Unsupported peer protocol.") };
            connection.Port = shared.Port;
            connection.Username = shared.Username;
            connection.Domain = shared.Domain;
            connection.Password = shared.Password;
            // These transient connections never enter the saved connection tree.
            Runtime.ConnectionInitiator.OpenConnection(connection);
        }

        private void ReportFailure(Exception exception)
        {
            if (DateTimeOffset.UtcNow - _lastFailure < TimeSpan.FromMinutes(1)) return;
            _lastFailure = DateTimeOffset.UtcNow;
            Runtime.MessageCollector.AddMessage(MessageClass.WarningMsg, "MDRemoteNG local discovery: " + exception.GetType().Name);
        }

        public void Dispose()
        {
            _disposed = true;
            _timer.Stop();
            _timer.Dispose();
            _stop.Cancel();
            MDRemoteNgSession.SettingsChanged -= SettingsChanged;
            _connections.Clear();
            _attempted.Clear();
        }
    }
}
