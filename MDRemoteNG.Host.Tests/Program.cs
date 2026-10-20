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

platform = new FakePlatform { Machine = new HostMachineInfo(true, "WinNT", "Core", "TEST-PC", -1, false, false) };
configured = HostConfiguration.Configure(platform, instance, helper, "SSH");
Check(configured.Protocol == "SSH" && configured.Port == 2222 && configured.Domain == "" &&
    configured.SshHostKeyFingerprints.SequenceEqual(FakePlatform.SshKeys),
    "SSH supports Windows Home independently of RDP policies and returns the actual port and pinned host identities.");
Check(platform.Operations.SequenceEqual(["Inspect", "Find", "PrepareSSH", "Create", "SSHGroups", "Enable"]),
    "Dedicated SSH setup creates a standard account and grants SSH membership without touching RDP settings or groups.");
platform = new FakePlatform();
configured = HostConfiguration.ConfigureCurrentAccount(platform, instance, helper, currentCredentials, "SSH");
Check(platform.Operations.SequenceEqual(["Inspect", "Validate", "PrepareSSH", "GrantSSHSid"]) && configured.Username == "TEST-DOMAIN\\alice" &&
    configured.Domain == "" && configured.Password == currentCredentials.Password && platform.Password == "",
    "Current-account SSH setup preserves the existing password and qualifies domain usernames for the SSH client.");
Check(HostConfiguration.SshUsername("alice", "TEST-PC", "TEST-PC") == "alice" &&
    HostConfiguration.SshUsername("alice", "MicrosoftAccount", "TEST-PC") == "alice",
    "SSH uses the local account name for local and internet-connected Windows accounts.");
platform = new FakePlatform { Account = new HostAccountInfo(HostConfiguration.OwnershipMarker(instance, "SSH"), false) };
HostConfiguration.Configure(platform, instance, helper, "SSH");
Check(platform.Operations.SequenceEqual(["Inspect", "Find", "PrepareSSH", "SSHGroups", "Password"]),
    "An owned SSH account rotates its password only after SSH preparation and membership succeed.");
Check(HostConfiguration.AccountName(instance, "SSH") != HostConfiguration.AccountName(instance) &&
    HostConfiguration.OwnershipMarker(instance, "SSH") != HostConfiguration.OwnershipMarker(instance) &&
    platform.LastPasswordAccount == HostConfiguration.AccountName(instance, "SSH"),
    "Managed SSH and RDP use separate owned accounts so SSH password rotation preserves saved RDP credentials.");
foreach (HostMachineInfo unsupported in new[]
{
    new HostMachineInfo(true, "LanmanNT", "ServerStandard", "TEST-DC", 3389),
    new HostMachineInfo(true, "WinNT", "Core", "TEST-PC", 3389, OsBuild: 17000),
    new HostMachineInfo(true, "WinNT", "Core", "TEST-PC", 3389, PolicyAllowsFirewall: false)
})
{
    platform = new FakePlatform { Machine = unsupported };
    try { HostConfiguration.Configure(platform, instance, helper, "SSH"); throw new Exception("Expected SSH preflight rejection."); }
    catch (HostSetupException) { }
    Check(platform.Operations.SequenceEqual(["Inspect"]), "SSH refuses domain controllers, unsupported Windows builds and firewall policy restrictions before mutations.");
}
platform = new FakePlatform { Failure = "PrepareSSH" };
Reject(() => HostConfiguration.Configure(platform, instance, helper, "SSH"), "Test failure");
Check(!platform.Operations.Contains("Create") && !platform.Operations.Contains("Password"), "SSH installation or policy failures cannot create accounts or rotate passwords.");
platform = new FakePlatform { Failure = "SSHGroups" };
Reject(() => HostConfiguration.Configure(platform, instance, helper, "SSH"), "Test failure");
Check(platform.Operations.Last() == "Delete" && !platform.Operations.Contains("Enable"), "Failed new SSH-account membership rolls back its disabled account.");
platform = new FakePlatform { SshFingerprints = [] };
Reject(() => HostConfiguration.Configure(platform, instance, helper, "SSH"), "host identity");
Check(!platform.Operations.Contains("Create"), "SSH cannot be auto-shared without a verified host fingerprint.");

