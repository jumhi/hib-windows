using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HIB.Features.Abstractions;

namespace HIB.Features.FindMy;

/// <summary>Relay preferences (same keys as the Mac).</summary>
public sealed class FindMySettings
{
    public const string DefaultServer = "https://ntfy.sh";
    private const string EnabledKey = "hibFindMyRelayEnabled";
    private const string ServerKey = "hibFindMyRelayServer";
    private readonly ISettingsStore _store;

    public FindMySettings(ISettingsStore store) { _store = store; }

    public bool RelayEnabled
    {
        get => _store.Get<bool?>(EnabledKey) ?? true;
        set => _store.Set(EnabledKey, value);
    }

    public string ServerUrl
    {
        get
        {
            var value = (_store.Get<string>(ServerKey) ?? "").Trim();
            return value.Length == 0 ? DefaultServer : value;
        }
        set => _store.Set(ServerKey, value);
    }

    /// <summary>Server without a trailing slash, or null if it isn't a usable http(s) URL.</summary>
    public string? NormalizedServer => Normalize(ServerUrl);

    internal static string? Normalize(string server)
    {
        server = server.TrimEnd('/');
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri)) return null;
        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme != "https" && scheme != "http") return null;
        if (string.IsNullOrEmpty(uri.Host)) return null;
        return server;
    }
}

/// <summary>Topic + key derived (HKDF-SHA256) from the pairing secret.</summary>
public sealed record FindMyRelayKeys(string Topic, byte[] Key)
{
    public const string TopicInfo = "hib-findmy-topic-v1";
    public const string KeyInfo = "hib-findmy-key-v1";
    internal const string StoreTopic = "hibFindMyTopic";
    internal const string StoreKey = "hibFindMyKey";

    /// <summary>HKDF-SHA256(IKM = pairing key, salt = empty, info, L = 32). Topic = "hib-" + first 32 lowercase hex.</summary>
    public static FindMyRelayKeys Derive(byte[] pairingKey)
    {
        var topicBytes = HKDF.DeriveKey(HashAlgorithmName.SHA256, pairingKey, 32, [], Encoding.UTF8.GetBytes(TopicInfo));
        var keyBytes = HKDF.DeriveKey(HashAlgorithmName.SHA256, pairingKey, 32, [], Encoding.UTF8.GetBytes(KeyInfo));
        var hex = Convert.ToHexStringLower(topicBytes);
        return new FindMyRelayKeys("hib-" + hex[..32], keyBytes);
    }

    /// <summary>Record that the phone connected with the current pairing key, enabling the relay.</summary>
    public static void MarkPaired(ISettingsStore store, byte[]? pairingKey)
    {
        if (pairingKey is null || pairingKey.Length == 0) return;
        var keys = Derive(pairingKey);
        store.Set(StoreTopic, keys.Topic);
        store.Set(StoreKey, Convert.ToBase64String(keys.Key));
    }

    /// <summary>The saved keys, as long as the pairing key hasn't been reset since the phone last
    /// connected. null means "not paired" and the relay stays off.</summary>
    public static FindMyRelayKeys? Current(ISettingsStore store, byte[]? pairingKey)
    {
        var topic = store.Get<string>(StoreTopic);
        var b64 = store.Get<string>(StoreKey);
        if (topic is null || b64 is null) return null;
        byte[] data;
        try { data = Convert.FromBase64String(b64); } catch { return null; }
        if (pairingKey is { Length: > 0 } && Derive(pairingKey).Topic != topic) return null;
        return new FindMyRelayKeys(topic, data);
    }

    public bool Equals(FindMyRelayKeys? other)
        => other is not null && Topic == other.Topic && Key.AsSpan().SequenceEqual(other.Key);

    public override int GetHashCode() => Topic.GetHashCode();
}

/// <summary>A verified relay message.</summary>
public sealed record FindMyEnvelope(string Type, JsonObject Data, long Ts, string Nonce, string From);

/// <summary>Relay envelope: AES-256-GCM over <c>{"v":1,type,data,ts,nonce,from}</c>, framed as
/// base64(nonce12 ‖ ciphertext ‖ tag16) — identical to CryptoKit's <c>SealedBox.combined</c>.</summary>
public static class FindMyCodec
{
    public const long MaxAgeMs = 5 * 60 * 1000;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static string RandomNonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public static string? Seal(string type, JsonObject data, FindMyRelayKeys keys, string from = "mac",
                               long? ts = null, string? nonce = null)
    {
        var plain = new JsonObject
        {
            ["v"] = 1,
            ["type"] = type,
            ["data"] = data.DeepClone(),
            ["ts"] = ts ?? NowMs(),
            ["nonce"] = nonce ?? RandomNonce(),
            ["from"] = from,
        };
        var json = Encoding.UTF8.GetBytes(plain.ToJsonString());
        try
        {
            return Convert.ToBase64String(SealRaw(json, keys.Key));
        }
        catch { return null; }
    }

