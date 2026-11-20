using System.Security.Cryptography;
using System.Text;

namespace HIB.Features.PhoneAgent;

/// <summary>
/// Per-provider API key storage. Windows: DPAPI (CurrentUser scope) encrypted blob in
/// %APPDATA%\H.I.B\keys\. Elsewhere: a plain file with mode 0600 in the same folder layout.
/// Keys are never logged or written to settings.
/// </summary>
public sealed class ApiKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("H.I.B Phone Agent API key v1");
    private readonly string _directory;

    public ApiKeyStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(HibPaths.DataDirectory, "keys");
    }

    private string PathFor(PhoneAgentProvider provider)
        => Path.Combine(_directory, provider == PhoneAgentProvider.Gemini ? "gemini-api-key" : "anthropic-api-key")
           + (OperatingSystem.IsWindows() ? ".dpapi" : ".key");

    public bool Save(string key, PhoneAgentProvider provider)
    {
        var trimmed = key.Trim();
        if (trimmed.Length == 0) return false;
        LogScrubber.AddSecret(trimmed);
        try
        {
            Directory.CreateDirectory(_directory);
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { }
            }
            var path = PathFor(provider);
            var tmp = path + ".tmp";
            var plain = Encoding.UTF8.GetBytes(trimmed);
            byte[] payload;
            if (OperatingSystem.IsWindows())
                payload = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            else
                payload = plain;

            if (OperatingSystem.IsWindows())
            {
                File.WriteAllBytes(tmp, payload);
            }
            else
            {
                // Create with 0600 before any bytes are written.
                using (var fs = new FileStream(tmp, new FileStreamOptions
                {
                    Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                }))
                {
                    fs.Write(payload);
                }
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            File.Move(tmp, path, overwrite: true);
            CryptographicOperations.ZeroMemory(plain);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public string? Load(PhoneAgentProvider provider)
    {
        try
        {
            var path = PathFor(provider);
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            byte[] plain = OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser)
                : bytes;
            var key = Encoding.UTF8.GetString(plain).Trim();
            LogScrubber.AddSecret(key); // never in hib.log
            return key.Length == 0 ? null : key;
        }
        catch
        {
            return null;
        }
    }

    public void Delete(PhoneAgentProvider provider)
    {
        try { File.Delete(PathFor(provider)); } catch { }
    }

    public bool HasKey(PhoneAgentProvider provider) => Load(provider) != null;
}
