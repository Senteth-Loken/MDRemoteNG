using MDRemoteNG.Host;

namespace mRemoteNG.App;

// Tests must inject a setup callback. A missed injection fails without starting an
// elevated child or changing accounts, services, registry values or firewall rules.
internal static class HostSetupClient
{
    public static Task<HostConfigurationResult> ConfigureAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("UI checks must inject their host setup operation.");

    public static Task<HostConfigurationResult> ConfigureCurrentAccountAsync(Guid instanceId, string password, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("UI checks must inject their Windows account setup operation.");
}