IReadOnlyList<HostFirewallRule> sshRules = HostConfiguration.FirewallRules(instance, 2222, helper, "SSH");
Check(sshRules.Count == 3 && sshRules.All(rule => rule.Profiles == 3 && rule.RemoteAddresses == "LocalSubnet") &&
    sshRules.Count(rule => rule.ServiceName == "sshd" && rule.LocalPort == 2222 && rule.Protocol == 6) == 1 && sshRules.All(rule => rule.ServiceName != "TermService"),
    "SSH firewall rules scope only its actual TCP port and discovery, without enabling or changing RDP.");

const string effectiveSsh = """
    port 2222
    passwordauthentication yes
    authenticationmethods any
    permittty yes
    forcecommand none
    listenaddress 0.0.0.0:2222
    hostkey C:/ProgramData/ssh/ssh_host_ed25519_key
    allowgroups administrators "openssh users"
    """;
var sshSettings = SshServerConfiguration.ParseEffective(effectiveSsh, "alice", true);
Check(sshSettings.Port == 2222 && sshSettings.HostKeyPaths.Count == 1,
    "Effective OpenSSH configuration determines the listener and preserves its standard OpenSSH Users access policy.");
foreach (string unsafeConfiguration in new[]
{
    effectiveSsh.Replace("passwordauthentication yes", "passwordauthentication no"),
    effectiveSsh.Replace("authenticationmethods any", "authenticationmethods publickey,password"),
    effectiveSsh + "\ndenyusers alice",
    effectiveSsh + "\nallowusers other-user",
    effectiveSsh.Replace("allowgroups administrators \"openssh users\"", "allowgroups other-group"),
    effectiveSsh.Replace("listenaddress 0.0.0.0:2222", "listenaddress 127.0.0.1:2222"),
    effectiveSsh + "\nport 22",
    effectiveSsh.Replace("forcecommand none", "forcecommand internal-sftp")
})
{
    try { SshServerConfiguration.ParseEffective(unsafeConfiguration, "alice", true); throw new Exception("Expected SSH configuration rejection."); }
    catch (HostSetupException) { }
    Check(true, "Incompatible existing SSH authentication, account or listener policies are refused instead of weakened.");
}
foreach (string conditionalConfiguration in new[] { "Match Address 192.168.*\n PasswordAuthentication no", "Include\tcustom.conf", "Match=Address 192.168.*\nPasswordAuthentication no", "Include=custom.conf" })
{
    Reject(() => SshServerConfiguration.ValidateConfigurationText(conditionalConfiguration), "conditional or included");
    Check(true, "Conditional SSH policies that cannot be verified for LAN peers are preserved and refused safely.");
}
SshServerConfiguration.ValidateConfigurationText("# Default Windows SSH configuration\nMatch Group administrators\n AuthorizedKeysFile __PROGRAMDATA__/ssh/administrators_authorized_keys");
Check(true, "The standard Windows administrators host-key configuration remains supported.");
string publicFingerprint = SshServerConfiguration.PublicKeyFingerprint("ssh-ed25519 " + Convert.ToBase64String(new byte[32]));
Check(publicFingerprint.StartsWith("SHA256:", StringComparison.Ordinal) && publicFingerprint.Length == 50 && !publicFingerprint.EndsWith('='),
    "SSH host pins use the canonical unpadded SHA256 public-key fingerprint format.");

