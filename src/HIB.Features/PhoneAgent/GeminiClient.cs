using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HIB.Features.PhoneAgent;

/// <summary>
/// Google Gemini provider for the Phone Agent: raw HTTPS to
/// <c>POST https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent</c>.
/// Faithful port of PhoneAgentGeminiClient.swift:
/// - the user's model first, then free-tier fallbacks, skipping models whose daily quota is used up;
/// - hedged requests: every 12 s of silence (or as soon as one fails) also ask the next model, first
///   good answer wins and becomes sticky for the rest of the run;
/// - per-model 40 s timeout; short 429s retried in place, long/daily 429s mark the model exhausted;
/// - when the conversation moves between models, thought signatures are replaced with the
///   validator-skip value.
/// The API key only ever goes in the x-goog-api-key header; it is never logged or persisted here.
/// </summary>
public sealed class GeminiClient : IDisposable
{
    internal const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";
    /// <summary>Gemini 3 accepts this in place of a thought signature from another model.</summary>
    internal const string SkipSignature = "skip_thought_signature_validator";

    private readonly string _apiKey;
    private readonly HttpClient _http;
    private readonly Action<string>? _onStatus;
    private readonly GeminiQuota _quota;
    private readonly object _lock = new();
    private string _activeModel;
    private bool _switched;

    /// <summary>How long to wait on one model before trying the next.</summary>
    internal TimeSpan PerModelTimeout { get; set; } = TimeSpan.FromSeconds(40);
    /// <summary>Seconds of silence before also asking the next model.</summary>
    internal TimeSpan HedgeDelay { get; set; } = TimeSpan.FromSeconds(12);
    /// <summary>Pause between the two passes over all models.</summary>
    internal TimeSpan PassRetryDelay { get; set; } = TimeSpan.FromSeconds(8);
    /// <summary>Multiplier for rate-limit / network backoff sleeps (tests shrink it).</summary>
    internal double SleepScale { get; set; } = 1;
    internal TimeSpan StatusTickInterval { get; set; } = TimeSpan.FromSeconds(3);

