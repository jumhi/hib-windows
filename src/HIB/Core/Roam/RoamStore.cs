// H.I.B Roam: what survives a restart — the per-computer choices (on/off, side, ⌘ as Ctrl, pointer and scroll speed,
// natural scrolling), this PC's Roam options (game mode, lock together) and the last screenLayout from the phone (so
// sides are known before the phone connects). roam.json in the data folder.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HIB.Core.Roam;

public sealed record RoamPairSettings
{
    public bool Enabled { get; init; } = true;
    /// <summary>"left" | "right" | "above" | "below": the other computer's side (overrides the aims).</summary>
    public string? ManualSide { get; init; }
    public bool CmdAsCtrl { get; init; } = true;
    /// <summary>SPACE.md §12.5: pointer speed on that computer, 0.5×–2×.</summary>
    public double PointerSpeed { get; init; } = 1;
    /// <summary>SPACE.md §12.5: scroll speed on that computer, 0.5×–2×.</summary>
    public double ScrollSpeed { get; init; } = 1;
    /// <summary>SPACE.md §12.5: scrolling there moves the content the way the fingers go.</summary>
    public bool NaturalScroll { get; init; }
}

/// <summary>This PC's Roam options (Settings).</summary>
public sealed record RoamOptionsSettings
{
    /// <summary>SPACE.md §12.4 "Game mode: automatic (default) / off".</summary>
    public bool GameMode { get; init; } = true;
    /// <summary>SPACE.md §12.5 lock together (default on).</summary>
    public bool LockTogether { get; init; } = true;
}

public sealed class RoamStore
{
    sealed class FileModel
    {
        public Dictionary<string, RoamPairSettings> Peers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<ScreenAim> Layout { get; set; } = new();
        public RoamOptionsSettings Options { get; set; } = new();
    }

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    readonly string _path;
    readonly object _gate = new();
    readonly Dictionary<string, RoamPairSettings> _peers = new(StringComparer.OrdinalIgnoreCase);
    List<ScreenAim> _layout = new();
    RoamOptionsSettings _options = new();

    public RoamStore(string dataDir)
    {
        _path = Path.Combine(dataDir, "roam.json");
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path), Json) is { } model)
            {
                foreach (var (k, v) in model.Peers) _peers[k] = v;
                _layout = model.Layout.Where(a => !string.IsNullOrWhiteSpace(a.Computer)).ToList();
                _options = model.Options ?? new RoamOptionsSettings();
            }
        }
        catch { /* start fresh */ }
    }

    public RoamPairSettings Get(string peerId)
    {
        lock (_gate) return _peers.TryGetValue(peerId, out var s) ? s : new RoamPairSettings();
    }

    public void Set(string peerId, RoamPairSettings settings)
    {
        lock (_gate)
        {
            if (_peers.TryGetValue(peerId, out var old) && old == settings) return;
            _peers[peerId] = settings;
        }
        Save();
    }

    public IReadOnlyList<ScreenAim> Layout { get { lock (_gate) return _layout; } }

    public RoamOptionsSettings Options { get { lock (_gate) return _options; } }

    public void SetOptions(RoamOptionsSettings options)
    {
        lock (_gate)
        {
            if (_options == options) return;
            _options = options;
        }
        Save();
    }

    /// <summary>Stores a new screenLayout; true when it differs from the saved one (a new Point setup).</summary>
    public bool SetLayout(IReadOnlyList<ScreenAim> layout)
    {
        lock (_gate)
        {
            if (RoamLayout.Signature(layout) == RoamLayout.Signature(_layout)) return false;
            _layout = layout.ToList();
        }
        Save();
        return true;
    }

    /// <summary>A new Point setup clears every learned or manual side.</summary>
    public void ClearSides()
    {
        lock (_gate)
        {
            foreach (var k in _peers.Keys.ToList()) _peers[k] = _peers[k] with { ManualSide = null };
        }
        Save();
    }

    void Save()
    {
        FileModel model;
        lock (_gate) model = new FileModel { Peers = new Dictionary<string, RoamPairSettings>(_peers, StringComparer.OrdinalIgnoreCase), Layout = _layout.ToList(), Options = _options };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(model, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch { /* best effort */ }
    }
}
