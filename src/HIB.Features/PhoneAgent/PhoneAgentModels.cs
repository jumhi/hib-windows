using System.Text.Json.Nodes;
using HIB.Features.Abstractions;

namespace HIB.Features.PhoneAgent;

public enum PhoneAgentEffort { Low, Medium, High }

/// <summary>Which model provider drives the Phone Agent. Gemini (free API keys) is the default.</summary>
public enum PhoneAgentProvider { Gemini, Claude }

public static class PhoneAgentProviderInfo
{
    public static string Raw(this PhoneAgentProvider p) => p == PhoneAgentProvider.Gemini ? "gemini" : "claude";
    public static string Label(this PhoneAgentProvider p) => p == PhoneAgentProvider.Gemini ? "Gemini" : "Claude";
    public static string KeyName(this PhoneAgentProvider p) => p == PhoneAgentProvider.Gemini ? "Gemini API key" : "Claude API key";
    public static string Company(this PhoneAgentProvider p) => p == PhoneAgentProvider.Gemini ? "Google" : "Anthropic";
    public static string Raw(this PhoneAgentEffort e) => e switch { PhoneAgentEffort.Low => "low", PhoneAgentEffort.High => "high", _ => "medium" };
}

/// <summary>Free-tier Gemini models suited to agentic tool use with screenshots.</summary>
public static class PhoneAgentGeminiModel
{
    public static readonly (string Id, string Label)[] Presets =
    [
        ("gemini-3.8-flash", "Gemini 3.8 Flash (stable)"),
        ("gemini-4-hydrogen", "Gemini 4 Hydrogen (newer)"),
    ];
    public const string DefaultId = "gemini-3.8-flash";
    public const string CustomTag = "custom";

    /// <summary>Tried in order when the chosen model is overloaded, rate-limited or not answering.</summary>
    public static readonly string[] Fallbacks =
    [
        "gemini-3-flash-preview",
        "gemini-3.1-flash-lite",
        "gemini-3.7-flash",
        "gemini-3.5-flash",
        "gemini-flash-latest",
        "gemini-4-hydrogen",
    ];

    public static string Label(string id)
    {
        foreach (var (pid, label) in Presets)
            if (pid == id) return label.Replace(" (stable)", "").Replace(" (newer)", "");
        return id.Replace("gemini-", "Gemini ")
                 .Replace("-", " ")
                 .Replace("flash", "Flash")
                 .Replace("lite", "Lite")
                 .Replace("preview", "Preview")
                 .Replace("latest", "Latest");
    }
}

/// <summary>Phone Agent preferences (same keys as the Mac).</summary>
public sealed class PhoneAgentSettings
{
    private const string ProviderKey = "hib.phoneAgent.provider";
    private const string GeminiModelKey = "hib.phoneAgent.geminiModel";
    private const string GeminiCustomModelKey = "hib.phoneAgent.geminiCustomModel";
    private const string EffortKey = "hib.phoneAgent.effort";
    private const string MaxStepsKey = "hib.phoneAgent.maxSteps";
    private const string ConfirmSendingKey = "hib.phoneAgent.confirmSending";
    private const string ConfirmDeletesKey = "hib.phoneAgent.confirmDeletes";
    private const string ConfirmCallsKey = "hib.phoneAgent.confirmCalls";

    private readonly ISettingsStore _s;
    public PhoneAgentSettings(ISettingsStore store) { _s = store; }

    public PhoneAgentProvider Provider
    {
        get => _s.Get<string>(ProviderKey) == "claude" ? PhoneAgentProvider.Claude : PhoneAgentProvider.Gemini;
        set => _s.Set(ProviderKey, value.Raw());
    }

    /// <summary>A preset model id, or <see cref="PhoneAgentGeminiModel.CustomTag"/>.</summary>
    public string GeminiModelChoice
    {
        get => _s.Get<string>(GeminiModelKey) ?? PhoneAgentGeminiModel.DefaultId;
        set => _s.Set(GeminiModelKey, value);
    }

    public string GeminiCustomModel
    {
        get => _s.Get<string>(GeminiCustomModelKey) ?? "";
        set => _s.Set(GeminiCustomModelKey, value.Trim());
    }

