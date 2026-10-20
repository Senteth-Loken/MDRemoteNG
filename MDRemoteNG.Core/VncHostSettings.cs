namespace MDRemoteNG.Core;

/// <summary>Private VNC server material. Stored only inside the DPAPI-protected profile.</summary>
public sealed class VncHostSettings
{
    public byte[] CertificatePfx { get; set; } = [];

    public void Validate()
    {
        if (CertificatePfx is null || CertificatePfx.Length is < 100 or > 16384)
            throw new InvalidDataException("Invalid protected VNC certificate.");
    }
}