string sshExecutable = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "test-windows", "OpenSSH", "sshd.exe"));
foreach (var unsafeProfile in new[] { (Enabled: false, Action: 0), (Enabled: true, Action: 1) })
{
    Reject(() => SshFirewallPolicy.ValidateProfile(unsafeProfile.Enabled, unsafeProfile.Action), "default inbound");
    Check(true, "Disabled or default-Allow firewall profiles are refused before SSH can be installed or started.");
}
SshFirewallPolicy.ValidateProfile(true, 0);
foreach (SshFirewallAllowance broadRule in new[]
{
    new SshFirewallAllowance(7, "TCP", "2222", sshExecutable, "Any", "Any"),
    new SshFirewallAllowance(0, "TCP", "2222", sshExecutable, "Any", "LocalSubnet"),
    new SshFirewallAllowance(3, "TCP", "2222", "Any", "sshd", "*"),
    new SshFirewallAllowance(3, "TCP", "Any", "Any", "Any", "Any"),
    new SshFirewallAllowance(7, "Any", "Any", "Any", "Any", "LocalSubnet"),
    new SshFirewallAllowance(3, "TCP", "2000-3000", "Any", "Any", "0.0.0.0/0")
})
{
    Reject(() => SshFirewallPolicy.ValidateRules([broadRule], 2222, sshExecutable), "existing enabled firewall");
    Check(true, "Pre-existing broad SSH, any-program/service or overlapping-port allow rules are refused without changing administrator rules.");
}
SshFirewallPolicy.ValidateRules([
    new SshFirewallAllowance(3, "TCP", "2222", sshExecutable, "sshd", "LocalSubnet"),
    new SshFirewallAllowance(7, "TCP", "3389", "Any", "Any", "Any"),
    new SshFirewallAllowance(7, "TCP", "Any", Path.Combine(Directory.GetCurrentDirectory(), "unrelated.exe"), "Any", "Any"),
    new SshFirewallAllowance(7, "UDP", "2222", "Any", "Any", "Any"),
    new SshFirewallAllowance(7, "TCP", "2222", "Any", "TermService", "Any")
], 2222, sshExecutable);
Check(true, "A scoped SSH allow rule coexists with unrelated ports, applications, UDP rules and other Windows services.");
DateTime sshProcessStarted = new(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc);
SshServerConfiguration.VerifyLoadedFiles(sshProcessStarted, [sshProcessStarted.AddMinutes(-1)]);
Reject(() => SshServerConfiguration.VerifyLoadedFiles(sshProcessStarted, [sshProcessStarted.AddSeconds(1)]), "changed after");
Check(true, "Configuration or actual private host keys newer than the live SSH service cause a safe refusal instead of stale identity pins or a live-session restart.");

platform = new FakePlatform { Machine = new HostMachineInfo(true, "WinNT", "Core", "TEST-PC", -1, false, false) };
configured = VncHostConfiguration.Configure(platform, instance, helper, 5900);
Check(configured.Protocol == "VNC" && configured.Port == 5900 && platform.Operations.SequenceEqual(["Inspect", "PrepareVNC"]) && configured.Password == "",
    "VNC preparation supports Windows Home and changes only its firewall seam, without Windows account or RDP operations.");
platform = new FakePlatform { Machine = new HostMachineInfo(true, "LanmanNT", "ServerStandard", "TEST-DC", 3389) };
Reject(() => VncHostConfiguration.Configure(platform, instance, helper, 5900), "member servers");
Check(platform.Operations.SequenceEqual(["Inspect"]), "VNC preparation rejects a domain controller before firewall or account changes.");

string administratorName = HostConfiguration.AccountName(instance, "SSH", true);
string administratorMarker = HostConfiguration.OwnershipMarker(instance, "SSH", true);
platform = new FakePlatform();
configured = HostConfiguration.Configure(platform, instance, helper, "SSH", administratorAccess: true);
Check(configured.AdministratorAccess && configured.Username == administratorName && platform.Account is { IsAdministrator: true, IsEnabled: true } &&
    configured.StatusMessage.Contains("dedicated administrator", StringComparison.Ordinal),
    "Explicit administrator SSH setup returns a separately enrolled administrator account and describes its privileges.");
Check(administratorName.StartsWith("mdadm_", StringComparison.Ordinal) && administratorName.Length <= 20 &&
    administratorName != HostConfiguration.AccountName(instance, "SSH") && administratorName != HostConfiguration.AccountName(instance) &&
    administratorMarker != HostConfiguration.OwnershipMarker(instance, "SSH"),
    "Administrator SSH has a distinct valid account name and ownership marker, preserving standard SSH and RDP accounts.");
Check(platform.Operations.SequenceEqual(["Inspect", "Find", "PrepareSSH", "Create", "AdminSSHGroups", "PrepareSSH", "AdminEnable"]) &&
    platform.AdminMembershipWasDisabled,
    "A new administrator account stays disabled while acquiring groups and checking its final SSH policy, then enables last.");
Check(configured.Port == 2223 && configured.SshHostKeyFingerprints.SequenceEqual(FakePlatform.SshKeys),
    "Administrator setup publishes the port and host identity from the final role-aware SSH policy evaluation.");

platform = new FakePlatform { Account = new HostAccountInfo(administratorMarker, true) };
HostConfiguration.Configure(platform, instance, helper, "SSH", administratorAccess: true);
Check(platform.Operations.SequenceEqual(["Inspect", "Find", "PrepareSSH", "AdminSSHGroups", "PrepareSSH", "AdminPassword"]) &&
    platform.LastPasswordAccount == administratorName,
    "An existing owned administrator SSH account rotates only its own password after all policy and group checks succeed.");
