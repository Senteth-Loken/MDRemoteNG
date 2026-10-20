using System.Buffers.Binary;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Agreement.Srp;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;

namespace MDRemoteNG.Core;

/// <summary>
/// SRP-6a mutual PIN authentication using Bouncy Castle's RFC 5054 3072-bit group.
/// No reusable PIN proof or PIN-derived encryption key is placed on the network.
/// Fresh ephemeral SRP values protect recorded sessions from offline PIN guessing.
/// Only after BOTH evidence messages succeed are local-host credentials exchanged.
/// </summary>
public static class PeerHandshake
{
    private const int MaxFrameSize = 8192;
    private static readonly byte[] Identity = Encoding.UTF8.GetBytes("MDRemoteNG peer protocol v1");
    private static readonly byte[] ServerContext = Encoding.UTF8.GetBytes("MDRemoteNG/v1/server-credentials");
    private static readonly byte[] ClientContext = Encoding.UTF8.GetBytes("MDRemoteNG/v1/client-credentials");

    private sealed class Challenge
    {
        public int Version { get; set; } = 1;
        public byte[] Salt { get; set; } = [];
        public byte[] Public { get; set; } = [];
    }

    private sealed class Evidence
    {
        public byte[] Public { get; set; } = [];
        public byte[] Proof { get; set; } = [];
    }

    public static async Task<RemotePeer> ExchangeAsync(Stream stream, Profile profile, bool isServer,
        CancellationToken cancellationToken = default, Func<bool>? profileIsCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(profile);
        profile.SharedConnection?.Validate();
        byte[] password = Encoding.UTF8.GetBytes(PinPolicy.Normalize(profile.NormalizedPin));
        byte[]? key = null;
        try
        {
            key = isServer
                ? await AuthenticateServerAsync(stream, password, cancellationToken).ConfigureAwait(false)
                : await AuthenticateClientAsync(stream, password, cancellationToken).ConfigureAwait(false);
            var local = new RemotePeer
            {
                InstanceId = profile.InstanceId,
                DisplayName = profile.DisplayName,
                SharedConnection = profile.SharedConnection
            };
            RemotePeer remote;
            if (isServer)
            {
                EnsureCurrentProfile(profileIsCurrent, cancellationToken);
                await WriteEncryptedAsync(stream, key, ServerContext, local, cancellationToken).ConfigureAwait(false);
                remote = await ReadEncryptedAsync(stream, key, ClientContext, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                remote = await ReadEncryptedAsync(stream, key, ServerContext, cancellationToken).ConfigureAwait(false);
                EnsureCurrentProfile(profileIsCurrent, cancellationToken);
                await WriteEncryptedAsync(stream, key, ClientContext, local, cancellationToken).ConfigureAwait(false);
            }
            if (remote.InstanceId == Guid.Empty || remote.InstanceId == profile.InstanceId || remote.DisplayName is null || remote.DisplayName.Length > 128)
                throw new AuthenticationException("Invalid peer identity.");
            remote.SharedConnection?.Validate();
            return remote;
        }
        catch (Org.BouncyCastle.Crypto.CryptoException ex)
        {
            throw new AuthenticationException("Peer PIN authentication failed.", ex);
        }
        catch (JsonException ex) { throw new AuthenticationException("Invalid peer message.", ex); }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
            if (key != null) CryptographicOperations.ZeroMemory(key);
        }
    }

    private static void EnsureCurrentProfile(Func<bool>? profileIsCurrent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (profileIsCurrent != null && !profileIsCurrent())
            throw new AuthenticationException("Local discovery settings changed during authentication.");
    }

