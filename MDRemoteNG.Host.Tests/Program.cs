using MDRemoteNG.Host;

int passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
    passed++;
}
void Reject(Action action, string expected)
{
    try { action(); throw new Exception("Expected a safe setup rejection."); }
    catch (HostSetupException exception)
    {
        if (!exception.Message.Contains(expected, StringComparison.OrdinalIgnoreCase)) throw;
    }
}

Guid instance = Guid.Parse("4514af49-31a6-456d-9402-d3e62c489da2");
string helper = Path.Combine(Directory.GetCurrentDirectory(), "test-install", "Discovery", "MDRemoteNG.Discovery.exe");
var platform = new FakePlatform();
HostConfigurationResult configured = HostConfiguration.Configure(platform, instance, helper);
Check(configured.Protocol == "RDP" && configured.Port == 3398 && configured.Domain == "TEST-PC" &&
    configured.Username == HostConfiguration.AccountName(instance), "Auto setup returns the machine's actual RDP port and dedicated local account.");
Check(platform.Operations.SequenceEqual(["Inspect", "Find", "Prepare", "Create", "Groups", "Enable"]),
    "Service and firewall setup completes before a new account is created and enabled.");
Check(platform.Password == configured.Password && platform.Marker == HostConfiguration.OwnershipMarker(instance),
    "Only the generated password and exact installation ownership marker are used for the account.");
Check(configured.Username.Length <= 20 && configured.Username != Environment.UserName,
    "The generated local account stays within Windows name limits and does not change the signed-in account.");

platform = new FakePlatform { Account = new HostAccountInfo(HostConfiguration.OwnershipMarker(instance), false) };
HostConfiguration.Configure(platform, instance, helper);
Check(platform.Operations.SequenceEqual(["Inspect", "Find", "Prepare", "Groups", "Password"]),
    "An existing owned non-administrator account changes its password only as the final operation.");

platform = new FakePlatform { Account = new HostAccountInfo(HostConfiguration.OwnershipMarker(instance), false, false) };
HostConfiguration.Configure(platform, instance, helper);
Check(platform.Operations.SequenceEqual(["Inspect", "Find", "Prepare", "Groups", "Password", "Enable"]) && platform.Account!.IsEnabled,
    "An existing disabled account is enabled only after its new password has been applied.");
platform = new FakePlatform { Account = new HostAccountInfo(HostConfiguration.OwnershipMarker(instance), false, false), Failure = "Password" };
Reject(() => HostConfiguration.Configure(platform, instance, helper), "Test failure");
Check(!platform.Account!.IsEnabled && !platform.Operations.Contains("Enable") && platform.Password == "",
    "Failed password rotation cannot re-enable an existing disabled account with its old password.");
platform = new FakePlatform { Account = new HostAccountInfo(HostConfiguration.OwnershipMarker(instance), false), Failure = "Password" };
Reject(() => HostConfiguration.Configure(platform, instance, helper), "Test failure");
Check(platform.Account!.IsEnabled && !platform.Operations.Contains("Enable") && platform.Password == "",
    "Failed password rotation leaves an already enabled account's state and password unchanged.");

foreach (HostAccountInfo conflict in new[] { new HostAccountInfo("Unrelated account", false), new HostAccountInfo(HostConfiguration.OwnershipMarker(instance), true) })
{
    platform = new FakePlatform { Account = conflict };
    Reject(() => HostConfiguration.Configure(platform, instance, helper), "unrelated or administrative");
    Check(platform.Operations.SequenceEqual(["Inspect", "Find"]), "Foreign or administrative accounts are rejected before any host changes.");
}

foreach (HostMachineInfo unsupported in new[]
{
    new HostMachineInfo(true, "WinNT", "Core", "TEST-PC", 3389),
    new HostMachineInfo(true, "WinNT", "Home", "TEST-PC", 3389),
    new HostMachineInfo(true, "WinNT", "Starter", "TEST-PC", 3389),
    new HostMachineInfo(true, "LanmanNT", "ServerStandard", "TEST-DC", 3389),
    new HostMachineInfo(true, "Unknown", "Professional", "TEST-PC", 3389)
})
{
    platform = new FakePlatform { Machine = unsupported };
    Reject(() => HostConfiguration.Configure(platform, instance, helper), "cannot host");
    Check(platform.Operations.SequenceEqual(["Inspect"]), "Unsupported editions and domain controllers are rejected before account or host mutations.");
}
Check(HostConfiguration.SupportsRemoteDesktop(new HostMachineInfo(true, "ServerNT", "ServerStandard", "TEST-SERVER", 3389)) &&
    HostConfiguration.SupportsRemoteDesktop(new HostMachineInfo(true, "WinNT", "EnterpriseN", "TEST-PC", 3389)),
    "Supported enterprise clients and member servers can host Remote Desktop.");