    /// <summary>The model id actually sent to the Gemini API.</summary>
    public string GeminiModel
    {
        get
        {
            var choice = GeminiModelChoice;
            if (choice == PhoneAgentGeminiModel.CustomTag)
            {
                var custom = GeminiCustomModel;
                if (custom.StartsWith("models/", StringComparison.Ordinal)) custom = custom["models/".Length..];
                return custom.Length == 0 ? PhoneAgentGeminiModel.DefaultId : custom;
            }
            return choice.Length == 0 ? PhoneAgentGeminiModel.DefaultId : choice;
        }
    }

    public PhoneAgentEffort Effort
    {
        get => _s.Get<string>(EffortKey) switch { "low" => PhoneAgentEffort.Low, "high" => PhoneAgentEffort.High, _ => PhoneAgentEffort.Medium };
        set => _s.Set(EffortKey, value.Raw());
    }

    public int MaxSteps
    {
        get
        {
            var value = _s.Get<int?>(MaxStepsKey) ?? 0;
            return value <= 0 ? 40 : Math.Clamp(value, 5, 150);
        }
        set => _s.Set(MaxStepsKey, Math.Clamp(value, 5, 150));
    }

    public bool ConfirmSending { get => _s.Get<bool?>(ConfirmSendingKey) ?? true; set => _s.Set(ConfirmSendingKey, value); }
    /// <summary>Purchases and payments always need confirmation; this is not a setting.</summary>
    public const bool ConfirmPurchases = true;
    public bool ConfirmDeletes { get => _s.Get<bool?>(ConfirmDeletesKey) ?? true; set => _s.Set(ConfirmDeletesKey, value); }
    public bool ConfirmCalls { get => _s.Get<bool?>(ConfirmCallsKey) ?? true; set => _s.Set(ConfirmCallsKey, value); }
}

public enum PhoneAgentApiErrorKind { InvalidKey, BadRequest, Http, Network, Decoding, Cancelled }

public sealed class PhoneAgentApiException : Exception
{
    public PhoneAgentApiException(PhoneAgentApiErrorKind kind, string provider, string detail = "", int status = 0)
        : base(Describe(kind, provider, detail, status))
    {
        Kind = kind;
        Provider = provider;
        Detail = detail;
        Status = status;
    }

    public PhoneAgentApiErrorKind Kind { get; }
    public string Provider { get; }
    public string Detail { get; }
    public int Status { get; }

    private static string Describe(PhoneAgentApiErrorKind kind, string provider, string detail, int status) => kind switch
    {
        PhoneAgentApiErrorKind.InvalidKey => $"{provider} API key invalid. Check it in Settings → Phone Agent.",
        PhoneAgentApiErrorKind.BadRequest => $"{provider} API rejected the request: {detail}",
        PhoneAgentApiErrorKind.Http => $"{provider} API error {status}: {detail}",
        PhoneAgentApiErrorKind.Network => $"Network error: {detail}",
        PhoneAgentApiErrorKind.Decoding => $"Couldn't read the {provider} API response.",
        _ => "Cancelled.",
    };

    public static PhoneAgentApiException Cancelled() => new(PhoneAgentApiErrorKind.Cancelled, "");
}

public struct PhoneAgentUsage
{
    public int Input;
    public int Output;
    public int CacheRead;
    public int CacheWrite;
    /// <summary>Free-tier provider (Gemini): no cost estimate, token counts only.</summary>
    public bool Free;

    /// <summary>Claude Opus 5.5: $4 / MTok input, $20 / MTok output, $0.20 cache reads, $5 cache writes.</summary>
    public readonly double CostUsd => Free ? 0 : (Input * 4.0 + Output * 20.0 + CacheRead * 0.20 + CacheWrite * 5.0) / 1_000_000;

    public void Add(PhoneAgentUsage other)
    {
        Input += other.Input;
        Output += other.Output;
        CacheRead += other.CacheRead;
        CacheWrite += other.CacheWrite;
        Free = Free || other.Free;
    }

