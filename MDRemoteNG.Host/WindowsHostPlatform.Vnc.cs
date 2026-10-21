namespace MDRemoteNG.Host;

public sealed partial class WindowsHostPlatform
{
    public void PrepareVnc(int port, string helperExecutablePath, Guid instanceId)
    {
        string helper = Path.GetFullPath(helperExecutablePath);
        if (!File.Exists(helper)) throw new HostSetupException("The MDRemoteNG discovery helper is missing. Reinstall the complete application folder.");
        if (port is < 1 or > 65535 || instanceId == Guid.Empty) throw new HostSetupException("The VNC host settings are invalid.");
        if (!FirewallPolicyAllowsChanges()) throw new HostSetupException("An organisation policy prevents applying the local-network firewall rules.");
        object? policyObject = null;
        try
        {
            policyObject = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!);
            dynamic policy = policyObject!;
            foreach (int profile in new[] { 1, 2, 4 })
                if (!(bool)policy.FirewallEnabled[profile])
                    throw new HostSetupException("Windows Firewall must be enabled before local-network desktop sharing can be configured.");
        }
        finally
        {
            if (policyObject != null && System.Runtime.InteropServices.Marshal.IsComObject(policyObject))
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(policyObject);
        }
        string prefix = "MDRemoteNG " + instanceId.ToString("N")[..12];
        AddFirewallRule(new HostFirewallRule(prefix + " VNC TLS", 6, port, helper, null));
        AddFirewallRule(new HostFirewallRule(prefix + " Discovery UDP", 17, 48765, helper, null));
        AddFirewallRule(new HostFirewallRule(prefix + " Discovery TCP", 6, null, helper, null));
    }
}

public static class VncHostConfiguration
{
    public static HostConfigurationResult Configure(IHostPlatform platform, Guid instanceId, string helper, int port)
    {
        if (instanceId == Guid.Empty || port is < 1 or > 65535 || string.IsNullOrWhiteSpace(helper))
            throw new HostSetupException("The VNC host settings are invalid.");
        HostMachineInfo machine = platform.Inspect();
        if (!machine.IsElevated) throw new HostSetupException("Approve the Windows administrator prompt to configure desktop sharing.");
        if (machine.ProductType is not ("WinNT" or "ServerNT"))
            throw new HostSetupException("Desktop sharing is supported on Windows client computers and member servers.");
        if (!machine.PolicyAllowsFirewall)
            throw new HostSetupException("An organisation policy prevents applying the local-network firewall rules.");
        platform.PrepareVnc(port, helper, instanceId);
        return new HostConfigurationResult
        {
            Protocol = "VNC", Port = port,
            StatusMessage = "Encrypted VNC sharing is prepared for this signed-in user's primary desktop. It runs while the discovery helper is running and uses generated MDRemoteNG credentials."
        };
    }
}
