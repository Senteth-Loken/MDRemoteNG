using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using MDRemoteNG.Host;
using Microsoft.Win32.SafeHandles;

namespace mRemoteNG.App
{
    public static class HostSetupClient
    {
        private const string SetupSwitch = "--configure-host";
        private const string PipePrefix = "MDRemoteNG.HostSetup.";

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

        public static Task<HostConfigurationResult> ConfigureAsync(Guid instanceId, CancellationToken cancellationToken = default)
            => ConfigureCoreAsync(instanceId, null, cancellationToken);

        public static async Task<HostConfigurationResult> ConfigureCurrentAccountAsync(Guid instanceId, string password, CancellationToken cancellationToken = default)
        {
            using var identity = WindowsIdentity.GetCurrent();
            string name = identity.Name;
            int separator = name.IndexOf('\\');
            if (separator <= 0 || separator == name.Length - 1 || identity.User == null)
                throw new HostSetupException("Your current Windows account could not be identified.");
            var credentials = new HostAccountCredentials
            {
                Username = name.Substring(separator + 1),
                Domain = name.Substring(0, separator),
                Password = password,
                Sid = identity.User.Value
            };
            // Validate before asking for elevation, without changing the account.
            await Task.Run(() => WindowsHostPlatform.ValidateCredentials(credentials), cancellationToken).ConfigureAwait(false);
            return await ConfigureCoreAsync(instanceId, credentials, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<HostConfigurationResult> ConfigureCoreAsync(Guid instanceId, HostAccountCredentials credentials, CancellationToken cancellationToken)
        {
            if (instanceId == Guid.Empty) throw new HostSetupException("The local MDRemoteNG profile is unavailable.");
            Guid requestId = Guid.NewGuid();
            string pipeName = PipePrefix + requestId.ToString("N");
            using var identity = WindowsIdentity.GetCurrent();
            var access = new PipeSecurity();
            access.AddAccessRule(new PipeAccessRule(identity.User ?? throw new HostSetupException("Your Windows account could not be identified."), PipeAccessRights.FullControl, AccessControlType.Allow));
            access.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            access.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, access);
            using var recipient = RSA.Create(3072);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Process child = null;
            try
            {
                string executable = Path.Combine(AppContext.BaseDirectory, "MDRemoteNG.exe");
                if (!File.Exists(executable)) throw new HostSetupException("The MDRemoteNG launcher is missing. Reinstall the complete application folder.");
                var start = new ProcessStartInfo(executable)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = AppContext.BaseDirectory,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                // Only identifiers and an ephemeral PUBLIC key are command-line arguments.
                start.ArgumentList.Add(SetupSwitch);
                start.ArgumentList.Add(pipeName);
                start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
                start.ArgumentList.Add(instanceId.ToString("N"));
                start.ArgumentList.Add(Convert.ToBase64String(recipient.ExportSubjectPublicKeyInfo()));
                child = await Task.Run(() => Process.Start(start), cancellationToken).ConfigureAwait(false)
                    ?? throw new HostSetupException("Windows could not start host configuration.");
                timeout.CancelAfter(TimeSpan.FromMinutes(5));
                Task exit = child.WaitForExitAsync();
                for (int attempts = 0; ; attempts++)
                {
                    Task connection = pipe.WaitForConnectionAsync(timeout.Token);
                    if (await Task.WhenAny(connection, exit).ConfigureAwait(false) == exit && !pipe.IsConnected)
                        throw new HostSetupException("Host configuration closed before it could connect. Try again and allow the Windows administrator prompt.");
                    await connection.ConfigureAwait(false);
                    if (GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint clientId) && clientId == child.Id)
                        break;
                    pipe.Disconnect();
                    if (attempts >= 7) throw new HostSetupException("The host configuration channel could not be verified.");
                }
                byte[] hello = await HostSetupTransport.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
                byte[] setupKey = HostSetupTransport.ReadHello(hello, recipient, requestId);
                var request = new HostSetupRequest
                {
                    Mode = credentials == null ? "managed" : "current-account",
                    Credentials = credentials
                };
                await HostSetupTransport.WriteAsync(pipe, HostSetupTransport.ProtectRequest(request, setupKey, requestId), timeout.Token).ConfigureAwait(false);
                byte[] frame = await HostSetupTransport.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
                HostSetupReply reply = HostSetupTransport.Unprotect(frame, recipient, requestId);
                await child.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                if (reply.Result == null) throw new HostSetupException(reply.Error);
                if (child.ExitCode != 0) throw new HostSetupException("Windows could not finish host configuration.");
                HostConfigurationResult result = reply.Result;
                if (result.Protocol != "RDP" || result.Port is < 1 or > 65535 ||
                    string.IsNullOrWhiteSpace(result.Username) || result.Username.Length > 256 ||
                    !string.Equals(result.Domain, credentials?.Domain ?? Environment.MachineName, StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrEmpty(result.Password) || result.Password.Length > 1024 || result.StatusMessage?.Length > 2048 ||
                    (credentials != null && (!string.Equals(result.Username, credentials.Username, StringComparison.OrdinalIgnoreCase) ||
                                             result.Password != credentials.Password)))
                    throw new HostSetupException("Windows returned incomplete host configuration details. Run auto-configuration again.");
                return result;
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
            {
                throw new OperationCanceledException("The Windows administrator prompt was cancelled.", exception, cancellationToken);
            }
            catch (HostSetupException) { throw; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                throw new HostSetupException("Host configuration timed out. Run it again when Windows is ready.");
            }
            catch (Exception exception) when (exception is IOException || exception is CryptographicException ||
                                              exception is Win32Exception || exception is System.Text.Json.JsonException ||
                                              exception is UnauthorizedAccessException)
            {
                throw new HostSetupException("Host configuration could not complete securely. Check Windows permissions and try again.");
            }
            finally { child?.Dispose(); }
        }

        // This child process uses Windows elevation, never the stored MDRemoteNG PIN.
        // It cannot open the main UI or read the parent's protected credential profile.
        internal static bool TryRunElevatedSetup(string[] arguments, out int exitCode)
        {
            exitCode = 1;
            if (arguments.Length == 0 || arguments[0] != SetupSwitch) return false;
            if (arguments.Length != 5 || !arguments[1].StartsWith(PipePrefix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(arguments[1].Substring(PipePrefix.Length), "N", out Guid requestId) ||
                !int.TryParse(arguments[2], NumberStyles.None, CultureInfo.InvariantCulture, out int parentId) || parentId <= 0 ||
                !Guid.TryParseExact(arguments[3], "N", out Guid instanceId) || instanceId == Guid.Empty)
                return true;
            try
            {
                RunElevatedAsync(arguments[1], requestId, parentId, instanceId, Convert.FromBase64String(arguments[4]))
                    .GetAwaiter().GetResult();
                exitCode = 0;
            }
            catch (Exception) { /* Never log the account password or IPC payload. */ }
            return true;
        }

        private static async Task RunElevatedAsync(string pipeName, Guid requestId, int parentId, Guid instanceId, byte[] publicKey)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(15000, timeout.Token).ConfigureAwait(false);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverId) || serverId != parentId)
                throw new InvalidOperationException("Unknown host configuration requestor.");
            // Reject an invalid recipient key BEFORE making any machine changes.
            using (var recipient = RSA.Create())
            {
                recipient.ImportSubjectPublicKeyInfo(publicKey, out int consumed);
                if (consumed != publicKey.Length || recipient.KeySize < 3072 || recipient.KeySize > 4096)
                    throw new CryptographicException("Invalid host configuration recipient.");
            }
            using var setupKey = RSA.Create(3072);
            await HostSetupTransport.WriteAsync(pipe,
                HostSetupTransport.ProtectHello(setupKey.ExportSubjectPublicKeyInfo(), publicKey, requestId), timeout.Token).ConfigureAwait(false);
            byte[] requestFrame = await HostSetupTransport.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
            HostSetupRequest request = HostSetupTransport.ReadRequest(requestFrame, setupKey, requestId);
            HostSetupReply reply;
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new HostSetupException("Windows administrator approval is required to configure this computer.");
                var platform = new WindowsHostPlatform();
                string helper = Path.Combine(AppContext.BaseDirectory, "Discovery", "MDRemoteNG.Discovery.exe");
                HostConfigurationResult result;
                if (request.Mode == "current-account")
                {
                    if (!string.Equals(request.Credentials.Sid, HostSetupIdentity.ParentAccountSid(parentId), StringComparison.Ordinal))
                        throw new HostSetupException("The requested Windows account does not match the application user.");
                    result = HostConfiguration.ConfigureCurrentAccount(platform, instanceId, helper, request.Credentials);
                }
                else result = HostConfiguration.Configure(platform, instanceId, helper);
                reply = new HostSetupReply { Result = result };
            }
            catch (HostSetupException exception) { reply = new HostSetupReply { Error = exception.Message }; }
            catch (Exception) { reply = new HostSetupReply { Error = "Windows could not configure remote access on this computer." }; }
            await HostSetupTransport.WriteAsync(pipe, HostSetupTransport.Protect(reply, publicKey, requestId), timeout.Token).ConfigureAwait(false);
        }

    }
}