    public static PhoneAgentUsage FromClaude(JsonObject? usage)
    {
        var r = new PhoneAgentUsage();
        if (usage is null) return r;
        r.Input = Json.Int(usage["input_tokens"]);
        r.Output = Json.Int(usage["output_tokens"]);
        r.CacheRead = Json.Int(usage["cache_read_input_tokens"]);
        r.CacheWrite = Json.Int(usage["cache_creation_input_tokens"]);
        return r;
    }
}

/// <summary>Provider-neutral tool definition (JSON-schema-like properties).</summary>
public sealed record PhoneAgentToolSpec(string Name, string Description, IReadOnlyDictionary<string, JsonObject> Properties);

public sealed record PhoneAgentToolCall(string Id, string Name, JsonObject Input);

/// <summary>Result of one tool call, sent back to the model.</summary>
public sealed class PhoneAgentToolReply
{
    public PhoneAgentToolReply(PhoneAgentToolCall call, string text, bool isError)
    {
        Call = call; Text = text; IsError = isError;
    }
    public PhoneAgentToolCall Call { get; }
    public string Text { get; set; }
    public bool IsError { get; set; }
    /// <summary>Fresh screen to attach to this reply (screenshot + UI tree).</summary>
    public PhoneAgentScreen? Screen { get; set; }
    /// <summary>Extra note (e.g. screen capture failed).</summary>
    public string? Note { get; set; }
}

public enum PhoneAgentTurnEndKind { ToolUse, EndTurn, MaxTokens, Retry, Refused, Pause, Other }

public sealed record PhoneAgentTurnEnd(PhoneAgentTurnEndKind Kind, string Message = "")
{
    public static readonly PhoneAgentTurnEnd ToolUse = new(PhoneAgentTurnEndKind.ToolUse);
    public static readonly PhoneAgentTurnEnd EndTurn = new(PhoneAgentTurnEndKind.EndTurn);
    public static readonly PhoneAgentTurnEnd MaxTokens = new(PhoneAgentTurnEndKind.MaxTokens);
    public static readonly PhoneAgentTurnEnd Pause = new(PhoneAgentTurnEndKind.Pause);
    public static PhoneAgentTurnEnd Retry(string m) => new(PhoneAgentTurnEndKind.Retry, m);
    public static PhoneAgentTurnEnd Refused(string m) => new(PhoneAgentTurnEndKind.Refused, m);
    public static PhoneAgentTurnEnd Other(string m) => new(PhoneAgentTurnEndKind.Other, m);
}

public sealed record PhoneAgentTurn(string Text, IReadOnlyList<PhoneAgentToolCall> ToolCalls, PhoneAgentTurnEnd End, PhoneAgentUsage Usage);

/// <summary>A conversation with one model provider. The controller's loop only talks to this.</summary>
public interface IPhoneAgentModelClient
{
    string ProviderName { get; }
    /// <summary>Starts the conversation: system prompt, tool list, first user message with the screen.</summary>
    void Begin(string system, IReadOnlyList<PhoneAgentToolSpec> tools, string intro, PhoneAgentScreen screen, string tree);
    /// <summary>Sends the conversation and records the model's turn exactly as received.</summary>
    Task<PhoneAgentTurn> NextTurnAsync(PhoneAgentEffort effort, CancellationToken ct);
    /// <summary>Sends all tool results for the previous turn (in one user message).</summary>
    void AddToolResults(IReadOnlyList<PhoneAgentToolReply> replies, Func<PhoneAgentScreen, string> tree);
    /// <summary>Answers any dangling calls with an error and adds a user nudge.</summary>
    void AddNudge(IReadOnlyList<PhoneAgentToolCall> calls, string message);
}

internal static class Json
{
    public static int Int(JsonNode? n)
    {
        if (n is not JsonValue v) return 0;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out long l)) return (int)l;
        if (v.TryGetValue(out double d)) return (int)d;
        return 0;
    }

    public static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    public static bool? Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue(out bool b) ? b : null;

    /// <summary>The Mac's number(): NSNumber or numeric string, else 0.</summary>
    public static double Number(JsonNode? n)
    {
        if (n is not JsonValue v) return 0;
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out string? s) && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p)) return p;
        if (v.TryGetValue(out bool b)) return b ? 1 : 0;
        return 0;
    }
}
