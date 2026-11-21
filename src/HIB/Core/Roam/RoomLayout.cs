// H.I.B Room (docs/SPACE.md §10.2): where every computer stands on the desk. Room space is millimetres,
// x right, y down; each computer is one group placed by an offset. The layout travels over the peer link
// as roomLayout (highest rev wins; equal rev → the lower `by` wins), and room.json keeps it (and the
// computers already seen, so "Arrange your desk" opens by itself only the first time one joins).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HIB.Core.Link;
using HIB.Core.Space;

namespace HIB.Core.Roam;

/// <summary>One computer's place in the room: the room position (mm) of its desktop origin.</summary>
public sealed record RoomGroup(string Computer, double X, double Y);

/// <summary>The <c>roomLayout</c> message (§10.2).</summary>
public sealed record RoomLayout(long Rev, string By, IReadOnlyList<RoomGroup> Groups, int V = 1)
{
    public const string Type = "roomLayout";

    public RoomGroup? Find(string computer) => Groups.FirstOrDefault(g => SpaceIds.Same(g.Computer, computer));

    public bool Has(string computer) => Find(computer) is not null;

    /// <summary>The same layout with one group moved (or added).</summary>
    public RoomLayout WithGroup(string computer, double x, double y)
    {
        var list = Groups.Where(g => !SpaceIds.Same(g.Computer, computer)).ToList();
        list.Add(new RoomGroup(computer, Round(x), Round(y)));
        return this with { Groups = list };
    }

    /// <summary>§10.2: does <paramref name="incoming"/> replace <paramref name="current"/>? Highest rev; equal rev → lower by (UTF-8 bytes).</summary>
    public static bool Wins(RoomLayout incoming, RoomLayout? current)
    {
        if (current is null) return true;
        if (incoming.Rev != current.Rev) return incoming.Rev > current.Rev;
        return SpaceIds.Compare(incoming.By, current.By) < 0;
    }

    /// <summary>Same groups at the same places (to 0.1 mm), whatever their order, rev or author.</summary>
    public bool SamePlaces(RoomLayout? other)
    {
        if (other is null || other.Groups.Count != Groups.Count) return false;
        foreach (var g in Groups)
        {
            if (other.Find(g.Computer) is not { } o) return false;
            if (Math.Abs(o.X - g.X) > 0.05 || Math.Abs(o.Y - g.Y) > 0.05) return false;
        }
        return true;
    }

    public static double Round(double v) => double.IsFinite(v) ? Math.Round(v, 1) : 0;

    public JsonObject ToWire()
    {
        var groups = new JsonArray();
        foreach (var g in Groups) groups.Add(new JsonObject { ["computer"] = g.Computer, ["x"] = Round(g.X), ["y"] = Round(g.Y) });
        return new JsonObject { ["v"] = V, ["rev"] = Rev, ["by"] = By, ["groups"] = groups };
    }

    public static RoomLayout? Parse(JsonElement d)
    {
        if (d.ValueKind != JsonValueKind.Object) return null;
        var groups = new List<RoomGroup>();
        if (d.TryGetProperty("groups", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in arr.EnumerateArray())
            {
                if (g.ValueKind != JsonValueKind.Object) continue;
                var c = g.Str("computer");
                if (string.IsNullOrWhiteSpace(c) || groups.Any(x => SpaceIds.Same(x.Computer, c))) continue;
                var x = g.Double("x") ?? 0;
                var y = g.Double("y") ?? 0;
                if (!double.IsFinite(x) || !double.IsFinite(y)) continue;
                groups.Add(new RoomGroup(c, x, y));
            }
        }
        var rev = d.Long("rev") ?? 0;
        return new RoomLayout(Math.Max(0, rev), d.Str("by") ?? "", groups, d.Int("v") ?? 1);
    }

    /// <summary>A new rev for an edit made now: the clock, but always past the current one.</summary>
    public static long NextRev(RoomLayout? current, DateTime nowUtc)
    {
        var ms = new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        return Math.Max(ms, (current?.Rev ?? 0) + 1);
    }
}

/// <summary>room.json: the layout and the computers already seen in the room.</summary>
public sealed class RoomStore
{
    sealed class FileModel
    {
        public long Rev { get; set; }
        public string By { get; set; } = "";
        public List<RoomGroup> Groups { get; set; } = new();
        public bool HasLayout { get; set; }
        public List<string> Seen { get; set; } = new();
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    readonly string _path;
    readonly object _gate = new();
    readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    RoomLayout? _layout;

    public RoomStore(string dataDir)
    {
        _path = Path.Combine(dataDir, "room.json");
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path), Json) is { } m)
            {
                if (m.HasLayout) _layout = new RoomLayout(m.Rev, m.By, m.Groups.Where(g => !string.IsNullOrWhiteSpace(g.Computer)).ToList());
                foreach (var s in m.Seen) _seen.Add(s);
            }
        }
        catch { /* start fresh */ }
    }

    public RoomLayout? Layout { get { lock (_gate) return _layout; } }

    public void SetLayout(RoomLayout? layout)
    {
        lock (_gate) _layout = layout;
        Save();
    }

    /// <summary>Marks a computer as seen; true the first time.</summary>
    public bool MarkSeen(string computer)
    {
        bool added;
        lock (_gate) added = _seen.Add(computer);
        if (added) Save();
        return added;
    }

    public bool WasSeen(string computer) { lock (_gate) return _seen.Contains(computer); }

    void Save()
    {
        FileModel m;
        lock (_gate)
        {
            m = new FileModel { Seen = _seen.OrderBy(x => x, StringComparer.Ordinal).ToList() };
            if (_layout is { } l)
            {
                m.HasLayout = true;
                m.Rev = l.Rev;
                m.By = l.By;
                m.Groups = l.Groups.ToList();
            }
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(m, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch { /* best effort */ }
    }
}
