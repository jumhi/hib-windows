namespace HIB.Features.Camera;

public enum CameraFacing { Front, Back }

public enum CameraQuality { Auto, Hd720, Hd1080, Uhd4k, Ar16x9, Ar4x3 }

/// <summary>Options for the wireless "H.I.B Camera" window (scrcpy <c>--video-source=camera</c>).
/// Mirrors the Mac's <c>PhoneCameraOptions</c>.</summary>
public sealed record PhoneCameraOptions
{
    public const string WindowTitle = "H.I.B Camera";
    public static readonly int[] FpsChoices = [15, 24, 30, 60];
    public static readonly int[] RotationChoices = [0, 90, 180, 270];

    public CameraFacing Facing { get; init; } = CameraFacing.Front;
    public CameraQuality Quality { get; init; } = CameraQuality.Hd1080;
    public int Fps { get; init; } = 30;
    public bool WithMicAudio { get; init; }
    public bool Borderless { get; init; } = true;
    public bool AlwaysOnTop { get; init; }
    public int Rotation { get; init; }

    public static string QualityLabel(CameraQuality q) => q switch
    {
        CameraQuality.Auto => "Automatic",
        CameraQuality.Hd720 => "720p (1280×720)",
        CameraQuality.Hd1080 => "1080p (1920×1080)",
        CameraQuality.Uhd4k => "4K (3840×2160)",
        CameraQuality.Ar16x9 => "Best 16:9",
        _ => "Best 4:3",
    };

    public List<string> Arguments(string serial)
    {
        var args = new List<string>
        {
            "-s", serial,
            "--video-source=camera",
            $"--camera-facing={(Facing == CameraFacing.Front ? "front" : "back")}",
            "--no-control",
            $"--window-title={WindowTitle}",
            "--video-codec=h264",
            "--video-bit-rate=8M",
        };
        switch (Quality)
        {
            case CameraQuality.Hd720: args.Add("--camera-size=1280x720"); break;
            case CameraQuality.Hd1080: args.Add("--camera-size=1920x1080"); break;
            case CameraQuality.Uhd4k: args.Add("--camera-size=3840x2160"); break;
            case CameraQuality.Ar16x9: args.Add("--camera-ar=16:9"); break;
            case CameraQuality.Ar4x3: args.Add("--camera-ar=4:3"); break;
        }
        args.Add($"--camera-fps={(FpsChoices.Contains(Fps) ? Fps : 30)}");
        if (WithMicAudio) { args.Add("--audio-source=mic"); args.Add("--audio-codec=opus"); }
        else args.Add("--no-audio");
        if (Borderless) args.Add("--window-borderless");
        if (AlwaysOnTop) args.Add("--always-on-top");
        if (Rotation != 0 && RotationChoices.Contains(Rotation)) args.Add($"--orientation={Rotation}");
        return args;
    }

    public static List<string> ListCamerasArguments(string serial) => ["-s", serial, "--list-cameras"];
}

/// <summary>Android 14+ USB webcam (UVC gadget) helpers.</summary>
public static class UsbWebcam
{
    public static readonly string[] UvcPropArguments = ["shell", "getprop", "ro.usb.uvc.enabled"];
    public static readonly string[] GetFunctionsArguments = ["shell", "svc", "usb", "getFunctions"];
    static readonly string[] Restorable = ["mtp", "ptp", "rndis", "midi", "ncm"];

    public static bool IsUvcSupported(string? prop) => string.Equals((prop ?? "").Trim(), "true", StringComparison.OrdinalIgnoreCase);

    public static List<string> ParseFunctions(string? output)
    {
        var line = (output ?? "").Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0 && !l.StartsWith("error", StringComparison.OrdinalIgnoreCase)) ?? "";
        return line.Split(',').Select(f => f.Trim().ToLowerInvariant()).Where(f => f.Length > 0).ToList();
    }

    public static bool IsWebcamActive(IEnumerable<string> functions) => functions.Contains("uvc");

    /// <summary>The previous standard USB function, else "" (charging).</summary>
    public static string RestoreFunction(IEnumerable<string> previous) => Restorable.FirstOrDefault(previous.Contains) ?? "";

    public static List<string> SetFunctionsArguments(string serial, string function)
    {
        var a = new List<string> { "-s", serial, "shell", "svc", "usb", "setFunctions" };
        if (function.Length > 0 && function.All(c => char.IsAsciiLetter(c) || c == ',')) a.Add(function);
        return a;
    }
}
