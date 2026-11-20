// H.I.B Space (docs/SPACE.md §3.3): the computers this one knows about, remembered across runs so
// computers link up directly at start even without the phone.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HIB.Core.Space;

/// <summary>A computer known from the phone's <c>desktopsList</c> or from a <c>peerHello</c>.</summary>
public sealed record KnownPeer
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Platform { get; init; } = "mac";
    public string Type { get; init; } = "";
    public List<string> Ips { get; init; } = new();
    public int Port { get; init; }
    public DateTimeOffset LastSeen { get; init; }
}

/// <summary>Persisted list of known peers plus the last "Send to" target (space.json in the data folder).</summary>
public sealed class SpacePeerStore
{
    sealed class FileModel
    {
        public List<KnownPeer> Peers { get; set; } = new();
        public string? LastTarget { get; set; }
    }

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    readonly string _path;
    readonly object _gate = new();
    readonly Dictionary<string, KnownPeer> _peers = new(StringComparer.Ordinal);
    string? _lastTarget;

    public SpacePeerStore(string dataDir)
    {
        _path = Path.Combine(dataDir, "space.json");
        try
        {
            if (File.Exists(_path))
            {
                var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path), Json);
                if (model is not null)
                {
                    foreach (var p in model.Peers.Where(p => !string.IsNullOrWhiteSpace(p.Id))) _peers[p.Id] = p;
                    _lastTarget = model.LastTarget;
                }
            }
        }
        catch { /* start empty */ }
    }

    public IReadOnlyList<KnownPeer> All { get { lock (_gate) return _peers.Values.ToList(); } }

    public KnownPeer? Get(string id) { lock (_gate) return _peers.TryGetValue(id, out var p) ? p : null; }

    public string? LastTarget
    {
        get { lock (_gate) return _lastTarget; }
        set
        {
            lock (_gate)
            {
                if (_lastTarget == value) return;
                _lastTarget = value;
            }
            Save();
        }
    }

    /// <summary>
    /// Adds or refreshes a peer. <paramref name="preferredIp"/> (the address that just worked) goes first.
    /// Returns true when something the dialler cares about changed.
    /// </summary>
    public bool Upsert(string id, string? name, string? platform, string? type, IEnumerable<string>? ips, int? port, string? preferredIp = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        bool changed, stale;
        lock (_gate)
        {
            _peers.TryGetValue(id, out var old);
            var merged = new List<string>();
            void Add(string? ip)
            {
                ip = ip?.Trim();
                if (string.IsNullOrEmpty(ip) || ip == "?" || merged.Contains(ip)) return;
                merged.Add(ip);
            }
            Add(preferredIp);
            foreach (var ip in ips ?? Array.Empty<string>()) Add(ip);
            foreach (var ip in old?.Ips ?? new List<string>()) Add(ip);
            if (merged.Count > 8) merged.RemoveRange(8, merged.Count - 8);
            var updated = new KnownPeer
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(name) ? old?.Name ?? id : name!,
                Platform = string.IsNullOrWhiteSpace(platform) ? old?.Platform ?? "mac" : platform!.ToLowerInvariant(),
                Type = string.IsNullOrWhiteSpace(type) ? old?.Type ?? "" : type!,
                Ips = merged,
                Port = port is > 0 and < 65536 ? port.Value : old?.Port ?? 0,
                LastSeen = DateTimeOffset.Now,
            };
            changed = old is null || old.Port != updated.Port || !old.Ips.SequenceEqual(updated.Ips)
                      || old.Name != updated.Name || old.Platform != updated.Platform || old.Type != updated.Type;
            stale = old is null || updated.LastSeen - old.LastSeen > TimeSpan.FromMinutes(10);
            _peers[id] = changed || stale ? updated : updated with { LastSeen = old!.LastSeen };
        }
        if (changed || stale) Save();
        return changed;
    }

    public void Remove(string id)
    {
        lock (_gate) { if (!_peers.Remove(id)) return; }
        Save();
    }

    public void Clear()
    {
        lock (_gate) _peers.Clear();
        Save();
    }

    void Save()
    {
        FileModel model;
        lock (_gate) model = new FileModel { Peers = _peers.Values.OrderBy(p => p.Id, StringComparer.Ordinal).ToList(), LastTarget = _lastTarget };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(model, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch { /* best effort */ }
    }
}
