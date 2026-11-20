using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using SkiaSharp;

namespace HIB.Features.PhoneAgent;

/// <summary>What the model sees of the phone.</summary>
public sealed record PhoneAgentScreen(
    byte[] Jpeg,       // downscaled (max 1280 px long side), quality 0.7
    byte[] Thumbnail,  // 220 px tall, for the step timeline
    int Width,         // screenshot size the model sees
    int Height,
    double Scale,      // device pixels per screenshot pixel (>= 1)
    string UiTree);    // compact UI tree in screenshot coordinates (may be empty)

public sealed class PhoneAgentAdbException : Exception
{
    public PhoneAgentAdbException(string message) : base(message) { }

    public static PhoneAgentAdbException NotConnected() =>
        new("The phone isn't connected over ADB. Turn on Wireless Debugging (or plug in the cable) and connect from H.I.B.");
    public static PhoneAgentAdbException AdbMissing() => new("adb isn't installed on this PC.");
}

/// <summary>ADB primitives for the Phone Agent: screenshots, UI tree, input events, apps.</summary>
public sealed class PhoneAgentAdb
{
    public const int MaxLongSide = 1280;

    public PhoneAgentAdb(string adbPath, string serial)
    {
        AdbPath = adbPath;
        Serial = serial;
    }

    public string AdbPath { get; }
    public string Serial { get; }

    // MARK: Process

    public Task<ProcessResult> RunAsync(IEnumerable<string> args, double timeoutSeconds = 15, CancellationToken ct = default)
        => ProcessRunner.RunAsync(AdbPath, new[] { "-s", Serial }.Concat(args), TimeSpan.FromSeconds(timeoutSeconds), null, ct);

    public async Task<string> ShellAsync(IEnumerable<string> command, double timeoutSeconds = 15, CancellationToken ct = default)
    {
        var result = await RunAsync(new[] { "shell" }.Concat(command), timeoutSeconds, ct).ConfigureAwait(false);
        var output = result.StdOutText;
        if (result.ExitCode != 0)
        {
            var detail = (result.StdErr.Length == 0 ? output : result.StdErr).Trim();
            if (detail.Length > 300) detail = detail[..300];
            throw new PhoneAgentAdbException(detail.Length == 0 ? $"adb exited with code {result.ExitCode}" : detail);
        }
        return output;
    }

    // MARK: Screen

