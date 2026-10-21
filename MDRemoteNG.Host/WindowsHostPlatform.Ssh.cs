using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Management;
using Microsoft.Win32;

namespace MDRemoteNG.Host;

public sealed record SshFirewallAllowance(int Profiles, string Protocol, string LocalPorts, string Application, string Service, string RemoteAddresses);

/// <summary>Conservative policy checks; existing administrator rules are never edited.</summary>
public static class SshFirewallPolicy
{
    public static void ValidateProfile(bool enabled, int defaultInboundAction)
    {
        if (!enabled || defaultInboundAction != 0)
            throw new HostSetupException("Automatic SSH setup requires Windows Firewall to be enabled with default inbound traffic blocked on every profile. Ask your administrator to configure that policy.");
    }

    public static void ValidateRules(IEnumerable<SshFirewallAllowance> rules, int port, string executable)
    {
        foreach (SshFirewallAllowance rule in rules)
        {
            if (rule.Protocol.ToUpperInvariant() is not ("TCP" or "6" or "ANY" or "256") ||
                !MatchesApplication(rule.Application, executable) || !IsAny(rule.Service) && !rule.Service.Equals("sshd", StringComparison.OrdinalIgnoreCase) ||
                !IncludesPort(rule.LocalPorts, port)) continue;
            bool publicOrAny = rule.Profiles == 0 || (rule.Profiles & 4) != 0;
            string[] addresses = rule.RemoteAddresses.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            bool subnetOnly = addresses.Length > 0 && addresses.All(value =>
                value.Equals("LocalSubnet", StringComparison.OrdinalIgnoreCase) || value.Equals("LocalSubnet4", StringComparison.OrdinalIgnoreCase) || value.Equals("LocalSubnet6", StringComparison.OrdinalIgnoreCase));
            if (publicOrAny || !subnetOnly)
                throw new HostSetupException("An existing enabled firewall allow rule could expose SSH beyond Private/Domain local subnets. Ask its administrator to restrict or disable the broad SSH/program/port rule, then retry. MDRemoteNG has preserved that rule.");
        }
    }

    private static bool IsAny(string value) => string.IsNullOrWhiteSpace(value) || value is "*" || value.Equals("Any", StringComparison.OrdinalIgnoreCase);
    private static bool MatchesApplication(string application, string executable)
    {
        if (IsAny(application)) return true;
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(application.Trim('"'))).Equals(executable, StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return true; } // An unverifiable rule cannot establish a safe listener scope.
    }
    private static bool IncludesPort(string ports, int port)
    {
        if (IsAny(ports)) return true;
        foreach (string value in ports.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string[] range = value.Split('-', 2);
            if (!int.TryParse(range[0], out int first)) return true;
            if (range.Length == 1) { if (port == first) return true; }
            else if (!int.TryParse(range[1], out int last)) return true;
            else if (port >= first && port <= last) return true;
        }
        return false;
    }
}

/// <summary>Checks effective OpenSSH settings without rewriting an existing administrator configuration.</summary>
public static class SshServerConfiguration
{
    public static void VerifyLoadedFiles(DateTime processStartedUtc, IEnumerable<DateTime> fileWritesUtc)
    {
        if (fileWritesUtc.Any(written => written > processStartedUtc))
            throw new HostSetupException("SSH configuration or host keys changed after the running service started. Restart SSH when its current sessions can be interrupted, then retry. MDRemoteNG has preserved the running sessions.");
    }

    public static void ValidateConfigurationText(string configuration)
    {
        foreach (string raw in configuration.Split('\n'))
        {
            string line = raw.Split('#', 2)[0].Trim();
            if (Regex.IsMatch(line, @"^Include(?:\s|=)", RegexOptions.IgnoreCase) ||
                (Regex.IsMatch(line, @"^Match(?:\s|=)", RegexOptions.IgnoreCase) &&
                 !Regex.IsMatch(line, @"^Match(?:\s+|\s*=\s*)Group\s+administrators\s*$", RegexOptions.IgnoreCase)))
                throw new HostSetupException("The existing SSH configuration contains conditional or included rules. Ask its administrator to configure password access for this account; MDRemoteNG has preserved the configuration.");
        }
    }

