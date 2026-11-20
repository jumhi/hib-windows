using System.Text.Json.Nodes;
using HIB.Features.Abstractions;
using HIB.Features.FindMy;
using HIB.Features.LoginCodes;
using HIB.Features.PhoneAgent;
using HIB.Features.PhoneSearch;
using HIB.Features.Scenes;

namespace HIB.Features.Tests;

public class OneTimeCodeDetectorTests
{
    // The Swift detector's own self-check table.
    [Theory]
    [InlineData("G-123456 is your Google verification code.", "123456")]
    [InlineData("Your WhatsApp code: 123-456. Don't share this code.", "123456")]
    [InlineData("Use OTP 482913 to confirm SAR 1,250.00 at AMAZON. Do not share.", "482913")]
    [InlineData("رمز التحقق الخاص بك هو 482913", "482913")]
    [InlineData("كود التفعيل: ٤٨٢٩", "4829")]
    [InlineData("Your Uber code: 1234. Never share this code.", "1234")]
    [InlineData("Your login code is AB12CD", "AB12CD")]
    [InlineData("123 456 is your Instagram code", "123456")]
    [InlineData("Your order #12345678 has shipped", null)]
    [InlineData("Payment of $1500 received. Call +966 55 123 4567", null)]
    [InlineData("Meeting at 12:30 on 18/11/2026", null)]
    [InlineData("Use promo code SAVE20 for 20% off", null)]
    [InlineData("Your verification code expires at 10:45. Ref 2024-11-18", null)]
    [InlineData("Your PIN for card ending 4821 was changed", null)]
    public void SelfCheckCases(string input, string? expected)
        => Assert.Equal(expected, OneTimeCodeDetector.Detect(input)?.Code);
}

public class LoginCodeAutoCopierTests
{
    private sealed class FakeClipboard : ILoginCodeClipboard
    {
        public long ChangeCount { get; set; } = 10;
        public string Text = "user text";
        public object? Snapshot() => Text;
        public void WriteCode(string code) { Text = code; ChangeCount++; }
        public void Restore(object snapshot) { Text = (string)snapshot; ChangeCount++; }
    }

    [Fact]
    public void CopiesOnce_DedupesAcrossNotifications_AndRestores()
    {
        var now = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        var clip = new FakeClipboard();
        var link = new FakePhoneLink();
        var copier = new LoginCodeAutoCopier(new InMemorySettingsStore(), clip, link, () => now);
        link.IsConnected = true;
        var n = new PhoneNotificationInfo("sms|1", "Google", "G-123456 is your Google verification code.", "Messages", "com.google.android.apps.messaging", false);

        Assert.Null(copier.Handle(n)); // within 6 s of connect: backlog
        now = now.AddSeconds(7);
        Assert.Equal("123456", copier.Handle(n));
        Assert.Equal("123456", clip.Text);
        Assert.Null(copier.Handle(n)); // same nid|code
        Assert.Null(copier.Handle(n with { Nid = "mail|9" })); // same code from another app within 2 min
        Assert.Null(copier.Handle(n with { Nid = "x", Ongoing = true }));

        copier.RestoreIfUnchanged();
        Assert.Equal("user text", clip.Text);
    }

    [Fact]
    public void DoesNotRestoreWhenUserCopiedSomethingElse()
    {
        var now = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        var clip = new FakeClipboard();
        var copier = new LoginCodeAutoCopier(new InMemorySettingsStore(), clip, null, () => now);
        copier.Handle(new PhoneNotificationInfo("1", "Uber", "Your Uber code: 1234. Never share this code.", "Uber", "com.ubercab", false));
        clip.Text = "new stuff"; clip.ChangeCount++;
        copier.RestoreIfUnchanged();
        Assert.Equal("new stuff", clip.Text);
    }

    [Fact]
    public void ListensToNotificationMessagesOnTheLink()
    {
        var now = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        var clip = new FakeClipboard();
        var link = new FakePhoneLink();
        string? copied = null;
        var copier = new LoginCodeAutoCopier(new InMemorySettingsStore(), clip, link, () => now);
        copier.CodeCopied += (code, _) => copied = code;
        link.Receive("notification", """{"id":"0|x|1","title":"Bank","body":"Your OTP is 482913","app":"Bank","package":"com.bank","priority":"alerting"}""");
        Assert.Equal("482913", copied);
    }
}

