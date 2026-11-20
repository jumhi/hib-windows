// H.I.B — Explorer "Send to" (SPACE.md §5): one shortcut per device in %APPDATA%\Microsoft\Windows\SendTo
// named "H.I.B – <device name>" that runs `HIB.exe --send-to "<id>" <files…>`. A running H.I.B gets the
// request over a named pipe; otherwise H.I.B starts and sends once the device is reachable.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HIB.Services;

/// <summary>A "send these files to that device" request from the command line.</summary>
public sealed record SendToRequest(string DeviceId, IReadOnlyList<string> Files)
{
    /// <summary>The request this process was started with (sent once the device is reachable).</summary>
    public static SendToRequest? Pending { get; set; }

    /// <summary><c>--send-to "&lt;id&gt;" file1 file2 …</c>, anywhere in the arguments; null if absent.</summary>
    public static SendToRequest? Parse(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], "--send-to", StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 >= args.Count) return null;
            var id = args[i + 1];
            var files = args.Skip(i + 2).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).Select(Full).ToList();
            return string.IsNullOrWhiteSpace(id) ? null : new SendToRequest(id, files);
        }
        return null;
    }

    static string Full(string p)
    {
        try { return Path.GetFullPath(p); } catch { return p; }
    }

    public string ToJson() => JsonSerializer.Serialize(new { id = DeviceId, files = Files });

    public static SendToRequest? FromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var id = doc.RootElement.GetProperty("id").GetString();
            var files = doc.RootElement.GetProperty("files").EnumerateArray().Select(f => f.GetString()).Where(f => !string.IsNullOrEmpty(f)).Select(f => f!).ToList();
            return string.IsNullOrWhiteSpace(id) ? null : new SendToRequest(id, files);
        }
        catch { return null; }
    }
}

/// <summary>The per-user named pipe a running H.I.B listens on for <c>--send-to</c> requests.</summary>
public static class SendToPipe
{
    /// <summary>Per user; test instances (HIB_PORT) get their own.</summary>
    public static string Name
    {
        get
        {
            var port = Environment.GetEnvironmentVariable("HIB_PORT");
            var user = new string(Environment.UserName.Where(char.IsLetterOrDigit).ToArray());
            return $"HIB-SendTo-{user}" + (string.IsNullOrEmpty(port) ? "" : "-" + port);
        }
    }

    /// <summary>Hands the request to a running H.I.B. False when none is listening.</summary>
    public static bool TryForward(SendToRequest request, int timeoutMs = 1200)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            var bytes = Encoding.UTF8.GetBytes(request.ToJson());
            client.Write(bytes);
            client.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Serves requests until <paramref name="ct"/> is cancelled.</summary>
    public static async Task ServeAsync(Action<SendToRequest> onRequest, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(Name, PipeDirection.In, 4, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var ms = new MemoryStream();
                using var read = CancellationTokenSource.CreateLinkedTokenSource(ct);
                read.CancelAfter(TimeSpan.FromSeconds(5));
                await server.CopyToAsync(ms, read.Token).ConfigureAwait(false);
                if (SendToRequest.FromJson(Encoding.UTF8.GetString(ms.ToArray())) is { } req) onRequest(req);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (IOException) { await Task.Delay(500, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { await Task.Delay(1000, CancellationToken.None).ConfigureAwait(false); }
            finally
            {
                try { server?.Dispose(); } catch { }
            }
        }
    }
}

/// <summary>Keeps the Explorer "Send to" shortcuts in step with the device list (Windows only).</summary>
public static class SendToShortcuts
{
    public const string Prefix = "H.I.B – ";

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static string? Folder => IsSupported
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "SendTo")
        : null;

    public static string FileNameFor(string deviceName)
    {
        var safe = new StringBuilder();
        foreach (var c in deviceName.Trim())
            safe.Append(Path.GetInvalidFileNameChars().Contains(c) || c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c);
        var name = safe.ToString().TrimEnd('.', ' ');
        return Prefix + (name.Length == 0 ? "Device" : name) + ".lnk";
    }

    /// <summary>Arguments for one device: <c>--send-to "&lt;id&gt;"</c> (Explorer appends the selected files).</summary>
    public static string ArgumentsFor(string deviceId) => $"--send-to \"{deviceId.Replace("\"", "\\\"")}\"";

    static string _lastSignature = "";

    /// <summary>Creates/updates one shortcut per device and removes stale "H.I.B – …" ones. Returns false on failure.</summary>
    public static bool Sync(IReadOnlyList<(string Id, string Name)> devices, string exePath)
    {
        if (!IsSupported || Folder is not { } folder) return false;
        var signature = exePath + "|" + string.Join("|", devices.Select(d => d.Id + "=" + d.Name));
        if (signature == _lastSignature) return true;
        try
        {
            Directory.CreateDirectory(folder);
            var wanted = new Dictionary<string, (string Id, string Name)>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in devices) wanted[FileNameFor(d.Name)] = d;
            foreach (var existing in Directory.GetFiles(folder, Prefix + "*.lnk"))
            {
                if (!wanted.ContainsKey(Path.GetFileName(existing)))
                    try { File.Delete(existing); } catch { }
            }
            foreach (var (file, d) in wanted)
                CreateShortcut(Path.Combine(folder, file), exePath, ArgumentsFor(d.Id), $"Send with H.I.B to {d.Name}");
            _lastSignature = signature;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Removes every H.I.B "Send to" shortcut.</summary>
    public static void RemoveAll()
    {
        if (Folder is not { } folder || !Directory.Exists(folder)) return;
        foreach (var f in Directory.GetFiles(folder, Prefix + "*.lnk"))
            try { File.Delete(f); } catch { }
        _lastSignature = "";
    }

    static void CreateShortcut(string path, string target, string args, string description)
    {
        if (!OperatingSystem.IsWindows()) return;
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetArguments(args);
            link.SetDescription(description);
            link.SetIconLocation(target, 0);
            link.SetWorkingDirectory(Path.GetDirectoryName(target) ?? "");
            ((IPersistFile)link).Save(path, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
