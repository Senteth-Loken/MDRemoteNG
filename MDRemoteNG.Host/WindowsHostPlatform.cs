using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace MDRemoteNG.Host;

/// <summary>Used only inside the explicitly elevated host-setup process.</summary>
public sealed partial class WindowsHostPlatform : IHostPlatform
{
    private const string TerminalKey = @"SYSTEM\CurrentControlSet\Control\Terminal Server";
    private const string ListenerKey = TerminalKey + @"\WinStations\RDP-Tcp";

    public HostMachineInfo Inspect()
    {
        if (!OperatingSystem.IsWindows()) throw new HostSetupException("Host setup requires Windows.");
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        bool elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        using RegistryKey? product = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ProductOptions");
        using RegistryKey? version = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        using RegistryKey? listener = Registry.LocalMachine.OpenSubKey(ListenerKey);
        using RegistryKey? policy = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services");
        return new HostMachineInfo(elevated, product?.GetValue("ProductType") as string ?? "", version?.GetValue("EditionID") as string ?? "",
            Environment.MachineName, Convert.ToInt32(listener?.GetValue("PortNumber", 3389) ?? 3389),
            Convert.ToInt32(policy?.GetValue("fDenyTSConnections", 0) ?? 0) == 0,
            Convert.ToInt32(policy?.GetValue("UserAuthentication", 1) ?? 1) == 1,
            FirewallPolicyAllowsChanges(), Environment.OSVersion.Version.Build);
    }

    public HostAccountInfo? FindAccount(string username)
    {
        uint result = NetUserGetInfo(null, username, 1, out IntPtr buffer);
        if (result == 2221) return null;
        Check(result, "inspect the managed account");
        try
        {
            UserInfo1 user = Marshal.PtrToStructure<UserInfo1>(buffer);
            return new HostAccountInfo(user.Comment ?? "", IsAdministrator(username), (user.Flags & 0x2) == 0);
        }
        finally { NetApiBufferFree(buffer); }
    }

    public void PrepareRemoteDesktop(int port, string helperExecutablePath, Guid instanceId)
    {
        string helper = Path.GetFullPath(helperExecutablePath);
        if (!File.Exists(helper)) throw new HostSetupException("The MDRemoteNG discovery helper is missing. Reinstall or rebuild the complete application folder.");
        EnsureTerminalService();
        // Use the supported Terminal Services provider and retain policy enforcement.
        // Configure NLA first; its failure must never enable weaker RDP access.
        InvokeTerminalMethod("Win32_TSGeneralSetting WHERE TerminalName='RDP-tcp'", "SetUserAuthenticationRequired", [1u]);
        InvokeTerminalMethod("Win32_TerminalServiceSetting", "SetAllowTSConnections", [1u, 0u]);
        foreach (HostFirewallRule rule in HostConfiguration.FirewallRules(instanceId, port, helper))
            AddFirewallRule(rule);
    }

    public void CreateAccount(string username, string password, string ownershipMarker)
    {
        var user = new UserInfo1
        {
            Name = username, Password = password, Privilege = 1, Comment = ownershipMarker,
            Flags = ManagedFlags | 0x2 // Remain disabled until group membership is complete.
        };
        Check(NetUserAdd(null, 1, ref user, out _), "create the managed remote-access account");
    }

    public void EnsureRemoteDesktopMembership(string username)
    {
        // Recheck immediately before granting access, including externally modified accounts.
        if (IsAdministrator(username)) throw new HostSetupException("The managed account has administrator access and cannot be shared.");
        AddToGroup(username, "S-1-5-32-545"); // Users.
        AddToGroup(username, "S-1-5-32-555"); // Remote Desktop Users, locale independent.
    }

    public void EnableManagedAccount(string username)
    {
        if (IsAdministrator(username)) throw new HostSetupException("The managed account has administrator access and cannot be shared.");
        var flags = new UserInfo1008 { Flags = ManagedFlags };
        Check(NetUserSetFlags(null, username, 1008, ref flags, out _), "enable the managed remote-access account");
    }

    public void SetAccountPassword(string username, string password)
    {
        if (IsAdministrator(username)) throw new HostSetupException("The managed account has administrator access and cannot be shared.");
        var user = new UserInfo1003 { Password = password };
        Check(NetUserSetPassword(null, username, 1003, ref user, out _), "set the managed remote-access password");
    }

    public void DeleteAccount(string username) => Check(NetUserDel(null, username), "remove the incomplete managed account");

    public void ValidateAccountCredentials(HostAccountCredentials credentials) => ValidateCredentials(credentials);