    public async Task<PhoneAgentScreen> CaptureScreenAsync(bool includeTree = true, CancellationToken ct = default)
    {
        var result = await RunAsync(["exec-out", "screencap", "-p"], 15, ct).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.StdOut.Length == 0)
            throw new PhoneAgentAdbException($"Couldn't take a screenshot ({result.StdErr.Trim()}). Is the phone unlocked?");
        var processed = ProcessScreenshot(result.StdOut)
            ?? throw new PhoneAgentAdbException($"Couldn't take a screenshot ({result.StdErr.Trim()}). Is the phone unlocked?");
        var tree = "";
        if (includeTree)
        {
            try { tree = await UiTreeAsync(processed.Scale, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { tree = ""; }
        }
        return processed with { UiTree = tree };
    }

    /// <summary>PNG → (JPEG ≤1280 px q0.7, 220 px thumbnail q0.6, size, scale). Null if not an image.</summary>
    public static PhoneAgentScreen? ProcessScreenshot(byte[] png)
    {
        SKBitmap? decoded;
        try { decoded = SKBitmap.Decode(png); } catch (ArgumentException) { decoded = null; }
        using var image = decoded;
        if (image is null || image.Width <= 0 || image.Height <= 0) return null;
        double deviceW = image.Width, deviceH = image.Height;
        var longSide = Math.Max(deviceW, deviceH);
        var factor = longSide > MaxLongSide ? MaxLongSide / longSide : 1;
        var w = Math.Max(1, (int)Math.Round(deviceW * factor, MidpointRounding.AwayFromZero));
        var h = Math.Max(1, (int)Math.Round(deviceH * factor, MidpointRounding.AwayFromZero));

        using var scaled = Resize(image, w, h);
        var jpeg = EncodeJpeg(scaled, 70) ?? throw new PhoneAgentAdbException("Couldn't encode the screenshot.");
        const int thumbH = 220;
        var thumbW = Math.Max(1, (int)((double)w * thumbH / h));
        byte[] thumb;
        using (var t = Resize(scaled, thumbW, thumbH)) thumb = EncodeJpeg(t, 60) ?? [];
        var scale = deviceW / w;
        return new PhoneAgentScreen(jpeg, thumb, w, h, scale, "");
    }

    private static SKBitmap Resize(SKBitmap source, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var dest = new SKBitmap(info);
        using var canvas = new SKCanvas(dest);
        canvas.Clear(SKColors.Black);
        using var img = SKImage.FromBitmap(source);
        canvas.DrawImage(img, new SKRect(0, 0, width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        canvas.Flush();
        return dest;
    }

    private static byte[]? EncodeJpeg(SKBitmap bitmap, int quality)
    {
        using var img = SKImage.FromBitmap(bitmap);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data?.ToArray();
    }

    // MARK: UI tree

    public async Task<string> UiTreeAsync(double scale, CancellationToken ct = default)
    {
        var xml = await ShellAsync(["uiautomator dump /sdcard/hib_ui.xml >/dev/null 2>&1 && cat /sdcard/hib_ui.xml"], 12, ct).ConfigureAwait(false);
        return CompactUiTree(xml, scale);
    }

    /// <summary>uiautomator XML → compact list (max 150 lines) in screenshot coordinates.
    /// Password fields never include their text.</summary>
    public static string CompactUiTree(string xml, double scale)
    {
        var start = xml.IndexOf("<?xml", StringComparison.Ordinal);
        if (start < 0) start = xml.IndexOf("<hierarchy", StringComparison.Ordinal);
        if (start < 0) return "";
        var lines = new List<string>();
        const int limit = 150;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml[start..]),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.Name != "node" || lines.Count >= limit) continue;
                string A(string name) => reader.GetAttribute(name) ?? "";
                var clickable = A("clickable") == "true" || A("long-clickable") == "true";
                var isPassword = A("password") == "true";
                var text = isPassword ? "" : A("text");
                var desc = A("content-desc");
                var cls = A("class");
                var editable = cls.Contains("EditText", StringComparison.Ordinal);
                if (!(clickable || editable || text.Length > 0 || desc.Length > 0)) continue;
                var bounds = reader.GetAttribute("bounds");
                if (bounds is null || ParseBounds(bounds) is not { } rect) continue;
                var s = Math.Max(scale, 0.0001);
                int x1 = (int)(rect.Item1 / s), y1 = (int)(rect.Item2 / s);
                int x2 = (int)(rect.Item3 / s), y2 = (int)(rect.Item4 / s);
                if (!(x2 > x1 && y2 > y1)) continue;
                var shortCls = cls.Split('.').LastOrDefault(p => p.Length > 0) ?? "";
                var parts = new List<string> { shortCls };
                if (text.Length > 0) parts.Add($"text=\"{Clip(text)}\"");
                if (desc.Length > 0) parts.Add($"desc=\"{Clip(desc)}\"");
                if (isPassword) parts.Add("password-field");
                var flags = new List<string>();
                if (clickable) flags.Add("clickable");
                if (editable) flags.Add("editable");
                if (A("checked") == "true") flags.Add("checked");
                if (A("selected") == "true") flags.Add("selected");
                if (A("focused") == "true") flags.Add("focused");
                if (flags.Count > 0) parts.Add(string.Join(",", flags));
                parts.Add($"center=({(x1 + x2) / 2},{(y1 + y2) / 2}) bounds=[{x1},{y1},{x2},{y2}]");
                lines.Add(string.Join(" ", parts));
            }
        }
        catch (XmlException) { /* keep what we have */ }
        return string.Join("\n", lines);
    }

    private static string Clip(string s)
    {
        var flat = s.Replace("\n", " ").Replace("\"", "'");
        var info = new StringInfo(flat);
        return info.LengthInTextElements > 80 ? info.SubstringByTextElements(0, 80) + "…" : flat;
    }

    /// <summary>"[x1,y1][x2,y2]".</summary>
    internal static (int, int, int, int)? ParseBounds(string s)
    {
        var nums = new List<int>();
        var sb = new StringBuilder();
        void Flush()
        {
            if (sb.Length > 0 && int.TryParse(sb.ToString(), out var v)) nums.Add(v);
            sb.Clear();
        }
        foreach (var c in s)
        {
            if (c == '-' || char.IsAsciiDigit(c)) sb.Append(c);
            else Flush();
        }
        Flush();
        if (nums.Count != 4) return null;
        return (nums[0], nums[1], nums[2], nums[3]);
    }

    // MARK: Input

    public Task TapAsync(int x, int y, CancellationToken ct = default)
        => ShellAsync(["input", "tap", Inv(x), Inv(y)], 15, ct);

    public Task SwipeAsync(int x1, int y1, int x2, int y2, int ms, CancellationToken ct = default)
        => ShellAsync(["input", "swipe", Inv(x1), Inv(y1), Inv(x2), Inv(y2), Inv(ms)], ms / 1000.0 + 10, ct);

    public Task KeyEventAsync(int code, CancellationToken ct = default)
        => ShellAsync(["input", "keyevent", Inv(code)], 15, ct);

    private static string Inv(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>The device-shell command used to type ASCII text, or null when the text needs the
    /// clipboard path. `input text` turns %s into a space; the whole argument is single-quoted.</summary>
    public static string? BuildTypeCommand(string text)
    {
        foreach (var r in text.EnumerateRunes())
            if (r.Value < 0x20 || r.Value >= 0x7F) return null;
        var encoded = text.Replace(" ", "%s");
        var quoted = "'" + encoded.Replace("'", "'\\''") + "'";
        return "input text " + quoted;
    }

    /// <summary>Types ASCII text with `input text`. Returns false when the text needs the clipboard path.</summary>
    public async Task<bool> TypeAsciiAsync(string text, CancellationToken ct = default)
    {
        var command = BuildTypeCommand(text);
        if (command is null) return false;
        await ShellAsync([command], 30, ct).ConfigureAwait(false);
        return true;
    }

    private static readonly Regex PackageRegex = new("^[A-Za-z0-9_.]+$", RegexOptions.Compiled);

    public async Task<string> OpenAppAsync(string package, CancellationToken ct = default)
    {
        if (!PackageRegex.IsMatch(package)) throw new PhoneAgentAdbException("Invalid package name.");
        var output = await ShellAsync(["monkey", "-p", package, "-c", "android.intent.category.LAUNCHER", "1"], 15, ct).ConfigureAwait(false);
        if (output.Contains("No activities found") || output.Contains("monkey aborted"))
            throw new PhoneAgentAdbException($"{package} has no launchable activity or isn't installed.");
        return $"Launched {package}.";
    }

    public async Task<List<string>> ThirdPartyPackagesAsync(CancellationToken ct = default)
    {
        string output;
        try { output = await ShellAsync(["pm", "list", "packages", "-3"], 15, ct).ConfigureAwait(false); }
        catch (PhoneAgentAdbException) { return []; }
        return output.Split('\n')
            .Select(l => l.Trim())
            .Where(s => s.StartsWith("package:", StringComparison.Ordinal))
            .Select(s => s["package:".Length..])
            .ToList();
    }
}
