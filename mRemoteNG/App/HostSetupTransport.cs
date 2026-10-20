#nullable enable
using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MDRemoteNG.Host;

namespace mRemoteNG.App
{
    internal sealed class HostSetupReply
    {
        public int Version { get; set; } = 1;
        public HostConfigurationResult? Result { get; set; }
        public string? Error { get; set; }
    }

    internal sealed class HostSetupRequest
    {
        public int Version { get; set; } = 1;
        public string Mode { get; set; } = "managed";
        public string Protocol { get; set; } = "RDP";
        public int Port { get; set; }
        public bool AdministratorAccess { get; set; }
        public HostAccountCredentials? Credentials { get; set; }
    }

    internal sealed class HostSetupHello
    {
        public int Version { get; set; } = 1;
        public byte[]? PublicKey { get; set; }
    }

    internal static class HostSetupTransport
    {
        internal const int MaximumFrameSize = 32 * 1024;

        private sealed class Envelope
        {
            public int Version { get; set; } = 1;
            public byte[]? Key { get; set; }
            public byte[]? Nonce { get; set; }
            public byte[]? Tag { get; set; }
            public byte[]? Ciphertext { get; set; }
        }

        private static byte[] Context(Guid requestId, string purpose) => Encoding.UTF8.GetBytes("MDRemoteNG host setup v1/" + purpose + "/" + requestId.ToString("N"));

        internal static byte[] Protect(HostSetupReply reply, byte[] publicKey, Guid requestId)
            => ProtectPayload(reply, publicKey, requestId, "reply");

        internal static byte[] ProtectHello(byte[] setupKey, byte[] parentKey, Guid requestId)
            => ProtectPayload(new HostSetupHello { PublicKey = setupKey }, parentKey, requestId, "hello");

        internal static byte[] ReadHello(byte[] frame, RSA recipient, Guid requestId)
        {
            var hello = UnprotectPayload<HostSetupHello>(frame, recipient, requestId, "hello");
            if (hello.Version != 1 || hello.PublicKey is null) throw new InvalidDataException("Invalid host setup key.");
            using var setup = RSA.Create();
            setup.ImportSubjectPublicKeyInfo(hello.PublicKey, out int consumed);
            if (consumed != hello.PublicKey.Length || setup.KeySize < 3072 || setup.KeySize > 4096)
                throw new CryptographicException("Invalid host setup key.");
            return hello.PublicKey;
        }

        internal static byte[] ProtectRequest(HostSetupRequest request, byte[] publicKey, Guid requestId)
            => ProtectPayload(request, publicKey, requestId, "request");

        internal static HostSetupRequest ReadRequest(byte[] frame, RSA recipient, Guid requestId)
        {
            var request = UnprotectPayload<HostSetupRequest>(frame, recipient, requestId, "request");
            if (request.Version != 1 || (request.Mode != "managed" && request.Mode != "current-account") ||
                (request.Mode == "managed") != (request.Credentials is null) ||
                request.Protocol is not ("RDP" or "SSH" or "VNC") ||
                (request.AdministratorAccess && (request.Mode != "managed" || request.Protocol != "SSH")) ||
                (request.Protocol == "VNC" && (request.Mode != "managed" || request.Port is < 1 or > 65535)) ||
                (request.Protocol != "VNC" && request.Port != 0))
                throw new InvalidDataException("Invalid host setup request.");
            return request;
        }

        private static byte[] ProtectPayload<T>(T payload, byte[] publicKey, Guid requestId, string purpose)
        {
            byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(payload);
            byte[] key = RandomNumberGenerator.GetBytes(32);
            try
            {
                if (plaintext.Length > MaximumFrameSize / 2) throw new InvalidDataException("Host setup reply is too large.");
                using var recipient = RSA.Create();
                recipient.ImportSubjectPublicKeyInfo(publicKey, out int consumed);
                if (consumed != publicKey.Length || recipient.KeySize < 3072 || recipient.KeySize > 4096)
                    throw new CryptographicException("Invalid host setup recipient key.");
                var envelope = new Envelope
                {
                    Key = recipient.Encrypt(key, RSAEncryptionPadding.OaepSHA256),
                    Nonce = RandomNumberGenerator.GetBytes(12),
                    Tag = new byte[16],
                    Ciphertext = new byte[plaintext.Length]
                };
                using var aes = new AesGcm(key, 16);
                aes.Encrypt(envelope.Nonce, plaintext, envelope.Ciphertext, envelope.Tag, Context(requestId, purpose));
                byte[] frame = JsonSerializer.SerializeToUtf8Bytes(envelope);
                if (frame.Length > MaximumFrameSize) throw new InvalidDataException("Host setup reply is too large.");
                return frame;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        internal static HostSetupReply Unprotect(byte[] frame, RSA recipient, Guid requestId)
        {
            var reply = UnprotectPayload<HostSetupReply>(frame, recipient, requestId, "reply");
            if (reply.Version != 1 || (reply.Result is null) == string.IsNullOrEmpty(reply.Error) || reply.Error?.Length > 1024)
                throw new InvalidDataException("Invalid host setup reply.");
            return reply;
        }

        private static T UnprotectPayload<T>(byte[] frame, RSA recipient, Guid requestId, string purpose) where T : class
        {
            if (frame.Length == 0 || frame.Length > MaximumFrameSize) throw new InvalidDataException("Invalid host setup reply.");
            var envelope = JsonSerializer.Deserialize<Envelope>(frame) ?? throw new InvalidDataException("Invalid host setup reply.");
            if (envelope.Version != 1 || envelope.Key is null || envelope.Key.Length is < 384 or > 512 ||
                envelope.Nonce is not { Length: 12 } || envelope.Tag is not { Length: 16 } ||
                envelope.Ciphertext is null || envelope.Ciphertext.Length > MaximumFrameSize / 2)
                throw new InvalidDataException("Invalid host setup reply.");
            byte[] key = recipient.Decrypt(envelope.Key, RSAEncryptionPadding.OaepSHA256);
            byte[] plaintext = new byte[envelope.Ciphertext.Length];
            try
            {
                if (key.Length != 32) throw new CryptographicException("Invalid host setup encryption key.");
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.Tag, plaintext, Context(requestId, purpose));
                return JsonSerializer.Deserialize<T>(plaintext) ?? throw new InvalidDataException("Missing host setup message.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        internal static async Task WriteAsync(Stream stream, byte[] frame, CancellationToken cancellationToken)
        {
            if (frame.Length == 0 || frame.Length > MaximumFrameSize) throw new InvalidDataException("Invalid host setup frame.");
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(header, frame.Length);
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        internal static async Task<byte[]> ReadAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = new byte[4];
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            int length = BinaryPrimitives.ReadInt32BigEndian(header);
            if (length < 1 || length > MaximumFrameSize) throw new InvalidDataException("Invalid host setup frame.");
            byte[] frame = new byte[length];
            await stream.ReadExactlyAsync(frame, cancellationToken).ConfigureAwait(false);
            return frame;
        }
    }
}