platform = new FakePlatform { Account = new HostAccountInfo(administratorMarker, true, false) };
HostConfiguration.Configure(platform, instance, helper, "SSH", administratorAccess: true);
Check(platform.Operations.TakeLast(2).SequenceEqual(["AdminPassword", "AdminEnable"]) && platform.Account!.IsEnabled,
    "A disabled owned administrator account receives the new password before it can be enabled.");
platform = new FakePlatform { Account = new HostAccountInfo(administratorMarker, true, false), Failure = "AdminPassword" };
Reject(() => HostConfiguration.Configure(platform, instance, helper, "SSH", administratorAccess: true), "Test failure");
Check(!platform.Account!.IsEnabled && !platform.Operations.Contains("AdminEnable") && platform.Password == "",
    "Failed administrator password rotation cannot re-enable a disabled account with its old credential.");

foreach (HostAccountInfo collision in new[]
{
    new HostAccountInfo("Unrelated administrator", true),
    new HostAccountInfo(HostConfiguration.OwnershipMarker(instance, "SSH"), true),
    new HostAccountInfo(administratorMarker, false),
    new HostAccountInfo(administratorMarker, false, false)
})
{
    platform = new FakePlatform { Account = collision };
    Reject(() => HostConfiguration.Configure(platform, instance, helper, "SSH", administratorAccess: true), "unrelated account");
    Check(platform.Operations.SequenceEqual(["Inspect", "Find"]),
        "Foreign account ownership and administrator role mismatches are rejected before any host or account changes.");
}
foreach (string failure in new[] { "AdminSSHGroups", "AdminEnable", "PrepareSSH2" })
{
    platform = new FakePlatform { Failure = failure };
    Reject(() => HostConfiguration.Configure(platform, instance, helper, "SSH", administratorAccess: true), "Test failure");
    Check(platform.Operations.Last() == "Delete" && platform.Account is null && !platform.Operations.Contains("AdminPassword"),
        "A failed new administrator group, enable or final policy check removes its incomplete account without exposing a credential.");
}
platform = new FakePlatform { Account = new HostAccountInfo(administratorMarker, true), Failure = "PrepareSSH2" };
Reject(() => HostConfiguration.Configure(platform, instance, helper, "SSH", administratorAccess: true), "Test failure");
Check(platform.Account!.IsEnabled && !platform.Operations.Contains("AdminPassword") && !platform.Operations.Contains("Delete"),
    "A final administrator SSH policy rejection preserves an existing account and password.");
platform = new FakePlatform { Account = new HostAccountInfo(administratorMarker, true), Failure = "AdminSSHGroups" };
Reject(() => HostConfiguration.Configure(platform, instance, helper, "SSH", administratorAccess: true), "Test failure");
Check(platform.Account!.IsEnabled && platform.Password == "" && !platform.Operations.Contains("AdminPassword") && !platform.Operations.Contains("Delete"),
    "A failed existing administrator membership check leaves its password and enabled state untouched.");
platform = new FakePlatform { Failure = "AdminEnable", FailDelete = true };
Reject(() => HostConfiguration.Configure(platform, instance, helper, "SSH", administratorAccess: true), "could not be removed");
Check(platform.Account is { IsAdministrator: true, IsEnabled: false },
    "If rollback fails, the newly created administrator account remains disabled and setup reports its cleanup requirement.");

foreach (string protocol in new[] { "RDP", "VNC" })
{
    platform = new FakePlatform();
    try { HostConfiguration.Configure(platform, instance, helper, protocol, administratorAccess: true); throw new Exception("Expected administrator mode rejection."); }
    catch (HostSetupException) { }
    Check(platform.Operations.Count == 0, "Dedicated administrator enrollment is limited to SSH and cannot change RDP or VNC accounts.");
}
foreach (var invalid in new[]
{
    (Name: HostConfiguration.AccountName(instance, "SSH"), Marker: administratorMarker, Account: new HostAccountInfo(administratorMarker, true)),
    (Name: administratorName, Marker: HostConfiguration.OwnershipMarker(instance, "SSH"), Account: new HostAccountInfo(administratorMarker, true)),
    (Name: administratorName, Marker: administratorMarker, Account: new HostAccountInfo("Foreign ownership", true)),
    (Name: administratorName, Marker: administratorMarker, Account: new HostAccountInfo(administratorMarker, false))
})
{
    try { ManagedAdministratorAccountGuard.Validate(invalid.Name, invalid.Marker, invalid.Account); throw new Exception("Expected privileged target rejection."); }
    catch (HostSetupException) { }
    Check(true, "Privileged native operations require the exact administrator account name, enrollment marker and actual group membership.");
}
platform = new FakePlatform();
configured = HostConfiguration.Configure(platform, instance, helper, "SSH");
Check(!configured.AdministratorAccess && platform.Account is { IsAdministrator: false } && !platform.Operations.Any(value => value.StartsWith("Admin", StringComparison.Ordinal)),
    "Standard dedicated SSH remains the default and never invokes privileged account operations.");
