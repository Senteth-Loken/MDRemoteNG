using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using MDRemoteNG.Core;

int passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
    passed++;
}

Check(PinPolicy.Normalize("aB1!") == "AB1!", "PINs ignore letter case and retain symbols");
Check(PinPolicy.Validate("!@#$%^&*") && PinPolicy.Validate("abcd"), "4–8 letters, digits and symbols accepted");
Check(!PinPolicy.Validate("abc") && !PinPolicy.Validate("123456789") && !PinPolicy.Validate("abc ") &&
      !PinPolicy.Validate("ab\nc") && !PinPolicy.Validate("Äbcd"), "Invalid length, whitespace and non-ASCII characters rejected");

string testDirectory = Path.Combine(Directory.GetCurrentDirectory(), ".build", "core-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testDirectory);
var store = new ProfileStore(Path.Combine(testDirectory, "profile.dat"));
var profile = store.Create("Ab1!");
profile.SharedConnection = new SharedConnection { Username = "test-user", Password = "unique-test-password" };
store.Save(profile);
Check(store.Unlock("aB1!").InstanceId == profile.InstanceId, "Persisted profile unlocks after case-folded PIN entry");
var staleProfile = store.Load();
string storedText = Encoding.UTF8.GetString(File.ReadAllBytes(store.Path));
Check(!storedText.Contains("AB1!") && !storedText.Contains("unique-test-password"), "PIN and account password are protected at rest");
try { store.Unlock("bad!"); throw new Exception("Wrong PIN accepted"); }
catch (UnauthorizedAccessException) { }
Check(store.Load().FailedAttempts == 1, "Wrong PIN attempt is persisted");
store.UpdateProfile(staleProfile);
store.Save(staleProfile);
Check(store.Load().FailedAttempts == 1 && store.Load().LockedUntil > DateTimeOffset.UtcNow,
    "Saving stale settings cannot reset PIN lockout state");
try { new ProfileStore(store.Path).Unlock("Ab1!"); throw new Exception("Lockout was lost"); }
catch (PinLockedException) { Check(true, "PIN lockout survives reopening the profile store"); }
var delay = store.Load().LockedUntil - DateTimeOffset.UtcNow;
if (delay > TimeSpan.Zero) await Task.Delay(delay + TimeSpan.FromMilliseconds(50));
var changed = store.ChangePin("AB1!", "new#");
Check(store.Unlock("NEW#").InstanceId == profile.InstanceId, "PIN change preserves identity and accepts case-insensitive replacement");
store.UpdateProfile(staleProfile);
Check(store.Unlock("NEW#").NormalizedPin == "NEW#", "Saving stale settings cannot restore an old PIN");
changed.DisplayName = "Updated peer";
changed = store.UpdateProfile(changed, "new#", "safe$123");
Check(store.Unlock("SAFE$123").DisplayName == "Updated peer", "Settings and PIN change are committed together");
byte[] protectedData = File.ReadAllBytes(store.Path);
protectedData[protectedData.Length / 2] ^= 1;
File.WriteAllBytes(store.Path, protectedData);
try { store.Load(); throw new Exception("Corrupt profile accepted"); }
catch (CryptographicException) { Check(true, "Corrupt protected profile fails closed"); }

Profile Peer(string pin) => new()
{
    NormalizedPin = PinPolicy.Normalize(pin),
    DisplayName = "peer-" + Guid.NewGuid().ToString("N"),
    SharedConnection = new SharedConnection { Username = "test-user", Password = "unique-test-password" }
};

async Task<(RemotePeer? Client, RemotePeer? Server, Exception? Error, byte[] ServerBytes, byte[] ClientBytes)> Exchange(
    string clientPin, string serverPin, bool tamper = false, bool revoke = false)
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    using var client = new TcpClient();
    var connect = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ct.Token);
    using var server = await listener.AcceptTcpClientAsync(ct.Token);
    await connect;
    listener.Stop();
    using var serverStream = new RecordingStream(server.GetStream(), tamper);
    using var clientStream = new RecordingStream(client.GetStream());
    var serverTask = PeerHandshake.ExchangeAsync(serverStream, Peer(serverPin), true, ct.Token, () => !revoke);
    var clientTask = PeerHandshake.ExchangeAsync(clientStream, Peer(clientPin), false, ct.Token);
    // Failure must close the transport, as the real helper does, so the peer cannot wait forever.
    var completed = await Task.WhenAny(clientTask, serverTask);
    if (completed.IsFaulted) { client.Close(); server.Close(); }
    Exception? error = null;
    try { await Task.WhenAll(clientTask, serverTask); }
    catch (Exception ex) { error = ex; }
    return (clientTask.IsCompletedSuccessfully ? clientTask.Result : null,
        serverTask.IsCompletedSuccessfully ? serverTask.Result : null, error,
        serverStream.Recorded.ToArray(), clientStream.Recorded.ToArray());
}

