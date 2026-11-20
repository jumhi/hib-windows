using System.Security.Cryptography;
using System.Text;

namespace HIB.FakePhone;

/// <summary>
/// Drives a separately running H.I.B app (e.g. `HIB_PORT=16996 dotnet run --project src/HIB`)
/// as a fake phone. Set HIB_QR to the QR text from the Pair page ("Show code text").
/// Skipped when HIB_QR is not set. HIB_HOLD_SECONDS keeps the session open afterwards (for screenshots).
/// </summary>
public class ExternalAppTests
{
    [Fact]
    public async Task Drive_running_app()
    {
        var qr = Environment.GetEnvironmentVariable("HIB_QR");
        if (string.IsNullOrWhiteSpace(qr)) return; // not configured: nothing to do

        await using var phone = new FakePhone(qr) { Name = "Pixel 9 Pro" };
        await phone.ConnectAsync(Environment.GetEnvironmentVariable("HIB_HOST") ?? "127.0.0.1");
        await phone.SendDeviceAsync(Convert.ToBase64String(SampleImage(160, 320, 0x6A4FB0)));
        var info = await phone.WaitForAsync("macInfo");
        Assert.True(info.Data.GetProperty("isPlusSubscription").GetBoolean());

        await phone.SendAsync("status", new
        {
            battery = new { level = 76, isCharging = true },
            isPaired = true,
            music = new { isPlaying = true, title = "Blinding Lights", artist = "The Weeknd", volume = 60, isMuted = false,
                albumArt = Convert.ToBase64String(SampleImage(200, 200, 0xD94F70)), duration = 200000, position = 64000,
                positionTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), isBuffering = false, likeStatus = "none" },
        });
        await Task.Delay(4500); // past the connect-backlog toast suppression
        await phone.SendAsync("notification", new { id = "0|com.whatsapp|1|null|10123", title = "Ali", body = "Are we still on for 7? 🍕", app = "WhatsApp", package = "com.whatsapp", priority = "alerting",
            actions = new[] { new { name = "Reply", type = "reply" }, new { name = "Mark as read", type = "button" } } });
        await phone.SendAsync("notification", new { id = "0|com.google.android.gm|2|null|10124", title = "Your boarding pass", body = "Flight SV 1020 · Gate B12 · Boarding 14:05", app = "Gmail", package = "com.google.android.gm", priority = "alerting" });
        await phone.SendAsync("clipboardUpdate", new { text = "https://hib.example/invite/7Q2X" });

        var file = Encoding.UTF8.GetBytes("Hello from the fake phone!\n");
        var id = Guid.NewGuid().ToString();
        await phone.SendAsync("fileTransferInit", new { id, name = "notes.txt", size = file.Length, mime = "text/plain", chunkSize = 65536, checksum = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant() });
        await phone.SendAsync("fileChunk", new { id, index = 0, chunk = Convert.ToBase64String(file) });
        await phone.WaitForAsync("fileChunkAck");
        await phone.SendAsync("fileTransferComplete", new { id, name = "notes.txt", size = file.Length });

        await phone.SendAsync("call_event", new { eventId = "e1", direction = "incoming", state = "ringing", number = "+966 50 123 4567", contactName = "Mom", simSlot = 0 });

        var hold = int.TryParse(Environment.GetEnvironmentVariable("HIB_HOLD_SECONDS"), out var h) ? h : 0;
        for (var i = 0; i < hold; i += 4)
        {
            await Task.Delay(4000);
            await phone.SendAsync("call_progress", new { eventId = "e1" });
        }
    }

    /// <summary>A tiny gradient BMP so the UI has something to show without bundling assets.</summary>
    static byte[] SampleImage(int w, int h, int rgb)
    {
        var rowSize = w * 3 + (4 - w * 3 % 4) % 4;
        var data = new byte[54 + rowSize * h];
        void W32(int o, int v) => BitConverter.GetBytes(v).CopyTo(data, o);
        data[0] = (byte)'B'; data[1] = (byte)'M';
        W32(2, data.Length); W32(10, 54); W32(14, 40); W32(18, w); W32(22, h);
        data[26] = 1; data[28] = 24; W32(34, rowSize * h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var t = (double)(x + y) / (w + h);
                var o = 54 + y * rowSize + x * 3;
                data[o] = (byte)((rgb & 0xFF) * (1 - t) + 40 * t);
                data[o + 1] = (byte)(((rgb >> 8) & 0xFF) * (1 - t) + 20 * t);
                data[o + 2] = (byte)(((rgb >> 16) & 0xFF) * (1 - t) + 60 * t);
            }
        return data;
    }
}
