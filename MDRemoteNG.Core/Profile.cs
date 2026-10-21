using System.Security.Cryptography;
using System.Text.Json;

namespace MDRemoteNG.Core;

public sealed class Profile
{
    public int Version { get; set; } = 1;
    public Guid InstanceId { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = Environment.MachineName;
    public string NormalizedPin { get; set; } = "";
    public byte[] PinSalt { get; set; } = [];
    public byte[] PinVerifier { get; set; } = [];
    public int FailedAttempts { get; set; }
    public DateTimeOffset LockedUntil { get; set; }
    public bool DiscoveryEnabled { get; set; } = true;
    public bool AutoConnect { get; set; } = true;
    public SharedConnection? SharedConnection { get; set; }
    public List<SharedConnection> SharedConnections { get; set; } = [];
    public VncHostSettings? VncHost { get; set; }
}

/// <summary>A local-host endpoint explicitly enrolled for matching-PIN peers.</summary>
public sealed class SharedConnection
{
    public string Protocol { get; set; } = "RDP";
    public int Port { get; set; } = 3389;
    public string Username { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Password { get; set; } = "";
    public string CertificateFingerprint { get; set; } = "";
    public List<string> SshHostKeyFingerprints { get; set; } = [];

    public void Validate()
    {
        if (Protocol is not ("RDP" or "SSH" or "VNC") || Port < 1 || Port > 65535 ||
            Username is null || Domain is null || Password is null || CertificateFingerprint is null || SshHostKeyFingerprints is null ||
            Username.Length > 256 || Domain.Length > 256 || Password.Length > 1024 ||
            (CertificateFingerprint.Length > 0 && (Protocol != "VNC" || CertificateFingerprint.Length != 64 ||
                                                  !CertificateFingerprint.All(char.IsAsciiHexDigit))) ||
            SshHostKeyFingerprints.Count > 8 || (Protocol != "SSH" && SshHostKeyFingerprints.Count != 0) ||
            SshHostKeyFingerprints.Any(fingerprint => !ValidSshFingerprint(fingerprint)) ||
            SshHostKeyFingerprints.Distinct(StringComparer.Ordinal).Count() != SshHostKeyFingerprints.Count)
            throw new InvalidDataException("Invalid shared connection settings.");
    }

    private static bool ValidSshFingerprint(string? fingerprint)
    {
        if (fingerprint is null || !fingerprint.StartsWith("SHA256:", StringComparison.Ordinal)) return false;
        string value = fingerprint[7..];
        if (value.Length is not (43 or 44) || (value.Length == 44 && value[43] != '=') ||
            value[..43].Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('+' or '/')))
            return false;
        Span<byte> decoded = stackalloc byte[32];
        return Convert.TryFromBase64String(value.Length == 43 ? value + "=" : value, decoded, out int count) && count == 32 &&
               Convert.ToBase64String(decoded).TrimEnd('=') == value.TrimEnd('=');
    }
}

public sealed class RemotePeer
{
    public Guid InstanceId { get; set; }
    public string DisplayName { get; set; } = "";
    public SharedConnection? SharedConnection { get; set; }
    public List<SharedConnection> SharedConnections { get; set; } = [];
}

/// <summary>Maintains a legacy primary endpoint alongside up to three distinct protocols.</summary>
public static class SharedConnectionSet
{
    public const int MaximumCount = 3;
    // Leave room for the peer identity, escaped display name and encryption overhead
    // within the existing 8192-byte peer-protocol frame understood by older installs.
    public const int MaximumEncodedBytes = 7168;

    public static void Validate(SharedConnection? primary, IReadOnlyList<SharedConnection>? connections)
    {
        if (connections is null || connections.Count > MaximumCount)
            throw new InvalidDataException("Invalid shared endpoint list.");
        primary?.Validate();
        var protocols = new HashSet<string>(StringComparer.Ordinal);
        foreach (SharedConnection? connection in connections)
        {
            if (connection is null) throw new InvalidDataException("Invalid shared endpoint list.");
            connection.Validate();
            if (!protocols.Add(connection.Protocol)) throw new InvalidDataException("Each shared protocol must occur only once.");
        }
        if (connections.Count > 0 && primary is not null && !connections.Any(connection => Equal(connection, primary)))
            throw new InvalidDataException("The primary shared endpoint must match its protocol in the endpoint list.");

        foreach (SharedConnection? encodedPrimary in new[] { primary, LegacyPrimary(connections) })
        {
            byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(new { SharedConnection = encodedPrimary, SharedConnections = connections });
            try
            {
                if (encoded.Length > MaximumEncodedBytes) throw new InvalidDataException("The shared connection details are too large.");
            }
            finally { CryptographicOperations.ZeroMemory(encoded); }
        }
    }

    public static IReadOnlyList<SharedConnection> GetConnections(SharedConnection? primary, IReadOnlyList<SharedConnection>? connections)
    {
        Validate(primary, connections);
        return connections!.Count > 0 ? connections.ToArray() : primary is null ? [] : [primary];
    }

    public static SharedConnection? GetLegacyPrimary(SharedConnection? primary, IReadOnlyList<SharedConnection>? connections)
        => LegacyPrimary(GetConnections(primary, connections));

    private static SharedConnection? LegacyPrimary(IReadOnlyList<SharedConnection> connections) =>
        connections.FirstOrDefault(connection => connection.Protocol == "RDP") ??
        connections.FirstOrDefault(connection => connection.Protocol == "SSH") ??
        connections.FirstOrDefault(connection => connection.Protocol == "VNC" && connection.CertificateFingerprint.Length == 0);

    public static void Upsert(Profile profile, SharedConnection connection)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(connection);
        connection.Validate();
        List<SharedConnection> updated = GetConnections(profile.SharedConnection, profile.SharedConnections).ToList();
        int index = updated.FindIndex(item => item.Protocol == connection.Protocol);
        if (index < 0) updated.Add(connection);
        else updated[index] = connection;
        Validate(connection, updated);
        profile.SharedConnection = connection;
        profile.SharedConnections = updated;
    }

    public static void Remove(Profile profile, string protocol)
    {
        ArgumentNullException.ThrowIfNull(profile);
        List<SharedConnection> updated = GetConnections(profile.SharedConnection, profile.SharedConnections)
            .Where(connection => connection.Protocol != protocol).ToList();
        SharedConnection? primary = profile.SharedConnection?.Protocol == protocol ? updated.FirstOrDefault() : profile.SharedConnection;
        Validate(primary, updated);
        profile.SharedConnection = primary;
        profile.SharedConnections = updated;
    }

    public static void Clear(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.SharedConnection = null;
        profile.SharedConnections = [];
    }

    private static bool Equal(SharedConnection left, SharedConnection right) =>
        left.Protocol == right.Protocol && left.Port == right.Port && left.Username == right.Username &&
        left.Domain == right.Domain && left.Password == right.Password &&
        left.CertificateFingerprint == right.CertificateFingerprint &&
        left.SshHostKeyFingerprints.SequenceEqual(right.SshHostKeyFingerprints, StringComparer.Ordinal);
}
