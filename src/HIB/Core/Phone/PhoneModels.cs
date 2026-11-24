using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HIB.Core.Link;

namespace HIB.Core.Phone;

public sealed record NotificationAction(string Name, string Type)
{
    public bool IsReply => Type.Equals("reply", StringComparison.OrdinalIgnoreCase);
}

public sealed record PhoneNotification
{
    public required string Id { get; init; }
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string App { get; init; } = "";
    public string Package { get; init; } = "";
    public string Priority { get; init; } = "alerting";
    public IReadOnlyList<NotificationAction> Actions { get; init; } = Array.Empty<NotificationAction>();
    public int? Progress { get; init; }
    public int? ProgressMax { get; init; }
    public bool ProgressIndeterminate { get; init; }
    public bool Ongoing { get; init; }
    public DateTimeOffset Received { get; init; } = DateTimeOffset.Now;

    public bool IsSilent => Priority.Equals("silent", StringComparison.OrdinalIgnoreCase);

    public static PhoneNotification? Parse(JsonElement d)
    {
        var id = d.Str("id");
        if (string.IsNullOrEmpty(id)) return null;
        var actions = new List<NotificationAction>();
        if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("actions", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                var name = a.Str("name");
                if (string.IsNullOrEmpty(name)) continue;
                actions.Add(new NotificationAction(name, a.Str("type") ?? "button"));
            }
        }
        return new PhoneNotification
        {
            Id = id,
            Title = d.Str("title") ?? "",
            Body = d.Str("body") ?? "",
            App = d.Str("app") ?? d.Str("package") ?? "",
            Package = d.Str("package") ?? "",
            Priority = d.Str("priority") ?? "alerting",
            Actions = actions,
            Progress = d.Int("progress"),
            ProgressMax = d.Int("progressMax"),
            ProgressIndeterminate = d.Bool("progressIndeterminate") ?? false,
            Ongoing = d.Bool("ongoing") ?? false,
        };
    }
}

public sealed record NowPlaying
{
    public bool IsPlaying { get; init; }
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public int Volume { get; init; }
    public bool IsMuted { get; init; }
    public byte[]? AlbumArt { get; init; }
    public long DurationMs { get; init; }
    public long PositionMs { get; init; }
    public long PositionTimestamp { get; init; }
    public bool IsBuffering { get; init; }
    public string LikeStatus { get; init; } = "none";

    public bool HasTrack => !string.IsNullOrWhiteSpace(Title) || !string.IsNullOrWhiteSpace(Artist);

    /// <summary>Position corrected for the time since the phone sampled it (like the Mac).</summary>
    public long CurrentPositionMs(long nowMs)
    {
        var pos = PositionMs;
        if (IsPlaying && !IsBuffering && PositionTimestamp > 0)
        {
            var delta = nowMs - PositionTimestamp;
            if (delta is > -2000 and < 5000) pos += delta;
        }
        if (DurationMs > 0) pos = Math.Clamp(pos, 0, DurationMs);
        return Math.Max(0, pos);
    }
}

