using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace HIB.Features.PhoneAgent;

/// <summary>Claude (Messages API) client — the optional second provider. The API key is only placed in
/// the x-api-key header; never logged or persisted here.</summary>
public sealed class ClaudeClient : IDisposable
{
    public const string Model = "claude-opus-5-5";
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2026-10-21";
    private const string FallbackBeta = "server-side-fallback-2026-07-01";

    private readonly string _apiKey;
    private readonly HttpClient _http;
    /// <summary>Turned off for the rest of the session if the API rejects the fallback parameters.</summary>
    private bool _useFallbacks = true;

    internal double SleepScale { get; set; } = 1;

    public ClaudeClient(string apiKey, HttpMessageHandler? handler = null)
    {
        _apiKey = apiKey;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(300);
    }

    /// <summary>Sends a Messages request. <paramref name="body"/> must not contain `fallbacks`; it's added here.</summary>
    public async Task<JsonObject> SendAsync(JsonObject body, CancellationToken ct = default)
    {
        var attempt = 0;
        var triedWithoutFallbacks = false;
        while (true)
        {
            if (ct.IsCancellationRequested) throw PhoneAgentApiException.Cancelled();
            var payload = (JsonObject)body.DeepClone();
            if (_useFallbacks) payload["fallbacks"] = "default";
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
            if (_useFallbacks) request.Headers.TryAddWithoutValidation("anthropic-beta", FallbackBeta);

            HttpResponseMessage response;
            string text;
            try
            {
                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw PhoneAgentApiException.Cancelled();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                attempt++;
                if (attempt <= 3)
                {
                    await Sleep(Math.Pow(2, attempt), ct).ConfigureAwait(false);
                    continue;
                }
                throw new PhoneAgentApiException(PhoneAgentApiErrorKind.Network, "Claude", ex.Message);
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                JsonObject? json = null;
                try { json = JsonNode.Parse(text) as JsonObject; } catch { }
                if (status == 200)
                    return json ?? throw new PhoneAgentApiException(PhoneAgentApiErrorKind.Decoding, "Claude");

                var message = Json.Str((json?["error"] as JsonObject)?["message"]) ?? (text.Length > 300 ? text[..300] : text);
                if (message.Length == 0) message = "Unknown error";

                switch (status)
                {
                    case 401:
                        throw new PhoneAgentApiException(PhoneAgentApiErrorKind.InvalidKey, "Claude");
                    case 400:
                        if (_useFallbacks && !triedWithoutFallbacks && message.Contains("fallback", StringComparison.OrdinalIgnoreCase))
                        {
                            _useFallbacks = false;
                            triedWithoutFallbacks = true;
                            continue;
                        }
                        throw new PhoneAgentApiException(PhoneAgentApiErrorKind.BadRequest, "Claude", message);
                    case 429:
                    case 529:
                    case >= 500 and <= 599:
                        attempt++;
                        if (attempt > 5) throw new PhoneAgentApiException(PhoneAgentApiErrorKind.Http, "Claude", message, status);
                        var delay = Math.Min(Math.Pow(2, attempt), 60);
                        if (response.Headers.TryGetValues("retry-after", out var values)
                            && double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                            delay = Math.Min(Math.Max(seconds, 1), 120);
                        await Sleep(delay, ct).ConfigureAwait(false);
                        continue;
                    default:
                        throw new PhoneAgentApiException(PhoneAgentApiErrorKind.Http, "Claude", message, status);
                }
            }
        }
    }

    private async Task Sleep(double seconds, CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(seconds * SleepScale), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw PhoneAgentApiException.Cancelled(); }
    }

    /// <summary>Tiny request used by "Test key".</summary>
    public Task TestKeyAsync(CancellationToken ct = default) => SendAsync(new JsonObject
    {
        ["model"] = Model,
        ["max_tokens"] = 16,
        ["output_config"] = new JsonObject { ["effort"] = "low" },
        ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Reply with OK." }),
    }, ct);

    public void Dispose() => _http.Dispose();
}

/// <summary>Claude conversation state (PhoneAgentClaudeConversation).</summary>
public sealed class ClaudeConversation : IPhoneAgentModelClient
{
    private readonly ClaudeClient _client;
    private string _system = "";
    private JsonArray _tools = new();
    private readonly List<JsonObject> _messages = new();

    public ClaudeConversation(ClaudeClient client) { _client = client; }

    public string ProviderName => "Claude";