    public GeminiClient(string apiKey, string model, GeminiQuota quota, Action<string>? onStatus = null,
                        HttpMessageHandler? handler = null)
    {
        _apiKey = apiKey;
        Model = model;
        _activeModel = model;
        _quota = quota;
        _onStatus = onStatus;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    /// <summary>The model the user picked.</summary>
    public string Model { get; }

    /// <summary>The model currently answering (sticky for the rest of the run once one works).</summary>
    public string ActiveModel { get { lock (_lock) return _activeModel; } }

    /// <summary>The user's model first, then free-tier fallbacks, without duplicates; models marked
    /// used-up are skipped unless every model is marked (the marks may be stale).</summary>
    internal List<string> Candidates()
    {
        var seen = new HashSet<string>();
        var all = new[] { ActiveModel, Model }.Concat(PhoneAgentGeminiModel.Fallbacks).Where(seen.Add).ToList();
        var usable = all.Where(m => !_quota.IsExhausted(m)).ToList();
        return usable.Count == 0 ? all : usable;
    }

    private abstract record Attempt;
    private sealed record Success(JsonObject Json) : Attempt;
    private sealed record TryNext(string Problem) : Attempt;

    public async Task<JsonObject> GenerateAsync(JsonObject body, CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        using var tickerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(async () =>
        {
            try
            {
                while (!tickerCts.IsCancellationRequested)
                {
                    await Task.Delay(StatusTickInterval, tickerCts.Token).ConfigureAwait(false);
                    _onStatus?.Invoke($"Waiting for Gemini… {(int)(DateTime.UtcNow - started).TotalSeconds}s");
                }
            }
            catch (OperationCanceledException) { }
        });
        try
        {
            var lastProblem = "No Gemini model answered.";
            for (var pass = 0; pass < 2; pass++)
            {
                if (pass > 0)
                {
                    _onStatus?.Invoke("All Gemini models are busy — retrying in 8s");
                    await SleepAsync(PassRetryDelay, ct).ConfigureAwait(false);
                }
                var models = Candidates();
                var (winner, problem) = await RaceAsync(models, body, ct).ConfigureAwait(false);
                if (problem != null) lastProblem = problem;
                ct.ThrowIfCancellationRequested();
                if (winner is { } w)
                {
                    lock (_lock)
                    {
                        if (_activeModel != w.Model) _switched = true;
                        _activeModel = w.Model;
                    }
                    return w.Json;
                }
            }
            if (_quota.AvailableCount(Model) == 0)
                throw new PhoneAgentApiException(PhoneAgentApiErrorKind.Http, "Gemini",
                    "Today's free Gemini requests are used up on every model (Google allows about 20 a day per model; one phone task uses 10–20). They reset overnight. For unlimited use, turn on billing for your key in Google AI Studio, or switch to Claude in Settings → Phone Agent.", 429);
            throw new PhoneAgentApiException(PhoneAgentApiErrorKind.Http, "Gemini",
                $"Gemini's free models are overloaded right now ({lastProblem}). Try again in a few minutes, or switch to Claude in Settings → Phone Agent.", 503);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw PhoneAgentApiException.Cancelled();
        }
        finally
        {
            tickerCts.Cancel();
        }
    }

    /// <summary>Hedged requests over <paramref name="models"/>. Returns the winning (model, json), or
    /// null with the last problem. Errors that aren't "try the next model" propagate.</summary>
    private async Task<((string Model, JsonObject Json)? Winner, string? Problem)> RaceAsync(
        List<string> models, JsonObject body, CancellationToken ct)
    {
        using var groupCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = groupCts.Token;
        var tasks = new List<Task<(string? Model, Attempt Outcome)>>();
        var next = 0;
        var running = 0;
        string? lastProblem = null;

        void Launch()
        {
            var candidate = models[next];
            next++;
            running++;
            tasks.Add(AttemptTagged(candidate, body, token));
        }
        void ScheduleHedge() => tasks.Add(HedgeTimer(token));

        Launch();
        ScheduleHedge();
        try
        {
            while (tasks.Count > 0)
            {
                var finished = await Task.WhenAny(tasks).ConfigureAwait(false);
                tasks.Remove(finished);
                var (candidate, outcome) = await finished.ConfigureAwait(false); // rethrows fatal errors
                if (candidate is null)
                {
                    // Hedge timer fired.
                    if (token.IsCancellationRequested) break;
                    if (next < models.Count)
                    {
                        Launch();
                        ScheduleHedge();
                    }
                    continue;
                }
                running--;
                switch (outcome)
                {
                    case Success s:
                        return ((candidate, s.Json), lastProblem);
                    case TryNext t:
                        lastProblem = t.Problem;
                        if (next < models.Count) Launch();
                        else if (running == 0) return (null, lastProblem);
                        break;
                }
            }
            return (null, lastProblem);
        }
        finally
        {
            groupCts.Cancel();
            // Observe the losers so their cancellation faults never surface as unobserved exceptions.
            foreach (var t in tasks) _ = t.ContinueWith(static x => _ = x.Exception, TaskScheduler.Default);
        }
    }

    private async Task<(string?, Attempt)> AttemptTagged(string model, JsonObject body, CancellationToken ct)
        => (model, await AttemptAsync(model, body, ct).ConfigureAwait(false));

    private async Task<(string?, Attempt)> HedgeTimer(CancellationToken ct)
    {
        try { await Task.Delay(HedgeDelay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
        return (null, new TryNext(""));
    }

    internal static string EndpointFor(string model) => BaseUrl + EscapePath(model) + ":generateContent";

    /// <summary>Percent-encodes like Foundation's urlPathAllowed.</summary>
    private static string EscapePath(string s)
    {
        const string allowed = "-._~!$&'()*+,;=:@/";
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || allowed.Contains(c))) sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>One model: retries short rate limits in place; overload, timeouts and long quotas move on.</summary>
    private async Task<Attempt> AttemptAsync(string candidate, JsonObject original, CancellationToken ct)
    {
        var url = EndpointFor(candidate);
        bool needsScrub;
        lock (_lock) needsScrub = _switched || candidate != _activeModel;
        var body = needsScrub ? (JsonObject)ReplacingSignatures(original)! : original;
        string payload;
        try { payload = body.ToJsonString(); }
        catch { throw new PhoneAgentApiException(PhoneAgentApiErrorKind.BadRequest, "Gemini", "Couldn't encode the request."); }
        var label = PhoneAgentGeminiModel.Label(candidate);

        var rateLimitAttempts = 0;
        var networkAttempts = 0;
        while (true)
        {
            if (ct.IsCancellationRequested) throw PhoneAgentApiException.Cancelled();
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("x-goog-api-key", _apiKey);

            HttpResponseMessage response;
            string responseText;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(PerModelTimeout);
            try
            {
                response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                responseText = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested) throw PhoneAgentApiException.Cancelled();
                return new TryNext($"{label} timed out");
            }
            catch (HttpRequestException ex)
            {
                if (ct.IsCancellationRequested) throw PhoneAgentApiException.Cancelled();
                networkAttempts++;
                if (networkAttempts <= 2)
                {
                    await SleepAsync(TimeSpan.FromSeconds(Math.Pow(2, networkAttempts)), ct).ConfigureAwait(false);
                    continue;
                }
                return new TryNext("network error: " + ex.Message);
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                JsonObject? json = null;
                try { json = JsonNode.Parse(responseText) as JsonObject; } catch { }
                if (status == 200)
                {
                    if (json is null) return new TryNext($"{label} sent an unreadable reply");
                    return new Success(json);
                }

                var error = json?["error"] as JsonObject;
                var message = Json.Str(error?["message"])
                    ?? (responseText.Length > 300 ? responseText[..300] : responseText);
                if (message.Length == 0) message = "Unknown error";

                if ((status == 400 && message.Contains("API key not valid", StringComparison.OrdinalIgnoreCase)) || status == 401)
                    throw new PhoneAgentApiException(PhoneAgentApiErrorKind.InvalidKey, "Gemini");
                if (status == 403 && message.Contains("key", StringComparison.OrdinalIgnoreCase))
                    throw new PhoneAgentApiException(PhoneAgentApiErrorKind.InvalidKey, "Gemini");

                switch (status)
                {
                    case 429:
                    {
                        rateLimitAttempts++;
                        var retryHeader = response.Headers.TryGetValues("retry-after", out var values) ? values.FirstOrDefault() : null;
                        var retry = RetryDelay(error, retryHeader);
                        // Free-tier quotas are per model: a long wait means try another model instead.
                        if ((retry ?? 0) > 120 || message.Contains("per day", StringComparison.OrdinalIgnoreCase))
                        {
                            // Daily quota: skip this model until it resets.
                            _quota.MarkExhausted(candidate, retry ?? 12 * 3600);
                            return new TryNext($"{label} used up today's free requests");
                        }
                        if (rateLimitAttempts > 2 || (retry ?? 0) > 30)
                            return new TryNext($"{label} rate-limited");
                        var delay = Math.Min(Math.Max(retry ?? 5, 1), 30);
                        _onStatus?.Invoke($"Free tier rate limit — waiting {(int)Math.Ceiling(delay)}s");
                        await SleepAsync(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
                        continue;
                    }
                    case 404:
                        return new TryNext($"{label} isn't available to this key");
                    case >= 500 and <= 599:
                        return new TryNext($"{label}: {message}");
                    case 400:
                        throw new PhoneAgentApiException(PhoneAgentApiErrorKind.BadRequest, "Gemini", message);
                    default:
                        throw new PhoneAgentApiException(PhoneAgentApiErrorKind.Http, "Gemini", message, status);
                }
            }
        }
    }

    /// <summary>Replaces every thoughtSignature so another model accepts the history.</summary>
    internal static JsonNode? ReplacingSignatures(JsonNode? value)
    {
        switch (value)
        {
            case JsonObject obj:
                var outObj = new JsonObject();
                foreach (var (key, inner) in obj)
                    outObj[key] = key == "thoughtSignature" ? JsonValue.Create(SkipSignature) : ReplacingSignatures(inner);
                return outObj;
            case JsonArray arr:
                var outArr = new JsonArray();
                foreach (var item in arr) outArr.Add(ReplacingSignatures(item));
                return outArr;
            default:
                return value?.DeepClone();
        }
    }

    /// <summary>google.rpc.RetryInfo.retryDelay ("37s" / "1.5s"), else a Retry-After header.</summary>
    internal static double? RetryDelay(JsonObject? error, string? header)
    {
        if (error?["details"] is JsonArray details)
        {
            foreach (var d in details)
            {
                if (d is not JsonObject detail) continue;
                var type = Json.Str(detail["@type"]);
                var raw = Json.Str(detail["retryDelay"]);
                if (type is null || !type.EndsWith("RetryInfo", StringComparison.Ordinal) || raw is null) continue;
                if (double.TryParse(raw.Trim('s'), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) return seconds;
            }
        }
        if (header != null && double.TryParse(header, NumberStyles.Float, CultureInfo.InvariantCulture, out var h)) return h;
        return null;
    }

    private async Task SleepAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(delay.TotalMilliseconds * SleepScale), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw PhoneAgentApiException.Cancelled();
        }
    }

    /// <summary>Tiny request used by "Test key".</summary>
    public Task TestKeyAsync(CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = "Reply with OK." }),
            }),
            ["generationConfig"] = new JsonObject { ["maxOutputTokens"] = 256 },
        };
        return GenerateAsync(body, ct);
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Gemini conversation state (PhoneAgentGeminiConversation).</summary>
public sealed class GeminiConversation : IPhoneAgentModelClient
{
    private readonly GeminiClient _client;
    private string _system = "";
    private JsonArray _declarations = new();
    private readonly List<JsonObject> _contents = new();
    private int _screenCounter;
    /// <summary>Dropped for the rest of the run if a (custom) model rejects thinkingConfig.</summary>
    private bool _sendThinkingLevel = true;

