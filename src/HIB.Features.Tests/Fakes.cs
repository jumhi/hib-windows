using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HIB.Features.Abstractions;

namespace HIB.Features.Tests;

internal sealed class FakePhoneLink : IPhoneLink
{
    private bool _connected;
    public bool IsConnected
    {
        get => _connected;
        set { _connected = value; ConnectionChanged?.Invoke(value); }
    }
    public string? DeviceName { get; set; } = "Test Galaxy";
    public byte[]? PairingKey { get; set; }
    public List<(string Type, string Json)> Sent { get; } = new();

    public Task SendAsync(string type, object data)
    {
        lock (Sent) Sent.Add((type, JsonSerializer.Serialize(data)));
        return Task.CompletedTask;
    }

    public event Action<string, JsonElement>? MessageReceived;
    public event Action<bool>? ConnectionChanged;

    public void Receive(string type, string json) => MessageReceived?.Invoke(type, JsonDocument.Parse(json).RootElement.Clone());
}

/// <summary>Scripted HTTP: a function from request (url, body) to a response, with optional delay.</summary>
internal sealed class ScriptedHandler(Func<HttpRequestMessage, string, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<(string Url, string Body, Dictionary<string, string> Headers)> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
        lock (Requests) Requests.Add((request.RequestUri!.ToString(), body, headers));
        var response = await respond(request, body).WaitAsync(ct);
        return response;
    }

    public static HttpResponseMessage Json(HttpStatusCode code, string json)
        => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal static class GeminiFixtures
{
    public static string TextReply(string text, string? signature = null)
    {
        var part = new JsonObject { ["functionCall"] = new JsonObject { ["id"] = "c1", ["name"] = "done", ["args"] = new JsonObject { ["summary"] = text } } };
        if (signature != null) part["thoughtSignature"] = signature;
        return new JsonObject
        {
            ["candidates"] = new JsonArray(new JsonObject
            {
                ["content"] = new JsonObject { ["role"] = "model", ["parts"] = new JsonArray(part) },
                ["finishReason"] = "STOP",
            }),
            ["usageMetadata"] = new JsonObject { ["promptTokenCount"] = 100, ["candidatesTokenCount"] = 7, ["thoughtsTokenCount"] = 3 },
        }.ToJsonString();
    }

    public const string PerDay429 = """
        {"error":{"code":429,"message":"You exceeded your current quota. Quota exceeded for metric: generate_content_free_tier_requests, limit: 20 per day","status":"RESOURCE_EXHAUSTED",
         "details":[{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"37s"}]}}
        """;

    public const string Overloaded503 = """{"error":{"code":503,"message":"The model is overloaded. Please try again later.","status":"UNAVAILABLE"}}""";
}
