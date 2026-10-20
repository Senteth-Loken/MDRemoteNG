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
}

/// <summary>The one local-host account explicitly enrolled for matching-PIN peers.</summary>
public sealed class SharedConnection
{
    public string Protocol { get; set; } = "RDP";
    public int Port { get; set; } = 3389;
    public string Username { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Password { get; set; } = "";

    public void Validate()
    {
        if (Protocol is not ("RDP" or "SSH" or "VNC") || Port < 1 || Port > 65535 ||
            Username is null || Domain is null || Password is null ||
            Username.Length > 256 || Domain.Length > 256 || Password.Length > 1024)
            throw new InvalidDataException("Invalid shared connection settings.");
    }
}

public sealed class RemotePeer
{
    public Guid InstanceId { get; set; }
    public string DisplayName { get; set; } = "";
    public SharedConnection? SharedConnection { get; set; }
}
