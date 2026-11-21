// H.I.B Roam (docs/SPACE.md §9.3): the ctl* peer messages, their ordered/coalesced outbox and the
// safety watchdog. Everything travels over the authenticated peer link (§3.3); coordinates are the
// target's points/DIPs, y down.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using HIB.Core.Link;

namespace HIB.Core.Roam;

public enum RoamButton { Left, Right, Middle, Back, Forward }

public static class RoamButtons
{
    public static string Wire(RoamButton b) => b switch
    {
        RoamButton.Left => "left",
        RoamButton.Right => "right",
        RoamButton.Middle => "middle",
        RoamButton.Back => "back",
        _ => "forward",
    };

    public static RoamButton? Parse(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "left" => RoamButton.Left,
        "right" => RoamButton.Right,
        "middle" => RoamButton.Middle,
        "back" => RoamButton.Back,
        "forward" => RoamButton.Forward,
        _ => null,
    };
}

/// <summary><c>ctlHello</c>: sent on every peer-link hello and whenever a field changes.</summary>
public sealed record CtlHello(bool Enabled, string Platform, IReadOnlyList<RoamScreen> Screens, RoamSide? ManualSide, bool CmdAsCtrl, int V = 1)
{
    public JsonObject ToWire()
    {
        var screens = new JsonArray();
        foreach (var s in Screens)
            screens.Add(new JsonObject
            {
                ["id"] = s.Id,
                ["x"] = Round(s.X),
                ["y"] = Round(s.Y),
                ["w"] = Round(s.W),
                ["h"] = Round(s.H),
                // §10.1
                ["name"] = s.Name,
                ["mmW"] = Math.Round(s.MmW, 1),
                ["mmH"] = Math.Round(s.MmH, 1),
                ["builtIn"] = s.BuiltIn,
                ["pxW"] = s.PxW > 0 ? s.PxW : (int)Math.Round(s.W * s.Scale),
                ["pxH"] = s.PxH > 0 ? s.PxH : (int)Math.Round(s.H * s.Scale),
                ["scale"] = Math.Round(s.Scale > 0 ? s.Scale : 1, 3),
            });
        return new JsonObject
        {
            ["v"] = V,
            ["enabled"] = Enabled,
            ["platform"] = Platform,
            ["screens"] = screens,
            ["manualSide"] = ManualSide is { } m ? RoamSides.Wire(m) : null,
            ["cmdAsCtrl"] = CmdAsCtrl,
        };
    }

    public static CtlHello Parse(JsonElement d)
    {
        var screens = new List<RoamScreen>();
        if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("screens", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in arr.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object) continue;
                var w = s.Double("w") ?? 0;
                var h = s.Double("h") ?? 0;
                if (w <= 0 || h <= 0) continue;
                static double Mm(double? v) => v is { } d && double.IsFinite(d) && d > 0 && d < 10000 ? d : 0;
                var scale = s.Double("scale") is { } sc && double.IsFinite(sc) && sc > 0 ? sc : 1;
                screens.Add(new RoamScreen(s.Str("id") ?? "", s.Double("x") ?? 0, s.Double("y") ?? 0, w, h)
                {
                    Name = s.Str("name") ?? "",
                    MmW = Mm(s.Double("mmW")),
                    MmH = Mm(s.Double("mmH")),
                    BuiltIn = s.Bool("builtIn") ?? false,
                    PxW = s.Int("pxW") is int pw && pw > 0 ? pw : (int)Math.Round(w * scale),
                    PxH = s.Int("pxH") is int ph && ph > 0 ? ph : (int)Math.Round(h * scale),
                    Scale = scale,
                });
            }
        }
        return new CtlHello(d.Bool("enabled") ?? true, (d.Str("platform") ?? "mac").ToLowerInvariant(), screens,
            RoamSides.Parse(d.Str("manualSide")), d.Bool("cmdAsCtrl") ?? true, d.Int("v") ?? 1);
    }

    internal static double Round(double v) => double.IsFinite(v) ? Math.Round(v, 2) : 0;
}

/// <summary>Builders for the other ctl* messages (explicit nulls, like the spec shows them).</summary>
public static class RoamWire
{
    public const string Hello = "ctlHello";
    public const string Enter = "ctlEnter";
    public const string Move = "ctlMove";
    public const string Button = "ctlButton";
    public const string Wheel = "ctlWheel";
    public const string Key = "ctlKey";
    public const string Leave = "ctlLeave";
    public const string Release = "ctlRelease";
    public const string DragBegin = "ctlDragBegin";
    public const string DragCancel = "ctlDragCancel";

    /// <summary>The most clipboard text a crossing carries (§9.4: ≤ 1 MB).</summary>
    public const int MaxClipboardBytes = 1024 * 1024;

    static JsonNode? Clip(string? text) => text is null ? null : new JsonObject { ["text"] = text };

    public static JsonObject EnterData(double x, double y, string? clipboard)
        => new() { ["x"] = CtlHello.Round(x), ["y"] = CtlHello.Round(y), ["clipboard"] = Clip(clipboard) };

    public static JsonObject MoveData(double x, double y) => new() { ["x"] = CtlHello.Round(x), ["y"] = CtlHello.Round(y) };

    public static JsonObject ButtonData(RoamButton b, bool down) => new() { ["b"] = RoamButtons.Wire(b), ["down"] = down };

