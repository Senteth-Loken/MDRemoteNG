using System.Security.Cryptography;
using System.Text.Json;

namespace MDRemoteNG.Core;

public sealed class ProfileStore
{
    private const int Iterations = 600_000;
    private const int MaximumProfileBytes = 64 * 1024;
    public static string DefaultPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MDRemoteNG", "profile.dat");
    public string Path { get; }
    public bool Exists => File.Exists(Path);

    public ProfileStore(string? path = null) => Path = System.IO.Path.GetFullPath(path ?? DefaultPath);

    public Profile Load()
    {
        using var guard = Lock();
        return LoadCore();
    }

    public void Save(Profile profile)
    {
        // A settings writer must never overwrite PIN state or lockout counters from
        // a stale snapshot. Only Create/Unlock/ChangePin manage those fields.
        UpdateProfile(profile);
    }

    public Profile Create(string pin)
    {
        string normalized = PinPolicy.Normalize(pin);
        using var guard = Lock();
        if (Exists) throw new InvalidOperationException("A PIN is already configured. Unlock MDRemoteNG to change it.");
        var profile = new Profile();
        SetPin(profile, normalized);
        SaveCore(profile);
        return profile;
    }

    public Profile Unlock(string pin)
    {
        using var guard = Lock();
        return UnlockCore(pin);
    }

    public Profile ChangePin(string currentPin, string newPin)
    {
        string normalized = PinPolicy.Normalize(newPin);
        using var guard = Lock();
        var profile = UnlockCore(currentPin);
        SetPin(profile, normalized);
        SaveCore(profile);
        return profile;
    }

    /// <summary>Commit settings and an optional PIN change in a single protected file replacement.</summary>
    public Profile UpdateProfile(Profile edited, string? currentPin = null, string? newPin = null)
    {
        string? normalized = newPin == null ? null : PinPolicy.Normalize(newPin);
        using var guard = Lock();
        var profile = normalized == null ? LoadCore() : UnlockCore(currentPin ?? "");
        profile.DisplayName = edited.DisplayName;
        profile.DiscoveryEnabled = edited.DiscoveryEnabled;
        profile.AutoConnect = edited.AutoConnect;
        profile.SharedConnection = edited.SharedConnection;
        if (normalized != null) SetPin(profile, normalized);
        SaveCore(profile);
        return profile;
    }

    private Profile UnlockCore(string pin)
    {
        var profile = LoadCore();
        var now = DateTimeOffset.UtcNow;
        if (profile.LockedUntil > now) throw new PinLockedException(profile.LockedUntil);
        string normalized = PinPolicy.Normalize(pin);
        byte[] candidate = Rfc2898DeriveBytes.Pbkdf2(normalized, profile.PinSalt, Iterations, HashAlgorithmName.SHA256, 32);
        bool matches = CryptographicOperations.FixedTimeEquals(candidate, profile.PinVerifier);
        CryptographicOperations.ZeroMemory(candidate);
        if (!matches)
        {
            profile.FailedAttempts = Math.Min(profile.FailedAttempts + 1, 30);
            // Every wrong attempt incurs a persistent delay, up to five minutes.
            profile.LockedUntil = now.AddSeconds(Math.Min(300, Math.Pow(2, Math.Min(profile.FailedAttempts - 1, 9))));
            SaveCore(profile);
            throw new UnauthorizedAccessException("Incorrect PIN.");
        }
        profile.FailedAttempts = 0;
        profile.LockedUntil = default;
        SaveCore(profile);
        return profile;
    }

    private static void SetPin(Profile profile, string normalized)
    {
        profile.NormalizedPin = normalized;
        profile.PinSalt = RandomNumberGenerator.GetBytes(32);
        profile.PinVerifier = Rfc2898DeriveBytes.Pbkdf2(normalized, profile.PinSalt, Iterations, HashAlgorithmName.SHA256, 32);
        profile.FailedAttempts = 0;
        profile.LockedUntil = default;
    }

    private Profile LoadCore()
    {
        var info = new FileInfo(Path);
        if (info.Length > MaximumProfileBytes) throw new InvalidDataException("The MDRemoteNG profile is too large.");
        byte[] plain = WindowsDataProtection.Unprotect(File.ReadAllBytes(Path));
        try
        {
            var profile = JsonSerializer.Deserialize<Profile>(plain) ?? throw new InvalidDataException("Invalid MDRemoteNG profile.");
            Validate(profile);
            return profile;
        }
        catch (JsonException ex) { throw new InvalidDataException("Invalid MDRemoteNG profile.", ex); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private void SaveCore(Profile profile)
    {
        Validate(profile);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(profile);
        string temp = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (plain.Length > MaximumProfileBytes / 2) throw new InvalidDataException("The MDRemoteNG profile is too large.");
            File.WriteAllBytes(temp, WindowsDataProtection.Protect(plain));
            File.Move(temp, Path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static void Validate(Profile profile)
    {
        if (profile.Version != 1 || profile.InstanceId == Guid.Empty || profile.DisplayName is null || profile.DisplayName.Length > 128 ||
            !PinPolicy.Validate(profile.NormalizedPin) || profile.NormalizedPin != PinPolicy.Normalize(profile.NormalizedPin) ||
            profile.PinSalt is not { Length: 32 } || profile.PinVerifier is not { Length: 32 } || profile.FailedAttempts < 0)
            throw new InvalidDataException("Invalid MDRemoteNG profile.");
        profile.SharedConnection?.Validate();
    }

    private FileStream Lock()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try { return new FileStream(Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(25); }
        }
    }
}
