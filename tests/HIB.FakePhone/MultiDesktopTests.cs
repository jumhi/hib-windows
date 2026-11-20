using System.Security.Cryptography;
using System.Text.Json;
using HIB.Core.Link;
using HIB.Core.Phone;

namespace HIB.FakePhone;

/// <summary>
/// Multi-desktop ecosystem (PROTOCOL.md §6.9 desktop→phone, §6.22): sending files to the phone,
/// to another computer through the phone (relayTo), receiving relayed files, and desktopsList.
/// </summary>
public class MultiDesktopTests
{
    static string TempFile(TestHost host, string name, int size)
    {
        var path = Path.Combine(host.Root, name);
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Plays the phone's receiver: acks every chunk, rebuilds the file, answers transferVerified.</summary>
    static async Task<(JsonElement init, byte[] bytes)> ReceiveOnPhoneAsync(FakePhone phone, bool verify = true)
    {
        var init = (await phone.WaitForAsync("fileTransferInit", TimeSpan.FromSeconds(10))).Data;
        var id = init.Str("id")!;
        var chunkSize = init.Int("chunkSize") ?? 65536;
        var buffer = new MemoryStream();
        var size = init.Long("size") ?? 0;
        var total = (int)((size + chunkSize - 1) / chunkSize);
        for (var n = 0; n < total; n++)
        {
            var msg = await phone.WaitForAsync("fileChunk", TimeSpan.FromSeconds(10));
            var index = msg.Data.Int("index")!.Value;
            var bytes = Convert.FromBase64String(msg.Data.Str("chunk")!);
            buffer.Position = (long)index * chunkSize;
            buffer.Write(bytes);
            await phone.SendAsync("fileChunkAck", new { id, index });
        }
        var complete = await phone.WaitForAsync("fileTransferComplete", TimeSpan.FromSeconds(10));
        Assert.Equal(id, complete.Data.Str("id"));
        if (verify) await phone.SendAsync("transferVerified", new { id, verified = true });
        return (init, buffer.ToArray());
    }

    [Fact]
    public async Task Send_to_phone_streams_chunks_with_checksum_and_waits_for_acks()
    {
        await using var host = new TestHost();
        await using var phone = await host.ConnectPhoneAsync();
        var path = TempFile(host, "holiday photo.jpg", 200_000); // 4 chunks

        var send = host.Core.Sender.SendAsync(path, null, "Fake Pixel");
        var (init, bytes) = await ReceiveOnPhoneAsync(phone);
        var result = await send;

        Assert.True(result.Ok, result.Error);
        Assert.Equal("holiday photo.jpg", init.Str("name"));
        Assert.Equal("image/jpeg", init.Str("mime"));
        Assert.Equal(200_000, init.Long("size"));
        Assert.Null(init.Str("relayTo"));
        var expected = File.ReadAllBytes(path);
        Assert.Equal(expected, bytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant(), init.Str("checksum"));
    }

    [Fact]
    public async Task Send_to_another_computer_sets_relayTo()
    {
        await using var host = new TestHost();
        await using var phone = await host.ConnectPhoneAsync();
        var path = TempFile(host, "notes.pdf", 70_000);

        var send = host.Core.Sender.SendAsync(path, "Haitham’s MacBook", "Haitham’s MacBook");
        var (init, bytes) = await ReceiveOnPhoneAsync(phone);
        var result = await send;

        Assert.True(result.Ok, result.Error);
        Assert.Equal("Haitham’s MacBook", init.Str("relayTo"));
        Assert.Equal(File.ReadAllBytes(path), bytes);
    }

    [Fact]
    public async Task Phone_cancel_with_targetOffline_fails_the_send_with_a_clear_reason()
    {
        await using var host = new TestHost();
        await using var phone = await host.ConnectPhoneAsync();
        var path = TempFile(host, "big.zip", 300_000);

        var send = host.Core.Sender.SendAsync(path, "Gone PC", "Gone PC");
        var init = await phone.WaitForAsync("fileTransferInit");
        await phone.SendAsync("fileTransferCancel", new { id = init.Data.Str("id"), reason = "targetOffline" });
        var result = await send;

        Assert.False(result.Ok);
        Assert.Contains("isn't connected", result.Error);
    }

    [Fact]
    public async Task Damaged_file_reported_by_receiver_fails_the_send()
    {
        await using var host = new TestHost();
        await using var phone = await host.ConnectPhoneAsync();
        var path = TempFile(host, "a.txt", 1000);

        var send = host.Core.Sender.SendAsync(path, null, "Fake Pixel");
        var (init, _) = await ReceiveOnPhoneAsync(phone, verify: false);
        await phone.SendAsync("transferVerified", new { id = init.Str("id"), verified = false });
        var result = await send;
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Desktops_list_feeds_send_targets_and_is_requested_on_connect()
    {
        await using var host = new TestHost();
        await using var phone = await host.ConnectPhoneAsync();

        // The desktop asks the phone for the other computers as soon as the link is up.
        Assert.True(await phone.ReceivesWithinAsync("desktopsRequest", TimeSpan.FromSeconds(5)));

        await phone.SendAsync("desktopsList", new
        {
            phone = new { id = "phone", name = "Haitham's S26" },
            desktops = new object[]
            {
                new { id = "Haitham’s MacBook", name = "Haitham’s MacBook", platform = "mac", type = "MacBook Pro", connected = true },
                new { id = "Office PC", name = "Office PC", platform = "windows", type = "Windows PC", connected = false },
                new { id = host.Core.Link.DesktopName, name = host.Core.Link.DesktopName, platform = "windows", connected = true },
            },
        });

        Assert.True(await Eventually.TrueAsync(() => host.Core.Ecosystem.Supported));
        var targets = host.Core.Ecosystem.Targets;
        Assert.Equal(new[] { "phone", "Haitham’s MacBook" }, targets.Select(t => t.Id).ToArray());
        Assert.Equal("Haitham's S26", targets[0].Name);
        Assert.Equal("mac", targets[1].Platform);
        Assert.Equal(2, host.Core.Ecosystem.AllDesktops.Count); // never lists itself
    }

    [Fact]
    public async Task Relayed_file_from_another_computer_lands_with_its_sender_name()
    {
        await using var host = new TestHost();
        await using var phone = await host.ConnectPhoneAsync();
        FlickInfo? flick = null;
        ReceivedFile? received = null;
        host.Core.Hub.FlickStarted += f => flick = f;
        host.Core.Files.Received += f => received = f;

        var id = Guid.NewGuid().ToString();
        var payload = new byte[90_000];
        Random.Shared.NextBytes(payload);
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        await phone.SendAsync("flickStart", new { id, kind = "file", name = "report.pdf", mime = "application/pdf", size = payload.Length, exitXRatio = 0.5, velocity = 1800.0, angle = 0.0, from = "Haitham’s MacBook", fromPlatform = "mac" });
        await phone.SendAsync("fileTransferInit", new { id, name = "report.pdf", size = payload.Length, mime = "application/pdf", chunkSize = 65536, isClipboard = false, checksum = sha, relayFrom = "Haitham’s MacBook", relayFromPlatform = "mac", flickId = id });
        for (var i = 0; i * 65536 < payload.Length; i++)
        {
            var len = Math.Min(65536, payload.Length - i * 65536);
            await phone.SendAsync("fileChunk", new { id, index = i, chunk = Convert.ToBase64String(payload, i * 65536, len) });
            var ack = await phone.WaitForAsync("fileChunkAck");
            Assert.Equal(i, ack.Data.Int("index"));
        }
        await phone.SendAsync("fileTransferComplete", new { id, name = "report.pdf", size = payload.Length, checksum = sha });

        var verified = await phone.WaitForAsync("transferVerified");
        Assert.Equal(id, verified.Data.Str("id"));
        Assert.True(verified.Data.Bool("verified"));
        Assert.True(await Eventually.TrueAsync(() => received is not null));
        Assert.Equal("Haitham’s MacBook", received!.From);
        Assert.Equal("Haitham’s MacBook", flick?.From);
        Assert.Equal(payload, File.ReadAllBytes(received.Path));
    }
}
