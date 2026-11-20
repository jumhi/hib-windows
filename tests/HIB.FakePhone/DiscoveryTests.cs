using System.Text;
using System.Text.Json;
using HIB.Core.Discovery;

namespace HIB.FakePhone;

public class DiscoveryTests
{
    static byte[] Query(string name, ushort qtype, ushort id = 0, bool unicastBit = false)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { (byte)(id >> 8), (byte)id, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
        foreach (var label in name.Split('.'))
        {
            var b = Encoding.UTF8.GetBytes(label);
            ms.WriteByte((byte)b.Length);
            ms.Write(b);
        }
        ms.WriteByte(0);
        ms.Write(new byte[] { (byte)(qtype >> 8), (byte)qtype, (byte)(unicastBit ? 0x80 : 0), 1 });
        return ms.ToArray();
    }

    sealed record Rr(string Name, ushort Type, byte[] Data, int DataOffset);

    static List<Rr> ParseAll(byte[] p)
    {
        var list = new List<Rr>();
        var count = (p[6] << 8 | p[7]) + (p[8] << 8 | p[9]) + (p[10] << 8 | p[11]);
        var off = 12;
        for (var i = 0; i < count; i++)
        {
            var name = string.Join('.', ManagedMdnsResponder.ReadName(p, ref off));
            var type = (ushort)(p[off] << 8 | p[off + 1]);
            var len = p[off + 8] << 8 | p[off + 9];
            off += 10;
            list.Add(new Rr(name, type, p, off));
            off += len;
        }
        return list;
    }

    static ManagedMdnsResponder Responder() => new("AirSync-Haitham’s PC", "HIB-ABCD1234", 6996,
        new Dictionary<string, string> { ["id"] = "ID-1", ["name"] = "Haitham’s PC", ["port"] = "6996", ["ver"] = "1", ["type"] = "mac" });

    [Fact]
    public void Ptr_query_answers_ptr_srv_txt_and_a()
    {
        var r = Responder();
        var reply = r.BuildReply(Query("_airsync._tcp.local", 12), 5353, out var unicast);
        Assert.NotNull(reply);
        Assert.False(unicast);
        Assert.Equal(0x84, reply![2]); // response + authoritative
        var rrs = ParseAll(reply);

        var ptr = rrs.Single(x => x.Type == 12 && x.Name == "_airsync._tcp.local");
        var o = ptr.DataOffset;
        Assert.Equal("AirSync-Haitham’s PC._airsync._tcp.local", string.Join('.', ManagedMdnsResponder.ReadName(reply, ref o)));

        var srv = rrs.Single(x => x.Type == 33);
        Assert.Equal(6996, reply[srv.DataOffset + 4] << 8 | reply[srv.DataOffset + 5]);
        o = srv.DataOffset + 6;
        Assert.Equal("HIB-ABCD1234.local", string.Join('.', ManagedMdnsResponder.ReadName(reply, ref o)));

        var txt = rrs.Single(x => x.Type == 16);
        var entries = new List<string>();
        var len = reply[txt.DataOffset - 2] << 8 | reply[txt.DataOffset - 1];
        for (var p = txt.DataOffset; p < txt.DataOffset + len;)
        {
            var l = reply[p];
            entries.Add(Encoding.UTF8.GetString(reply, p + 1, l));
            p += l + 1;
        }
        Assert.Contains("type=mac", entries);
        Assert.Contains("ver=1", entries);
        Assert.Contains("port=6996", entries);
        Assert.Contains("name=Haitham’s PC", entries);
        Assert.Contains("id=ID-1", entries);
    }

    [Fact]
    public void Unrelated_queries_are_ignored_and_legacy_unicast_keeps_id()
    {
        var r = Responder();
        Assert.Null(r.BuildReply(Query("_googlecast._tcp.local", 12), 5353, out _));
        var reply = r.BuildReply(Query("AirSync-Haitham’s PC._airsync._tcp.local", 33, id: 0xBEEF, unicastBit: true), 50000, out var unicast);
        Assert.NotNull(reply);
        Assert.True(unicast);
        Assert.Equal(0xBE, reply![0]);
        Assert.Equal(0xEF, reply[1]);
        Assert.Contains(ParseAll(reply), x => x.Type == 33);
    }

    [Fact]
    public void Presence_and_wakeup_payloads_match_phone_parser()
    {
        var udp = new UdpPresence(() => "Haitham’s PC", () => "ID-1", () => 6996, () => false, () => Array.Empty<string>());
        using var p = JsonDocument.Parse(udp.BuildPresenceJson());
        Assert.Equal("presence", p.RootElement.GetProperty("type").GetString());
        Assert.Equal("mac", p.RootElement.GetProperty("deviceType").GetString());
        Assert.Equal("Haitham’s PC", p.RootElement.GetProperty("name").GetString());
        Assert.Equal(6996, p.RootElement.GetProperty("port").GetInt32());
        Assert.Equal(JsonValueKind.Array, p.RootElement.GetProperty("ips").ValueKind);

        var wake = WakeUpClient.BuildPayload("192.168.1.20", 6996, "Haitham’s PC");
        Assert.StartsWith("{", wake); // bare JSON, no AIRSYNC_WAKEUP: prefix
        using var w = JsonDocument.Parse(wake);
        Assert.Equal("wakeUpRequest", w.RootElement.GetProperty("type").GetString());
        var d = w.RootElement.GetProperty("data");
        Assert.Equal("192.168.1.20", d.GetProperty("macIP").GetString());
        Assert.Equal(6996, d.GetProperty("macPort").GetInt32());
        Assert.Equal("Haitham’s PC", d.GetProperty("macName").GetString());
        Assert.True(d.GetProperty("isPlus").GetBoolean());
        Assert.False(d.TryGetProperty("key", out _));
    }
}