public class ApiKeyStoreTests
{
    [Fact]
    public void SaveLoadDelete_AndFileIsPrivate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hib-keys-" + Guid.NewGuid());
        try
        {
            var store = new ApiKeyStore(dir);
            Assert.False(store.HasKey(PhoneAgentProvider.Gemini));
            Assert.False(store.Save("   ", PhoneAgentProvider.Gemini));
            Assert.True(store.Save("  AIza-test-123  ", PhoneAgentProvider.Gemini));
            Assert.Equal("AIza-test-123", store.Load(PhoneAgentProvider.Gemini));
            Assert.Null(store.Load(PhoneAgentProvider.Claude));
            var file = Assert.Single(Directory.GetFiles(dir));
            if (OperatingSystem.IsWindows())
            {
                Assert.DoesNotContain("AIza-test-123", File.ReadAllText(file)); // DPAPI blob
            }
            else
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            }
            store.Delete(PhoneAgentProvider.Gemini);
            Assert.False(store.HasKey(PhoneAgentProvider.Gemini));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

public class LinkProtocolTests
{
    [Fact]
    public async Task ScenesBridge_PhoneActionRoundTrip()
    {
        var link = new FakePhoneLink { IsConnected = true };
        using var bridge = new ScenesBridge(link);
        var task = bridge.SetRingerAsync("silent");
        var sent = JsonNode.Parse(link.Sent.Single(s => s.Type == "phoneAction").Json)!;
        Assert.Equal("ringer", sent["action"]!.GetValue<string>());
        Assert.Equal("silent", sent["mode"]!.GetValue<string>());
        var rid = sent["requestId"]!.GetValue<string>();
        link.Receive("phoneActionResult", $$"""{"requestId":"{{rid}}","action":"ringer","ok":true,"message":"Ringer silent"}""");
        Assert.Equal("Ringer silent", await task);

        var failing = bridge.OpenAppAsync("com.x");
        var rid2 = JsonNode.Parse(link.Sent.Last().Json)!["requestId"]!.GetValue<string>();
        link.Receive("phoneActionResult", $$"""{"requestId":"{{rid2}}","ok":false,"error":"Not installed"}""");
        var ex = await Assert.ThrowsAsync<SceneActionException>(() => failing);
        Assert.Equal("Not installed", ex.Message);
    }

    [Fact]
    public void PhoneSearch_ParsesResultsAndBatches()
    {
        var link = new FakePhoneLink { IsConnected = true };
        using var client = new PhoneSearchClient(link);
        var batches = new List<(int Count, bool Done)>();
        var id = client.Search("pdf", (r, done) => batches.Add((r.Count, done)))!;
        var sent = JsonNode.Parse(link.Sent.Single().Json)!;
        Assert.Equal(["files", "photos", "apps", "contacts"], sent["kinds"]!.AsArray().Select(k => k!.GetValue<string>()).ToArray());
        link.Receive("phoneSearchResults", $$"""
            {"id":"{{id}}","results":[{"kind":"photos","title":"IMG_1.jpg","subtitle":"DCIM/Camera","mime":"image/jpeg","path":"/storage/emulated/0/DCIM/Camera/IMG_1.jpg","uri":"content://media/external/images/media/123","size":2345678,"date":1763600000000},{"kind":"bogus","title":"x"}],"done":false}
            """);
        link.Receive("phoneSearchResults", $$"""{"id":"{{id}}","results":[],"done":true}""");
        link.Receive("phoneSearchResults", $$"""{"id":"{{id}}","results":[],"done":true}""");
        Assert.Equal([(1, false), (0, true)], batches);
        var r = PhoneSearchResult.Parse(System.Text.Json.JsonDocument.Parse("""{"kind":"files","title":"a.pdf","date":1763600000}""").RootElement)!;
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1763600000), r.Date);
    }

    [Fact]
    public void FindMy_UsesLinkWhenConnected_AndHandlesLocation()
    {
        var link = new FakePhoneLink { IsConnected = true, PairingKey = new byte[32] };
        var store = new InMemorySettingsStore();
        using var findMy = new FindMyService(link, store);
        findMy.Locate();
        Assert.Equal("findLocate", link.Sent.Last().Type);
        Assert.Equal(FindMyRoute.Nearby, findMy.LastRoute);
        findMy.HandleIncoming("findLocation", JsonNode.Parse("""{"lat":24.7,"lon":46.6,"accuracyM":12,"timestamp":1763600000000,"batteryPct":80,"provider":"fused"}""")!.AsObject(), viaRelay: false);
        Assert.False(findMy.IsLocating);
        Assert.Equal(24.7, findMy.LastLocation!.Lat);
        Assert.Equal(80, store.Get<PhoneLocationReport>("hibFindMyLastPhoneLocation")!.BatteryPct);

        findMy.ShowMessage(new string('x', 600));
        Assert.Equal(500, JsonNode.Parse(link.Sent.Last().Json)!["text"]!.GetValue<string>().Length);
    }

    [Fact]
    public void Ring_PrefersLink_AndTracksPhoneState()
    {
        var link = new FakePhoneLink { IsConnected = true };
        var adb = new HIB.Features.Adb.AdbService(settings: new InMemorySettingsStore());
        using var ring = new HIB.Features.Ring.RingService(link, adb);
        ring.Start();
        Assert.True(ring.IsRinging);
        Assert.Equal(("ringPhone", "start"), (link.Sent.Last().Type, JsonNode.Parse(link.Sent.Last().Json)!["action"]!.GetValue<string>()));
        link.Receive("ringState", """{"ringing":false}""");
        Assert.False(ring.IsRinging);

        link.IsConnected = false;
        ring.Start();
        Assert.False(ring.IsRinging);
        Assert.Equal("Phone isn't connected over Wi-Fi, Bluetooth or ADB.", ring.LastError);
    }
}
