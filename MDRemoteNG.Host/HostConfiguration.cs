using System.Security.Cryptography;

namespace MDRemoteNG.Host;

public sealed class HostSetupException : Exception
{
    public HostSetupException(string message) : base(message) { }
    public HostSetupException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class HostConfigurationResult
{
    public string Protocol { get; set; } = "RDP";
    public int Port { get; set; }
    public string Username { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Password { get; set; } = "";
    public string StatusMessage { get; set; } = "";
    public List<string> SshHostKeyFingerprints { get; set; } = [];
    public string CertificateFingerprint { get; set; } = "";
    public byte[] VncCertificatePfx { get; set; } = [];
    public bool AdministratorAccess { get; set; }
}

// A class deliberately avoids a generated record ToString containing the password.
public sealed class HostAccountCredentials
{
    public string Username { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Password { get; set; } = "";
    public string Sid { get; set; } = "";
}

public sealed record HostMachineInfo(bool IsElevated, string ProductType, string EditionId,
    string MachineName, int RdpPort, bool PolicyAllowsRdp = true, bool PolicyAllowsNla = true, bool PolicyAllowsFirewall = true, int OsBuild = 17763);

public sealed record HostAccountInfo(string Comment, bool IsAdministrator, bool IsEnabled = true);
public sealed record HostSshConfiguration(int Port, IReadOnlyList<string> HostKeyFingerprints);

public sealed record HostFirewallRule(string Name, int Protocol, int? LocalPort, string? ApplicationName, string? ServiceName)
{
    public int Profiles => 3; // Domain and Private.
    public string RemoteAddresses => "LocalSubnet";
}

/// <summary>All operating-system changes are behind this interface for non-invasive tests.</summary>
public interface IHostPlatform
{
    HostMachineInfo Inspect();
    HostAccountInfo? FindAccount(string username);
    void PrepareRemoteDesktop(int port, string helperExecutablePath, Guid instanceId);
    void CreateAccount(string username, string password, string ownershipMarker);
    void EnsureRemoteDesktopMembership(string username);
    void EnableManagedAccount(string username);
    void SetAccountPassword(string username, string password);
    void DeleteAccount(string username);
    void ValidateAccountCredentials(HostAccountCredentials credentials);
    void GrantRemoteDesktopAccess(string sid);
    HostSshConfiguration PrepareSecureShell(string username, string domain, string helperExecutablePath, Guid instanceId);
    void EnsureSecureShellMembership(string username);
    void GrantSecureShellAccess(string sid);
    void PrepareVnc(int port, string helperExecutablePath, Guid instanceId);
    void EnsureAdministratorSecureShellMembership(string username, string ownershipMarker);
    void EnableAdministratorManagedAccount(string username, string ownershipMarker);
    void SetAdministratorManagedAccountPassword(string username, string password, string ownershipMarker);
}

public static class HostConfiguration
{
    public static string AccountName(Guid instanceId, string protocol = "RDP", bool administratorAccess = false)
    {
        ValidateAdministratorMode(protocol, administratorAccess);
        return (administratorAccess ? "mdadm_" : NormalizeProtocol(protocol) == "SSH" ? "mdssh_" : "mdrng_") + instanceId.ToString("N")[..12];
    }
    public static string OwnershipMarker(Guid instanceId, string protocol = "RDP", bool administratorAccess = false)
    {
        ValidateAdministratorMode(protocol, administratorAccess);
        return "MDRemoteNG managed host " + instanceId.ToString("N") +
            (administratorAccess ? " SSH administrator" : NormalizeProtocol(protocol) == "SSH" ? " SSH" : "");
    }

    public static IReadOnlyList<HostFirewallRule> FirewallRules(Guid instanceId, int rdpPort, string helperExecutablePath, string protocol = "RDP")
    {
        if (instanceId == Guid.Empty || rdpPort is < 1 or > 65535)
            throw new HostSetupException("The host firewall settings are invalid.");
        string helper = Path.GetFullPath(helperExecutablePath);
        string prefix = "MDRemoteNG " + instanceId.ToString("N")[..12];
        var rules = new List<HostFirewallRule>
        {
            new HostFirewallRule(prefix + " Discovery UDP", 17, 48765, helper, null),
            new HostFirewallRule(prefix + " Discovery TCP", 6, null, helper, null)
        };
        if (NormalizeProtocol(protocol) == "RDP")
        {
            rules.Insert(0, new HostFirewallRule(prefix + " RDP UDP", 17, rdpPort, null, "TermService"));
            rules.Insert(0, new HostFirewallRule(prefix + " RDP TCP", 6, rdpPort, null, "TermService"));
        }
        else rules.Insert(0, new HostFirewallRule(prefix + " SSH TCP", 6, rdpPort, null, "sshd"));
        return rules.AsReadOnly();
    }

    public static HostConfigurationResult Configure(IHostPlatform platform, Guid instanceId, string helperExecutablePath, string protocol = "RDP", bool administratorAccess = false)
    {
        protocol = NormalizeProtocol(protocol);
        ValidateAdministratorMode(protocol, administratorAccess);
        HostMachineInfo machine = InspectForConfiguration(platform, instanceId, helperExecutablePath, protocol);
        string username = AccountName(instanceId, protocol, administratorAccess);
        string marker = OwnershipMarker(instanceId, protocol, administratorAccess);
        HostAccountInfo? account = platform.FindAccount(username);
        if (account is not null && (account.Comment != marker || account.IsAdministrator != administratorAccess))
            throw new HostSetupException(administratorAccess
                ? "The managed administrator account name is already used by an unrelated account or an account with unexpected privileges. No account was changed."
                : "The managed account name is already used by an unrelated or administrative account. No account was changed.");

        // Do service/firewall work before changing a password. Existing enabled accounts
        // rotate last; disabled accounts remain disabled until rotation has succeeded.
        HostSshConfiguration prepared = PrepareProtocol(platform, machine, username, machine.MachineName, helperExecutablePath, instanceId, protocol);
        string password = GeneratePassword();
        bool created = false;
        try
        {
            if (account is null)
            {
                platform.CreateAccount(username, password, marker);
                created = true;
            }
            if (administratorAccess)
            {
                platform.EnsureAdministratorSecureShellMembership(username, marker);
                // Match Group administrators may select different authentication or shell
                // policy. Re-evaluate with the real enrolled group membership before the
                // account is enabled or an existing credential is replaced.
                prepared = PrepareProtocol(platform, machine, username, machine.MachineName, helperExecutablePath, instanceId, protocol);
            }
            else if (protocol == "RDP") platform.EnsureRemoteDesktopMembership(username);
            else platform.EnsureSecureShellMembership(username);
            if (created)
            {
                if (administratorAccess) platform.EnableAdministratorManagedAccount(username, marker);
                else platform.EnableManagedAccount(username);
            }
            else
            {
                if (administratorAccess) platform.SetAdministratorManagedAccountPassword(username, password, marker);
                else platform.SetAccountPassword(username, password);
                if (!account!.IsEnabled)
                {
                    if (administratorAccess) platform.EnableAdministratorManagedAccount(username, marker);
                    else platform.EnableManagedAccount(username);
                }
            }
            return new HostConfigurationResult
            {
                Protocol = protocol,
                Port = prepared.Port,
                Username = username,
                Domain = protocol == "RDP" ? machine.MachineName : "",
                Password = password,
                SshHostKeyFingerprints = prepared.HostKeyFingerprints.ToList(),
                AdministratorAccess = administratorAccess,
                StatusMessage = (protocol == "RDP" ? "Remote Desktop" : "SSH") + " is ready. A dedicated " +
                    (administratorAccess ? "administrator" : "non-administrator") + " account and local-subnet Private/Domain firewall rules have been configured."
            };
        }
        catch (Exception failure)
        {
            if (created)
            {
                try { platform.DeleteAccount(username); }
                catch (Exception rollback)
                {
                    throw new HostSetupException("Host setup did not complete and its new account could not be removed. Ask an administrator to remove account " + username + ".", new AggregateException(failure, rollback));
                }
            }
            throw;
        }
    }

    public static HostConfigurationResult ConfigureCurrentAccount(IHostPlatform platform, Guid instanceId,
        string helperExecutablePath, HostAccountCredentials credentials, string protocol = "RDP")
    {
        protocol = NormalizeProtocol(protocol);
        ValidateCredentialsInput(credentials);
        HostMachineInfo machine = InspectForConfiguration(platform, instanceId, helperExecutablePath, protocol);
        // Reauthenticate inside the elevated process before any service, firewall or group
        // changes. In particular, elevation may have used a different administrator account.
        platform.ValidateAccountCredentials(credentials);
        HostSshConfiguration prepared = PrepareProtocol(platform, machine, credentials.Username, credentials.Domain, helperExecutablePath, instanceId, protocol);
        if (protocol == "RDP") platform.GrantRemoteDesktopAccess(credentials.Sid);
        else platform.GrantSecureShellAccess(credentials.Sid);
        return new HostConfigurationResult
        {
            Protocol = protocol,
            Port = prepared.Port,
            Username = protocol == "RDP" ? credentials.Username : SshUsername(credentials.Username, credentials.Domain, machine.MachineName),
            Domain = protocol == "RDP" ? credentials.Domain : "",
            Password = credentials.Password,
            SshHostKeyFingerprints = prepared.HostKeyFingerprints.ToList(),
            StatusMessage = (protocol == "RDP" ? "Remote Desktop" : "SSH") + " is ready for your Windows account with local-subnet Private/Domain firewall rules. Your Windows password was not changed."
        };
    }

    internal static void ValidateCredentialsInput(HostAccountCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrWhiteSpace(credentials.Username) || string.IsNullOrWhiteSpace(credentials.Sid) ||
            string.IsNullOrEmpty(credentials.Password) || credentials.Domain is null ||
            (credentials.Domain.Length == 0 && !credentials.Username.Contains('@')) ||
            credentials.Username.Length > 256 || credentials.Domain.Length > 256 || credentials.Password.Length > 512 || credentials.Sid.Length > 184 ||
            new[] { credentials.Username, credentials.Domain, credentials.Password, credentials.Sid }.Any(value => value.Contains('\0')))
            throw new HostSetupException("Enter the Windows account password and its account details. A Windows Hello PIN cannot be used for remote access password sharing.");
    }

    public static string SshUsername(string username, string domain, string machineName) =>
        string.IsNullOrEmpty(domain) || domain == "." || domain.Equals(machineName, StringComparison.OrdinalIgnoreCase) || domain.Equals("MicrosoftAccount", StringComparison.OrdinalIgnoreCase)
            ? username : domain + "\\" + username;

    private static HostSshConfiguration PrepareProtocol(IHostPlatform platform, HostMachineInfo machine, string username, string domain, string helper,
        Guid instanceId, string protocol)
    {
        if (protocol == "SSH")
        {
            if (domain.Equals("AzureAD", StringComparison.OrdinalIgnoreCase))
                throw new HostSetupException("Windows OpenSSH cannot authenticate a Microsoft Entra account. Use the dedicated account option for SSH.");
            HostSshConfiguration prepared = platform.PrepareSecureShell(username, domain, helper, instanceId);
            if (prepared.Port is < 1 or > 65535) throw new HostSetupException("The SSH listener port is invalid.");
            if (prepared.HostKeyFingerprints.Count is < 1 or > 8 || prepared.HostKeyFingerprints.Any(key => !key.StartsWith("SHA256:", StringComparison.Ordinal)))
                throw new HostSetupException("Windows could not verify the SSH host identity.");
            return prepared;
        }
        platform.PrepareRemoteDesktop(machine.RdpPort, helper, instanceId);
        return new HostSshConfiguration(machine.RdpPort, []);
    }

    private static string NormalizeProtocol(string protocol) => protocol?.ToUpperInvariant() switch
    {
        "RDP" => "RDP", "SSH" => "SSH", _ => throw new HostSetupException("Choose RDP or SSH for Windows account auto-configuration.")
    };

    private static void ValidateAdministratorMode(string protocol, bool administratorAccess)
    {
        if (administratorAccess && NormalizeProtocol(protocol) != "SSH")
            throw new HostSetupException("A dedicated administrator account can only be configured for SSH.");
    }

    private static HostMachineInfo InspectForConfiguration(IHostPlatform platform, Guid instanceId, string helperExecutablePath, string protocol)
    {
        ArgumentNullException.ThrowIfNull(platform);
        if (instanceId == Guid.Empty) throw new HostSetupException("The MDRemoteNG installation identity is missing.");
        if (string.IsNullOrWhiteSpace(helperExecutablePath)) throw new HostSetupException("The discovery helper path is missing.");
        HostMachineInfo machine = platform.Inspect();
        if (!machine.IsElevated) throw new HostSetupException("Approve the Windows administrator prompt to configure this host.");
        if (protocol == "RDP" && !SupportsRemoteDesktop(machine))
            throw new HostSetupException("This Windows edition cannot host Remote Desktop. Windows Pro, Enterprise, Education or a member server is required.");
        if (protocol == "SSH" && (!new[] { "WinNT", "ServerNT" }.Contains(machine.ProductType, StringComparer.OrdinalIgnoreCase) || machine.OsBuild < 17763))
            throw new HostSetupException("SSH host setup requires Windows 10 build 1809 or later, or a Windows member server. Domain controllers are not supported.");
        if ((protocol == "RDP" && (!machine.PolicyAllowsRdp || !machine.PolicyAllowsNla)) || !machine.PolicyAllowsFirewall)
            throw new HostSetupException("An organisation policy prevents configuring this host and its local-subnet firewall rules. Contact your administrator.");
        if ((protocol == "RDP" && machine.RdpPort is < 1 or > 65535) || string.IsNullOrWhiteSpace(machine.MachineName))
            throw new HostSetupException("The computer's Remote Desktop settings are invalid.");
        return machine;
    }

    public static bool SupportsRemoteDesktop(HostMachineInfo machine)
    {
        if (machine.ProductType.Equals("ServerNT", StringComparison.OrdinalIgnoreCase)) return true;
        if (!machine.ProductType.Equals("WinNT", StringComparison.OrdinalIgnoreCase)) return false; // Excludes domain controllers.
        string edition = machine.EditionId;
        return edition.StartsWith("Professional", StringComparison.OrdinalIgnoreCase) ||
               edition.StartsWith("Enterprise", StringComparison.OrdinalIgnoreCase) ||
               edition.StartsWith("Education", StringComparison.OrdinalIgnoreCase);
    }

    public static string GeneratePassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", lower = "abcdefghijkmnopqrstuvwxyz", digits = "23456789", symbols = "!#%+,-.:=?@_";
        const string all = upper + lower + digits + symbols;
        char[] characters = new char[40];
        characters[0] = upper[RandomNumberGenerator.GetInt32(upper.Length)];
        characters[1] = lower[RandomNumberGenerator.GetInt32(lower.Length)];
        characters[2] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
        characters[3] = symbols[RandomNumberGenerator.GetInt32(symbols.Length)];
        for (int i = 4; i < characters.Length; i++) characters[i] = all[RandomNumberGenerator.GetInt32(all.Length)];
        for (int i = characters.Length - 1; i > 0; i--)
        {
            int next = RandomNumberGenerator.GetInt32(i + 1);
            (characters[i], characters[next]) = (characters[next], characters[i]);
        }
        string result = new(characters);
        Array.Clear(characters);
        return result;
    }
}
