// H.I.B Space (docs/SPACE.md §3.2): the space key is a secret. Windows: DPAPI (CurrentUser);
// elsewhere (tests, dev runs on a Mac): a 0600 file in the data folder. Never logged.
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using HIB.Core.Link;

namespace HIB.Core.Space;

public sealed class SpaceKeyStore
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("H.I.B space key v1");
    readonly string _path;
    readonly object _gate = new();
    byte[]? _cached;
    bool _loaded;

    public SpaceKeyStore(string dataDir)
    {
        _path = Path.Combine(dataDir, OperatingSystem.IsWindows() ? "space-key.dpapi" : "space-key.bin");
    }

    /// <summary>The stored 32-byte key (a copy), or null when the phone never sent one.</summary>
    public byte[]? Load()
    {
        lock (_gate)
        {
            if (!_loaded)
            {
                _loaded = true;
                _cached = ReadFile();
                if (_cached is not null) HIB.Features.LogScrubber.AddSecret(Convert.ToBase64String(_cached));
            }
            return _cached is null ? null : (byte[])_cached.Clone();
        }
    }

    public bool HasKey { get { lock (_gate) return Load() is not null; } }

    /// <summary>Stores <paramref name="key"/>. Returns true when it differs from the stored key.</summary>
    public bool Save(byte[] key)
    {
        if (key.Length != LinkCrypto.KeySize) throw new ArgumentException("A space key is 32 bytes", nameof(key));
        lock (_gate)
        {
            var current = Load();
            if (current is not null && CryptographicOperations.FixedTimeEquals(current, key)) return false;
            _cached = (byte[])key.Clone();
            _loaded = true;
            HIB.Features.LogScrubber.AddSecret(Convert.ToBase64String(key)); // never in hib.log
            WriteFile(key);
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _cached = null;
            _loaded = true;
            try { File.Delete(_path); } catch { }
        }
    }

    byte[]? ReadFile()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var bytes = File.ReadAllBytes(_path);
            var plain = OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser)
                : bytes;
            return plain.Length == LinkCrypto.KeySize ? plain : null;
        }
        catch { return null; }
    }

    void WriteFile(byte[] key)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllBytes(tmp, ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser));
            }
            else
            {
                using (var fs = new FileStream(tmp, new FileStreamOptions
                {
                    Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                }))
                {
                    fs.Write(key);
                }
            }
            File.Move(tmp, _path, overwrite: true);
        }
        catch { /* keep the in-memory key for this run */ }
    }
}
