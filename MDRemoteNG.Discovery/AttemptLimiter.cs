using System.Text.Json;

namespace MDRemoteNG.Discovery;

// Limits survive helper restarts. No PINs or credentials are stored in this file.
internal sealed class AttemptLimiter
{
    private readonly object _gate = new();
    private readonly string _path;
    private State _state;
    internal sealed class State
    {
        public DateTimeOffset WindowStart { get; set; }
        public int Attempts { get; set; }
        public Dictionary<string, Entry> Sources { get; set; } = [];
    }
    internal sealed class Entry
    {
        public int Failures { get; set; }
        public DateTimeOffset NextAllowed { get; set; }
        public DateTimeOffset LastAttempt { get; set; }
    }

    public AttemptLimiter(string path)
    {
        _path = path;
        try { _state = JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? new State(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { _state = new State(); }
    }

    public bool TryBegin(string address)
    {
        lock (_gate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (now - _state.WindowStart >= TimeSpan.FromMinutes(1))
            {
                _state.WindowStart = now;
                _state.Attempts = 0;
            }
            if (_state.Attempts >= 30) return false;
            if (!_state.Sources.TryGetValue(address, out Entry? source))
            {
                if (_state.Sources.Count >= 256)
                {
                    string? oldest = _state.Sources.Where(e => now - e.Value.LastAttempt > TimeSpan.FromHours(2))
                        .OrderBy(e => e.Value.LastAttempt).Select(e => e.Key).FirstOrDefault();
                    if (oldest is null) return false;
                    _state.Sources.Remove(oldest);
                }
                _state.Sources[address] = source = new Entry();
            }
            if (now < source.NextAllowed) return false;
            source.LastAttempt = now;
            // Every admitted attempt is charged before expensive cryptography starts.
            source.NextAllowed = now.AddSeconds(5);
            _state.Attempts++;
            return Persist();
        }
    }

    public void Complete(string address, bool success)
    {
        lock (_gate)
        {
            if (!_state.Sources.TryGetValue(address, out Entry? source)) return;
            source.Failures = success ? 0 : Math.Min(12, source.Failures + 1);
            source.NextAllowed = DateTimeOffset.UtcNow.AddSeconds(success ? 30 :
                Math.Min(3600, 5 * Math.Pow(2, source.Failures)));
            Persist();
        }
    }

    private bool Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_state));
            File.Move(temporary, _path, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return false; } // Fail closed when the restart-resistant limiter cannot be saved.
    }
}
