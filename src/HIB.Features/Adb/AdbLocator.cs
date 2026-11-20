namespace HIB.Features.Adb;

/// <summary>
/// Finds adb and scrcpy. Order: the bundled <c>tools\</c> folder next to the app, then PATH, then
/// well-known install locations (Android SDK, winget, scoop, Chocolatey, Program Files; Homebrew
/// and /usr/local on macOS). Results are cached; call <see cref="Refresh"/> after the user installs
/// something.
/// </summary>
public sealed class AdbLocator
{
    public static AdbLocator Shared { get; } = new();

    private readonly object _lock = new();
    private string? _adb;
    private string? _scrcpy;
    private bool _adbSearched;
    private bool _scrcpySearched;

    /// <summary>Extra directories the app wants searched first (e.g. a user-picked folder).</summary>
    public List<string> ExtraDirectories { get; } = new();

    /// <summary>Folder searched before anything else (default: &lt;app dir&gt;/tools).</summary>
    public string BundledToolsDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "tools");

    public string? AdbPath
    {
        get
        {
            lock (_lock)
            {
                if (!_adbSearched) { _adb = Find("adb", AdbFallbackPaths()); _adbSearched = true; }
                return _adb;
            }
        }
    }

    public string? ScrcpyPath
    {
        get
        {
            lock (_lock)
            {
                if (!_scrcpySearched) { _scrcpy = Find("scrcpy", ScrcpyFallbackPaths()); _scrcpySearched = true; }
                return _scrcpy;
            }
        }
    }

    public void Refresh()
    {
        lock (_lock) { _adbSearched = false; _scrcpySearched = false; _adb = null; _scrcpy = null; }
    }

    /// <summary>
    /// Environment for launching scrcpy: <c>PATH=&lt;adbDir&gt;;$PATH</c> and <c>ADB=&lt;adbPath&gt;</c>
    /// (the Mac's PhoneADBTarget.adbEnvironment), so scrcpy uses the same adb server we do.
    /// </summary>
    public static Dictionary<string, string> AdbEnvironment(string adbPath)
    {
        var env = new Dictionary<string, string>();
        var dir = Path.GetDirectoryName(adbPath) ?? "";
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        env["PATH"] = dir + Path.PathSeparator + path;
        env["ADB"] = adbPath;
        return env;
    }

    private string? Find(string name, IEnumerable<string> fallbacks)
    {
        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;

        foreach (var dir in ExtraDirectories.Append(BundledToolsDirectory)
                     .Append(Path.Combine(BundledToolsDirectory, name)))
        {
            var candidate = Path.Combine(dir, exe);
            if (IsExecutable(candidate)) return Found(name, candidate, "bundled");
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try { candidate = Path.Combine(dir.Trim('"'), exe); } catch { continue; }
            if (IsExecutable(candidate)) return Found(name, candidate, "PATH");
        }

        foreach (var candidate in fallbacks)
        {
            if (IsExecutable(candidate)) return Found(name, candidate, "fallback path");
        }
        HibFeatures.Trace($"[adb-locator] {name} not found in tools, PATH or fallback locations.");
        return null;
    }

    private static string Found(string name, string path, string where)
    {
        HibFeatures.Trace($"[adb-locator] {name} found at {path} ({where}).");
        return path;
    }

    internal static bool IsExecutable(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (OperatingSystem.IsWindows()) return true;
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch { return false; }
    }

    private static IEnumerable<string> AdbFallbackPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var sdkRoots = new[]
            {
                Environment.GetEnvironmentVariable("ANDROID_HOME"),
                Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
                Path.Combine(local, "Android", "Sdk"),
            };
            foreach (var root in sdkRoots)
                if (!string.IsNullOrEmpty(root)) yield return Path.Combine(root, "platform-tools", "adb.exe");
            yield return Path.Combine(pf, "platform-tools", "adb.exe");
            yield return Path.Combine(pf86, "platform-tools", "adb.exe");
            yield return @"C:\platform-tools\adb.exe";
            yield return @"C:\Android\platform-tools\adb.exe";
            yield return Path.Combine(home, "scoop", "shims", "adb.exe");
            yield return @"C:\ProgramData\chocolatey\bin\adb.exe";
            // scrcpy's Windows zip ships its own adb.exe.
            foreach (var p in WingetScrcpyDirs(local)) yield return Path.Combine(p, "adb.exe");
            yield return Path.Combine(pf, "scrcpy", "adb.exe");
            yield break;
        }
        yield return Path.Combine(home, "Library", "Android", "sdk", "platform-tools", "adb");
        yield return "/opt/homebrew/bin/adb";
        yield return "/usr/local/bin/adb";
        yield return Path.Combine(home, "Android", "Sdk", "platform-tools", "adb");
        yield return "/usr/bin/adb";
    }

    private static IEnumerable<string> ScrcpyFallbackPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            foreach (var p in WingetScrcpyDirs(local)) yield return Path.Combine(p, "scrcpy.exe");
            yield return Path.Combine(local, "Microsoft", "WinGet", "Links", "scrcpy.exe");
            yield return Path.Combine(home, "scoop", "shims", "scrcpy.exe");
            yield return @"C:\ProgramData\chocolatey\bin\scrcpy.exe";
            yield return Path.Combine(pf, "scrcpy", "scrcpy.exe");
            yield return @"C:\scrcpy\scrcpy.exe";
            yield break;
        }
        yield return "/opt/scrcpy/scrcpy";
        yield return "/opt/homebrew/bin/scrcpy";
        yield return "/usr/local/bin/scrcpy";
        yield return "/usr/bin/scrcpy";
        yield return "/snap/bin/scrcpy";
    }

    /// <summary>winget installs scrcpy to %LOCALAPPDATA%\Microsoft\WinGet\Packages\Genymobile.scrcpy_*\scrcpy-win64-v*\.</summary>
    private static IEnumerable<string> WingetScrcpyDirs(string local)
    {
        var packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        string[] roots;
        try { roots = Directory.Exists(packages) ? Directory.GetDirectories(packages, "Genymobile.scrcpy*") : []; }
        catch { roots = []; }
        foreach (var root in roots)
        {
            string[] inner;
            try { inner = Directory.GetDirectories(root, "scrcpy-win64*"); } catch { inner = []; }
            foreach (var dir in inner.OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)) yield return dir;
            yield return root;
        }
    }
}