    public static void ValidateCredentials(HostAccountCredentials credentials)
    {
        HostConfiguration.ValidateCredentialsInput(credentials);
        SecurityIdentifier expected;
        try { expected = new SecurityIdentifier(credentials.Sid); }
        catch (ArgumentException exception) { throw new HostSetupException("The Windows account identity is invalid.", exception); }
        string? domain = credentials.Domain.Length == 0 ? null : credentials.Domain;
        if (!LogonUser(credentials.Username, domain, credentials.Password, 3, 0, out var token))
        {
            int error = Marshal.GetLastWin32Error();
            token.Dispose();
            throw new HostSetupException("Windows could not validate this account password (error " + error + "). Use your Windows account password rather than a Windows Hello PIN.");
        }
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            if (identity.User != expected)
                throw new HostSetupException("These credentials belong to a different Windows account. Enter the password for the account shown.");
        }
    }

    public void GrantRemoteDesktopAccess(string sid)
    {
        var account = new SecurityIdentifier(sid);
        byte[] bytes = new byte[account.BinaryLength];
        account.GetBinaryForm(bytes, 0);
        IntPtr buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            var member = new LocalGroupSidMember { Sid = buffer };
            uint result = NetLocalGroupAddSidMember(null, LocalGroupName("S-1-5-32-555"), 0, ref member, 1);
            if (result != 1378) Check(result, "grant your Windows account Remote Desktop access");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static uint ManagedFlags => 0x1 | 0x200 | 0x10000; // Script, normal, no expiry. Password-change rights are ACL-controlled.

    private static bool IsAdministrator(string username)
    {
        uint result = NetUserGetLocalGroups(null, username, 0, 1, out IntPtr buffer, uint.MaxValue, out uint count, out _);
        Check(result, "inspect the managed account's groups");
        try
        {
            string administrators = LocalGroupName("S-1-5-32-544");
            for (int index = 0; index < count; index++)
            {
                IntPtr name = Marshal.ReadIntPtr(buffer, index * IntPtr.Size);
                if (string.Equals(Marshal.PtrToStringUni(name), administrators, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        finally { if (buffer != IntPtr.Zero) NetApiBufferFree(buffer); }
    }

    private static string LocalGroupName(string sid)
    {
        string name = ((NTAccount)new SecurityIdentifier(sid).Translate(typeof(NTAccount))).Value;
        return name[(name.LastIndexOf('\\') + 1)..];
    }

    private static void AddToGroup(string username, string sid)
    {
        var member = new LocalGroupMember { Name = Environment.MachineName + "\\" + username };
        uint result = NetLocalGroupAddMembers(null, LocalGroupName(sid), 3, ref member, 1);
        if (result != 1378) Check(result, "grant managed Remote Desktop access"); // Already a member.
    }

    private static void Check(uint result, string operation)
    {
        if (result != 0) throw new HostSetupException("Windows could not " + operation + " (error " + result + "): " + new Win32Exception((int)result).Message);
    }

    private static void InvokeTerminalMethod(string queryClass, string method, object[] arguments)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\cimv2\terminalservices", new ConnectionOptions
            {
                EnablePrivileges = true,
                Authentication = AuthenticationLevel.PacketPrivacy
            });
            using var search = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM " + queryClass));
            using ManagementObjectCollection objects = search.Get();
            foreach (ManagementObject setting in objects)
            {
                using (setting)
                {
                    uint result = Convert.ToUInt32(setting.InvokeMethod(method, arguments));
                    Check(result, "configure Remote Desktop through Windows policy");
                    return;
                }
            }
            throw new HostSetupException("Windows could not find its Remote Desktop host settings.");
        }
        catch (ManagementException exception)
        {
            throw new HostSetupException("Windows could not configure Remote Desktop. An organisation policy or missing Remote Desktop host component may prevent setup.", exception);
        }
    }

    private static void AddFirewallRule(HostFirewallRule configuration, bool block = false, int? profiles = null, string? remoteAddresses = null)
    {
        object? policyObject = null, ruleObject = null, rulesObject = null;
        try
        {
            policyObject = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!);
            ruleObject = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!);
            dynamic policy = policyObject!, rule = ruleObject!;
            rule.Name = configuration.Name;
            rule.Description = "MDRemoteNG authenticated local-network host access";
            rule.Protocol = configuration.Protocol;
            if (configuration.LocalPort.HasValue) rule.LocalPorts = configuration.LocalPort.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (configuration.ApplicationName != null) rule.ApplicationName = configuration.ApplicationName;
            if (configuration.ServiceName != null) rule.ServiceName = configuration.ServiceName;
            rule.RemoteAddresses = remoteAddresses ?? configuration.RemoteAddresses;
            rule.Profiles = profiles ?? configuration.Profiles;
            rule.Direction = 1; // Inbound.
            rule.Action = block ? 0 : 1;
            rule.EdgeTraversal = false;
            rule.Enabled = true;
            rulesObject = policy.Rules;
            dynamic rules = rulesObject;
            // INetFwRules.Add replaces a rule with the same name, making setup idempotent.
            rules.Add(rule);
        }
        catch (COMException exception)
        {
            throw new HostSetupException("Windows Firewall could not apply the local-subnet host rules (error " + exception.ErrorCode + ").", exception);
        }
        finally
        {
            foreach (object? value in new[] { rulesObject, ruleObject, policyObject })
                if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }
    }

    private static bool FirewallPolicyAllowsChanges()
    {
        object? policyObject = null;
        try
        {
            policyObject = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!);
            dynamic policy = policyObject!;
            return (int)policy.LocalPolicyModifyState == 0;
        }
        catch (COMException exception)
        {
            throw new HostSetupException("Windows Firewall policy could not be inspected. Host setup has not changed any accounts.", exception);
        }
        finally
        {
            if (policyObject != null && Marshal.IsComObject(policyObject)) Marshal.FinalReleaseComObject(policyObject);
        }
    }
}
