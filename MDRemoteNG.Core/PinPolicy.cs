namespace MDRemoteNG.Core;

public static class PinPolicy
{
    public const string Description = "Use 4–8 characters: letters, numbers and symbols. Letters are case insensitive. Spaces are not allowed.";

    public static bool Validate(string? pin) => pin is { Length: >= 4 and <= 8 } && pin.All(c => c >= '!' && c <= '~');

    public static string Normalize(string? pin)
    {
        if (!Validate(pin)) throw new ArgumentException(Description, nameof(pin));
        return pin!.ToUpperInvariant();
    }
}

public sealed class PinLockedException(DateTimeOffset lockedUntil) : InvalidOperationException(
    $"Too many incorrect PIN attempts. Try again after {lockedUntil.ToLocalTime():T}.")
{
    public DateTimeOffset LockedUntil { get; } = lockedUntil;
}
