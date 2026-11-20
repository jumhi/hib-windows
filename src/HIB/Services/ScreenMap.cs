// H.I.B — this computer's monitors as Point & Flick names them (SPACE.md §7.1): the Avalonia Screen.DisplayName
// (e.g. \\.\DISPLAY1), else "x,y,wxh" of its bounds. One place for the ids so screensInfo, the aim light,
// the point target and flickStart.screen all agree.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using HIB.Core.Phone;

namespace HIB.Services;

public sealed partial class ScreenMap
{
    readonly Func<Window?> _anchor;

    public ScreenMap(Func<Window?> anchor) => _anchor = anchor;

    /// <summary>Tests: stand-in monitors (Avalonia's headless platform has a single 1920×1280 screen).</summary>
    public Func<IReadOnlyList<Screen>>? Override { get; set; }

    /// <summary>The live Avalonia screens (for <c>Screens.Changed</c>); null while overridden or without a window.</summary>
    public Screens? Live => Override is null ? _anchor()?.Screens : null;

    /// <summary>Every monitor (UI thread).</summary>
    public IReadOnlyList<Screen> All
    {
        get
        {
            if (Override is { } o) return o();
            try { return _anchor()?.Screens?.All ?? (IReadOnlyList<Screen>)Array.Empty<Screen>(); }
            catch { return Array.Empty<Screen>(); }
        }
    }

    public Screen? Primary
    {
        get
        {
            var all = All;
            return all.FirstOrDefault(s => s.IsPrimary) ?? all.FirstOrDefault();
        }
    }

    /// <summary>The monitors with their ids, names and DIP bounds, in Avalonia's order.</summary>
    public IReadOnlyList<(DisplayScreen Info, Screen Screen)> Current() => Describe(All);

    /// <summary>The monitor with exactly this id, or null.</summary>
    public Screen? Find(string? id)
        => string.IsNullOrEmpty(id) ? null : Current().FirstOrDefault(x => x.Info.Id == id).Screen;

    /// <summary>"x,y,wxh" of a monitor's pixel bounds (§7.1 fallback id).</summary>
    public static string BoundsId(PixelRect b) => $"{b.X},{b.Y},{b.Width}x{b.Height}";

    public static IReadOnlyList<(DisplayScreen Info, Screen Screen)> Describe(IReadOnlyList<Screen> screens)
    {
        var result = new List<(DisplayScreen, Screen)>(screens.Count);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var anyPrimary = screens.Any(s => s.IsPrimary);
        for (var i = 0; i < screens.Count; i++)
        {
            var s = screens[i];
            var display = s.DisplayName?.Trim();
            // Two identical monitors can report the same name: the second one falls back to its bounds.
            var id = !string.IsNullOrEmpty(display) && !used.Contains(display) ? display : BoundsId(s.Bounds);
            for (var n = 2; used.Contains(id); n++) id = $"{BoundsId(s.Bounds)}#{n}";
            used.Add(id);
            var scale = s.Scaling > 0 ? s.Scaling : 1;
            var b = s.Bounds;
            result.Add((new DisplayScreen(id, NameFor(display, i), s.IsPrimary || (!anyPrimary && i == 0),
                b.X / scale, b.Y / scale, b.Width / scale, b.Height / scale), s));
        }
        return result;
    }

    /// <summary>A friendly name: "Display 2" for a GDI device path, the monitor's own name otherwise.</summary>
    public static string NameFor(string? displayName, int index)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return $"Display {index + 1}";
        var m = DevicePath().Match(displayName);
        return m.Success ? $"Display {m.Groups[1].Value}" : displayName;
    }

    [GeneratedRegex(@"^\\\\\.\\DISPLAY(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DevicePath();
}
