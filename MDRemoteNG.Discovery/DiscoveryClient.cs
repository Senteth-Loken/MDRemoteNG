using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MDRemoteNG.Core;

namespace MDRemoteNG.Discovery;

public sealed class DiscoveredPeer
{
    public Guid InstanceId { get; set; }
    public string DisplayName { get; set; } = "";
    // This address is the authenticated socket's LAN source, never an advertised redirect.
    public string Address { get; set; } = "";
    public SharedConnection? SharedConnection { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
}

public static class DiscoveryClient
{
    internal const int MaximumMessageSize = 2 * 1024 * 1024;
    internal static string UserKey { get; } = GetUserKey();
    internal static string PipeName => "MDRemoteNG.Discovery." + UserKey;

    private static string GetUserKey()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            identity.User?.Value ?? throw new InvalidOperationException("No Windows user identity."))))[..24];
    }

    public static void SetAutoStart(string helperExePath, bool enabled) => SetStartAtLogon(helperExePath, enabled);

    public static void SetStartAtLogon(string helperExePath, bool enabled)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (enabled)
        {
            string path = Path.GetFullPath(helperExePath);
            if (!File.Exists(path)) throw new FileNotFoundException("The MDRemoteNG discovery helper is missing.", path);
            key.SetValue("MDRemoteNGDiscovery", "\"" + path + "\" --helper");
        }
        else key.DeleteValue("MDRemoteNGDiscovery", throwOnMissingValue: false);
    }

    public static void EnsureRunning(string helperExePath)
    {
        using (var pipe = CreatePipe())
        {
            try { pipe.Connect(100); return; }
            catch (TimeoutException) { }
            catch (IOException) { }
        }
        string path = Path.GetFullPath(helperExePath);
        if (!File.Exists(path)) throw new FileNotFoundException("The MDRemoteNG discovery helper is missing.", path);
        Process.Start(new ProcessStartInfo(path, "--helper")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(path)!
        })?.Dispose();
    }

    public static async Task<IReadOnlyList<DiscoveredPeer>> GetPeersAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var pipe = CreatePipe();
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        await Framing.WriteAsync(pipe, "peers"u8.ToArray(), timeout.Token).ConfigureAwait(false);
        byte[] response = await Framing.ReadAsync(pipe, MaximumMessageSize, timeout.Token).ConfigureAwait(false);
        try { return JsonSerializer.Deserialize<List<DiscoveredPeer>>(response) ?? []; }
        finally { CryptographicOperations.ZeroMemory(response); }
    }

    private static NamedPipeClientStream CreatePipe() => new(".", PipeName, PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
}

internal static class Framing
{
    public static async Task<byte[]> ReadAsync(Stream stream, int maximumSize, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 1 || length > maximumSize) throw new InvalidDataException("Invalid message size.");
        byte[] data = new byte[length];
        await stream.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
        return data;
    }

    public static async Task WriteAsync(Stream stream, byte[] data, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
