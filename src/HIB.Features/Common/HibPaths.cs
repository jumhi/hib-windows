namespace HIB.Features;

/// <summary>Where H.I.B keeps its files on this machine.</summary>
public static class HibPaths
{
    private static string? _dataOverride;

    /// <summary>%APPDATA%\H.I.B on Windows, ~/Library/Application Support/H.I.B on macOS,
    /// $XDG_CONFIG_HOME/H.I.B (or ~/.config/H.I.B) elsewhere.</summary>
    public static string DataDirectory
    {
        get => _dataOverride ?? DefaultDataDirectory();
        set => _dataOverride = value;
    }

    private static string DefaultDataDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "H.I.B");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", "H.I.B");
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg, "H.I.B");
    }

    /// <summary>~/Downloads/H.I.B (phone search fetches land here, like the Mac).</summary>
    public static string DownloadsDirectory
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Downloads", "H.I.B");
        }
    }
}
