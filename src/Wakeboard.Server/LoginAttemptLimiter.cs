using System.Collections.Concurrent;

namespace Wakeboard;

public sealed class LoginAttemptLimiter(int maxAttempts = 10, int windowMinutes = 15)
{
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> attempts = new();

    public bool IsBlocked(string key, DateTimeOffset now) => Recent(key, now).Count >= maxAttempts;

    public void RecordFailure(string key, DateTimeOffset now)
    {
        var recent = Recent(key, now);
        recent.Add(now);
        attempts[key] = recent;
    }

    public void Clear(string key) => attempts.TryRemove(key, out _);

    private List<DateTimeOffset> Recent(string key, DateTimeOffset now) =>
        attempts.GetOrAdd(key, _ => []).Where(value => now - value < TimeSpan.FromMinutes(windowMinutes)).ToList();
}