    internal static byte[] SealRaw(byte[] plaintext, byte[] key, byte[]? iv = null)
    {
        iv ??= RandomNumberGenerator.GetBytes(NonceSize);
        var output = new byte[NonceSize + plaintext.Length + TagSize];
        iv.CopyTo(output, 0);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(iv, plaintext, output.AsSpan(NonceSize, plaintext.Length), output.AsSpan(NonceSize + plaintext.Length, TagSize));
        return output;
    }

    internal static byte[]? OpenRaw(byte[] combined, byte[] key)
    {
        if (combined.Length < NonceSize + TagSize) return null;
        var ctLen = combined.Length - NonceSize - TagSize;
        var plain = new byte[ctLen];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(combined.AsSpan(0, NonceSize), combined.AsSpan(NonceSize, ctLen),
                        combined.AsSpan(NonceSize + ctLen, TagSize), plain);
            return plain;
        }
        catch (CryptographicException) { return null; }
    }

    /// <summary>Decrypts and validates (v == 1, type, non-empty nonce, numeric ts within 5 min).
    /// Returns null for anything else.</summary>
    public static FindMyEnvelope? Open(string base64, FindMyRelayKeys keys, long? nowMs = null)
    {
        byte[] combined;
        try { combined = Convert.FromBase64String(base64.Trim()); } catch { return null; }
        var plain = OpenRaw(combined, keys.Key);
        if (plain is null) return null;
        JsonObject? obj;
        try { obj = JsonNode.Parse(plain) as JsonObject; } catch { return null; }
        if (obj is null) return null;

        if (!TryGetInt(obj["v"], out var v) || v != 1) return null;
        if (obj["type"] is not JsonValue typeNode || !typeNode.TryGetValue<string>(out var type)) return null;
        if (obj["nonce"] is not JsonValue nonceNode || !nonceNode.TryGetValue<string>(out var nonce) || nonce.Length == 0) return null;
        if (!TryGetLong(obj["ts"], out var ts)) return null;
        if (Math.Abs((nowMs ?? NowMs()) - ts) > MaxAgeMs) return null;

        var data = obj["data"] as JsonObject ?? new JsonObject();
        var from = obj["from"] is JsonValue f && f.TryGetValue<string>(out var fs) ? fs : "";
        return new FindMyEnvelope(type, (JsonObject)data.DeepClone(), ts, nonce, from);
    }

    private static bool TryGetLong(JsonNode? node, out long value)
    {
        value = 0;
        if (node is not JsonValue jv || jv.GetValueKind() != JsonValueKind.Number) return false;
        if (jv.TryGetValue(out long l)) { value = l; return true; }
        if (jv.TryGetValue(out double d)) { value = (long)d; return true; }
        return false;
    }

    private static bool TryGetInt(JsonNode? node, out long value)
    {
        value = 0;
        if (node is not JsonValue jv || jv.GetValueKind() != JsonValueKind.Number) return false;
        if (jv.TryGetValue(out long l)) { value = l; return true; }
        // Swift's `as? Int` also accepts 1.0 from JSONSerialization.
        if (jv.TryGetValue(out double d) && d == Math.Floor(d)) { value = (long)d; return true; }
        return false;
    }
}

/// <summary>Remembers recently seen nonces (persisted, LRU of 200) so a message is never acted on twice.</summary>
public sealed class FindMyReplayCache
{
    private const string StoreKey = "hibFindMyNonces";
    private const int Limit = 200;
    private readonly ISettingsStore _store;
    private readonly List<string> _nonces;
    private readonly object _lock = new();

    public FindMyReplayCache(ISettingsStore store)
    {
        _store = store;
        _nonces = store.Get<List<string>>(StoreKey) ?? new List<string>();
    }

    /// <summary>True if the nonce is new (and records it).</summary>
    public bool Accept(string nonce)
    {
        lock (_lock)
        {
            if (_nonces.Contains(nonce)) return false;
            _nonces.Add(nonce);
            if (_nonces.Count > Limit) _nonces.RemoveRange(0, _nonces.Count - Limit);
            _store.Set(StoreKey, _nonces);
            return true;
        }
    }
}
