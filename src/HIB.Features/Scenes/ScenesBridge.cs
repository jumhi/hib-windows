using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using HIB.Features.Abstractions;

namespace HIB.Features.Scenes;

public sealed class SceneActionException(string message) : Exception(message);

/// <summary>A scene as shown on the phone's Scenes tile.</summary>
public sealed record PhoneSceneInfo(string Id, string Name, string Icon);

/// <summary>A <c>phoneEvent</c> from the phone (headset / wifi / charging / battery), raw data included.</summary>
public sealed record PhoneEvent(string Event, JsonElement Data);

/// <summary>
/// The protocol side of Scenes (§6.18), ported from SceneActionRunner.swift / SceneEngine.swift.
/// The desktop scene engine itself (triggers, editor, PC actions) is app logic; this class gives it:
/// <list type="bullet">
/// <item><see cref="SendPhoneActionAsync"/>: <c>phoneAction</c> → awaits the matching <c>phoneActionResult</c> (10 s timeout).</item>
/// <item><see cref="PublishScenesAsync"/>: <c>scenesList</c> for the phone tile.</item>
/// <item><see cref="RunSceneRequested"/> for <c>runScene</c>, answered with <see cref="ReportSceneResultAsync"/>.</item>
/// <item><see cref="PhoneEventReceived"/> for <c>phoneEvent</c> (triggers fire only on change after the first sample per connection).</item>
/// </list>
/// </summary>
public sealed class ScenesBridge : IDisposable
{
    private readonly IPhoneLink _link;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new();

    public ScenesBridge(IPhoneLink link)
    {
        _link = link;
        _link.MessageReceived += OnMessage;
    }

    /// <summary>The phone asked to run scene <c>id</c>.</summary>
    public event Action<string>? RunSceneRequested;
    public event Action<PhoneEvent>? PhoneEventReceived;

    // Phone actions -----------------------------------------------------------------------

    public Task<string> SetDoNotDisturbAsync(bool enabled) => SendPhoneActionAsync("dnd", new JsonObject { ["enabled"] = enabled });
    /// <summary>mode: silent | vibrate | normal.</summary>
    public Task<string> SetRingerAsync(string mode) => SendPhoneActionAsync("ringer", new JsonObject { ["mode"] = mode });
    public Task<string> SetMediaVolumeAsync(int percent) => SendPhoneActionAsync("volume", new JsonObject { ["stream"] = "media", ["percent"] = Math.Clamp(percent, 0, 100) });
    public Task<string> OpenAppAsync(string package) => SendPhoneActionAsync("openApp", new JsonObject { ["package"] = package });
    public Task<string> NotifyAsync(string title, string body)
        => SendPhoneActionAsync("notify", new JsonObject { ["title"] = title.Length == 0 ? "H.I.B" : title, ["body"] = body });

    /// <summary>Sends <c>phoneAction {action, requestId, …fields}</c> and awaits the result message.
    /// Throws <see cref="SceneActionException"/> on failure or timeout.</summary>
    public async Task<string> SendPhoneActionAsync(string action, JsonObject fields, TimeSpan? timeout = null)
    {
        if (!_link.IsConnected) throw new SceneActionException("Phone isn't connected.");
        var requestId = Guid.NewGuid().ToString().ToUpperInvariant();
        var data = (JsonObject)fields.DeepClone();
        data["action"] = action;
        data["requestId"] = requestId;
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;
        await _link.SendAsync("phoneAction", data).ConfigureAwait(false);
        var finished = await Task.WhenAny(tcs.Task, Task.Delay(timeout ?? TimeSpan.FromSeconds(10))).ConfigureAwait(false);
        if (finished != tcs.Task && _pending.TryRemove(requestId, out _))
            throw new SceneActionException("Phone didn't answer (is H.I.B up to date on the phone?).");
        return await tcs.Task.ConfigureAwait(false);
    }

    // Scenes list / results ---------------------------------------------------------------

    /// <summary>Only enabled scenes with "show on phone". Icons are SF Symbol names on the Mac; the phone maps them.</summary>
    public Task PublishScenesAsync(IEnumerable<PhoneSceneInfo> scenes)
    {
        if (!_link.IsConnected) return Task.CompletedTask;
        var arr = new JsonArray(scenes.Select(s => (JsonNode)new JsonObject { ["id"] = s.Id, ["name"] = s.Name, ["icon"] = s.Icon }).ToArray());
        return _link.SendAsync("scenesList", new JsonObject { ["scenes"] = arr });
    }

    public Task ReportSceneResultAsync(string id, bool ok, string message)
        => _link.IsConnected ? _link.SendAsync("sceneRunResult", new JsonObject { ["id"] = id, ["ok"] = ok, ["message"] = message }) : Task.CompletedTask;

    private void OnMessage(string type, JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return;
        switch (type)
        {
            case "phoneActionResult":
                if (!data.TryGetProperty("requestId", out var rid) || rid.ValueKind != JsonValueKind.String) return;
                if (!_pending.TryRemove(rid.GetString()!, out var tcs)) return;
                var ok = data.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
                string? Str(string k) => data.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (ok) tcs.TrySetResult(Str("message") ?? "Done on phone");
                else tcs.TrySetException(new SceneActionException(Str("error") ?? "Phone reported an error."));
                break;
            case "runScene":
                if (data.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    var sceneId = id.GetString()!;
                    HibFeatures.Dispatch(() => RunSceneRequested?.Invoke(sceneId));
                }
                break;
            case "phoneEvent":
                if (data.TryGetProperty("event", out var ev) && ev.ValueKind == JsonValueKind.String)
                {
                    var e = new PhoneEvent(ev.GetString()!, data.Clone());
                    HibFeatures.Dispatch(() => PhoneEventReceived?.Invoke(e));
                }
                break;
        }
    }

    public void Dispose() => _link.MessageReceived -= OnMessage;
}
