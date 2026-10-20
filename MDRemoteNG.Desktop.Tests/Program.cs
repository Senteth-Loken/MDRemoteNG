using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using MDRemoteNG.Desktop;

int passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
    passed++;
}

var configured = VncSecurity.Create();
using var certificate = VncSecurity.LoadCertificate(configured.Settings, configured.Fingerprint);
Check(certificate.HasPrivateKey && configured.Fingerprint.Length == 64 && configured.Password.Length == 64,
    "VNC setup creates an ephemeral certificate and an independent 256-bit sharing token.");
Check(VncSecurity.Matches(certificate, configured.Fingerprint.ToLowerInvariant()) &&
      !VncSecurity.Matches(certificate, new string('0', 64)) && !VncSecurity.Matches(certificate, "invalid"),
    "Certificate pins match the exact SHA256 digest and reject other certificates or malformed pins.");
bool invalidToken = false;
try { VncSecurity.ValidatePassword("12345678"); }
catch (InvalidDataException) { invalidToken = true; }
Check(invalidToken, "A legacy eight-character VNC password cannot replace the full sharing token.");
bool mismatchPfx = false;
try { using var rejected = VncSecurity.LoadCertificate(configured.Settings, new string('0', 64)); }
catch (InvalidDataException) { mismatchPfx = true; }
Check(mismatchPfx, "Private server material must match the advertised certificate pin.");

async Task<(Exception? Client, Exception? Server)> ExchangeSecurity(string fingerprint, string password, bool plaintext = false)
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    Task<Exception?> server = Task.Run(async () =>
    {
        try
        {
            using TcpClient accepted = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = new SslStream(accepted.GetStream(), false);
            await VncSecurity.AuthenticateServerAsync(stream, certificate, configured.Password, timeout.Token);
            return null;
        }
        catch (Exception exception) when (exception is IOException or AuthenticationException or OperationCanceledException)
        { return exception; }
    });
    Exception? clientError = null;
    using (var client = new TcpClient())
    {
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
        try
        {
            if (plaintext)
            {
                await client.GetStream().WriteAsync("RFB 003.008\n"u8.ToArray(), timeout.Token);
                byte[] response = new byte[1];
                int count = await client.GetStream().ReadAsync(response, timeout.Token);
                if (count == 0) clientError = new EndOfStreamException();
                else clientError = new AuthenticationException("The endpoint did not accept plaintext RFB.");
            }
            else
            {
                using var stream = new SslStream(client.GetStream(), false);
                await VncSecurity.AuthenticateClientAsync(stream, "127.0.0.1", fingerprint, password, timeout.Token);
            }
        }
        catch (Exception exception) when (exception is IOException or AuthenticationException or OperationCanceledException)
        { clientError = exception; }
    }
    return (clientError, await server);
}

var acceptedSecurity = await ExchangeSecurity(configured.Fingerprint, configured.Password);
if (acceptedSecurity.Client != null || acceptedSecurity.Server != null)
    throw new Exception("Valid loopback TLS failed. Client: " + acceptedSecurity.Client + "; server: " + acceptedSecurity.Server);
Check(acceptedSecurity.Client == null && acceptedSecurity.Server == null, "A pinned TLS connection accepts the full matching sharing token.");
var rejectedPin = await ExchangeSecurity(new string('0', 64), configured.Password);
Check(rejectedPin.Client != null && rejectedPin.Server != null, "An incorrect host certificate pin prevents credential authentication.");
string wrongPassword = (configured.Password[0] == 'A' ? "B" : "A") + configured.Password[1..];
var rejectedToken = await ExchangeSecurity(configured.Fingerprint, wrongPassword);
Check(rejectedToken.Client != null && rejectedToken.Server is AuthenticationException, "A correct certificate with an incorrect sharing token is rejected.");
var rejectedPlaintext = await ExchangeSecurity(configured.Fingerprint, configured.Password, plaintext: true);
Check(rejectedPlaintext.Client != null && rejectedPlaintext.Server != null, "The VNC endpoint rejects an unencrypted RFB handshake.");

// Inspect native layouts without constructing a desktop session or calling user32.
Type desktopType = typeof(VncSecurity).Assembly.GetType("MDRemoteNG.Desktop.WindowsDesktopSession", throwOnError: true)!;
Type inputType = desktopType.GetNestedType("Input", BindingFlags.NonPublic)!;
Type mouseType = desktopType.GetNestedType("MouseInput", BindingFlags.NonPublic)!;
Type keyboardType = desktopType.GetNestedType("KeyboardInput", BindingFlags.NonPublic)!;
Check(Marshal.SizeOf(inputType) == (IntPtr.Size == 8 ? 40 : 28) &&
      Marshal.OffsetOf(inputType, "Data").ToInt32() == (IntPtr.Size == 8 ? 8 : 4),
    "The native INPUT union has Windows-compatible size and alignment on this architecture.");
Check(Marshal.OffsetOf(mouseType, "Extra").ToInt32() == (IntPtr.Size == 8 ? 24 : 20) &&
      Marshal.OffsetOf(keyboardType, "Extra").ToInt32() == (IntPtr.Size == 8 ? 16 : 12),
    "Mouse and keyboard native pointer fields retain their correct alignment.");

passed += await VncHostChecks.RunAsync(configured.Settings, configured.Fingerprint, configured.Password);
Console.WriteLine($"All {passed} synthetic desktop and loopback TLS checks passed. No real desktop was captured and no Windows input was sent.");
