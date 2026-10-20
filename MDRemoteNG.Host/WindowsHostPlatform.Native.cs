using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MDRemoteNG.Host;

public sealed partial class WindowsHostPlatform
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Password;
        public uint PasswordAge, Privilege;
        [MarshalAs(UnmanagedType.LPWStr)] public string? HomeDirectory;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ScriptPath;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo1003 { [MarshalAs(UnmanagedType.LPWStr)] public string Password; }
    [StructLayout(LayoutKind.Sequential)] private struct UserInfo1008 { public uint Flags; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LocalGroupMember { [MarshalAs(UnmanagedType.LPWStr)] public string Name; }
    [StructLayout(LayoutKind.Sequential)] private struct LocalGroupSidMember { public IntPtr Sid; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus { public uint Type, State, Controls, Win32ExitCode, ExitCode, Checkpoint, WaitHint; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceConfiguration
    {
        public uint Type, StartType, ErrorControl;
        public IntPtr BinaryPath, LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies, ServiceStartName, DisplayName;
    }

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)] private static extern uint NetUserGetInfo(string? server, string username, uint level, out IntPtr buffer);
    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)] private static extern uint NetUserAdd(string? server, uint level, ref UserInfo1 info, out uint error);
    [DllImport("Netapi32.dll", EntryPoint = "NetUserSetInfo", CharSet = CharSet.Unicode)] private static extern uint NetUserSetPassword(string? server, string username, uint level, ref UserInfo1003 info, out uint error);
    [DllImport("Netapi32.dll", EntryPoint = "NetUserSetInfo", CharSet = CharSet.Unicode)] private static extern uint NetUserSetFlags(string? server, string username, uint level, ref UserInfo1008 info, out uint error);
    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)] private static extern uint NetUserDel(string? server, string username);
    [DllImport("Netapi32.dll")] private static extern uint NetApiBufferFree(IntPtr buffer);
    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)] private static extern uint NetUserGetLocalGroups(string? server, string username, uint level, uint flags, out IntPtr buffer, uint preferredLength, out uint count, out uint total);
    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)] private static extern uint NetLocalGroupAddMembers(string? server, string group, uint level, ref LocalGroupMember members, uint count);
    [DllImport("Netapi32.dll", EntryPoint = "NetLocalGroupAddMembers", CharSet = CharSet.Unicode)] private static extern uint NetLocalGroupAddSidMember(string? server, string group, uint level, ref LocalGroupSidMember members, uint count);
    [DllImport("advapi32.dll", EntryPoint = "LogonUserW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool LogonUser(string username, string? domain, string password, int logonType, int provider, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string service, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceConfig(IntPtr service, IntPtr configuration, uint size, out uint needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ChangeServiceConfig(IntPtr service, uint type, uint start, uint error, string? binary, string? group, IntPtr tag, string? dependencies, string? account, string? password, string? displayName);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StartService(IntPtr service, uint count, IntPtr arguments);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr service);

    private static void EnsureTerminalService()
    {
        IntPtr manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) ThrowServiceError("open the service manager");
        IntPtr service = IntPtr.Zero, configuration = IntPtr.Zero;
        try
        {
            service = OpenService(manager, "TermService", 1 | 2 | 4 | 16);
            if (service == IntPtr.Zero) ThrowServiceError("open Remote Desktop Services");
            QueryServiceConfig(service, IntPtr.Zero, 0, out uint needed);
            if (needed == 0 || needed > 65536) ThrowServiceError("inspect Remote Desktop Services");
            configuration = Marshal.AllocHGlobal((int)needed);
            if (!QueryServiceConfig(service, configuration, needed, out _)) ThrowServiceError("inspect Remote Desktop Services");
            var config = Marshal.PtrToStructure<ServiceConfiguration>(configuration);
            if (config.StartType == 4 && !ChangeServiceConfig(service, uint.MaxValue, 3, uint.MaxValue, null, null, IntPtr.Zero, null, null, null, null))
                ThrowServiceError("enable Remote Desktop Services");
            if (!StartService(service, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() != 1056)
                ThrowServiceError("start Remote Desktop Services");
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            do
            {
                if (!QueryServiceStatus(service, out ServiceStatus status)) ThrowServiceError("check Remote Desktop Services");
                if (status.State == 4) return;
                if (status.State == 1) throw new HostSetupException("Remote Desktop Services stopped before host setup completed.");
                Thread.Sleep(100);
            } while (DateTime.UtcNow < deadline);
            throw new HostSetupException("Remote Desktop Services did not start within 30 seconds.");
        }
        finally
        {
            if (configuration != IntPtr.Zero) Marshal.FreeHGlobal(configuration);
            if (service != IntPtr.Zero) CloseServiceHandle(service);
            CloseServiceHandle(manager);
        }
    }

    private static void ThrowServiceError(string operation)
    {
        int error = Marshal.GetLastWin32Error();
        throw new HostSetupException("Windows could not " + operation + " (error " + error + "): " + new Win32Exception(error).Message);
    }
}
