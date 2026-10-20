namespace MDRemoteNG.Host;

public sealed partial class WindowsHostPlatform
{
    public void EnsureAdministratorSecureShellMembership(string username, string ownershipMarker)
    {
        // A new identity is still disabled while acquiring its groups. Reused identities
        // must already have the requested administrator role and exact enrollment marker.
        ManagedAdministratorAccountGuard.Validate(username, ownershipMarker, FindAccount(username), allowNewDisabled: true);
        AddToGroup(username, "S-1-5-32-545");
        if (HasOpenSshUsersGroup()) AddToNamedGroup(username, OpenSshUsers);
        ManagedAdministratorAccountGuard.Validate(username, ownershipMarker, FindAccount(username), allowNewDisabled: true);
        AddToNamedGroup(username, LocalGroupName("S-1-5-32-544"));
        ManagedAdministratorAccountGuard.Validate(username, ownershipMarker, FindAccount(username));
    }

    public void EnableAdministratorManagedAccount(string username, string ownershipMarker)
    {
        ManagedAdministratorAccountGuard.Validate(username, ownershipMarker, FindAccount(username));
        var flags = new UserInfo1008 { Flags = ManagedFlags };
        Check(NetUserSetFlags(null, username, 1008, ref flags, out _), "enable the managed administrator SSH account");
    }

    public void SetAdministratorManagedAccountPassword(string username, string password, string ownershipMarker)
    {
        ManagedAdministratorAccountGuard.Validate(username, ownershipMarker, FindAccount(username));
        var user = new UserInfo1003 { Password = password };
        Check(NetUserSetPassword(null, username, 1003, ref user, out _), "set the managed administrator SSH password");
    }
}
