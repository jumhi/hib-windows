using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using HIB.Core.Link;

namespace HIB.Core.Pairing;

/// <summary>Where H.I.B keeps its data: %APPDATA%\HIB on Windows (override with HIB_DATA_DIR).</summary>
public static class AppPaths
{
    public static string DataDir { get; private set; } = ResolveDefault();

    static string ResolveDefault()
    {
        var env = Environment.GetEnvironmentVariable("HIB_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HIB");
    }

    public static void Override(string dir) => DataDir = dir;

    public static string Ensure(string? sub = null)
    {
        var dir = sub is null ? DataDir : Path.Combine(DataDir, sub);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string DownloadsDir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("HIB_DOWNLOADS_DIR");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }
}

/// <summary>Persisted settings + pairing identity (key, desktop name, stable id).</summary>
public sealed class AppSettings
{
    // ---- identity (never change silently: the phone keys its pairing on the name) ----
    public string? EncryptionKey { get; set; }
    public string? DesktopName { get; set; }
    public string? StableId { get; set; }

    // ---- behaviour ----
    public bool ClipboardSync { get; set; } = true;
    public bool ClipboardImages { get; set; } = true;
    public bool NotificationToasts { get; set; } = true;
    public bool SilentToasts { get; set; } = false;
    public int ToastSeconds { get; set; } = 6;
    public bool CallPopups { get; set; } = true;
    public bool StartMinimized { get; set; } = false;
    public bool CloseToTray { get; set; } = true;
    public bool AutoOpenLinks { get; set; } = true;
    public bool Discovery { get; set; } = true;
    /// <summary>The Flick shelf: a slim pill at the top of the screen that opens into device bubbles while dragging files (Windows).</summary>
    public bool ShowFlickShelf { get; set; } = true;

    // ---- last phone (for wake-up / reconnect) ----
    public string? LastPhoneName { get; set; }
    public List<string> LastPhoneIps { get; set; } = new();

    // ---- app list (macInfo.savedAppPackages) ----
    public List<string> SavedAppPackages { get; set; } = new();

    [JsonIgnore] public string FilePath { get; private set; } = "";

    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    readonly object _gate = new();

    public static AppSettings Load(string? dataDir = null)
    {
        var dir = dataDir ?? AppPaths.Ensure();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "hib-desktop.json");
        AppSettings s;
        try
        {
            s = File.Exists(path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings()
                : new AppSettings();
        }
        catch
        {
            // Corrupt file: keep a backup and start fresh (a new key means re-pairing).
            try { File.Copy(path, path + ".bak", true); } catch { }
            s = new AppSettings();
        }
        s.FilePath = path;
        if (s.EnsureIdentity()) s.Save();
        return s;
    }

    /// <summary>Fills in key/name/id if missing. Returns true if anything changed.</summary>
    public bool EnsureIdentity()
    {
        var changed = false;
        if (LinkKeyBytes() is null)
        {
            EncryptionKey = Convert.ToBase64String(LinkCrypto.GenerateKey());
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(DesktopName))
        {
            DesktopName = DefaultDesktopName();
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(StableId))
        {
            StableId = Guid.NewGuid().ToString().ToUpperInvariant();
            changed = true;
        }
        return changed;
    }

    public static string DefaultDesktopName()
    {
        var machine = Environment.MachineName;
        if (string.IsNullOrWhiteSpace(machine)) machine = "Windows PC";
        // Strip a ".local" suffix some platforms report.
        if (machine.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) machine = machine[..^6];
        return machine;
    }

    public byte[]? LinkKeyBytes()
    {
        if (string.IsNullOrWhiteSpace(EncryptionKey)) return null;
        try
        {
            var b = Convert.FromBase64String(EncryptionKey);
            return b.Length == LinkCrypto.KeySize ? b : null;
        }
        catch (FormatException) { return null; }
    }

    /// <summary>Generates a new key. The phone must re-scan the QR afterwards.</summary>
    public void ResetKey()
    {
        EncryptionKey = Convert.ToBase64String(LinkCrypto.GenerateKey());
        Save();
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
                File.Move(tmp, FilePath, true);
            }
            catch { /* best effort */ }
        }
    }
}
