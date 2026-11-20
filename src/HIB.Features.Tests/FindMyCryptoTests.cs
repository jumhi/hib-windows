using System.Text;
using System.Text.Json.Nodes;
using HIB.Features.Abstractions;
using HIB.Features.FindMy;

namespace HIB.Features.Tests;

/// <summary>Expected values were produced by running the Mac's Swift/CryptoKit derivation
/// (FindMyRelayKeys.derive, AES.GCM.seal) in a standalone `swift` script.</summary>
public class FindMyCryptoTests
{
    private static readonly byte[] Key1 = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] Key2 = Convert.FromBase64String("q83vEjRWeJCrze8SNFZ4kKvN7xI0VniQq83vEjRWeJA=");

    private const string SwiftSealed1 =
        "oKGio6SlpqeoqaqrgtFqoZT+VNhvb3W7YEiIhVfTnMFDMNtb8jWDnwqSyisUtp/DSDXlICRsjJKUrQC65RtmoAyS5vwLfbT4aH3n8fdy1mqQZBGVFZ8/nCddxCZCRpPXg14Oc2URmYuzDZPKApgjnWE3foZchblQEO04RPRnNV4OeYFOK8ok1zKJrrEE9AlIW0f5XC+syh/YPoMvveF4";
    private const string SwiftPlain1 =
        """{"v":1,"type":"findRingState","data":{"ringing":true},"ts":1763600000000,"nonce":"00112233445566778899aabbccddeeff","from":"phone"}""";

    [Fact]
    public void Derive_MatchesSwift_SequentialKey()
    {
        var keys = FindMyRelayKeys.Derive(Key1);
        Assert.Equal("hib-2bf2843ed35edf9c0307ae3de117c747", keys.Topic);
        Assert.Equal("c1CRAztI2+R78Lz3JuYBBnoe/u4kBjfHJ8Tok73esS8=", Convert.ToBase64String(keys.Key));
    }

    [Fact]
    public void Derive_MatchesSwift_RandomLookingKey()
    {
        var keys = FindMyRelayKeys.Derive(Key2);
        Assert.Equal("hib-f502263a1316b12fe3949808d5a68936", keys.Topic);
        Assert.Equal("QiY+MbYu+9dUlCzcqKO2GYqzeO4GHfYnvqHHvXarsfo=", Convert.ToBase64String(keys.Key));
    }

    [Fact]
    public void SealRaw_SameNonce_IsByteIdenticalToCryptoKitCombined()
    {
        var keys = FindMyRelayKeys.Derive(Key1);
        var nonce = Enumerable.Range(0, 12).Select(i => (byte)(0xA0 + i)).ToArray();
        var sealedBytes = FindMyCodec.SealRaw(Encoding.UTF8.GetBytes(SwiftPlain1), keys.Key, nonce);
        Assert.Equal(SwiftSealed1, Convert.ToBase64String(sealedBytes));
    }

    [Fact]
    public void Open_DecryptsSwiftEnvelope()
    {
        var keys = FindMyRelayKeys.Derive(Key1);
        var env = FindMyCodec.Open(SwiftSealed1, keys, nowMs: 1763600000000 + 1000);
        Assert.NotNull(env);
        Assert.Equal("findRingState", env!.Type);
        Assert.Equal("phone", env.From);
        Assert.Equal("00112233445566778899aabbccddeeff", env.Nonce);
        Assert.Equal(1763600000000, env.Ts);
        Assert.True(env.Data["ringing"]!.GetValue<bool>());
    }

    [Fact]
    public void Open_RejectsStaleAndFutureMessages()
    {
        var keys = FindMyRelayKeys.Derive(Key1);
        Assert.Null(FindMyCodec.Open(SwiftSealed1, keys, nowMs: 1763600000000 + FindMyCodec.MaxAgeMs + 1));
        Assert.Null(FindMyCodec.Open(SwiftSealed1, keys, nowMs: 1763600000000 - FindMyCodec.MaxAgeMs - 1));
        Assert.NotNull(FindMyCodec.Open(SwiftSealed1, keys, nowMs: 1763600000000 + FindMyCodec.MaxAgeMs));
    }

    [Fact]
    public void Envelope_RoundTrip_FromMac()
    {
        var keys = FindMyRelayKeys.Derive(Key2);
        var sealedB64 = FindMyCodec.Seal("findRing", new JsonObject { ["action"] = "start" }, keys);
        Assert.NotNull(sealedB64);
        var raw = Convert.FromBase64String(sealedB64!);
        Assert.True(raw.Length > 12 + 16);
        var env = FindMyCodec.Open(sealedB64!, keys);
        Assert.NotNull(env);
        Assert.Equal("findRing", env!.Type);
        Assert.Equal("mac", env.From);
        Assert.Equal("start", env.Data["action"]!.GetValue<string>());
        Assert.Equal(32, env.Nonce.Length);
        Assert.Matches("^[0-9a-f]{32}$", env.Nonce);

        // Plaintext shape: {"v":1,"type","data","ts","nonce","from"}
        var plain = JsonNode.Parse(FindMyCodec.OpenRaw(raw, keys.Key)!)!.AsObject();
        Assert.Equal(new[] { "v", "type", "data", "ts", "nonce", "from" }, plain.Select(p => p.Key).ToArray());
        Assert.Equal(1, plain["v"]!.GetValue<int>());
    }

    [Fact]
    public void Open_RejectsWrongKeyTamperingAndBadVersion()
    {
        var keys = FindMyRelayKeys.Derive(Key1);
        var other = FindMyRelayKeys.Derive(Key2);
        Assert.Null(FindMyCodec.Open(SwiftSealed1, other, nowMs: 1763600000000));

        var bytes = Convert.FromBase64String(SwiftSealed1);
        bytes[20] ^= 1;
        Assert.Null(FindMyCodec.Open(Convert.ToBase64String(bytes), keys, nowMs: 1763600000000));
        Assert.Null(FindMyCodec.Open("not base64!!", keys));

        var v2 = Convert.ToBase64String(FindMyCodec.SealRaw(Encoding.UTF8.GetBytes(
            $$"""{"v":2,"type":"findRing","data":{},"ts":{{FindMyCodec.NowMs()}},"nonce":"ab","from":"phone"}"""), keys.Key));
        Assert.Null(FindMyCodec.Open(v2, keys));
        var noNonce = Convert.ToBase64String(FindMyCodec.SealRaw(Encoding.UTF8.GetBytes(
            $$"""{"v":1,"type":"findRing","data":{},"ts":{{FindMyCodec.NowMs()}},"nonce":"","from":"phone"}"""), keys.Key));
        Assert.Null(FindMyCodec.Open(noNonce, keys));
    }

    [Fact]
    public void ReplayCache_AcceptsOnce_PersistsAndCapsAt200()
    {
        var store = new InMemorySettingsStore();
        var cache = new FindMyReplayCache(store);
        Assert.True(cache.Accept("a"));
        Assert.False(cache.Accept("a"));
        Assert.False(new FindMyReplayCache(store).Accept("a")); // persisted
        for (var i = 0; i < 250; i++) cache.Accept("n" + i);
        Assert.Equal(200, store.Get<List<string>>("hibFindMyNonces")!.Count);
        Assert.True(cache.Accept("a")); // evicted by the LRU
    }

    [Fact]
    public void Relay_HandleLine_OnlyAcceptsFreshPhoneMessagesOnce()
    {
        var store = new InMemorySettingsStore();
        using var relay = new FindMyRelay(store, () => Key1);
        var keys = FindMyRelayKeys.Derive(Key1);
        var received = new List<FindMyEnvelope>();
        relay.MessageReceived += received.Add;

        var fromPhone = FindMyCodec.Seal("findPong", new JsonObject { ["id"] = "x" }, keys, from: "phone")!;
        var fromMac = FindMyCodec.Seal("findPong", new JsonObject { ["id"] = "y" }, keys, from: "mac")!;
        string Line(string msg) => new JsonObject { ["id"] = "1", ["event"] = "message", ["topic"] = keys.Topic, ["message"] = msg }.ToJsonString();

        Assert.True(relay.HandleLine(Line(fromPhone), keys));
        Assert.False(relay.HandleLine(Line(fromPhone), keys));   // replay
        Assert.False(relay.HandleLine(Line(fromMac), keys));     // our own echo
        Assert.False(relay.HandleLine("""{"event":"keepalive"}""", keys));
        Assert.Single(received);
        Assert.Equal("x", received[0].Data["id"]!.GetValue<string>());
    }

    [Fact]
    public void Keys_MarkPaired_AndResetDetection()
    {
        var store = new InMemorySettingsStore();
        Assert.Null(FindMyRelayKeys.Current(store, Key1));
        FindMyRelayKeys.MarkPaired(store, Key1);
        var cur = FindMyRelayKeys.Current(store, Key1);
        Assert.Equal(FindMyRelayKeys.Derive(Key1), cur);
        // Phone offline but app restarted without the key loaded: saved keys still usable.
        Assert.NotNull(FindMyRelayKeys.Current(store, null));
        // Pairing key was reset: relay is off until the phone reconnects.
        Assert.Null(FindMyRelayKeys.Current(store, Key2));
    }

    [Theory]
    [InlineData("https://ntfy.sh/", "https://ntfy.sh")]
    [InlineData("http://10.0.0.2:8080//", "http://10.0.0.2:8080")]
    [InlineData("ftp://x", null)]
    [InlineData("ntfy.sh", null)]
    public void Settings_NormalizeServer(string input, string? expected)
        => Assert.Equal(expected, FindMySettings.Normalize(input));

    [Fact]
    public async Task Relay_Publish_PostsTextPlainCiphertextToTopic()
    {
        var store = new InMemorySettingsStore();
        FindMyRelayKeys.MarkPaired(store, Key1);
        var handler = new ScriptedHandler((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)));
        using var relay = new FindMyRelay(store, () => Key1, handler);
        Assert.True(relay.IsAvailable);
        Assert.True(await relay.PublishAsync("findLocate", new JsonObject()));
        var (url, body, _) = Assert.Single(handler.Requests);
        Assert.Equal("https://ntfy.sh/hib-2bf2843ed35edf9c0307ae3de117c747", url);
        var env = FindMyCodec.Open(body, FindMyRelayKeys.Derive(Key1));
        Assert.Equal("findLocate", env!.Type);
        Assert.Equal("mac", env.From);
    }
}