var match = await Exchange("pin#", "PIN#");
Check(match.Error == null && match.Client?.SharedConnection?.Password == "unique-test-password" &&
      match.Server?.SharedConnection?.Password == "unique-test-password", "Matching PINs mutually authenticate and exchange encrypted credentials");
string wire = Encoding.UTF8.GetString(match.ServerBytes) + Encoding.UTF8.GetString(match.ClientBytes);
Check(!wire.Contains("unique-test-password") && !wire.Contains("test-user") && !wire.Contains("PIN#"),
    "Neither PINs nor credentials appear in captured network traffic");
var mismatch = await Exchange("bad#", "PIN#");
Check(mismatch.Error != null && mismatch.Client == null && mismatch.Server == null,
    "Mismatched PINs fail before either peer releases credentials");
var tampered = await Exchange("PIN#", "pin#", true);
Check(tampered.Error != null && tampered.Client == null, "Modified encrypted credentials are rejected");
var revoked = await Exchange("PIN#", "pin#", revoke: true);
Check(revoked.Error != null && revoked.Client == null && revoked.Server == null,
    "A settings change during authentication cancels credential disclosure");

try { new SharedConnection { Username = null! }.Validate(); throw new Exception("Null username accepted"); }
catch (InvalidDataException) { Check(true, "Null credential fields are rejected as invalid data"); }

byte[] malformed = Encoding.UTF8.GetBytes("{\"Version\":1,\"Salt\":\"" + Convert.ToBase64String(new byte[32]) + "\",\"Public\":null}");
byte[] malformedFrame = new byte[malformed.Length + 4];
System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(malformedFrame, malformed.Length);
malformed.CopyTo(malformedFrame, 4);
using (var malformedStream = new ReplayStream(malformedFrame))
{
    try { await PeerHandshake.ExchangeAsync(malformedStream, Peer("PIN#"), false); throw new Exception("Null public key accepted"); }
    catch (AuthenticationException) { Check(true, "Unauthenticated null protocol fields fail authentication"); }
}

// Replaying an old salt/B/proof/ciphertext transcript cannot authenticate a fresh client ephemeral key.
using (var replay = new ReplayStream(match.ServerBytes))
{
    try
    {
        await PeerHandshake.ExchangeAsync(replay, Peer("PIN#"), false);
        throw new Exception("Recorded server transcript authenticated");
    }
    catch (AuthenticationException) { Check(true, "Replayed handshake transcript fails authentication"); }
}
Console.WriteLine($"All {passed} core checks passed.");

sealed class RecordingStream(Stream inner, bool tamperThirdFrame = false) : Stream
{
    public MemoryStream Recorded { get; } = new();
    private int writes;
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        byte[] bytes = buffer.ToArray();
        writes++;
        // Frame 3 is the first encrypted payload; each frame uses a header and payload write.
        if (tamperThirdFrame && writes == 6) bytes[^1] ^= 1;
        Recorded.Write(bytes);
        await inner.WriteAsync(bytes, ct);
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

sealed class ReplayStream(byte[] transcript) : Stream
{
    private readonly MemoryStream input = new(transcript);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => input.ReadAsync(buffer, ct);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => ValueTask.CompletedTask;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => input.Length;
    public override long Position { get => input.Position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
    public override void Write(byte[] buffer, int offset, int count) { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
