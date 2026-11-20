using System.Text;
using System.Text.RegularExpressions;

namespace HIB.Features;

/// <summary>
/// Removes secrets from log text before it reaches the log file or a peer: the pairing key, the space key,
/// API keys (Gemini, Anthropic) and anything that looks like a key or token. Defensive by design: it
/// would rather blank a harmless long base64 blob than leak a key.
/// </summary>
public static partial class LogScrubber
{
    public const string Redacted = "[redacted]";

    private static readonly object Gate = new();
    private static string[] _secrets = [];

    /// <summary>Exact values that must never be written (e.g. the pairing key text). Ignored when short.</summary>
    public static void AddSecret(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Trim().Length < 8) return;
        lock (Gate)
        {
            if (_secrets.Contains(secret)) return;
            _secrets = [.. _secrets, secret];
        }
    }

    // key=…, "key":"…", apiKey: …, token=…, x-goog-api-key: …, Authorization: Bearer …
    [GeneratedRegex("""(?i)(\b(?:key|api[_-]?key|space[_-]?key|encryption[_-]?key|pairing[_-]?key|secret|token|access[_-]?token|password|x-goog-api-key|x-api-key|authorization)\b["']?\s*[:=]\s*["']?)(?:bearer\s+)?[^\s"'&,;}\]]+""")]
    private static partial Regex KeyValue();

    // Google API keys (Gemini).
    [GeneratedRegex(@"AIza[0-9A-Za-z\-_]{30,}")]
    private static partial Regex GoogleKey();

    // Anthropic / OpenAI style secret keys.
    [GeneratedRegex(@"\bsk-[A-Za-z0-9\-_]{16,}")]
    private static partial Regex SkKey();

    // Base64 blobs of 40+ characters: a 32-byte key is 44 characters ("…=").
    [GeneratedRegex(@"(?<![A-Za-z0-9+/])[A-Za-z0-9+/]{40,}={0,2}")]
    private static partial Regex Base64Blob();

    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        string[] secrets;
        lock (Gate) secrets = _secrets;
        foreach (var s in secrets)
            if (text.Contains(s, StringComparison.Ordinal)) text = text.Replace(s, Redacted, StringComparison.Ordinal);
        text = KeyValue().Replace(text, m => m.Groups[1].Value + Redacted);
        text = GoogleKey().Replace(text, Redacted);
        text = SkKey().Replace(text, Redacted);
        text = Base64Blob().Replace(text, m => LooksLikeKey(m.Value) ? Redacted : m.Value);
        return text;
    }

    /// <summary>Random base64 (a 32-byte key always ends in "=") rather than a path like /Users/name/Library/….</summary>
    private static bool LooksLikeKey(string s)
    {
        bool upper = false, lower = false, digitOrPad = s.EndsWith('=');
        foreach (var c in s)
        {
            if (char.IsAsciiLetterUpper(c)) upper = true;
            else if (char.IsAsciiLetterLower(c)) lower = true;
            else if (char.IsAsciiDigit(c) || c == '+') digitOrPad = true;
        }
        return upper && lower && digitOrPad;
    }
}

/// <summary>
/// An append-only, size-capped log file with one rotation: when the next write would pass
/// <see cref="MaxBytes"/>, <c>hib.log</c> becomes <c>hib.log.1</c> (replacing the old one) and a fresh
/// file starts. Every write is scrubbed (<see cref="LogScrubber"/>). Never throws.
/// </summary>
public sealed class RollingLogFile : IDisposable
{
    public const long DefaultMaxBytes = 2 * 1024 * 1024;

    private readonly object _gate = new();
    private FileStream? _stream;
    private bool _disposed;

    public RollingLogFile(string path, long maxBytes = DefaultMaxBytes)
    {
        FilePath = path;
        MaxBytes = Math.Max(1024, maxBytes);
    }

    public string FilePath { get; }
    public string RotatedPath => FilePath + ".1";
    public long MaxBytes { get; }

