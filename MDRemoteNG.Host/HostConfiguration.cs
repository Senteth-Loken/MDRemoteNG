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
    string MachineName, int RdpPort, bool PolicyAllowsRdp = true, bool PolicyAllowsNla = true, bool PolicyAllowsFirewall = true);

public sealed record HostAccountInfo(string Comment, bool IsAdministrator, bool IsEnabled = true);

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
}

public static class HostConfiguration
{
    public static string AccountName(Guid instanceId) => "mdrng_" + instanceId.ToString("N")[..12];
    public static string OwnershipMarker(Guid instanceId) => "MDRemoteNG managed host " + instanceId.ToString("N");

    public static IReadOnlyList<HostFirewallRule> FirewallRules(Guid instanceId, int rdpPort, string helperExecutablePath)
    {
        if (instanceId == Guid.Empty || rdpPort is < 1 or > 65535)
            throw new HostSetupException("The host firewall settings are invalid.");
        string helper = Path.GetFullPath(helperExecutablePath);
        string prefix = "MDRemoteNG " + instanceId.ToString("N")[..12];
        return Array.AsReadOnly(new[]
        {
            new HostFirewallRule(prefix + " RDP TCP", 6, rdpPort, null, "TermService"),
            new HostFirewallRule(prefix + " RDP UDP", 17, rdpPort, null, "TermService"),
            new HostFirewallRule(prefix + " Discovery UDP", 17, 48765, helper, null),
            new HostFirewallRule(prefix + " Discovery TCP", 6, null, helper, null)
        });
    }

    public static HostConfigurationResult Configure(IHostPlatform platform, Guid instanceId, string helperExecutablePath)
    {
        HostMachineInfo machine = InspectForConfiguration(platform, instanceId, helperExecutablePath);
        string username = AccountName(instanceId);
        string marker = OwnershipMarker(instanceId);
        HostAccountInfo? account = platform.FindAccount(username);
        if (account is not null && (account.Comment != marker || account.IsAdministrator))
            throw new HostSetupException("The managed account name is already used by an unrelated or administrative account. No account was changed.");

        // Do service/firewall work before changing a password. Existing enabled accounts
        // rotate last; disabled accounts remain disabled until rotation has succeeded.
        platform.PrepareRemoteDesktop(machine.RdpPort, helperExecutablePath, instanceId);
        string password = GeneratePassword();
        bool created = false;
        try
        {
            if (account is null)
            {
                platform.CreateAccount(username, password, marker);
                created = true;
            }
            platform.EnsureRemoteDesktopMembership(username);
            if (created)
            {
                platform.EnableManagedAccount(username);
            }
            else
            {
                platform.SetAccountPassword(username, password);
                if (!account!.IsEnabled) platform.EnableManagedAccount(username);
            }
            return new HostConfigurationResult
            {
                Port = machine.RdpPort,
                Username = username,
                Domain = machine.MachineName,
                Password = password,
                StatusMessage = "Remote Desktop is ready. A dedicated non-administrator account and local-subnet Private/Domain firewall rules have been configured."
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
        string helperExecutablePath, HostAccountCredentials credentials)
    {
        ValidateCredentialsInput(credentials);
        HostMachineInfo machine = InspectForConfiguration(platform, instanceId, helperExecutablePath);
        // Reauthenticate inside the elevated process before any service, firewall or group
        // changes. In particular, elevation may have used a different administrator account.
        platform.ValidateAccountCredentials(credentials);
        platform.PrepareRemoteDesktop(machine.RdpPort, helperExecutablePath, instanceId);
        platform.GrantRemoteDesktopAccess(credentials.Sid);
        return new HostConfigurationResult
        {
            Port = machine.RdpPort,
            Username = credentials.Username,
            Domain = credentials.Domain,
            Password = credentials.Password,
            StatusMessage = "Remote Desktop is ready for your Windows account with local-subnet Private/Domain firewall rules. Your Windows password was not changed."
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
            throw new HostSetupException("Enter the Windows account password and its account details. A Windows Hello PIN cannot be used for Remote Desktop password sharing.");
    }

    private static HostMachineInfo InspectForConfiguration(IHostPlatform platform, Guid instanceId, string helperExecutablePath)
    {
        ArgumentNullException.ThrowIfNull(platform);
        if (instanceId == Guid.Empty) throw new HostSetupException("The MDRemoteNG installation identity is missing.");
        if (string.IsNullOrWhiteSpace(helperExecutablePath)) throw new HostSetupException("The discovery helper path is missing.");
        HostMachineInfo machine = platform.Inspect();
        if (!machine.IsElevated) throw new HostSetupException("Approve the Windows administrator prompt to configure this host.");
        if (!SupportsRemoteDesktop(machine))
            throw new HostSetupException("This Windows edition cannot host Remote Desktop. Windows Pro, Enterprise, Education or a member server is required.");
        if (!machine.PolicyAllowsRdp || !machine.PolicyAllowsNla || !machine.PolicyAllowsFirewall)
            throw new HostSetupException("An organisation policy prevents enabling Remote Desktop with Network Level Authentication. Contact your administrator.");
        if (machine.RdpPort is < 1 or > 65535 || string.IsNullOrWhiteSpace(machine.MachineName))
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
