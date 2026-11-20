using System.Text.Json.Nodes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HIB.Features.PhoneAgent;
using HIB.Features.PhoneSearch;
using HIB.Services;
using HIB.ViewModels;
using HIB.Views;

namespace HIB.FakePhone;

/// <summary>The HIB.Features services wired into the app, driven by the fake phone.</summary>
public class FeatureIntegrationTests
{
    static readonly string? ShotDir = Environment.GetEnvironmentVariable("HIB_SCREENSHOT_DIR");

    static async Task Pump(int ms = 300)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
    }

    static async Task<T> Await<T>(Task<T> task, int timeoutMs = 10000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!task.IsCompleted && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        return await task.WaitAsync(TimeSpan.FromMilliseconds(100));
    }

    static void Save(Avalonia.Controls.Window w, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (ShotDir is null) return;
        Directory.CreateDirectory(ShotDir);
        frame!.Save(Path.Combine(ShotDir, name + ".png"));
    }

    [AvaloniaFact]
    public async Task Features_are_wired_to_the_link_and_pages_render()
    {
        await using var host = new TestHost();
        var window = new MainWindow { Width = 1080, Height = 760 };
        var controller = new AppController(host.Core, null) { WindowOverride = window };
        using var features = new FeatureHost(host.Core, controller);
        controller.Features = features;
        features.AdbAutoConnect = false; // never touch the dev machine's adb server from tests
        var main = new MainViewModel(controller);
        window.DataContext = main;
        window.Show();

        // Shared data folder, separate file names.
        Assert.Equal(host.Core.Options.DataDir, HIB.Features.HibPaths.DataDirectory);
        features.Settings.Set("hib.test", 1);
        Assert.True(File.Exists(Path.Combine(host.Core.Options.DataDir, "features.json")));
        Assert.True(File.Exists(Path.Combine(host.Core.Options.DataDir, "hib-desktop.json")));

        await using var phone = new FakePhone(host.Core.BuildQr()) { Name = "Pixel 9 Pro" };
        await phone.ConnectAsync();
        await phone.SendDeviceAsync();
        await phone.WaitForAsync("macInfo");
        await Pump();

        // Adapter: IPhoneLink sees the session.
        Assert.True(features.Link.IsConnected);
        Assert.Equal("Pixel 9 Pro", features.Link.DeviceName);
        Assert.Equal(host.Core.Settings.LinkKeyBytes(), features.Link.PairingKey);
        string? seen = null;
        features.Link.MessageReceived += (t, _) => { if (t == "phoneEvent") seen = t; };
        await phone.SendAsync("phoneEvent", new { @event = "headset", connected = true, name = "Buds" });
        await features.Link.SendAsync("findLocate", new JsonObject());
        await phone.WaitForAsync("findLocate");
        Assert.True(await Eventually.TrueAsync(() => seen == "phoneEvent"));

        // Ring goes through RingService (link route) and follows ringState.
        features.Ring.Start();
        Assert.Equal("start", (await phone.WaitForAsync("ringPhone")).Data.GetProperty("action").GetString());
        await phone.SendAsync("ringState", new { ringing = true });
        await Pump();
        Assert.True(features.Ring.IsRinging);
        Assert.True(main.Home.IsRinging);
        features.Ring.Stop();
        Assert.Equal("stop", (await phone.WaitForAsync("ringPhone")).Data.GetProperty("action").GetString());

        // Scenes: a runScene gets an answer instead of hanging.
        await phone.SendAsync("runScene", new { id = "s1" });
        var sceneResult = await phone.WaitForAsync("sceneRunResult");
        Assert.False(sceneResult.Data.GetProperty("ok").GetBoolean());

        // Find My: locate → findLocation updates the last location.
        features.FindMy.Locate();
        await phone.WaitForAsync("findLocate");
        await phone.SendAsync("findLocation", new { lat = 24.7136, lon = 46.6753, accuracyM = 12.0, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), provider = "fused", batteryPct = 76, isCharging = true, networkName = "Home Wi-Fi" });
        await Pump(500);
        Assert.NotNull(features.FindMy.LastLocation);

        // App list for the Phone page.
        var icon = "data:image/png;base64," + Convert.ToBase64String(SamplePng());
        await phone.SendAsync("appIcons", new Dictionary<string, object>
        {
            ["com.whatsapp"] = new { name = "WhatsApp", icon, listening = true, systemApp = false },
            ["com.spotify.music"] = new { name = "Spotify", icon = "", listening = true, systemApp = false },
            ["com.google.android.youtube"] = new { name = "YouTube", icon = "", listening = true, systemApp = false },
            ["org.telegram.messenger"] = new { name = "Telegram", icon = "", listening = true, systemApp = false },
        });
        await Pump();

        // Phone search: query → results; fetch over WebSocket → PhoneFetchRegistry → ~/Downloads/H.I.B.
        main.Navigate("search");
        await Pump();
        var search = (SearchPhoneViewModel)main.CurrentContent!;
        search.Query = "boarding pass";
        search.SearchCommand.Execute(null);
        var q = await phone.WaitForAsync("phoneSearch");
        var sid = q.Data.GetProperty("id").GetString();
        var fileName = "Boarding pass SV1020.pdf";
        await phone.SendAsync("phoneSearchResults", new
        {
            id = sid,
            done = false,
            results = new object[]
            {
                new { kind = "files", title = fileName, subtitle = "Download", mime = "application/pdf", path = "/storage/emulated/0/Download/" + fileName, size = 5, date = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                new { kind = "photos", title = "IMG_2041.jpg", subtitle = "DCIM/Camera", mime = "image/jpeg", path = "/storage/emulated/0/DCIM/Camera/IMG_2041.jpg", size = 2345678, date = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), thumbnail = Convert.ToBase64String(SamplePng()) },
                new { kind = "contacts", title = "Mom", subtitle = "+966 50 123 4567", mime = "text/vcard" },
            },
        });
        await phone.SendAsync("phoneSearchResults", new { id = sid, done = true, results = Array.Empty<object>() });
        Assert.True(await Eventually.TrueAsync(() => { Dispatcher.UIThread.RunJobs(); return search.Results.Count == 3; }));
        Save(window, "11-search-phone");

        var item = search.Results.First(r => r.Title == fileName);
        var download = item.DownloadCommand.ExecuteAsync(null);
        var fetch = await phone.WaitForAsync("phoneFetchFile");
        var fid = fetch.Data.GetProperty("id").GetString()!;
        Assert.StartsWith("hibfetch-", fid);
        var bytes = "%PDF-"u8.ToArray();
        await phone.SendAsync("fileTransferInit", new { id = fid, name = fileName, size = bytes.Length, chunkSize = 65536, searchFetchId = fid });
        await phone.SendAsync("fileChunk", new { id = fid, index = 0, chunk = Convert.ToBase64String(bytes) });
        await phone.SendAsync("fileTransferComplete", new { id = fid, name = fileName, size = bytes.Length });
        await Await(download.ContinueWith(_ => true));
        Assert.NotNull(item.SavedPath);
        try
        {
            Assert.Equal(bytes, await File.ReadAllBytesAsync(item.SavedPath!));
            Assert.Equal(HIB.Features.HibPaths.DownloadsDirectory, Path.GetDirectoryName(item.SavedPath));
        }
        finally { File.Delete(item.SavedPath!); }

        // Pages render.
        main.Navigate("phone");
        await Pump();
        Save(window, "10-phone");
        main.Navigate("findmy");
        await Pump();
        Save(window, "12-find-my");

        main.Navigate("agent");
        await Pump();
        var agentVm = (PhoneAgentViewModel)main.CurrentContent!;
        agentVm.ShowSettings = true;
        features.Agent.Prompt = "Open WhatsApp and tell Ali I'll be 10 minutes late";
        features.Agent.Steps.Add(new PhoneAgentStep(PhoneAgentStepKind.User, "Open WhatsApp and tell Ali I'll be 10 minutes late"));
        features.Agent.Steps.Add(new PhoneAgentStep(PhoneAgentStepKind.Action, "Open app WhatsApp") { Thumbnail = SamplePng() });
        features.Agent.Steps.Add(new PhoneAgentStep(PhoneAgentStepKind.Action, "Tap “Ali”", "Chat list, first row") { Thumbnail = SamplePng() });
        features.Agent.Steps.Add(new PhoneAgentStep(PhoneAgentStepKind.Confirmation, "Send “I'll be 10 minutes late” to Ali?"));
        await Pump();
        Save(window, "13-phone-agent");
        Assert.True(agentVm.IsGemini);

        // Login code from a notification: copied locally, not synced back to the phone.
        string? copied = null;
        features.Codes.CodeCopied += (code, _) => copied = code;
        await Task.Delay(6300); // past the 6 s connect backlog window
        await phone.SendAsync("notification", new { id = "otp1", title = "Bank", body = "Your verification code is 482915. Do not share it.", app = "Messages", package = "com.google.android.apps.messaging" });
        Assert.True(await Eventually.TrueAsync(() => { Dispatcher.UIThread.RunJobs(); return copied is not null; }));
        Assert.Equal("482915", copied);
        await Pump(800);
        Assert.DoesNotContain(phone.History, m => m.Type == "clipboardUpdate" && m.Raw.Contains("482915"));

        window.AllowClose = true;
        window.Close();
    }

    /// <summary>A tiny valid PNG (lavender gradient) built without image libraries.</summary>
    internal static byte[] SamplePng(int w = 120, int h = 240)
    {
        using var raw = new MemoryStream();
        for (var y = 0; y < h; y++)
        {
            raw.WriteByte(0);
            for (var x = 0; x < w; x++)
            {
                var t = (double)(x + y) / (w + h);
                raw.WriteByte((byte)(183 - 90 * t));
                raw.WriteByte((byte)(156 - 100 * t));
                raw.WriteByte((byte)(255 - 120 * t));
            }
        }
        using var z = new MemoryStream();
        using (var zl = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Fastest, true)) raw.WriteTo(zl);
        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        void Chunk(string type, byte[] data)
        {
            var len = BitConverter.GetBytes(data.Length); Array.Reverse(len); png.Write(len);
            var td = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            png.Write(td);
            var crc = BitConverter.GetBytes(Crc(td)); Array.Reverse(crc); png.Write(crc);
        }
        var ihdr = new byte[13];
        var wb = BitConverter.GetBytes(w); Array.Reverse(wb); wb.CopyTo(ihdr, 0);
        var hb = BitConverter.GetBytes(h); Array.Reverse(hb); hb.CopyTo(ihdr, 4);
        ihdr[8] = 8; ihdr[9] = 2;
        Chunk("IHDR", ihdr);
        Chunk("IDAT", z.ToArray());
        Chunk("IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    static uint Crc(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in data)
        {
            c ^= b;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return c ^ 0xFFFFFFFF;
    }
}
