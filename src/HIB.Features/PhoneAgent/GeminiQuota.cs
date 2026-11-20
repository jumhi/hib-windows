using HIB.Features.Abstractions;

namespace HIB.Features.PhoneAgent;

/// <summary>
/// Remembers models whose free daily quota is used up (≈20 requests/day per model on the free tier)
/// so later turns and runs skip them instead of wasting seconds on a 429. Persisted under
/// <c>hib.phoneAgent.geminiExhaustedUntil</c> as {model: unix-seconds}.
/// </summary>
public sealed class GeminiQuota
{
    public const string StoreKey = "hib.phoneAgent.geminiExhaustedUntil";
    private readonly ISettingsStore _store;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _lock = new();

    public GeminiQuota(ISettingsStore store, Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _now = clock ?? (() => DateTimeOffset.UtcNow);
    }

    private double NowSeconds => _now().ToUnixTimeMilliseconds() / 1000.0;

    private Dictionary<string, double> Map() => _store.Get<Dictionary<string, double>>(StoreKey) ?? new();

    public bool IsExhausted(string model)
    {
        lock (_lock)
        {
            return Map().TryGetValue(model, out var until) && until > NowSeconds;
        }
    }

    /// <summary>Marks a model used up for <paramref name="seconds"/> (clamped to 60 s … 24 h);
    /// expired entries are dropped.</summary>
    public void MarkExhausted(string model, double seconds)
    {
        lock (_lock)
        {
            var now = NowSeconds;
            var map = Map().Where(kv => kv.Value > now).ToDictionary(kv => kv.Key, kv => kv.Value);
            map[model] = now + Math.Min(Math.Max(seconds, 60), 24 * 3600);
            _store.Set(StoreKey, map);
        }
    }

    /// <summary>Models that still have free requests left today, in fallback order.</summary>
    public int AvailableCount(string preferred)
    {
        var seen = new HashSet<string>();
        return new[] { preferred }.Concat(PhoneAgentGeminiModel.Fallbacks)
            .Count(m => seen.Add(m) && !IsExhausted(m));
    }

    public void Clear()
    {
        lock (_lock) _store.Remove(StoreKey);
    }
}
