namespace MDRemoteNG.Host;

/// <summary>Limits privileged account changes to the separately enrolled administrator SSH identity.</summary>
public static class ManagedAdministratorAccountGuard
{
    public static void Validate(string username, string ownershipMarker, HostAccountInfo? account, bool allowNewDisabled = false)
    {
        const string prefix = "MDRemoteNG managed host ", suffix = " SSH administrator";
        if (ownershipMarker.Length != prefix.Length + 32 + suffix.Length ||
            !ownershipMarker.StartsWith(prefix, StringComparison.Ordinal) || !ownershipMarker.EndsWith(suffix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(ownershipMarker[prefix.Length..^suffix.Length], "N", out Guid instanceId) || instanceId == Guid.Empty ||
            !username.Equals(HostConfiguration.AccountName(instanceId, "SSH", true), StringComparison.Ordinal) ||
            !ownershipMarker.Equals(HostConfiguration.OwnershipMarker(instanceId, "SSH", true), StringComparison.Ordinal))
            throw new HostSetupException("The managed administrator SSH account identity is invalid. No account was changed.");
        if (account is null || !account.Comment.Equals(ownershipMarker, StringComparison.Ordinal))
            throw new HostSetupException("This administrator SSH account is not owned by the MDRemoteNG installation. No account was changed.");
        if (!account.IsAdministrator && !(allowNewDisabled && !account.IsEnabled))
            throw new HostSetupException("The managed administrator SSH account has unexpected privileges. No account was changed.");
    }
}
