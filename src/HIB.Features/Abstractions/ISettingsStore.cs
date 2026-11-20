using System.Text.Json;
using System.Text.Json.Nodes;

namespace HIB.Features.Abstractions;

/// <summary>Small key/value preference store (the Mac's UserDefaults). Keys match the Mac app.</summary>
public interface ISettingsStore
{
    T? Get<T>(string key);
    bool Contains(string key);
    void Set<T>(string key, T value);
    void Remove(string key);
}

/// <summary>
/// Default store: one JSON file (settings.json in <see cref="HibPaths.DataDirectory"/>), written
/// atomically on every change. Thread-safe.
/// </summary>
public sealed class JsonFileSettingsStore : ISettingsStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private JsonObject _root;

    public JsonFileSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(HibPaths.DataDirectory, "settings.json");
        _root = Load(_path);
    }

    private static JsonObject Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject obj) return obj;
        }
        catch { /* corrupt file: start fresh */ }
        return new JsonObject();
    }

    public T? Get<T>(string key)
    {
        lock (_lock)
        {
            if (!_root.TryGetPropertyValue(key, out var node) || node is null) return default;
            try { return node.Deserialize<T>(); } catch { return default; }
        }
    }

    public bool Contains(string key)
    {
        lock (_lock) return _root.ContainsKey(key);
    }

    public void Set<T>(string key, T value)
    {
        lock (_lock)
        {
            _root[key] = JsonSerializer.SerializeToNode(value);
            Save();
        }
    }

    public void Remove(string key)
    {
        lock (_lock)
        {
            if (_root.Remove(key)) Save();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch { /* preferences are best effort */ }
    }
}

/// <summary>In-memory store for tests and previews.</summary>
public sealed class InMemorySettingsStore : ISettingsStore
{
    private readonly Dictionary<string, JsonNode?> _values = new();
    private readonly object _lock = new();

    public T? Get<T>(string key)
    {
        lock (_lock)
        {
            if (!_values.TryGetValue(key, out var node) || node is null) return default;
            try { return node.Deserialize<T>(); } catch { return default; }
        }
    }

    public bool Contains(string key) { lock (_lock) return _values.ContainsKey(key); }
    public void Set<T>(string key, T value) { lock (_lock) _values[key] = JsonSerializer.SerializeToNode(value); }
    public void Remove(string key) { lock (_lock) _values.Remove(key); }
}