platform = new FakePlatform { Machine = new HostMachineInfo(false, "WinNT", "Professional", "TEST-PC", 3389) };
Reject(() => HostConfiguration.Configure(platform, instance, helper), "administrator prompt");
Check(platform.Operations.SequenceEqual(["Inspect"]), "Unelevated setup is rejected without changing the computer.");
foreach (HostMachineInfo policy in new[]
{
    new HostMachineInfo(true, "WinNT", "Professional", "TEST-PC", 3389, false, true),
    new HostMachineInfo(true, "WinNT", "Professional", "TEST-PC", 3389, true, false),
    new HostMachineInfo(true, "WinNT", "Professional", "TEST-PC", 3389, true, true, false)
})
{
    platform = new FakePlatform { Machine = policy };
    Reject(() => HostConfiguration.Configure(platform, instance, helper), "organisation policy");
    Check(platform.Operations.SequenceEqual(["Inspect"]), "Organisation policy restrictions are respected before setup mutations.");
}

platform = new FakePlatform { Failure = "Prepare" };
Reject(() => HostConfiguration.Configure(platform, instance, helper), "Test failure");
Check(!platform.Operations.Contains("Create") && !platform.Operations.Contains("Password"), "Preparation failures do not create accounts or rotate passwords.");
platform = new FakePlatform { Failure = "Groups" };
Reject(() => HostConfiguration.Configure(platform, instance, helper), "Test failure");
Check(platform.Operations.Last() == "Delete" && !platform.Operations.Contains("Enable"), "Failed new-account setup removes its incomplete account before returning an error.");
platform = new FakePlatform { Account = new HostAccountInfo(HostConfiguration.OwnershipMarker(instance), false), Failure = "Groups" };
Reject(() => HostConfiguration.Configure(platform, instance, helper), "Test failure");
Check(!platform.Operations.Contains("Delete") && !platform.Operations.Contains("Password"), "A failed existing-account setup neither deletes the account nor changes its password.");
platform = new FakePlatform();
Reject(() => HostConfiguration.Configure(platform, Guid.Empty, helper), "identity");
Check(platform.Operations.Count == 0, "An empty installation identity is rejected before accessing host settings.");

var passwords = Enumerable.Range(0, 200).Select(_ => HostConfiguration.GeneratePassword()).ToArray();
Check(passwords.All(value => value.Length >= 32 && value.Any(char.IsUpper) && value.Any(char.IsLower) && value.Any(char.IsDigit) && value.Any(c => !char.IsLetterOrDigit(c))),
    "Generated passwords have high length and every Windows password-complexity character class.");
Check(passwords.Distinct(StringComparer.Ordinal).Count() == passwords.Length, "Separate configurations receive independently generated passwords.");

IReadOnlyList<HostFirewallRule> rules = HostConfiguration.FirewallRules(instance, 3398, helper);
Check(rules.Count == 4 && rules.All(rule => rule.Profiles == 3 && rule.RemoteAddresses == "LocalSubnet"),
    "All host rules permit only local-subnet traffic on Private and Domain profiles.");
Check(rules.Where(rule => rule.ServiceName == "TermService").All(rule => rule.LocalPort == 3398) &&
    rules.Count(rule => rule.ServiceName == "TermService") == 2,
    "RDP firewall rules use the actual listener port and are scoped to Remote Desktop Services.");
Check(rules.Where(rule => rule.ApplicationName != null).All(rule => rule.ApplicationName == Path.GetFullPath(helper)) &&
    rules.Any(rule => rule.ApplicationName != null && rule.Protocol == 17 && rule.LocalPort == 48765),
    "Discovery rules are scoped to the exact helper executable and its discovery port.");
Check(rules.All(rule => rule.ServiceName != "WinRM" && rule.LocalPort != 22 && rule.LocalPort != 5900),
    "Auto setup does not enable SSH, VNC, WinRM, or unrelated remote services.");

HostAccountCredentials currentCredentials = new()
{
    Username = "alice", Domain = "TEST-DOMAIN", Password = "ExistingAccountPassword!", Sid = FakePlatform.CurrentSid
};
platform = new FakePlatform { Account = new HostAccountInfo("Existing current administrator", true) };
configured = HostConfiguration.ConfigureCurrentAccount(platform, instance, helper, currentCredentials);
Check(platform.Operations.SequenceEqual(["Inspect", "Validate", "Prepare", "GrantSid"]) && platform.GrantedSid == FakePlatform.CurrentSid,
    "Current-account setup validates the password and SID before granting only that account Remote Desktop access.");
Check(configured.Username == currentCredentials.Username && configured.Domain == currentCredentials.Domain &&
    configured.Password == currentCredentials.Password && configured.Port == 3398 && platform.Password == "",
    "Current-account setup returns the authenticated existing credentials without resetting the Windows password.");
Check(!platform.Operations.Any(operation => new[] { "Find", "Create", "Delete", "Enable", "Groups", "Password" }.Contains(operation)),
    "Current-account setup accepts an administrator account without creating users, changing flags or altering primary Users membership.");