    public static (int Port, IReadOnlyList<string> HostKeyPaths) ParseEffective(string output, string username, bool hasOpenSshUsersGroup)
    {
        var settings = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = line.IndexOf(' ');
            if (separator < 0) continue;
            string name = line[..separator], value = line[(separator + 1)..].Trim();
            if (!settings.TryGetValue(name, out List<string>? values)) settings[name] = values = [];
            values.Add(value);
        }
        string Value(string name, string fallback = "") => settings.TryGetValue(name, out var values) ? values[0] : fallback;
        List<string> Values(string name) => settings.TryGetValue(name, out var values) ? values : [];
        var ports = Values("port").Distinct(StringComparer.Ordinal).ToArray();
        if (ports.Length != 1 || !int.TryParse(ports[0], NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
            throw new HostSetupException("SSH must have one valid listener port before it can be shared automatically.");
        if (Value("passwordauthentication") != "yes" || Value("authenticationmethods", "any") is not ("any" or "password"))
            throw new HostSetupException("The existing SSH server requires another authentication method. MDRemoteNG will not weaken its authentication policy; ask its administrator to enable suitable account access.");
        if (Value("forcecommand", "none") != "none" || Value("permittty", "yes") == "no")
            throw new HostSetupException("The existing SSH server restricts interactive shell access. MDRemoteNG has preserved its settings.");
        if (Values("denyusers").Count != 0 || Values("denygroups").Count != 0)
            throw new HostSetupException("The existing SSH server has account deny rules. Ask its administrator to verify access for this account; MDRemoteNG has preserved those rules.");
        var allowedUsers = Tokens(string.Join(' ', Values("allowusers")));
        if (allowedUsers.Count != 0 && !allowedUsers.Contains("*", StringComparer.Ordinal) && !allowedUsers.Contains(username.ToLowerInvariant(), StringComparer.Ordinal))
            throw new HostSetupException("The existing SSH server does not explicitly allow this account. Its account restrictions were preserved.");
        var allowedGroups = Tokens(string.Join(' ', Values("allowgroups")));
        if (allowedGroups.Count != 0 && !(hasOpenSshUsersGroup && allowedGroups.Contains("openssh users", StringComparer.Ordinal)))
            throw new HostSetupException("The existing SSH server restricts access to other groups. Ask its administrator to configure account access; MDRemoteNG has preserved those restrictions.");
        var listeners = Values("listenaddress");
        if (listeners.Count != 0 && listeners.All(value => value.StartsWith("127.", StringComparison.Ordinal) || value.StartsWith("[::1]:", StringComparison.Ordinal)))
            throw new HostSetupException("The existing SSH server listens only on this computer. Its listener settings were preserved.");
        var keys = Values("hostkey").Distinct(StringComparer.OrdinalIgnoreCase).Take(9).ToArray();
        if (keys.Length is < 1 or > 8) throw new HostSetupException("Windows could not identify the configured SSH host keys.");
        return (port, keys);
    }

    public static string PublicKeyFingerprint(string publicKey)
    {
        string[] fields = publicKey.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2 || fields[1].Length > 16384) throw new HostSetupException("An SSH host public key is invalid.");
        try
        {
            byte[] blob = Convert.FromBase64String(fields[1]);
            if (blob.Length < 16) throw new FormatException();
            return "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
        }
        catch (FormatException exception) { throw new HostSetupException("An SSH host public key is invalid.", exception); }
    }

    private static List<string> Tokens(string text) => Regex.Matches(text, "\"([^\"]+)\"|([^\\s]+)")
        .Select(match => (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).ToLowerInvariant()).ToList();
}

public sealed partial class WindowsHostPlatform
{
    private const string OpenSshUsers = "OpenSSH Users";

