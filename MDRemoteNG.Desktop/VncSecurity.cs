using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MDRemoteNG.Core;

namespace MDRemoteNG.Desktop;

public static class VncSecurity
{
    private static readonly byte[] Greeting = "MDRemoteNG VNC 1\n"u8.ToArray();
    private static readonly byte[] Accepted = "OK\n"u8.ToArray();
    public const int TokenLength = 64;

    public static (VncHostSettings Settings, string Fingerprint, string Password) Create()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=MDRemoteNG VNC", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2));
        return (new VncHostSettings { CertificatePfx = certificate.Export(X509ContentType.Pkcs12) },
            Fingerprint(certificate), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    }

    public static X509Certificate2 LoadCertificate(VncHostSettings settings, string fingerprint)
    {
        settings.Validate();
        // Schannel cannot use EphemeralKeySet server keys on supported Windows versions.
        // UserKeySet without PersistKeySet creates a temporary user key removed on Dispose.
        var certificate = X509CertificateLoader.LoadPkcs12(settings.CertificatePfx, null, X509KeyStorageFlags.UserKeySet);
        if (!certificate.HasPrivateKey || !Matches(certificate, fingerprint))
        {
            certificate.Dispose();
            throw new InvalidDataException("The protected VNC certificate does not match its shared endpoint.");
        }
        return certificate;
    }

    public static string Fingerprint(X509Certificate certificate) => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    public static bool Matches(X509Certificate? certificate, string expected)
    {
        if (certificate == null || expected == null || expected.Length != 64 || !expected.All(Uri.IsHexDigit)) return false;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()), Convert.FromHexString(expected));
    }

    public static async Task AuthenticateClientAsync(SslStream stream, string host, string fingerprint, string password, CancellationToken token)
    {
        ValidatePassword(password);
        await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            RemoteCertificateValidationCallback = (_, certificate, _, _) => Matches(certificate, fingerprint),
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }, token).ConfigureAwait(false);
        await stream.WriteAsync(Greeting, token).ConfigureAwait(false);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(password), token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
        byte[] response = new byte[Accepted.Length];
        await stream.ReadExactlyAsync(response, token).ConfigureAwait(false);
        if (!response.AsSpan().SequenceEqual(Accepted)) throw new AuthenticationException("The VNC host did not accept this sharing credential.");
    }

    public static async Task AuthenticateServerAsync(SslStream stream, X509Certificate2 certificate, string password, CancellationToken token)
    {
        ValidatePassword(password);
        await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = certificate,
            ClientCertificateRequired = false,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }, token).ConfigureAwait(false);
        byte[] request = new byte[Greeting.Length + TokenLength];
        try
        {
            await stream.ReadExactlyAsync(request, token).ConfigureAwait(false);
            if (!request.AsSpan(0, Greeting.Length).SequenceEqual(Greeting) ||
                !CryptographicOperations.FixedTimeEquals(request.AsSpan(Greeting.Length), Encoding.ASCII.GetBytes(password)))
                throw new AuthenticationException("Invalid VNC sharing credential.");
        }
        finally { CryptographicOperations.ZeroMemory(request); }
        await stream.WriteAsync(Accepted, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public static void ValidatePassword(string password)
    {
        if (password == null || password.Length != TokenLength || !password.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid protected VNC sharing credential. Run VNC auto-configuration again.");
    }
}
