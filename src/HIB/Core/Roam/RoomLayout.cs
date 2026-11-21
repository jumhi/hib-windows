// H.I.B Room (docs/SPACE.md §10.2): where every computer stands on the desk. Room space is millimetres,
// x right, y down; each computer is one group placed by an offset: the room position of its desktop origin,
// in whole millimetres. The layout travels over the peer link as roomLayout — the highest rev wins; equal
// rev → the lower `by` (UTF-8); the same author's same rev with other groups replaces it; a computer never
// takes back an older copy of its own — and room.json keeps it (and the computers already seen, so
// "Arrange your desk" opens by itself only the first time one joins). Matches the Mac's RoomLogic.swift.
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

/// <summary>One computer's place in the room: the room position (whole mm) of its desktop origin.</summary>
public sealed record RoomGroup(string Computer, double X, double Y);

/// <summary>The <c>roomLayout</c> message (§10.2).</summary>
public sealed record RoomLayout(long Rev, string By, IReadOnlyList<RoomGroup> Groups, int V = 1)
{
    public const string Type = "roomLayout";

    static readonly IComparer<string> IdOrder = Comparer<string>.Create(SpaceIds.Compare);

    public RoomGroup? Find(string computer) => Groups.FirstOrDefault(g => g.Computer == computer) ?? Groups.FirstOrDefault(g => SpaceIds.Same(g.Computer, computer));

    public bool Has(string computer) => Find(computer) is not null;

    /// <summary>The same layout with one group moved (or added), same rev and author.</summary>
    public RoomLayout WithGroup(string computer, double x, double y) => Replacing(new[] { new RoomGroup(computer, x, y) }, Rev, By);

    /// <summary>This layout with these groups set (the others kept), whole millimetres, sorted by id (UTF-8), with this rev and author.</summary>
    public RoomLayout Replacing(IEnumerable<RoomGroup> groups, long rev, string by)
    {
        var set = groups.ToList();
        var list = Groups.Where(g => !set.Any(n => SpaceIds.Same(n.Computer, g.Computer))).ToList();
        foreach (var g in set)
            if (!list.Any(x => SpaceIds.Same(x.Computer, g.Computer))) list.Add(new RoomGroup(g.Computer, Round(g.X), Round(g.Y)));
        list.Sort((a, b) => SpaceIds.Compare(a.Computer, b.Computer));
        return new RoomLayout(rev, by, list, V);
    }

    public static RoomLayout Make(IEnumerable<RoomGroup> groups, long rev, string by)
        => new RoomLayout(rev, by, Array.Empty<RoomGroup>()).Replacing(groups, rev, by);

    /// <summary>
    /// §10.2: does <paramref name="incoming"/> replace <paramref name="current"/>? The highest rev; equal rev → the lower by (UTF-8
    /// bytes); the same author's same rev with other groups replaces it (an auto layout re-sent after another computer joined).
    /// </summary>
    public static bool Wins(RoomLayout incoming, RoomLayout? current)
    {
        if (current is null) return true;
        if (incoming.Rev != current.Rev) return incoming.Rev > current.Rev;
        if (incoming.By != current.By) return SpaceIds.Compare(incoming.By, current.By) < 0;
        return !incoming.SamePlaces(current);
    }

    /// <summary>
    /// Should computer <paramref name="me"/> take <paramref name="incoming"/> over the layout it stands by? <see cref="Wins"/>, except that a
    /// computer never takes back an older copy of its own layout (relayed by another) — only one with a higher rev (its storage was lost).
    /// </summary>
    public static bool Adopt(RoomLayout incoming, RoomLayout? current, string me)
    {
        if (incoming.By == me && incoming.Rev <= (current?.Rev ?? -1)) return false;
        return Wins(incoming, current);
    }

    /// <summary>Same groups at the same places (whole mm), whatever their order, rev or author.</summary>
    public bool SamePlaces(RoomLayout? other)
    {
        if (other is null || other.Groups.Count != Groups.Count) return false;
        foreach (var g in Groups)
        {
            if (other.Find(g.Computer) is not { } o) return false;
            if (Round(o.X) != Round(g.X) || Round(o.Y) != Round(g.Y)) return false;
        }
        return true;
    }

    /// <summary>The canonical text, so each layout goes to a computer once.</summary>
    public string Key => $"{V}|{Rev}|{By}|" + string.Join(";", Groups.OrderBy(g => g.Computer, IdOrder).Select(g => $"{g.Computer}={Round(g.X)},{Round(g.Y)}"));

    /// <summary>Offsets are whole millimetres, rounded half away from zero (Swift's rounded()).</summary>
    public static double Round(double v) => double.IsFinite(v) ? Math.Round(v, MidpointRounding.AwayFromZero) : 0;

    public JsonObject ToWire()
    {
        var groups = new JsonArray();
        foreach (var g in Groups) groups.Add(new JsonObject { ["computer"] = g.Computer, ["x"] = (long)Round(g.X), ["y"] = (long)Round(g.Y) });
        return new JsonObject { ["v"] = V, ["rev"] = Rev, ["by"] = By, ["groups"] = groups };
    }

    public static RoomLayout? Parse(JsonElement d)
    {
        if (d.ValueKind != JsonValueKind.Object || d.Str("by") is not { } by) return null;
        if (d.Double("rev") is not { } revD || !double.IsFinite(revD) || revD < 0) return null;
        var groups = new List<RoomGroup>();
        if (d.TryGetProperty("groups", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in arr.EnumerateArray())
            {
                if (g.ValueKind != JsonValueKind.Object) continue;
                var c = g.Str("computer");
                if (string.IsNullOrEmpty(c) || groups.Any(x => SpaceIds.Same(x.Computer, c))) continue;
                if (g.Double("x") is not { } x || g.Double("y") is not { } y || !double.IsFinite(x) || !double.IsFinite(y)) continue;
                groups.Add(new RoomGroup(c, Round(x), Round(y)));
            }
        }
        return new RoomLayout((long)revD, by, groups, d.Int("v") ?? 1);
    }

    /// <summary>An edit's rev: now (ms), and always above the layout it replaces (another computer's clock may be ahead).</summary>
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