    public static JsonObject WheelData(int dx, int dy) => new() { ["dx"] = dx, ["dy"] = dy };

    public static JsonObject KeyData(int usage, bool down) => new() { ["usage"] = usage, ["down"] = down };

    public static JsonObject LeaveData(string? clipboard) => new() { ["clipboard"] = Clip(clipboard) };

    public static JsonObject ReleaseData() => new();

    public static JsonObject DragBeginData(string id, int count, string name) => new() { ["id"] = id, ["count"] = count, ["name"] = name };

    public static JsonObject DragCancelData(string id) => new() { ["id"] = id };

    /// <summary>The clipboard text a crossing carries, or null (none, or over the limit).</summary>
    public static string? ClipboardText(JsonElement d)
    {
        if (d.Obj("clipboard") is not { } c) return null;
        var text = c.Str("text");
        if (text is null || System.Text.Encoding.UTF8.GetByteCount(text) > MaxClipboardBytes) return null;
        return text;
    }

    /// <summary>A file drop's point (flickStart.at), or null.</summary>
    public static (double X, double Y)? At(JsonElement d)
        => d.Obj("at") is { } at && at.Double("x") is { } x && at.Double("y") is { } y && double.IsFinite(x) && double.IsFinite(y) ? (x, y) : null;
}

/// <summary>One thing the outbox hands out: a message (its data ready, or still being read — the clipboard), or an action.</summary>
public sealed record RoamOutboxItem(string Type, JsonObject? Data, System.Threading.Tasks.Task<JsonObject>? Deferred = null,
    Func<System.Threading.Tasks.Task>? Run = null);

/// <summary>
/// The send queue to one peer: strictly in order, with mouse moves coalesced to at most one per
/// <see cref="MoveInterval"/> (the newest position wins). Keys, buttons and wheel are never coalesced; a move
/// queued before a key is sent before it, at the position it had when the key came.
/// Not thread-safe on its own: the engine serialises access.
/// </summary>
public sealed class RoamOutbox
{
    public static readonly TimeSpan MoveInterval = TimeSpan.FromMilliseconds(8);

    sealed class Item
    {
        public required string Type;
        public JsonObject? Data;
        public System.Threading.Tasks.Task<JsonObject>? Deferred;
        public Func<System.Threading.Tasks.Task>? Run;
        public bool IsMove;
        public double X, Y;
    }

    readonly LinkedList<Item> _queue = new();
    DateTime _lastMove = DateTime.MinValue;

    public int Count => _queue.Count;

    public void Enqueue(string type, JsonObject data) => _queue.AddLast(new Item { Type = type, Data = data });

    /// <summary>A message whose data is still being produced (ctlEnter/ctlLeave wait for the clipboard); later ones wait behind it.</summary>
    public void EnqueueDeferred(string type, System.Threading.Tasks.Task<JsonObject> data) => _queue.AddLast(new Item { Type = type, Deferred = data });

    /// <summary>Runs <paramref name="action"/> once everything queued before it went out (e.g. start a file transfer after the drop).</summary>
    public void EnqueueAction(string name, Func<System.Threading.Tasks.Task> action) => _queue.AddLast(new Item { Type = name, Run = action });

    /// <summary>A new virtual-cursor position: updates the queued move if nothing was queued after it.</summary>
    public void OfferMove(double x, double y)
    {
        if (_queue.Last?.Value is { IsMove: true } last)
        {
            last.X = x;
            last.Y = y;
            return;
        }
        _queue.AddLast(new Item { Type = RoamWire.Move, IsMove = true, X = x, Y = y });
    }

    /// <summary>
    /// The next item to send now, or false with how long to wait (<see cref="System.Threading.Timeout.InfiniteTimeSpan"/> when empty).
    /// </summary>
    public bool TryTake(DateTime now, out RoamOutboxItem item, out TimeSpan wait)
    {
        item = null!;
        wait = System.Threading.Timeout.InfiniteTimeSpan;
        if (_queue.First?.Value is not { } head) return false;
        if (head.IsMove)
        {
            var due = _lastMove + MoveInterval;
            if (now < due)
            {
                wait = due - now;
                return false;
            }
            _lastMove = now;
            _queue.RemoveFirst();
            item = new RoamOutboxItem(RoamWire.Move, RoamWire.MoveData(head.X, head.Y));
            wait = TimeSpan.Zero;
            return true;
        }
        _queue.RemoveFirst();
        item = new RoamOutboxItem(head.Type, head.Data, head.Deferred, head.Run);
        wait = TimeSpan.Zero;
        return true;
    }

    public void Clear() => _queue.Clear();
}

/// <summary>
/// SPACE.md §9.1 safety: while controlling, no pong for <see cref="Timeout"/> → release at once.
/// </summary>
public sealed class RoamWatchdog
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    /// <summary>How often the controller pings while away.</summary>
    public static readonly TimeSpan PingEvery = TimeSpan.FromMilliseconds(500);

    DateTime _since;

    public bool Armed { get; private set; }

    public void Arm(DateTime now)
    {
        Armed = true;
        _since = now;
    }

    public void Pong(DateTime now)
    {
        if (Armed && now > _since) _since = now;
    }

    public void Disarm() => Armed = false;

    public bool Expired(DateTime now) => Armed && now - _since > Timeout;
}