    private static async Task<byte[]> AuthenticateServerAsync(Stream stream, byte[] password, CancellationToken ct)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(32);
        var generator = new Srp6VerifierGenerator();
        generator.Init(Srp6StandardGroups.rfc5054_3072, new Sha256Digest());
        var verifier = generator.GenerateVerifier(salt, Identity, password);
        var server = new Srp6Server();
        server.Init(Srp6StandardGroups.rfc5054_3072, verifier, new Sha256Digest(), new SecureRandom());
        var publicValue = server.GenerateServerCredentials();
        await WriteJsonAsync(stream, new Challenge { Salt = salt, Public = publicValue.ToByteArrayUnsigned() }, ct).ConfigureAwait(false);
        var evidence = await ReadJsonAsync<Evidence>(stream, ct).ConfigureAwait(false);
        server.CalculateSecret(ParsePublic(evidence.Public));
        if (!server.VerifyClientEvidenceMessage(ParseProof(evidence.Proof)))
            throw new AuthenticationException("Peer PIN authentication failed.");
        await WriteFrameAsync(stream, server.CalculateServerEvidenceMessage().ToByteArrayUnsigned(), ct).ConfigureAwait(false);
        return SessionKey(server.CalculateSessionKey());
    }

    private static async Task<byte[]> AuthenticateClientAsync(Stream stream, byte[] password, CancellationToken ct)
    {
        var challenge = await ReadJsonAsync<Challenge>(stream, ct).ConfigureAwait(false);
        if (challenge.Version != 1 || challenge.Salt is not { Length: 32 })
            throw new AuthenticationException("Unsupported peer protocol.");
        var client = new Srp6Client();
        client.Init(Srp6StandardGroups.rfc5054_3072, new Sha256Digest(), new SecureRandom());
        var publicValue = client.GenerateClientCredentials(challenge.Salt, Identity, password);
        client.CalculateSecret(ParsePublic(challenge.Public));
        await WriteJsonAsync(stream, new Evidence
        {
            Public = publicValue.ToByteArrayUnsigned(),
            Proof = client.CalculateClientEvidenceMessage().ToByteArrayUnsigned()
        }, ct).ConfigureAwait(false);
        var proof = await ReadFrameAsync(stream, ct).ConfigureAwait(false);
        if (!client.VerifyServerEvidenceMessage(ParseProof(proof)))
            throw new AuthenticationException("Peer PIN authentication failed.");
        return SessionKey(client.CalculateSessionKey());
    }

    private static BigInteger ParsePublic(byte[] bytes)
    {
        if (bytes is null || bytes.Length is < 1 or > 384) throw new AuthenticationException("Invalid SRP public value.");
        var value = new BigInteger(1, bytes);
        if (value.SignValue <= 0 || value.CompareTo(Srp6StandardGroups.rfc5054_3072.N) >= 0)
            throw new AuthenticationException("Invalid SRP public value.");
        return value;
    }

    private static BigInteger ParseProof(byte[] bytes)
    {
        if (bytes is null || bytes.Length is < 1 or > 32) throw new AuthenticationException("Invalid SRP proof.");
        return new BigInteger(1, bytes);
    }

    private static byte[] SessionKey(BigInteger key)
    {
        byte[] bytes = key.ToByteArrayUnsigned();
        try { return HKDF.DeriveKey(HashAlgorithmName.SHA256, bytes, 32, info: Identity); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static async Task WriteEncryptedAsync(Stream stream, byte[] sessionKey, byte[] context, RemotePeer peer, CancellationToken ct)
    {
        byte[] key = HKDF.DeriveKey(HashAlgorithmName.SHA256, sessionKey, 32, info: context);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(peer);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] frame = new byte[12 + 16 + plain.Length];
        nonce.CopyTo(frame, 0);
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plain, frame.AsSpan(28), frame.AsSpan(12, 16), context);
            await WriteFrameAsync(stream, frame, ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static async Task<RemotePeer> ReadEncryptedAsync(Stream stream, byte[] sessionKey, byte[] context, CancellationToken ct)
    {
        byte[] frame = await ReadFrameAsync(stream, ct).ConfigureAwait(false);
        if (frame.Length < 28) throw new AuthenticationException("Invalid encrypted peer message.");
        byte[] plain = new byte[frame.Length - 28];
        byte[] key = HKDF.DeriveKey(HashAlgorithmName.SHA256, sessionKey, 32, info: context);
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(frame.AsSpan(0, 12), frame.AsSpan(28), frame.AsSpan(12, 16), plain, context);
            return JsonSerializer.Deserialize<RemotePeer>(plain) ?? throw new AuthenticationException("Missing peer data.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static Task WriteJsonAsync<T>(Stream stream, T value, CancellationToken ct) =>
        WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(value), ct);

    private static async Task<T> ReadJsonAsync<T>(Stream stream, CancellationToken ct) =>
        JsonSerializer.Deserialize<T>(await ReadFrameAsync(stream, ct).ConfigureAwait(false))
        ?? throw new AuthenticationException("Missing peer message.");

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken ct)
    {
        if (payload.Length is < 1 or > MaxFrameSize) throw new AuthenticationException("Peer message is too large.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 1 or > MaxFrameSize) throw new AuthenticationException("Invalid peer message length.");
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return payload;
    }
}