platform = new FakePlatform { Failure = "Validate" };
Reject(() => HostConfiguration.ConfigureCurrentAccount(platform, instance, helper, currentCredentials), "Test failure");
Check(platform.Operations.SequenceEqual(["Inspect", "Validate"]),
    "An invalid current-account password fails before any service, firewall or group changes.");
var wrongIdentity = new HostAccountCredentials
{
    Username = "another-user", Domain = "TEST-DOMAIN", Password = "ExistingAccountPassword!", Sid = "S-1-5-21-1-2-3-1002"
};
platform = new FakePlatform();
Reject(() => HostConfiguration.ConfigureCurrentAccount(platform, instance, helper, wrongIdentity), "different Windows account");
Check(platform.Operations.SequenceEqual(["Inspect", "Validate"]),
    "Credentials for a different Windows SID are rejected before current-account setup can grant access.");

foreach (HostMachineInfo unsupported in new[]
{
    new HostMachineInfo(true, "WinNT", "Core", "TEST-PC", 3389),
    new HostMachineInfo(true, "LanmanNT", "ServerStandard", "TEST-DC", 3389),
    new HostMachineInfo(true, "WinNT", "Professional", "TEST-PC", 3389, false)
})
{
    platform = new FakePlatform { Machine = unsupported };
    try { HostConfiguration.ConfigureCurrentAccount(platform, instance, helper, currentCredentials); throw new Exception("Expected preflight rejection."); }
    catch (HostSetupException) { }
    Check(platform.Operations.SequenceEqual(["Inspect"]), "Current-account setup also refuses unsupported hosts and organisation policy restrictions.");
}

foreach (HostAccountCredentials incomplete in new[]
{
    new HostAccountCredentials { Username = "alice", Domain = "TEST-PC", Sid = FakePlatform.CurrentSid },
    new HostAccountCredentials { Username = "alice", Domain = "TEST-PC", Password = "existing" },
    new HostAccountCredentials { Username = "alice", Password = "existing", Sid = FakePlatform.CurrentSid },
    new HostAccountCredentials { Username = "alice", Domain = "TEST-PC", Password = "existing\0hidden", Sid = FakePlatform.CurrentSid }
})
{
    platform = new FakePlatform();
    Reject(() => HostConfiguration.ConfigureCurrentAccount(platform, instance, helper, incomplete), "account password");
    Check(platform.Operations.Count == 0, "Missing or ambiguous current credentials are rejected before accessing host settings.");
}

platform = new FakePlatform { Failure = "GrantSid" };
Reject(() => HostConfiguration.ConfigureCurrentAccount(platform, instance, helper, currentCredentials), "Test failure");
Check(platform.Operations.SequenceEqual(["Inspect", "Validate", "Prepare", "GrantSid"]) && platform.Password == "",
    "A current-account membership failure never resets, deletes or disables the account.");

passed += await HostSetupProtocolChecks.RunAsync();
Console.WriteLine($"All {passed} host-configuration and protected-transport checks passed. Only a fake platform was used; no machine settings changed.");

sealed class FakePlatform : IHostPlatform
{
    public const string CurrentSid = "S-1-5-21-1-2-3-1001";
    public HostMachineInfo Machine { get; set; } = new(true, "WinNT", "Professional", "TEST-PC", 3398);
    public HostAccountInfo? Account { get; set; }
    public string? Failure { get; set; }
    public List<string> Operations { get; } = [];
    public string Password { get; private set; } = "";
    public string Marker { get; private set; } = "";
    public string? GrantedSid { get; private set; }
    private void Perform(string operation)
    {
        Operations.Add(operation);
        if (Failure == operation) throw new HostSetupException("Test failure during " + operation);
    }
    public HostMachineInfo Inspect() { Perform("Inspect"); return Machine; }
    public HostAccountInfo? FindAccount(string username) { Perform("Find"); return Account; }
    public void PrepareRemoteDesktop(int port, string helperExecutablePath, Guid instanceId) => Perform("Prepare");
    public void CreateAccount(string username, string password, string ownershipMarker) { Perform("Create"); Password = password; Marker = ownershipMarker; Account = new HostAccountInfo(ownershipMarker, false, false); }
    public void EnsureRemoteDesktopMembership(string username) => Perform("Groups");
    public void EnableManagedAccount(string username) { Perform("Enable"); Account = Account! with { IsEnabled = true }; }
    public void SetAccountPassword(string username, string password) { Perform("Password"); Password = password; }
    public void DeleteAccount(string username) => Perform("Delete");
    public void ValidateAccountCredentials(HostAccountCredentials credentials)
    {
        Perform("Validate");
        if (credentials.Sid != CurrentSid) throw new HostSetupException("These credentials belong to a different Windows account.");
    }
    public void GrantRemoteDesktopAccess(string sid) { Perform("GrantSid"); GrantedSid = sid; }
}