    public HostSshConfiguration PrepareSecureShell(string username, string domain, string helperExecutablePath, Guid instanceId)
    {
        string helper = Path.GetFullPath(helperExecutablePath);
        if (!File.Exists(helper)) throw new HostSetupException("The MDRemoteNG discovery helper is missing. Reinstall or rebuild the complete application folder.");
        string sshd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "sshd.exe");
        string guard = "MDRemoteNG " + instanceId.ToString("N")[..12] + " SSH installation guard";
        bool installing = !File.Exists(sshd);
        bool completingInstallation = installing || HasSshGuard(guard);
        bool guarded = false;
        try
        {
            EnsureSshFirewallEnabled();
            // Check broad existing allows before installing or starting a server. Repeat
            // with its effective port below so custom listeners receive the same checks.
            if (installing || !completingInstallation) ValidateEffectiveSshFirewall(22, sshd);
            if (completingInstallation)
            {
                if (installing)
                {
                    using RegistryKey? existing = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\sshd");
                    if (existing is not null) throw new HostSetupException("An existing SSH service uses another installation. Ask its administrator to configure it; MDRemoteNG has preserved it.");
                }
                // The Windows feature installer creates a broad allow rule. Block the exact
                // new server executable first, including Public networks, until scoped rules
                // are verified. Block rules override ordinary allow rules.
                AddFirewallRule(new HostFirewallRule(guard, 6, null, sshd, null), true, 7, "*");
                guarded = true;
                string installResult = installing ? RunSshPowerShell(InstallOpenSshScript, "install the Windows OpenSSH optional feature", TimeSpan.FromMinutes(10)) : "";
                RunSshPowerShell(RestrictInstalledSshFirewallScript, "restrict the new SSH firewall rule", TimeSpan.FromSeconds(30));
                ValidateEffectiveSshFirewall(22, sshd);
                if (installResult.Contains("MDREMOTENG_RESTART_REQUIRED", StringComparison.Ordinal))
                    throw new HostSetupException("Windows must restart to complete the OpenSSH optional feature. Restart Windows, then retry automatic SSH setup.");
                if (!File.Exists(sshd)) throw new HostSetupException("Windows OpenSSH installation did not produce its server executable. A Windows restart or administrator assistance may be required.");
            }
            VerifySshServiceImage(sshd);
            string configuration = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ssh", "sshd_config");
            if (completingInstallation)
            {
                // The newly installed Windows service creates its own protected configuration
                // and host keys. It remains behind the installation guard during this start.
                RunSshPowerShell(StartNewSshScript, "start the newly installed SSH service", TimeSpan.FromSeconds(45));
                // Only this new guarded service is restarted. Its generated settings/keys
                // must precede its final process start; existing user sessions are preserved.
                RunSshPowerShell(RestartNewSshScript, "finish initializing the new SSH service", TimeSpan.FromSeconds(45));
            }
            if (!File.Exists(configuration)) throw new HostSetupException("The installed SSH server has no configuration. Ask its administrator to complete setup; MDRemoteNG has preserved the existing installation.");
            VerifyRunningSshFiles([configuration]);
            string text = File.ReadAllText(configuration);
            SshServerConfiguration.ValidateConfigurationText(text);
            string sshUsername = HostConfiguration.SshUsername(username, domain, Environment.MachineName);
            if (sshUsername.Contains(',') || sshUsername.Any(char.IsControl)) throw new HostSetupException("This account name cannot be evaluated safely by Windows OpenSSH.");
            string effective = RunSshProgram(sshd, ["-T", "-C", "user=" + sshUsername.ToLowerInvariant() + ",host=mdremoteng.local,addr=127.0.0.1"],
                "inspect the effective SSH server configuration", TimeSpan.FromSeconds(20));
            var settings = SshServerConfiguration.ParseEffective(effective, sshUsername, HasOpenSshUsersGroup());
            ValidateEffectiveSshFirewall(settings.Port, sshd);
            List<string> fingerprints = [];
            List<string> keyPaths = [];
            foreach (string key in settings.HostKeyPaths)
            {
                string privatePath = key.Replace("__PROGRAMDATA__", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), StringComparison.OrdinalIgnoreCase);
                if (!Path.IsPathFullyQualified(privatePath) || !File.Exists(privatePath)) continue;
                keyPaths.Add(privatePath);
                VerifyRunningSshFiles([privatePath]);
                // Derive the public value from the actual private host key. A .pub sidecar
                // can be stale or absent and must never establish the server identity.
                string keygen = Path.Combine(Path.GetDirectoryName(sshd)!, "ssh-keygen.exe");
                string publicKey = RunSshProgram(keygen, ["-y", "-f", privatePath], "read the SSH host public key", TimeSpan.FromSeconds(20));
                fingerprints.Add(SshServerConfiguration.PublicKeyFingerprint(publicKey));
            }
            if (fingerprints.Count == 0) throw new HostSetupException("Windows could not read the SSH host public keys. Complete SSH host-key setup before sharing this host.");
            foreach (HostFirewallRule rule in HostConfiguration.FirewallRules(instanceId, settings.Port, helper, "SSH")) AddFirewallRule(rule);
            RunSshPowerShell(completingInstallation ? StartNewSshScript : StartExistingSshScript, "start the SSH service", TimeSpan.FromSeconds(45));
            VerifyRunningSshFiles(keyPaths.Prepend(configuration));
            RemoveSshGuard(guard); // Also clears a guard retained by an earlier failed installation.
            return new HostSshConfiguration(settings.Port, fingerprints.Distinct(StringComparer.Ordinal).ToArray());
        }
        catch
        {
            if (guarded)
            {
                // Leave the block guard in place even if stopping fails. Never leave a new
                // server reachable after a failed firewall or authentication check.
                try { RunSshPowerShell(StopNewSshScript, "stop the incomplete SSH service", TimeSpan.FromSeconds(30)); } catch (HostSetupException) { }
            }
            throw;
        }
    }

    public void EnsureSecureShellMembership(string username)
    {
        if (IsAdministrator(username)) throw new HostSetupException("The managed account has administrator access and cannot be shared.");
        AddToGroup(username, "S-1-5-32-545");
        if (HasOpenSshUsersGroup()) AddToNamedGroup(username, OpenSshUsers);
    }

    public void GrantSecureShellAccess(string sid)
    {
        if (!HasOpenSshUsersGroup()) return;
        var account = new SecurityIdentifier(sid);
        byte[] bytes = new byte[account.BinaryLength];
        account.GetBinaryForm(bytes, 0);
        IntPtr buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            var member = new LocalGroupSidMember { Sid = buffer };
            uint result = NetLocalGroupAddSidMember(null, OpenSshUsers, 0, ref member, 1);
            if (result != 1378) Check(result, "grant your Windows account SSH access");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool HasOpenSshUsersGroup()
    {
        uint result = NetLocalGroupGetInfo(null, OpenSshUsers, 0, out IntPtr buffer);
        if (result == 2220) return false;
        Check(result, "inspect SSH account access");
        if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
        return true;
    }

    private static void AddToNamedGroup(string username, string group)
    {
        var member = new LocalGroupMember { Name = Environment.MachineName + "\\" + username };
        uint result = NetLocalGroupAddMembers(null, group, 3, ref member, 1);
        if (result != 1378) Check(result, "grant managed SSH access");
    }

    private static void VerifySshServiceImage(string executable)
    {
        using RegistryKey? service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\sshd");
        string path = Environment.ExpandEnvironmentVariables(service?.GetValue("ImagePath") as string ?? "").Trim().Trim('"');
        if (!path.Equals(executable, StringComparison.OrdinalIgnoreCase))
            throw new HostSetupException("The existing SSH service uses a custom executable or configuration command. Ask its administrator to configure access; MDRemoteNG has preserved it.");
    }

    private static void EnsureSshFirewallEnabled()
    {
        object? policyObject = null;
        try
        {
            policyObject = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!);
            dynamic policy = policyObject!;
            foreach (int profile in new[] { 1, 2, 4 })
                SshFirewallPolicy.ValidateProfile((bool)policy.FirewallEnabled[profile], (int)policy.DefaultInboundAction[profile]);
        }
        finally
        {
            if (policyObject != null && Marshal.IsComObject(policyObject)) Marshal.FinalReleaseComObject(policyObject);
        }
    }

    private static void ValidateEffectiveSshFirewall(int port, string executable)
    {
        string json = RunSshPowerShell(ReadEffectiveFirewallScript, "verify the effective SSH firewall policy", TimeSpan.FromSeconds(45));
        var rules = JsonSerializer.Deserialize<List<SshFirewallAllowance>>(json) ?? throw new HostSetupException("Windows could not inspect its effective firewall rules.");
        SshFirewallPolicy.ValidateRules(rules, port, executable);
    }

    private static void VerifyRunningSshFiles(IEnumerable<string> files)
    {
        using var search = new ManagementObjectSearcher("SELECT ProcessId, State FROM Win32_Service WHERE Name='sshd'");
        using ManagementObjectCollection services = search.Get();
        foreach (ManagementObject service in services)
        {
            using (service)
            {
                if (!string.Equals(service["State"] as string, "Running", StringComparison.OrdinalIgnoreCase)) return;
                int processId = Convert.ToInt32(service["ProcessId"], CultureInfo.InvariantCulture);
                using Process process = Process.GetProcessById(processId);
                DateTime started = process.StartTime.ToUniversalTime();
                SshServerConfiguration.VerifyLoadedFiles(started, files.Select(File.GetLastWriteTimeUtc));
                return;
            }
        }
    }

    private static void RemoveSshGuard(string name)
    {
        object? policyObject = null, rulesObject = null;
        try
        {
            policyObject = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!);
            dynamic policy = policyObject!;
            rulesObject = policy.Rules;
            dynamic rules = rulesObject;
            rules.Remove(name);
        }
        catch (COMException exception) when (exception.ErrorCode == unchecked((int)0x80070002)) { }
        finally
        {
            foreach (object? value in new[] { rulesObject, policyObject })
                if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }
    }

    private static bool HasSshGuard(string name)
    {
        object? policyObject = null, rulesObject = null, ruleObject = null;
        try
        {
            policyObject = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!);
            dynamic policy = policyObject!;
            rulesObject = policy.Rules;
            dynamic rules = rulesObject;
            ruleObject = rules.Item(name);
            return ruleObject is not null;
        }
        catch (COMException exception) when (exception.ErrorCode == unchecked((int)0x80070002)) { return false; }
        finally
        {
            foreach (object? value in new[] { ruleObject, rulesObject, policyObject })
                if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }
    }

    private static string RunSshPowerShell(string script, string operation, TimeSpan timeout)
    {
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        string command = "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)\n" + script;
        return RunSshProgram(powershell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command))], operation, timeout);
    }

    private static string RunSshProgram(string executable, string[] arguments, string operation, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        // Elevated Windows PowerShell must load only system modules, never a module from
        // the original user's writable PSModulePath. No password enters any argument.
        start.Environment["PSModulePath"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "Modules");
        using Process process = Process.Start(start) ?? throw new HostSetupException("Windows could not " + operation + ".");
        process.StandardInput.Close(); // A key requiring a passphrase must fail, never prompt or hang.
        Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            throw new HostSetupException("Windows timed out while trying to " + operation + ". Retry after Windows servicing has completed.");
        }
        Task.WaitAll(output, errors);
        if (process.ExitCode != 0) throw new HostSetupException("Windows could not " + operation + " (exit " + process.ExitCode + "). Check Windows optional-feature availability and SSH policy with your administrator.");
        if (output.Result.Length > 131072) throw new HostSetupException("The SSH configuration response is too large.");
        return output.Result;
    }

    private const string InstallOpenSshScript = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        Import-Module "$PSHOME\Modules\Dism\Dism.psd1" -ErrorAction Stop
        Import-Module "$PSHOME\Modules\NetSecurity\NetSecurity.psd1" -ErrorAction Stop
        if (Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue) {
            throw 'An existing OpenSSH firewall rule requires administrator review before installation.'
        }
        $capability = Get-WindowsCapability -Online -Name 'OpenSSH.Server~~~~0.0.1.0'
        if ($capability.State -ne 'Installed') {
            $result = Add-WindowsCapability -Online -Name 'OpenSSH.Server~~~~0.0.1.0' -ErrorAction Stop
        }
        if ($result.RestartNeeded) { 'MDREMOTENG_RESTART_REQUIRED' }
        """;
    private const string RestrictInstalledSshFirewallScript = """
        $ErrorActionPreference = 'Stop'
        Import-Module "$PSHOME\Modules\NetSecurity\NetSecurity.psd1" -ErrorAction Stop
        $rule = Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue
        if ($rule) {
            $rule | Set-NetFirewallRule -Profile Domain,Private -RemoteAddress LocalSubnet -Enabled False -ErrorAction Stop
            $rule = Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction Stop
            if ($rule.Enabled -ne 'False' -or [int]$rule.Profile -ne 3) { throw 'The OpenSSH firewall rule could not be restricted.' }
            $addresses = @($rule | Get-NetFirewallAddressFilter | Select-Object -ExpandProperty RemoteAddress)
            if ($addresses.Count -ne 1 -or $addresses[0] -ne 'LocalSubnet') { throw 'The OpenSSH firewall address scope could not be restricted.' }
        }
        """;
    private const string StartNewSshScript = """
        $ErrorActionPreference = 'Stop'
        Set-Service -Name sshd -StartupType Automatic
        Start-Service -Name sshd
        (Get-Service -Name sshd).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
        """;
    private const string StartExistingSshScript = """
        $ErrorActionPreference = 'Stop'
        $service = Get-Service -Name sshd
        if ($service.StartType -eq 'Disabled') { throw 'The existing SSH service is disabled. Ask its administrator to enable it.' }
        Start-Service -Name sshd
        (Get-Service -Name sshd).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
        """;
    private const string RestartNewSshScript = """
        $ErrorActionPreference = 'Stop'
        Stop-Service -Name sshd -Force
        Start-Service -Name sshd
        (Get-Service -Name sshd).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
        """;
    private const string StopNewSshScript = """
        $ErrorActionPreference = 'Stop'
        $service = Get-Service -Name sshd -ErrorAction SilentlyContinue
        if ($service) { Stop-Service -Name sshd -Force -ErrorAction Stop; Set-Service -Name sshd -StartupType Disabled }
        """;
    private const string ReadEffectiveFirewallScript = """
        $ErrorActionPreference = 'Stop'
        Import-Module "$PSHOME\Modules\NetSecurity\NetSecurity.psd1" -ErrorAction Stop
        $result = @(Get-NetFirewallRule -PolicyStore ActiveStore -Enabled True -Direction Inbound -Action Allow -ErrorAction Stop | ForEach-Object {
            $rule = $_
            $application = $rule | Get-NetFirewallApplicationFilter -ErrorAction Stop
            $service = $rule | Get-NetFirewallServiceFilter -ErrorAction Stop
            $port = $rule | Get-NetFirewallPortFilter -ErrorAction Stop
            $address = $rule | Get-NetFirewallAddressFilter -ErrorAction Stop
            [pscustomobject]@{
                Profiles = [int]$rule.Profile
                Protocol = [string]$port.Protocol
                LocalPorts = ($port.LocalPort -join ',')
                Application = [string]$application.Program
                Service = [string]$service.Service
                RemoteAddresses = ($address.RemoteAddress -join ',')
            }
        })
        ConvertTo-Json -InputObject $result -Compress -Depth 3
        """;
}