public sealed record CallInfo
{
    public string EventId { get; init; } = "";
    public string Direction { get; init; } = "incoming";
    public string State { get; init; } = "ringing";
    public string? Number { get; init; }
    public string? NormalizedNumber { get; init; }
    public string? ContactName { get; init; }
    public byte[]? ContactPhoto { get; init; }
    public int SimSlot { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public string DisplayNumber => !string.IsNullOrWhiteSpace(NormalizedNumber) ? NormalizedNumber! : Number ?? "";
    public string DisplayName => !string.IsNullOrWhiteSpace(ContactName) ? ContactName! :
        !string.IsNullOrWhiteSpace(DisplayNumber) ? DisplayNumber : "Unknown caller";

    public bool IsIncoming => Direction.Equals("incoming", StringComparison.OrdinalIgnoreCase);
    public bool IsRinging => State.Equals("ringing", StringComparison.OrdinalIgnoreCase);
    public bool IsActive => State.Equals("offhook", StringComparison.OrdinalIgnoreCase) || State.Equals("accepted", StringComparison.OrdinalIgnoreCase);
    public bool IsOver => State is "idle" or "missed" or "ended" or "rejected";

    public static CallInfo Parse(JsonElement d) => new()
    {
        EventId = d.Str("eventId") ?? "",
        Direction = d.Str("direction") ?? "incoming",
        State = (d.Str("state") ?? "ringing").ToLowerInvariant(),
        Number = d.Str("number"),
        NormalizedNumber = d.Str("normalizedNumber"), // optional: Gson omits nulls
        ContactName = d.Str("contactName"),
        ContactPhoto = LenientJson.Base64(d.Str("contactPhoto")),
        SimSlot = d.Int("simSlot") ?? 0,
        Timestamp = d.Long("timestamp") is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime() : DateTimeOffset.Now,
    };
}

public sealed record PhoneApp(string Package, string Name, string? IconPath, bool Listening, bool SystemApp);

public sealed record FlickInfo(string Id, string Kind, string Name, string? Mime, long Size, byte[]? Thumbnail, string? Text)
{
    /// <summary>Swipe angle in degrees: 0 = straight up, negative = left, positive = right.</summary>
    public double Angle { get; init; }
    /// <summary>Where the card left the sender's screen, 0 (left) … 1 (right).</summary>
    public double ExitXRatio { get; init; } = 0.5;
    /// <summary>Launch speed in points per second.</summary>
    public double Velocity { get; init; } = 1800;
    /// <summary>Where the phone points: compass heading, degrees clockwise from north (optional).</summary>
    public double? Heading { get; init; }
    /// <summary>Phone elevation in degrees above the horizon (optional).</summary>
    public double? Elevation { get; init; }
    /// <summary>The id of the screen to land on (Point &amp; Flick, SPACE.md §7.4); wins when that screen exists here.</summary>
    public string? Screen { get; init; }
    public bool IsTransferless => Kind is "link" or "text";
    /// <summary>Set when another computer sent this through the phone (multi-desktop relay).</summary>
    public string? From { get; init; }
    /// <summary>Roam file drag (SPACE.md §9.5): the drop point in this computer's points/DIPs; the card appears there.</summary>
    public (double X, double Y)? At { get; init; }
    /// <summary>Part of a bundle (SPACE.md §14): every item of one throw shares the bundle id and lands as one stack.</summary>
    public FlickBundleTag? Bundle { get; init; }
}

/// <summary>
/// <c>flickStart.bundle {id, index, count}</c> (SPACE.md §14): many items thrown at once. Every item carries the same
/// <see cref="Id"/> and <see cref="Count"/>; <see cref="Index"/> is its place (0 = the top of the stack).
/// </summary>
public sealed record FlickBundleTag(string Id, int Index, int Count)
{
    /// <summary>Reads <c>bundle</c> from a flickStart; null when absent, malformed, or a "bundle" of one.</summary>
    public static FlickBundleTag? Parse(System.Text.Json.JsonElement flickStart)
    {
        if (flickStart.ValueKind != System.Text.Json.JsonValueKind.Object || flickStart.Obj("bundle") is not { } b) return null;
        var id = b.Str("id");
        var index = b.Int("index");
        var count = b.Int("count");
        if (string.IsNullOrWhiteSpace(id) || index is not { } i || count is not { } n || n < 2 || i < 0 || i >= n) return null;
        return new FlickBundleTag(id, i, Math.Min(n, 1000));
    }

    /// <summary>The wire form: <c>{"id":…,"index":…,"count":…}</c>.</summary>
    public object ToWire() => new Dictionary<string, object?> { ["id"] = Id, ["index"] = Index, ["count"] = Count };
}

public sealed record ReceivedFile(string Id, string Name, string Path, long Size, string? FlickId, bool IsSearchFetch, DateTimeOffset Received)
{
    /// <summary>The computer that sent it through the phone, if any (multi-desktop relay).</summary>
    public string? From { get; init; }
}

public sealed record TransferProgress(string Id, string Name, long Size, long Received, bool Done, bool Failed, string? FlickId);