platform = new FakePlatform { Account = new HostAccountInfo(HostConfiguration.OwnershipMarker(instance, "SSH"), true) };
Reject(() => HostConfiguration.Configure(platform, instance, helper, "SSH"), "unrelated or administrative");
Check(platform.Operations.SequenceEqual(["Inspect", "Find"]),
    "Standard SSH still refuses an owned standard account that was externally added to Administrators.");
platform = new FakePlatform { Account = new HostAccountInfo("Current administrator", true) };
configured = HostConfiguration.ConfigureCurrentAccount(platform, instance, helper, currentCredentials, "SSH");
Check(!configured.AdministratorAccess && platform.Account!.IsAdministrator && platform.Operations.SequenceEqual(["Inspect", "Validate", "PrepareSSH", "GrantSSHSid"]),
    "Current-account SSH keeps the account's existing privileges and never enrolls or promotes a dedicated administrator.");

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
    public static readonly string[] SshKeys = ["SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"];
    public IReadOnlyList<string> SshFingerprints { get; set; } = SshKeys;
    public string? LastPasswordAccount { get; private set; }
    public bool AdminMembershipWasDisabled { get; private set; }
    public bool FailDelete { get; set; }
    private int _sshPreparations;
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
    public void SetAccountPassword(string username, string password) { Perform("Password"); Password = password; LastPasswordAccount = username; }
    public void DeleteAccount(string username) { Perform("Delete"); if (FailDelete) throw new HostSetupException("Test failure during rollback"); Account = null; }
    public void ValidateAccountCredentials(HostAccountCredentials credentials)
    {
        Perform("Validate");
        if (credentials.Sid != CurrentSid) throw new HostSetupException("These credentials belong to a different Windows account.");
    }
    public void GrantRemoteDesktopAccess(string sid) { Perform("GrantSid"); GrantedSid = sid; }
    public HostSshConfiguration PrepareSecureShell(string username, string domain, string helperExecutablePath, Guid instanceId)
    {
        Perform("PrepareSSH");
        _sshPreparations++;
        if (Failure == "PrepareSSH2" && _sshPreparations == 2) throw new HostSetupException("Test failure during final administrator policy check");
        return new HostSshConfiguration(_sshPreparations == 1 ? 2222 : 2223, SshFingerprints);
    }
    public void EnsureSecureShellMembership(string username) => Perform("SSHGroups");
    public void GrantSecureShellAccess(string sid) { Perform("GrantSSHSid"); GrantedSid = sid; }
    public void PrepareVnc(int port, string helperExecutablePath, Guid instanceId) => Perform("PrepareVNC");
    public void EnsureAdministratorSecureShellMembership(string username, string ownershipMarker)
    {
        ManagedAdministratorAccountGuard.Validate(username, ownershipMarker, Account, allowNewDisabled: true);
        AdminMembershipWasDisabled = !Account!.IsEnabled;
        Perform("AdminSSHGroups");
        Account = Account with { IsAdministrator = true };
    }
    public void EnableAdministratorManagedAccount(string username, string ownershipMarker)
    {
        ManagedAdministratorAccountGuard.Validate(username, ownershipMarker, Account);
        Perform("AdminEnable");
        Account = Account! with { IsEnabled = true };
    }
    public void SetAdministratorManagedAccountPassword(string username, string password, string ownershipMarker)
    {
        ManagedAdministratorAccountGuard.Validate(username, ownershipMarker, Account);
        Perform("AdminPassword");
        Password = password;
        LastPasswordAccount = username;
    }
}