    public GeminiConversation(GeminiClient client) { _client = client; }

    public string ProviderName => "Gemini";

    /// <summary>The model that answered last, when it isn't the one picked in Settings.</summary>
    public string? FallbackModelLabel
    {
        get
        {
            var active = _client.ActiveModel;
            return active == _client.Model ? null : PhoneAgentGeminiModel.Label(active);
        }
    }

    internal IReadOnlyList<JsonObject> Contents => _contents;

    public void Begin(string system, IReadOnlyList<PhoneAgentToolSpec> tools, string intro, PhoneAgentScreen screen, string tree)
    {
        _system = system;
        _declarations = new JsonArray();
        foreach (var spec in tools)
        {
            var declaration = new JsonObject { ["name"] = spec.Name, ["description"] = spec.Description };
            if (spec.Properties.Count > 0)
            {
                // OpenAPI-subset schema: no additionalProperties / strict.
                var props = new JsonObject();
                foreach (var (k, v) in spec.Properties) props[k] = v.DeepClone();
                declaration["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = props,
                    ["required"] = new JsonArray(spec.Properties.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => (JsonNode)k!).ToArray()),
                };
            }
            _declarations.Add(declaration);
        }
        _contents.Clear();
        _contents.Add(new JsonObject
        {
            ["role"] = "user",
            ["parts"] = new JsonArray(
                new JsonObject { ["text"] = intro },
                new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = "image/jpeg", ["data"] = Convert.ToBase64String(screen.Jpeg) } },
                new JsonObject { ["text"] = tree }),
        });
    }

    /// <summary>The generateContent body for the next turn.</summary>
    internal JsonObject BuildRequestBody(PhoneAgentEffort effort)
    {
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = _system }) },
            ["contents"] = new JsonArray(_contents.Select(c => (JsonNode)c.DeepClone()).ToArray()),
            ["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = _declarations.DeepClone() }),
            // ANY: every turn must call a tool; the agent ends via done / ask_user.
            ["toolConfig"] = new JsonObject { ["functionCallingConfig"] = new JsonObject { ["mode"] = "ANY" } },
        };
        if (_sendThinkingLevel)
            body["generationConfig"] = new JsonObject { ["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = effort.Raw() } };
        return body;
    }

    public async Task<PhoneAgentTurn> NextTurnAsync(PhoneAgentEffort effort, CancellationToken ct)
    {
        JsonObject response;
        try
        {
            response = await _client.GenerateAsync(BuildRequestBody(effort), ct).ConfigureAwait(false);
        }
        catch (PhoneAgentApiException e) when (e.Kind == PhoneAgentApiErrorKind.BadRequest && _sendThinkingLevel
                                                && e.Detail.Contains("thinking", StringComparison.OrdinalIgnoreCase))
        {
            _sendThinkingLevel = false;
            return await NextTurnAsync(effort, ct).ConfigureAwait(false);
        }
        return ParseResponse(response);
    }

    /// <summary>Records the model's turn and maps finishReason to a turn end. Internal for tests.</summary>
    internal PhoneAgentTurn ParseResponse(JsonObject response)
    {
        var usage = new PhoneAgentUsage { Free = true };
        if (response["usageMetadata"] is JsonObject meta)
        {
            usage.Input = Json.Int(meta["promptTokenCount"]);
            usage.Output = Json.Int(meta["candidatesTokenCount"]) + Json.Int(meta["thoughtsTokenCount"]);
            usage.CacheRead = Json.Int(meta["cachedContentTokenCount"]);
        }

        if ((response["candidates"] as JsonArray)?.FirstOrDefault() is not JsonObject candidate)
        {
            var reason = Json.Str((response["promptFeedback"] as JsonObject)?["blockReason"]);
            if (reason != null)
                return new PhoneAgentTurn("", [], PhoneAgentTurnEnd.Refused($"Gemini blocked the request ({reason})."), usage);
            return new PhoneAgentTurn("", [], PhoneAgentTurnEnd.Retry("Your previous response was empty. Continue the task by calling one of the tools."), usage);
        }

        var finish = Json.Str(candidate["finishReason"]) ?? "STOP";
        var content = candidate["content"] as JsonObject;
        var parts = (content?["parts"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];

        // Append the model's turn exactly as received (thought signatures included).
        if (content != null && parts.Count > 0)
        {
            var turn = (JsonObject)content.DeepClone();
            if (!turn.ContainsKey("role")) turn["role"] = "model";
            _contents.Add(turn);
        }

        var text = string.Join("\n", parts
            .Where(p => Json.Bool(p["thought"]) != true)
            .Select(p => Json.Str(p["text"]))
            .Where(s => s != null)).Trim();
        var calls = new List<PhoneAgentToolCall>();
        foreach (var part in parts)
        {
            if (part["functionCall"] is not JsonObject call || Json.Str(call["name"]) is not { } name) continue;
            var args = call["args"] as JsonObject;
            calls.Add(new PhoneAgentToolCall(Json.Str(call["id"]) ?? "", name, (JsonObject?)args?.DeepClone() ?? new JsonObject()));
        }

        PhoneAgentTurnEnd end;
        switch (finish)
        {
            case "STOP":
            case "FINISH_REASON_UNSPECIFIED":
                if (calls.Count > 0) end = PhoneAgentTurnEnd.ToolUse;
                else if (parts.Count == 0) end = PhoneAgentTurnEnd.Retry("Your previous response was empty. Continue the task by calling one of the tools.");
                else end = PhoneAgentTurnEnd.EndTurn;
                break;
            case "MAX_TOKENS":
                end = PhoneAgentTurnEnd.MaxTokens;
                break;
            case "MALFORMED_FUNCTION_CALL":
            case "UNEXPECTED_TOOL_CALL":
            case "TOO_MANY_TOOL_CALLS":
            case "MISSING_THOUGHT_SIGNATURE":
                end = PhoneAgentTurnEnd.Retry($"Your previous function call was invalid ({finish}). Call one of the declared tools with valid arguments.");
                break;
            case "SAFETY":
            case "RECITATION":
            case "PROHIBITED_CONTENT":
            case "BLOCKLIST":
            case "SPII":
            case "IMAGE_SAFETY":
            case "IMAGE_PROHIBITED_CONTENT":
            case "LANGUAGE":
            case "OTHER":
                var message = Json.Str(candidate["finishMessage"]);
                end = PhoneAgentTurnEnd.Refused($"Gemini stopped ({finish}){(message != null ? ": " + message : ".")}");
                break;
            default:
                end = calls.Count == 0 ? PhoneAgentTurnEnd.Other($"Gemini stopped unexpectedly ({finish}).") : PhoneAgentTurnEnd.ToolUse;
                break;
        }
        return new PhoneAgentTurn(text, calls, end, usage);
    }

    public void AddToolResults(IReadOnlyList<PhoneAgentToolReply> replies, Func<PhoneAgentScreen, string> tree)
    {
        var parts = new JsonArray();
        foreach (var reply in replies)
        {
            var result = new JsonObject { [reply.IsError ? "error" : "output"] = reply.Text };
            var functionResponse = new JsonObject { ["name"] = reply.Call.Name };
            if (reply.Call.Id.Length > 0) functionResponse["id"] = reply.Call.Id;
            if (reply.Screen is { } screen)
            {
                _screenCounter++;
                var displayName = $"screen-{_screenCounter}.jpg";
                result["screenshot"] = new JsonObject { ["$ref"] = displayName };
                result["ui_elements"] = tree(screen);
                result["screenshot_size"] = $"{screen.Width}x{screen.Height}";
                functionResponse["parts"] = new JsonArray(new JsonObject
                {
                    ["inlineData"] = new JsonObject
                    {
                        ["displayName"] = displayName,
                        ["mimeType"] = "image/jpeg",
                        ["data"] = Convert.ToBase64String(screen.Jpeg),
                    },
                });
            }
            if (reply.Note != null) result["note"] = reply.Note;
            functionResponse["response"] = result;
            parts.Add(new JsonObject { ["functionResponse"] = functionResponse });
        }
        _contents.Add(new JsonObject { ["role"] = "user", ["parts"] = parts });
    }

    public void AddNudge(IReadOnlyList<PhoneAgentToolCall> calls, string message)
    {
        var parts = new List<JsonNode>();
        foreach (var call in calls)
        {
            var response = new JsonObject
            {
                ["name"] = call.Name,
                ["response"] = new JsonObject { ["error"] = "Your response was cut off before this call finished. Re-issue it if still needed." },
            };
            if (call.Id.Length > 0) response["id"] = call.Id;
            parts.Add(new JsonObject { ["functionResponse"] = response });
        }
        parts.Add(new JsonObject { ["text"] = message });
        // Never send two user turns in a row: merge into the previous user turn if there is one.
        if (calls.Count == 0 && _contents.Count > 0 && Json.Str(_contents[^1]["role"]) == "user")
        {
            var lastParts = _contents[^1]["parts"] as JsonArray ?? new JsonArray();
            if (_contents[^1]["parts"] is null) _contents[^1]["parts"] = lastParts;
            foreach (var p in parts) lastParts.Add(p);
        }
        else
        {
            _contents.Add(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(parts.ToArray()) });
        }
    }
}
