using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HIB.Core.Phone;

/// <summary>A monitor as the picker sees it: a stable key (device name) and its desktop bounds.</summary>
public sealed record FlickScreen(string Key, double X, double Y, double Width, double Height)
{
    public double MidX => X + Width / 2;
    public double MidY => Y + Height / 2;
    public bool Contains(double px, double py) => px >= X && px < X + Width && py >= Y && py < Y + Height;
}

/// <summary>
/// Chooses which monitor a flicked card lands on (port of the Mac's FlickScreenPicker):
/// 1. teaching mode: the next flick's aim is recorded for the screen being taught;
/// 2. with ≥ 2 taught screens, the nearest remembered aim (≤ 50°) wins;
/// 3. otherwise swipe angle ≤ -18° → screen left of the pointer's, ≥ 18° → right, else the pointer's screen.
/// </summary>
public sealed class FlickScreenPicker
{
    public sealed record Aim(double Heading, double Elevation);

    public const double SideSwipeDegrees = 18;
    public const double MaxAimDistance = 50;

    readonly string? _storePath;
    readonly Dictionary<string, Aim> _aims;
    readonly List<string> _teachingQueue = new();
    readonly object _gate = new();

    public FlickScreenPicker(string? storePath)
    {
        _storePath = storePath;
        _aims = Load(storePath);
    }

    /// <summary>Raised when teaching advances: the key of the next screen to teach, or null when done/cancelled.</summary>
    public event Action<string?, int, int>? TeachingStepChanged;

    public bool IsTeaching { get { lock (_gate) return _teachingQueue.Count > 0; } }
    public string? TeachingScreen { get { lock (_gate) return _teachingQueue.FirstOrDefault(); } }

    public int TaughtCount(IEnumerable<FlickScreen> screens)
    {
        lock (_gate) return screens.Count(s => _aims.ContainsKey(s.Key));
    }

    public IReadOnlyDictionary<string, Aim> Aims { get { lock (_gate) return new Dictionary<string, Aim>(_aims); } }

    public void StartTeaching(IReadOnlyList<FlickScreen> screens)
    {
        if (screens.Count < 2) return; // nothing to choose between
        lock (_gate)
        {
            _teachingQueue.Clear();
            _teachingQueue.AddRange(screens.Select(s => s.Key));
        }
        TeachingStepChanged?.Invoke(TeachingScreen, 1, screens.Count);
    }

    public void CancelTeaching()
    {
        lock (_gate) _teachingQueue.Clear();
        TeachingStepChanged?.Invoke(null, 0, 0);
    }

    public void ForgetAims()
    {
        lock (_gate) _aims.Clear();
        Save();
    }

    public FlickScreen? Pick(IReadOnlyList<FlickScreen> screens, double? heading, double? elevation, double swipeAngle,
        double pointerX, double pointerY)
    {
        if (screens.Count == 0) return null;
        var pointerScreen = screens.FirstOrDefault(s => s.Contains(pointerX, pointerY)) ?? screens[0];
        if (screens.Count == 1) return screens[0];

        // 1. Teaching: this flick belongs to the screen being taught.
        string? teachKey = null;
        int remaining = 0;
        lock (_gate)
        {
            if (_teachingQueue.Count > 0)
            {
                teachKey = _teachingQueue[0];
                _teachingQueue.RemoveAt(0);
                remaining = _teachingQueue.Count;
                if (heading is { } h) _aims[teachKey] = new Aim(Normalize(h), elevation ?? 0);
            }
        }
        if (teachKey is not null)
        {
            Save();
            var next = TeachingScreen;
            TeachingStepChanged?.Invoke(next, next is null ? 0 : screens.Count - remaining + 1, next is null ? 0 : screens.Count);
            var taught = screens.FirstOrDefault(s => s.Key == teachKey);
            if (taught is not null) return taught;
        }

        // 2. Aim: nearest taught screen.
        if (heading is { } hh)
        {
            var here = new Aim(Normalize(hh), elevation ?? 0);
            List<(FlickScreen s, double d)> scored;
            lock (_gate)
                scored = screens.Where(s => _aims.ContainsKey(s.Key)).Select(s => (s, Distance(here, _aims[s.Key]))).ToList();
            if (scored.Count >= 2)
            {
                var best = scored.MinBy(x => x.d);
                if (best.d <= MaxAimDistance) return best.s;
            }
        }

        // 3. Swipe direction relative to the pointer's screen.
        if (swipeAngle <= -SideSwipeDegrees) return Neighbour(screens, pointerScreen, toLeft: true) ?? pointerScreen;
        if (swipeAngle >= SideSwipeDegrees) return Neighbour(screens, pointerScreen, toLeft: false) ?? pointerScreen;
        return pointerScreen;
    }

    static FlickScreen? Neighbour(IReadOnlyList<FlickScreen> screens, FlickScreen b, bool toLeft)
        => screens.Where(s => s != b && (toLeft ? s.MidX < b.MidX : s.MidX > b.MidX))
            .OrderBy(s => Math.Abs(s.MidX - b.MidX) + Math.Abs(s.MidY - b.MidY) * 0.5)
            .FirstOrDefault();

    public static double Normalize(double degrees)
    {
        var r = degrees % 360;
        return r < 0 ? r + 360 : r;
    }

    public static double Distance(Aim a, Aim b)
    {
        var dh = Math.Abs(a.Heading - b.Heading);
        if (dh > 180) dh = 360 - dh;
        var de = a.Elevation - b.Elevation;
        return Math.Sqrt(dh * dh + de * de);
    }

    static Dictionary<string, Aim> Load(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, Aim>>(File.ReadAllText(path)) ?? new();
        }
        catch { }
        return new();
    }

    void Save()
    {
        if (_storePath is null) return;
        try
        {
            string json;
            lock (_gate) json = JsonSerializer.Serialize(_aims);
            File.WriteAllText(_storePath, json);
        }
        catch { }
    }
}
