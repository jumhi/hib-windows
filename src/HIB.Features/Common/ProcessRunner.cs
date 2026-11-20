using System.Diagnostics;
using System.Text;

namespace HIB.Features;

/// <summary>Result of a finished child process.</summary>
public sealed record ProcessResult(int ExitCode, byte[] StdOut, string StdErr, bool TimedOut)
{
    public string StdOutText => Encoding.UTF8.GetString(StdOut);
    /// <summary>stdout + stderr as text (what the Mac reads from a shared pipe).</summary>
    public string Combined => StdOutText + StdErr;
    public bool Success => ExitCode == 0 && !TimedOut;
}

/// <summary>Runs adb / scrcpy without a console window, with timeouts and argument quoting
/// handled by <see cref="ProcessStartInfo.ArgumentList"/>.</summary>
public static class ProcessRunner
{
    public static ProcessStartInfo CreateStartInfo(string fileName, IEnumerable<string> args,
                                                   IDictionary<string, string>? environment = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            // adb and scrcpy write UTF-8 (device names like "Haitham’s S26"); a GUI app has no console code page.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (environment != null)
            foreach (var (k, v) in environment) psi.Environment[k] = v;
        return psi;
    }

    /// <summary>Runs to completion (or kills it after <paramref name="timeout"/>).</summary>
    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args,
        TimeSpan? timeout = null, IDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var psi = CreateStartInfo(fileName, args, environment);
        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start()) return new ProcessResult(-1, [], "Process did not start.", false);
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, [], ex.Message, false);
        }
        try { process.StandardInput.Close(); } catch { }

        var outTask = ReadAllBytesAsync(process.StandardOutput.BaseStream);
        var errTask = process.StandardError.ReadToEndAsync();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } t) cts.CancelAfter(t);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            KillQuietly(process);
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
            if (cancellationToken.IsCancellationRequested) throw;
        }

        byte[] stdout;
        string stderr;
        try { stdout = await outTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { stdout = []; }
        try { stderr = await errTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { stderr = ""; }
        int code;
        try { code = process.HasExited ? process.ExitCode : -1; } catch { code = -1; }
        return new ProcessResult(timedOut ? -1 : code, stdout, stderr, timedOut);
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms).ConfigureAwait(false);
        return ms.ToArray();
    }

    internal static void KillQuietly(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }
}