    public void Begin(string system, IReadOnlyList<PhoneAgentToolSpec> tools, string intro, PhoneAgentScreen screen, string tree)
    {
        _system = system;
        _tools = new JsonArray();
        foreach (var spec in tools)
        {
            var props = new JsonObject();
            foreach (var (k, v) in spec.Properties) props[k] = v.DeepClone();
            _tools.Add(new JsonObject
            {
                ["name"] = spec.Name,
                ["description"] = spec.Description,
                ["strict"] = true,
                ["input_schema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = props,
                    ["required"] = new JsonArray(spec.Properties.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => (JsonNode)k!).ToArray()),
                    ["additionalProperties"] = false,
                },
            });
        }
        _messages.Clear();
        _messages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = intro },
                ImageBlock(screen),
                new JsonObject { ["type"] = "text", ["text"] = tree }),
        });
    }

    internal JsonObject BuildRequestBody(PhoneAgentEffort effort) => new()
    {
        ["model"] = ClaudeClient.Model,
        ["max_tokens"] = 16000,
        ["system"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = _system, ["cache_control"] = new JsonObject { ["type"] = "ephemeral" } }),
        ["tools"] = _tools.DeepClone(),
        ["tool_choice"] = new JsonObject { ["type"] = "auto" },
        ["output_config"] = new JsonObject { ["effort"] = effort.Raw() },
        ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
        ["messages"] = new JsonArray(_messages.Select(m => (JsonNode)m.DeepClone()).ToArray()),
    };

    public async Task<PhoneAgentTurn> NextTurnAsync(PhoneAgentEffort effort, CancellationToken ct)
    {
        var response = await _client.SendAsync(BuildRequestBody(effort), ct).ConfigureAwait(false);
        var content = (response["content"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var stopReason = Json.Str(response["stop_reason"]) ?? "";
        // pause_turn: resend unchanged; otherwise append the assistant turn exactly as returned.
        if (stopReason != "pause_turn")
            _messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = response["content"]?.DeepClone() ?? new JsonArray() });

        var text = string.Join("\n", content.Where(b => Json.Str(b["type"]) == "text").Select(b => Json.Str(b["text"])).Where(s => s != null)).Trim();
        var calls = new List<PhoneAgentToolCall>();
        foreach (var block in content)
        {
            if (Json.Str(block["type"]) != "tool_use" || Json.Str(block["id"]) is not { } id || Json.Str(block["name"]) is not { } name) continue;
            calls.Add(new PhoneAgentToolCall(id, name, (JsonObject?)(block["input"] as JsonObject)?.DeepClone() ?? new JsonObject()));
        }

        var end = stopReason switch
        {
            "tool_use" => PhoneAgentTurnEnd.ToolUse,
            "end_turn" => PhoneAgentTurnEnd.EndTurn,
            "max_tokens" => PhoneAgentTurnEnd.MaxTokens,
            "pause_turn" => PhoneAgentTurnEnd.Pause,
            "refusal" => PhoneAgentTurnEnd.Refused(Json.Str((response["stop_details"] as JsonObject)?["explanation"])
                                                    ?? (text.Length == 0 ? "Claude declined this request." : text)),
            _ => PhoneAgentTurnEnd.Other("Unexpected stop reason: " + stopReason),
        };
        return new PhoneAgentTurn(text, calls, end, PhoneAgentUsage.FromClaude(response["usage"] as JsonObject));
    }

    public void AddToolResults(IReadOnlyList<PhoneAgentToolReply> replies, Func<PhoneAgentScreen, string> tree)
    {
        var blocks = new JsonArray();
        foreach (var reply in replies)
        {
            var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = reply.Text });
            if (reply.Screen is { } screen)
            {
                content.Add(ImageBlock(screen));
                content.Add(new JsonObject { ["type"] = "text", ["text"] = tree(screen) });
            }
            if (reply.Note != null) content.Add(new JsonObject { ["type"] = "text", ["text"] = reply.Note });
            var block = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = reply.Call.Id, ["content"] = content };
            if (reply.IsError) block["is_error"] = true;
            blocks.Add(block);
        }
        _messages.Add(new JsonObject { ["role"] = "user", ["content"] = blocks });
    }

    public void AddNudge(IReadOnlyList<PhoneAgentToolCall> calls, string message)
    {
        var blocks = new JsonArray();
        foreach (var call in calls)
            blocks.Add(new JsonObject
            {
                ["type"] = "tool_result", ["tool_use_id"] = call.Id, ["is_error"] = true,
                ["content"] = "Your response was cut off before this call finished. Re-issue it if still needed.",
            });
        blocks.Add(new JsonObject { ["type"] = "text", ["text"] = message });
        _messages.Add(new JsonObject { ["role"] = "user", ["content"] = blocks });
    }

    private static JsonObject ImageBlock(PhoneAgentScreen screen) => new()
    {
        ["type"] = "image",
        ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/jpeg", ["data"] = Convert.ToBase64String(screen.Jpeg) },
    };
}
