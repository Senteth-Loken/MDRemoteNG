using System.Net;
using MDRemoteNG.Core;
using MDRemoteNG.Discovery;

if (args.Contains("--network-diagnostic", StringComparer.Ordinal))
{
    var report = NetworkScope.InspectNetworkCategories();
    var subnets = NetworkScope.GetAllowedSubnets();
    Console.WriteLine($"Network category enumeration: succeeded={report.Succeeded}; connected={report.ConnectedNetworks}; private={report.PrivateNetworks}; domain={report.DomainNetworks}; allowed subnets={subnets.Count}");
    return;
}

int passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
    passed++;
}

Check(NetworkScope.IsPrivateAddress(IPAddress.Parse("10.1.2.3")) &&
      NetworkScope.IsPrivateAddress(IPAddress.Parse("172.16.1.2")) &&
      NetworkScope.IsPrivateAddress(IPAddress.Parse("192.168.1.2")), "Private IPv4 address ranges accepted");
Check(!NetworkScope.IsPrivateAddress(IPAddress.Parse("8.8.8.8")) &&
      !NetworkScope.IsPrivateAddress(IPAddress.Parse("172.32.1.2")) &&
      !NetworkScope.IsPrivateAddress(IPAddress.Loopback) &&
      !NetworkScope.IsPrivateAddress(IPAddress.IPv6Loopback), "Public and loopback addresses excluded");
var subnet = new LocalSubnet(IPAddress.Parse("192.168.12.10"), IPAddress.Parse("255.255.255.0"), 1);
Check(subnet.Contains(IPAddress.Parse("192.168.12.11")) &&
      !subnet.Contains(IPAddress.Parse("192.168.13.11")) &&
      !subnet.Contains(IPAddress.Parse("8.8.8.8")), "Discovery source must belong to the approved local subnet");
Check(subnet.Broadcast.Equals(IPAddress.Parse("192.168.12.255")), "Directed broadcast stays within each approved subnet");
IReadOnlyList<LocalSubnet> allowed = NetworkScope.GetAllowedSubnets();
Check(allowed.All(s => NetworkScope.IsPrivateAddress(s.Address)), "Windows network-category enumeration fails closed");

using (var stream = new MemoryStream())
{
    await Framing.WriteAsync(stream, "hello"u8.ToArray(), CancellationToken.None);
    stream.Position = 0;
    Check((await Framing.ReadAsync(stream, 5, CancellationToken.None)).AsSpan().SequenceEqual("hello"u8),
        "Named-pipe framing round trip preserves payload");
    stream.Position = 0;
    try { await Framing.ReadAsync(stream, 4, CancellationToken.None); throw new Exception("Oversized frame accepted"); }
    catch (InvalidDataException) { Check(true, "Oversized named-pipe frame rejected before allocation"); }
}
using (var truncated = new MemoryStream([0, 0, 0, 4, 1]))
{
    try { await Framing.ReadAsync(truncated, 16, CancellationToken.None); throw new Exception("Truncated frame accepted"); }
    catch (EndOfStreamException) { Check(true, "Truncated named-pipe frames fail closed"); }
}

string testDirectory = Path.Combine(Directory.GetCurrentDirectory(), ".build", "discovery-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testDirectory);
string limiterPath = Path.Combine(testDirectory, "limits.json");
var limiter = new AttemptLimiter(limiterPath);
Check(limiter.TryBegin("192.168.12.11"), "First source authentication attempt admitted");
limiter.Complete("192.168.12.11", false);
Check(!new AttemptLimiter(limiterPath).TryBegin("192.168.12.11"), "Failed-source backoff survives helper restart");
int admitted = 0;
for (int i = 20; i < 60; i++) if (limiter.TryBegin("192.168.12." + i)) admitted++;
Check(admitted is > 0 and <= 29, "Persistent global budget limits distributed authentication attempts");
Check(!new AttemptLimiter(limiterPath).TryBegin("192.168.12.99"), "Global authentication budget survives helper restart");

var profile = new Profile { NormalizedPin = "AB1!", SharedConnection = new SharedConnection { Password = "test" } };
string fingerprint = PeerDiscoveryHost.Fingerprint(profile);
profile.FailedAttempts = 2;
profile.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(1);
Check(PeerDiscoveryHost.Fingerprint(profile) == fingerprint, "Startup lockout changes do not restart discovery sessions");
profile.SharedConnection.Password = "changed";
Check(PeerDiscoveryHost.Fingerprint(profile) != fingerprint, "Credential changes revoke the old discovery profile");
profile.SharedConnection.Password = "test";
profile.NormalizedPin = "NEW!";
Check(PeerDiscoveryHost.Fingerprint(profile) != fingerprint, "PIN changes revoke the old discovery profile");
profile.NormalizedPin = "AB1!";
profile.DiscoveryEnabled = false;
Check(PeerDiscoveryHost.Fingerprint(profile) != fingerprint, "Disabling discovery invalidates cached credentials");

Console.WriteLine($"All {passed} discovery checks passed.");