    /// <summary>Formats one entry: <c>2026-11-20 14:03:22.123 [scrcpy] text</c>; extra lines are indented.</summary>
    internal static string Format(DateTime at, string category, string text)
    {
        var sb = new StringBuilder();
        var stamp = at.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var first = true;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (first) sb.Append(stamp).Append(" [").Append(category).Append("] ").Append(line).Append('\n');
            else sb.Append("    ").Append(line).Append('\n');
            first = false;
        }
        return sb.ToString();
    }

    public void Write(string category, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var entry = Format(DateTime.Now, category, LogScrubber.Scrub(text));
        if (entry.Length == 0) return;
        var bytes = Encoding.UTF8.GetBytes(entry);
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                var stream = Open();
                if (stream is null) return;
                if (stream.Length > 0 && stream.Length + bytes.Length > MaxBytes)
                {
                    Rotate();
                    stream = Open();
                    if (stream is null) return;
                }
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
            catch
            {
                try { _stream?.Dispose(); } catch { }
                _stream = null;
            }
        }
    }

    private FileStream? Open()
    {
        if (_stream is not null) return _stream;
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        return _stream;
    }

    private void Rotate()
    {
        try { _stream?.Dispose(); } catch { }
        _stream = null;
        try
        {
            if (File.Exists(RotatedPath)) File.Delete(RotatedPath);
            File.Move(FilePath, RotatedPath);
        }
        catch
        {
            // Someone holds the file without FILE_SHARE_DELETE: copy it aside and start over.
            try { File.Copy(FilePath, RotatedPath, overwrite: true); } catch { }
            try { using var _ = new FileStream(FilePath, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); } catch { }
        }
    }

    /// <summary>The last <paramref name="maxBytes"/> of the log (reaching into <c>hib.log.1</c> when the
    /// current file is shorter), starting at a line boundary and scrubbed again.</summary>
    public string ReadTail(int maxBytes)
    {
        if (maxBytes <= 0) return "";
        byte[] current, older;
        lock (_gate)
        {
            try { _stream?.Flush(); } catch { }
            current = ReadLast(FilePath, maxBytes);
            older = current.Length < maxBytes ? ReadLast(RotatedPath, maxBytes - current.Length) : [];
        }
        var all = new byte[older.Length + current.Length];
        older.CopyTo(all, 0);
        current.CopyTo(all, older.Length);
        var text = Encoding.UTF8.GetString(all);
        var cut = all.Length >= maxBytes;
        if (cut)
        {
            // Drop the partial first line (and any half UTF-8 sequence with it).
            var nl = text.IndexOf('\n');
            if (nl >= 0) text = text[(nl + 1)..];
        }
        return LogScrubber.Scrub(text);
    }

    private static byte[] ReadLast(string path, int count)
    {
        try
        {
            if (count <= 0 || !File.Exists(path)) return [];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var take = (int)Math.Min(count, fs.Length);
            fs.Seek(-take, SeekOrigin.End);
            var buf = new byte[take];
            var read = 0;
            while (read < take)
            {
                var n = fs.Read(buf, read, take - read);
                if (n <= 0) break;
                read += n;
            }
            return read == take ? buf : buf[..read];
        }
        catch { return []; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            try { _stream?.Dispose(); } catch { }
            _stream = null;
        }
    }
}

/// <summary>
/// H.I.B's diagnostic log file: <c>%LOCALAPPDATA%\H.I.B\logs\hib.log</c> (or <c>$HIB_DATA_DIR/logs/hib.log</c>),
/// ≤ 2 MB with one rotation. It holds the ADB console, scrcpy command lines and output, and the app trace,
/// scrubbed of keys. Off until <see cref="Start"/> (tests never write the user's log).
/// </summary>
public static class HibLog
{
    public const int DiagnosticsTailBytes = 256 * 1024;

    private static RollingLogFile? _file;

    public static RollingLogFile? Current => Volatile.Read(ref _file);
    public static bool IsStarted => Current is not null;

    public static string DefaultPath()
    {
        var data = Environment.GetEnvironmentVariable("HIB_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(data)) return Path.Combine(data, "logs", "hib.log");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) local = Path.GetTempPath();
        return Path.Combine(local, "H.I.B", "logs", "hib.log");
    }

    public static RollingLogFile Start(string? path = null, long maxBytes = RollingLogFile.DefaultMaxBytes)
    {
        var file = new RollingLogFile(path ?? DefaultPath(), maxBytes);
        Interlocked.Exchange(ref _file, file)?.Dispose();
        return file;
    }

    public static void Stop() => Interlocked.Exchange(ref _file, null)?.Dispose();

    public static void Write(string category, string? text) => Current?.Write(category, text);

    /// <summary>The newest part of the log (empty when logging is off).</summary>
    public static string Tail(int maxBytes = DiagnosticsTailBytes) => Current?.ReadTail(maxBytes) ?? "";
}
