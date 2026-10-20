#nullable enable
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using MDRemoteNG.Host;
using Microsoft.Win32.SafeHandles;

namespace mRemoteNG.App
{
    internal static class HostSetupIdentity
    {
        private const int AccessDenied = 5;
        private const int NotAllAssigned = 1300;
        private const uint QueryLimitedInformation = 0x1000;
        private const uint TokenQuery = 0x8;
        private const uint TokenAdjustPrivileges = 0x20;
        private const uint PrivilegeEnabled = 0x2;

        [StructLayout(LayoutKind.Sequential)]
        private struct Luid
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TokenPrivileges
        {
            public uint Count;
            public Luid Luid;
            public uint Attributes;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token,
            [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges state,
            uint bufferLength, out TokenPrivileges previous, out uint returnedLength);

        [DllImport("advapi32.dll", EntryPoint = "AdjustTokenPrivileges", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RestoreTokenPrivileges(SafeAccessTokenHandle token,
            [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges state,
            uint bufferLength, IntPtr previous, IntPtr returnedLength);

        internal static string ParentAccountSid(int parentId)
        {
            if (parentId <= 0) throw VerificationFailure();
            try
            {
                return ReadAccountSid(parentId);
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == AccessDenied)
            {
                // Alternate-account UAC may need this privilege to query the original
                // user's process token. Ordinary same-account reads never adjust it.
                return ReadWithTemporaryDebugPrivilege(parentId);
            }
            catch (Win32Exception exception)
            {
                throw VerificationFailure(exception);
            }
        }

        private static string ReadAccountSid(int processId)
        {
            using SafeProcessHandle process = OpenProcess(QueryLimitedInformation, false, processId);
            if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            bool opened = OpenProcessToken(process, TokenQuery, out SafeAccessTokenHandle token);
            int error = opened ? 0 : Marshal.GetLastWin32Error();
            using (token)
            {
                if (!opened) throw new Win32Exception(error);
                using var identity = new WindowsIdentity(token.DangerousGetHandle());
                return identity.User?.Value ?? throw VerificationFailure();
            }
        }

        private static string ReadWithTemporaryDebugPrivilege(int parentId)
        {
            using var process = new SafeProcessHandle(GetCurrentProcess(), ownsHandle: false);
            bool opened = OpenProcessToken(process, TokenQuery | TokenAdjustPrivileges, out SafeAccessTokenHandle token);
            int error = opened ? 0 : Marshal.GetLastWin32Error();
            using (token)
            {
                if (!opened) throw VerificationFailure(new Win32Exception(error));
                if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out Luid luid))
                    throw VerificationFailure(new Win32Exception(Marshal.GetLastWin32Error()));

                var requested = new TokenPrivileges { Count = 1, Luid = luid, Attributes = PrivilegeEnabled };
                TokenPrivileges previous = default;
                try
                {
                    bool adjusted = AdjustTokenPrivileges(token, false, ref requested,
                        (uint)Marshal.SizeOf<TokenPrivileges>(), out previous, out _);
                    int adjustmentError = Marshal.GetLastWin32Error();
                    // The API can return true while reporting that this token does not
                    // possess the privilege. Never retry the read in that case.
                    if (adjustmentError == NotAllAssigned || !adjusted || adjustmentError != 0)
                        throw VerificationFailure(new Win32Exception(adjustmentError));
                    try { return ReadAccountSid(parentId); }
                    catch (Win32Exception exception) { throw VerificationFailure(exception); }
                }
                finally
                {
                    // PreviousState contains only privileges actually changed. A zero
                    // count means the privilege was already enabled or was not assigned.
                    if (previous.Count != 0)
                    {
                        bool restored = RestoreTokenPrivileges(token, false, ref previous, 0, IntPtr.Zero, IntPtr.Zero);
                        int restorationError = Marshal.GetLastWin32Error();
                        if (!restored || restorationError != 0)
                            throw new HostSetupException("Windows could not restore the temporary host-setup identity-check permission.",
                                new Win32Exception(restorationError));
                    }
                }
            }
        }

        private static HostSetupException VerificationFailure(Exception? inner = null)
            => inner is null
                ? new HostSetupException("The requesting Windows account could not be verified.")
                : new HostSetupException("The requesting Windows account could not be verified with this administrator account.", inner);
    }
}
