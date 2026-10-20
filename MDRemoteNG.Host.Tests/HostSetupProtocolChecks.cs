using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MDRemoteNG.Host;
using mRemoteNG.App;

internal static class HostSetupProtocolChecks
{
    internal static async Task<int> RunAsync()
    {
        int passed = 0;
        void Check(bool value, string description)
        {
            if (!value) throw new Exception(description);
            Console.WriteLine("PASS " + description);
            passed++;
        }
        void Reject(Action action, string description)
        {
            bool rejected = false;
            try { action(); }
            catch (Exception exception) when (exception is CryptographicException or InvalidDataException or System.Text.Json.JsonException)
            { rejected = true; }
            Check(rejected, description);
        }

        using var recipient = RSA.Create(3072);
        Guid request = Guid.NewGuid();
        var expected = new HostSetupReply
        {
            Result = new HostConfigurationResult
            {
                Port = 3389, Username = "mdrng_test", Domain = "TEST-PC",
                Password = "test-only-host-password-1!", StatusMessage = "Host ready"
            }
        };
        byte[] publicKey = recipient.ExportSubjectPublicKeyInfo();
        byte[] frame = HostSetupTransport.Protect(expected, publicKey, request);
        HostSetupReply restored = HostSetupTransport.Unprotect(frame, recipient, request);
        Check(restored.Result?.Password == expected.Result.Password && restored.Result.Username == expected.Result.Username,
            "Host setup returns the generated credential only to the ephemeral private-key owner.");
        string captured = Encoding.UTF8.GetString(frame);
        Check(!captured.Contains(expected.Result.Password) && !captured.Contains(expected.Result.Username),
            "Captured setup messages do not contain plaintext credentials.");
        Check(!frame.AsSpan().SequenceEqual(HostSetupTransport.Protect(expected, publicKey, request)),
            "Each host setup reply receives a fresh random encryption key and nonce.");
        using var otherRecipient = RSA.Create(3072);
        Reject(() => HostSetupTransport.Unprotect(frame, otherRecipient, request), "A different local key cannot decrypt setup credentials.");
        Reject(() => HostSetupTransport.Unprotect(frame, recipient, Guid.NewGuid()), "A setup reply cannot be replayed in another request.");
        JsonObject envelope = JsonNode.Parse(frame)!.AsObject();
        byte[] ciphertext = Convert.FromBase64String(envelope["Ciphertext"]!.GetValue<string>());
        ciphertext[0] ^= 1;
        envelope["Ciphertext"] = Convert.ToBase64String(ciphertext);
        Reject(() => HostSetupTransport.Unprotect(Encoding.UTF8.GetBytes(envelope.ToJsonString()), recipient, request),
            "Tampered host setup details are rejected before use.");
        envelope = JsonNode.Parse(frame)!.AsObject();
        envelope["Version"] = 2;
        Reject(() => HostSetupTransport.Unprotect(Encoding.UTF8.GetBytes(envelope.ToJsonString()), recipient, request),
            "Unsupported setup envelope versions fail closed.");
        using var weakKey = RSA.Create(2048);
        Reject(() => HostSetupTransport.Protect(expected, weakKey.ExportSubjectPublicKeyInfo(), request),
            "Recipient keys below the required strength are rejected before provisioning.");
        byte[] errorFrame = HostSetupTransport.Protect(new HostSetupReply { Error = "Administrator approval is required." }, publicKey, request);
        Check(HostSetupTransport.Unprotect(errorFrame, recipient, request).Error == "Administrator approval is required.",
            "User-safe setup errors survive the authenticated return channel.");
        byte[] invalidReply = HostSetupTransport.Protect(new HostSetupReply(), publicKey, request);
        Reject(() => HostSetupTransport.Unprotect(invalidReply, recipient, request), "Empty setup replies cannot be mistaken for successful configuration.");
        using var setupRecipient = RSA.Create(3072);
        byte[] setupPublicKey = setupRecipient.ExportSubjectPublicKeyInfo();
        byte[] hello = HostSetupTransport.ProtectHello(setupPublicKey, publicKey, request);
        Check(HostSetupTransport.ReadHello(hello, recipient, request).AsSpan().SequenceEqual(setupPublicKey),
            "Only the original application can receive the child process's ephemeral setup key.");
        Reject(() => HostSetupTransport.Unprotect(hello, recipient, request),
            "A setup-key message cannot be substituted for a configuration result.");
        var accountRequest = new HostSetupRequest
        {
            Mode = "current-account",
            Credentials = new HostAccountCredentials
            {
                Username = "test-user", Domain = "TEST-DOMAIN", Password = "test-only-current-account-password!", Sid = "S-1-5-21-1-2-3-1001"
            }
        };
        byte[] accountFrame = HostSetupTransport.ProtectRequest(accountRequest, setupPublicKey, request);
        HostSetupRequest accountRestored = HostSetupTransport.ReadRequest(accountFrame, setupRecipient, request);
        Check(accountRestored.Credentials?.Password == accountRequest.Credentials.Password && accountRestored.Mode == "current-account",
            "Current-account credentials reach only the child's private-key owner.");
        Check(!Encoding.UTF8.GetString(accountFrame).Contains(accountRequest.Credentials.Password),
            "Current-account passwords are never plaintext in the setup request.");
        Reject(() => HostSetupTransport.ReadRequest(accountFrame, recipient, request),
            "The parent response key cannot decrypt a child-bound password request.");
        Reject(() => HostSetupTransport.ReadRequest(accountFrame, setupRecipient, Guid.NewGuid()),
            "A password request cannot be replayed into another setup operation.");
        byte[] replyAsRequest = HostSetupTransport.Protect(expected, setupPublicKey, request);
        Reject(() => HostSetupTransport.ReadRequest(replyAsRequest, setupRecipient, request),
            "Authenticated message purpose prevents result-to-request substitution.");
        accountRequest.Mode = "managed";
        Reject(() => HostSetupTransport.ReadRequest(HostSetupTransport.ProtectRequest(accountRequest, setupPublicKey, request), setupRecipient, request),
            "Managed-account setup cannot accept an existing account's credentials.");
        accountRequest.Mode = "current-account";
        accountRequest.Credentials = null;
        Reject(() => HostSetupTransport.ReadRequest(HostSetupTransport.ProtectRequest(accountRequest, setupPublicKey, request), setupRecipient, request),
            "Current-account setup cannot proceed without supplied credentials.");
        byte[] managedFrame = HostSetupTransport.ProtectRequest(new HostSetupRequest(), setupPublicKey, request);
        Check(HostSetupTransport.ReadRequest(managedFrame, setupRecipient, request).Credentials is null,
            "Dedicated-account setup explicitly requests generation without an existing password.");
        Check(!HostSetupTransport.ReadRequest(managedFrame, setupRecipient, request).AdministratorAccess,
            "Existing setup requests default to standard account access.");
        var administratorRequest = new HostSetupRequest { Protocol = "SSH", AdministratorAccess = true };
        byte[] administratorFrame = HostSetupTransport.ProtectRequest(administratorRequest, setupPublicKey, request);
        Check(HostSetupTransport.ReadRequest(administratorFrame, setupRecipient, request).AdministratorAccess,
            "Dedicated administrator access survives the authenticated SSH request.");
        administratorRequest.Protocol = "RDP";
        Reject(() => HostSetupTransport.ReadRequest(HostSetupTransport.ProtectRequest(administratorRequest, setupPublicKey, request), setupRecipient, request),
            "RDP setup cannot request the SSH administrator role.");
        administratorRequest.Protocol = "VNC";
        administratorRequest.Port = 5900;
        Reject(() => HostSetupTransport.ReadRequest(HostSetupTransport.ProtectRequest(administratorRequest, setupPublicKey, request), setupRecipient, request),
            "VNC setup cannot request a Windows administrator account.");
        administratorRequest.Protocol = "SSH";
        administratorRequest.Port = 0;
        administratorRequest.Mode = "current-account";
        administratorRequest.Credentials = new HostAccountCredentials { Username = "test", Domain = "TEST", Password = "test-only", Sid = "S-1-5-21-1-2-3-1001" };
        Reject(() => HostSetupTransport.ReadRequest(HostSetupTransport.ProtectRequest(administratorRequest, setupPublicKey, request), setupRecipient, request),
            "Current-account setup cannot promote an account through the dedicated administrator flag.");
        var administratorReply = new HostSetupReply { Result = new HostConfigurationResult { Protocol = "SSH", AdministratorAccess = true } };
        Check(HostSetupTransport.Unprotect(HostSetupTransport.Protect(administratorReply, publicKey, request), recipient, request).Result!.AdministratorAccess,
            "The elevated result reports the dedicated administrator role through encrypted IPC.");
        var vncRequest = new HostSetupRequest { Protocol = "VNC", Port = 5900 };
        byte[] vncFrame = HostSetupTransport.ProtectRequest(vncRequest, setupPublicKey, request);
        Check(HostSetupTransport.ReadRequest(vncFrame, setupRecipient, request).Protocol == "VNC",
            "The authenticated setup request carries the explicit VNC protocol and listener port.");
        vncRequest.Mode = "current-account";
        vncRequest.Credentials = new HostAccountCredentials { Username = "test", Domain = "TEST", Password = "test-only", Sid = "S-1-5-21-1-2-3-1001" };
        Reject(() => HostSetupTransport.ReadRequest(HostSetupTransport.ProtectRequest(vncRequest, setupPublicKey, request), setupRecipient, request),
            "VNC setup cannot request or consume Windows-account credentials.");
        vncRequest = new HostSetupRequest { Protocol = "SSH", Port = 5900 };
        Reject(() => HostSetupTransport.ReadRequest(HostSetupTransport.ProtectRequest(vncRequest, setupPublicKey, request), setupRecipient, request),
            "Only VNC setup can supply a parent-selected listener port.");
        vncRequest = new HostSetupRequest { Protocol = "UNKNOWN" };
        Reject(() => HostSetupTransport.ReadRequest(HostSetupTransport.ProtectRequest(vncRequest, setupPublicKey, request), setupRecipient, request),
            "Unknown setup protocols are rejected before machine changes.");
        using (var stream = new MemoryStream())
        {
            await HostSetupTransport.WriteAsync(stream, frame, CancellationToken.None);
            stream.Position = 0;
            Check((await HostSetupTransport.ReadAsync(stream, CancellationToken.None)).AsSpan().SequenceEqual(frame),
                "Bounded setup framing round-trips an encrypted reply.");
        }
        using (var oversized = new MemoryStream(new byte[] { 0, 1, 0, 0 }))
        {
            bool rejected = false;
            try { await HostSetupTransport.ReadAsync(oversized, CancellationToken.None); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Oversized setup frames are rejected before allocation.");
        }
        using (var truncated = new MemoryStream(new byte[] { 0, 0, 0, 8, 1 }))
        {
            bool rejected = false;
            try { await HostSetupTransport.ReadAsync(truncated, CancellationToken.None); }
            catch (EndOfStreamException) { rejected = true; }
            Check(rejected, "Truncated setup replies cannot yield partial credentials.");
        }
        return passed;
    }
}
