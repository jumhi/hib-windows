// H.I.B Roam (docs/SPACE.md §9): shared mouse & keyboard over the direct peer link. The engine keeps
// each linked computer's Roam state (its ctlHello, the side, this PC's choices), runs the controller
// (RoamController) against the platform's captured input, injects what another computer sends while
// it drives this one, carries the clipboard across, and hands dropped files to the Space engine.
// Round 2 (SPACE.md §12, RoamEngine.Round2.cs): injection on a dedicated input thread, Mouse Keys for a PC without a
// mouse, the UIPI guard and key-path counters, game mode, hotkeys, lock together, keep awake, speeds, clipboard images.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using HIB.Core.Link;
using HIB.Core.Phone;
using HIB.Core.Space;

namespace HIB.Core.Roam;

public sealed class RoamOptions
{
    /// <summary>Capture/injection (Windows hooks + SendInput by default; nothing elsewhere).</summary>
    public Func<IRoamPlatform> Platform { get; set; } = RoamPlatforms.CreateDefault;
    public Func<DateTime>? Clock { get; set; }
    /// <summary>The watchdog/ping tick while this computer controls another.</summary>
    public TimeSpan Tick { get; set; } = TimeSpan.FromMilliseconds(100);
    /// <summary>The most a crossing waits for the clipboard.</summary>
    public TimeSpan ClipboardTimeout { get; set; } = TimeSpan.FromMilliseconds(250);
    /// <summary>The room (SPACE.md §10): roomLayout and portals. Off = §9.2 only, like an older H.I.B (tests of the §9.2 path).</summary>
    public bool Room { get; set; } = true;
    /// <summary>SPACE.md §12: Mouse Keys, the window in front, keep awake, lock (Windows by default; nothing elsewhere).</summary>
    public Func<IRoamSystem> System { get; set; } = RoamSystems.CreateDefault;
    /// <summary>How often the app in front is looked at (§12.4) and the other housekeeping runs.</summary>
    public TimeSpan Housekeeping { get; set; } = TimeSpan.FromMilliseconds(250);
    /// <summary>How often a ready computer is pinged for the latency readout (§12.3) while not away.</summary>
    public TimeSpan LatencyPing { get; set; } = TimeSpan.FromSeconds(2);
}

public static class RoamPlatforms
{
    /// <summary>Windows: low-level hooks and SendInput (HIB_ROAM=0 turns it off). Elsewhere: nothing.</summary>
    public static IRoamPlatform CreateDefault()
    {
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("HIB_ROAM") is not ("0" or "false"))
        {
            try { return new Windows.WindowsRoamPlatform(); }
            catch { /* fall through */ }
        }
        return new NullRoamPlatform();
    }
}

/// <summary>Where a computer's side comes from. <see cref="Room"/>: the room (SPACE.md §10) places it.</summary>
public enum RoamSideSource { Default, PointSetup, Manual, Room }

public enum RoamRole { None, Controlling, Controlled }

/// <summary>Everything the Devices tab shows for one computer.</summary>
public sealed record RoamPeerStatus(string PeerId, string Name, string Platform, bool Linked, bool Enabled, bool RemoteEnabled,
    bool HasRemote, RoamSide Side, RoamSideSource SideSource, bool NeedsAsk, bool CmdAsCtrl, bool Ready, RoamRole Role, string Status)
{
    public bool IsMac => Platform != "windows";
    /// <summary>§12.3: the link's ping round trip (median of the last 20), null before the first.</summary>
    public int? LatencyMs { get; init; }
    /// <summary>§12.5 this computer's speeds for that one.</summary>
    public double PointerSpeed { get; init; } = 1;
    public double ScrollSpeed { get; init; } = 1;
    public bool NaturalScroll { get; init; }
    /// <summary>§12.4: what that computer's app in front is doing.</summary>
    public RoamGameMode RemoteGame { get; init; }
    public string RemoteApp { get; init; } = "";
}

/// <summary>A file drag from the other computer, shown as a ghost card under the cursor here (points).</summary>
public sealed record RoamGhost(string Id, string Name, int Count, double X, double Y);

/// <summary>A side learned from dead-edge pushes ("&lt;name&gt; is on this side now · Undo").</summary>
public sealed record RoamLearnedNotice(string PeerId, string Name, RoamSide Side);

public sealed partial class RoamEngine : IDisposable
{
    sealed class Peer
    {
        public required string Id;
        public string Name = "";
        public string Platform = "mac";
        public PeerLink? Link;
        public CtlHello? Remote;
        public bool GotHello;
        public string? LastRemoteManual;
        public bool? LastRemoteCmd;
        public readonly RoamOutbox Outbox = new();
        public readonly SemaphoreSlim Signal = new(0, 1);
        public CancellationTokenSource? Pump;
        public DateTime LastPing;
        /// <summary>It sent roomLayout on this link (it speaks the room, §10.2).</summary>
        public bool SentRoom;
        /// <summary>The layout it has from us (or sent us): only a different one goes out.</summary>
        public string? LastSentRoom;
        /// <summary>§12.4: its app in front (ctlTargetState).</summary>
        public RoamGameMode Game;
        public string GameApp = "";
        public DateTime LastLatencyPing;
        public int? ShownLatency;
    }

    readonly SpaceEngine _space;
    readonly ILinkSession _phone;
    readonly RoamStore _store;
    readonly RoamOptions _options;
    readonly IRoamPlatform _platform;
    readonly RoamController _controller;
    readonly Func<DateTime> _clock;
    readonly object _gate = new();
    readonly List<Action> _effects = new();
    readonly Dictionary<string, Peer> _peers = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<int> _injectedKeys = new();
    readonly HashSet<RoamButton> _injectedButtons = new();
    readonly Timer _timer;
    IReadOnlyList<RoamDisplay> _displays = Array.Empty<RoamDisplay>();
    string? _controlledBy;
    (double X, double Y) _remoteCursor;
    RoamGhost? _ghost;
    string? _lastCrossText;
    (string PeerId, string? Previous)? _undo;
    bool _timerOn;
    bool _disposed;

    public RoamEngine(SpaceEngine space, ILinkSession phone, string dataDir, RoamOptions? options = null)
    {
        _space = space;
        _phone = phone;
        _options = options ?? new RoamOptions();
        _clock = _options.Clock ?? (() => DateTime.UtcNow);
        _store = new RoamStore(dataDir);
        _roomStore = new RoomStore(dataDir);
        _platform = _options.Platform();
        _controller = new RoamController(new Host(this), _clock) { LocalPlatform = _platform.Platform };
        _timer = new Timer(_ => OnTick(), null, Timeout.Infinite, Timeout.Infinite);
        _platform.Log = WriteLog;
        _platform.Input += OnInput;
        _system = _options.System();
        _injector = new RoamInjector(_platform, _stats, () => _system.Foreground());
        _mouseKeys = new MouseKeysKeeper(_system, dataDir, WriteLog);
        StartRound2();
        space.Network.LinkUp += OnLinkUp;
        space.Network.LinkDown += OnLinkDown;
        space.FlickArrived += f =>
        {
            if (f.At is not null) Locked(() => SetGhost(null)); // the dropped file's card replaces the ghost
        };
        phone.MessageReceived += OnPhoneMessage;
    }

    /// <summary>Where the clipboard is read and written on crossings (SPACE.md §9.4).</summary>
    public Func<IClipboardBridge?> Clipboard { get; set; } = () => null;

    public IRoamPlatform Platform => _platform;

    /// <summary>The Devices tab and the overlays refresh on this (any thread).</summary>
    public event Action? Changed;
    /// <summary>The edge glow: show at this edge, or hide (null).</summary>
    public event Action<RoamEdgeHint?>? HintChanged;
    /// <summary>The drop catcher: under the cursor at this edge, or hidden (null).</summary>
    public event Action<RoamEdgeHint?>? ProbeChanged;
    /// <summary>A file drag from another computer: the ghost card (null hides it). Positions in this PC's points.</summary>
    public event Action<RoamGhost?>? GhostChanged;
    public event Action<RoamLearnedNotice>? SideLearned;
    public event Action<string>? Log;

    public RoamMode Mode { get { lock (_gate) return _controller.Mode; } }
    public string? ControllingId { get { lock (_gate) return _controller.Target?.PeerId; } }
    public string? ControlledById { get { lock (_gate) return _controlledBy; } }
    public RoamGhost? Ghost { get { lock (_gate) return _ghost; } }
    public IReadOnlyList<RoamDisplay> Displays { get { lock (_gate) return _displays; } }
    public IReadOnlyList<ScreenAim> Layout => _store.Layout;
    public RoamEdgeHint? CurrentHint { get { lock (_gate) return _controller.CurrentHint; } }

    // ---------------- locking & effects ----------------

    /// <summary>Runs <paramref name="body"/> under the lock, then the side effects it queued (events, activity) outside it.</summary>
    void Locked(Action body)
    {
        List<Action>? fx = null;
        lock (_gate)
        {
            body();
            if (_effects.Count > 0)
            {
                fx = new List<Action>(_effects);
                _effects.Clear();
            }
        }
        if (fx is null) return;
        foreach (var f in fx)
        {
            try { f(); }
            catch (Exception ex) { WriteLog("Roam: " + ex.Message); }
        }
    }

    void After(Action a) => _effects.Add(a);

    // ---------------- this PC ----------------

    string Own => _space.ThisDeviceName;

    bool IsSelf(string computer)
        => SpaceIds.Same(computer, _space.ThisDeviceName) || (_space.ListState.You is { Length: > 0 } you && SpaceIds.Same(computer, you));

    /// <summary>This PC's monitors (the app pushes them from Avalonia's screens; pixels + scale).</summary>
    public void SetDisplays(IReadOnlyList<RoamDisplay> displays)
    {
        Locked(() =>
        {
            var list = displays.Where(d => !d.Pixels.IsEmpty).ToList();
            if (list.SequenceEqual(_displays)) return;
            _displays = list;
            _controller.SetDisplays(list);
            _platform.SetDisplays(list);
            foreach (var p in _peers.Values) SendHello(p);
            Recompute();
        });
    }

    public void SetFileDrag(IReadOnlyList<string>? files) => Locked(() => _controller.SetFileDrag(files));

    // ---------------- the Devices tab ----------------

    public RoamPeerStatus? StatusFor(string peerId)
    {
        lock (_gate) return _peers.TryGetValue(peerId, out var p) ? Status(p) : null;
    }

    public IReadOnlyList<RoamPeerStatus> Statuses { get { lock (_gate) return _peers.Values.Select(Status).ToList(); } }

    public void SetEnabled(string peerId, bool on) => Change(peerId, s => s with { Enabled = on }, p =>
    {
        if (on) return;
        if (_controller.Target is { } t && SpaceIds.Same(t.PeerId, p.Id)) _controller.ReleaseWithLeave();
        if (_controlledBy is { } by && SpaceIds.Same(by, p.Id))
        {
            Enqueue(p, RoamWire.Release, RoamWire.ReleaseData());
            StopControlled();
        }
    });

    public void SetSide(string peerId, RoamSide side) => Change(peerId, s => s with { ManualSide = RoamSides.Wire(side) }, p =>
    {
        if (_undo is { } u && SpaceIds.Same(u.PeerId, p.Id)) _undo = null;
        // With a room, the choice moves that computer's group beside this PC (§10).
        if (UsesRoom(p)) MoveGroupNextTo(p.Id, side);
    });

    public void SetCmdAsCtrl(string peerId, bool on) => Change(peerId, s => s with { CmdAsCtrl = on }, null);

    /// <summary>"Undo" on the learned-side pill: the side goes back to what it was.</summary>
    public void UndoLearned()
    {
        Locked(() =>
        {
            if (_roomUndo is { } ru)
            {
                _roomUndo = null;
                ApplyRoomEdit(ru);
                return;
            }
            if (_undo is not { } u) return;
            _undo = null;
            _store.Set(u.PeerId, _store.Get(u.PeerId) with { ManualSide = u.Previous });
            if (_peers.TryGetValue(u.PeerId, out var p)) SendHello(p);
            Recompute();
        });
    }

    void Change(string peerId, Func<RoamPairSettings, RoamPairSettings> edit, Action<Peer>? then)
    {
        Locked(() =>
        {
            _store.Set(peerId, edit(_store.Get(peerId)));
            if (_peers.TryGetValue(peerId, out var p))
            {
                then?.Invoke(p);
                SendHello(p);
            }
            Recompute();
        });
    }

    RoamPeerStatus Status(Peer p)
    {
        var s = _store.Get(p.Id);
        var room = UsesRoom(p);
        var (side, source) = room ? (RoomSide(p.Id), RoamSideSource.Room) : SideOf(p, s);
        var layout = _store.Layout;
        var needsAsk = !room && s.ManualSide is null && !(RoamLayout.HasAims(layout, Own) && RoamLayout.HasAims(layout, p.Id));
        var linked = p.Link is { IsOpen: true };
        var role = _controller.Target is { } t && SpaceIds.Same(t.PeerId, p.Id) ? RoamRole.Controlling
            : _controlledBy is { } by && SpaceIds.Same(by, p.Id) ? RoamRole.Controlled : RoamRole.None;
        var ready = IsReady(p, s);
        string text;
        if (!_platform.IsAvailable) text = _platform.UnavailableReason;
        else if (!linked) text = $"Needs a direct link to {p.Name}";
        else if (!s.Enabled) text = "Off";
        else if (p.Remote is null) text = $"Update H.I.B on {p.Name} to share the mouse and keyboard";
        else if (!p.Remote.Enabled) text = $"Turned off on {p.Name}";
        else if (role == RoamRole.Controlling) text = $"Using {p.Name} · come back across its {RoamSides.EdgeWord(RoamSides.Mirror(side))} edge";
        else if (role == RoamRole.Controlled) text = $"{p.Name} is using this PC";
        else if (_displays.Count == 0 || p.Remote.Screens.Count == 0) text = "Getting the screens ready…";
        else if (room && !_roomMap.Touches(Own, p.Id)) text = $"{p.Name} isn't next to this PC on your desk · Arrange to connect them";
        else text = $"Move the cursor off the {RoamSides.EdgeWord(side)} edge to go to {p.Name}";
        if (role == RoamRole.Controlling && _controller.RelativeMode) text = $"Game mode on {p.Name} · the mouse stays there · {RoamPill.LeaveKeys} to leave";
        else if (role == RoamRole.Controlling && _controller.UserLock) text = $"Locked to {p.Name} · {RoamPill.LeaveKeys} to unlock";
        return new RoamPeerStatus(p.Id, p.Name, p.Platform, linked, s.Enabled, p.Remote?.Enabled ?? false, p.Remote is not null,
            side, source, needsAsk, s.CmdAsCtrl, ready, role, text)
        {
            LatencyMs = linked ? p.Link!.Latency.MedianMs : null,
            PointerSpeed = RoamSpeed.Clamp(s.PointerSpeed),
            ScrollSpeed = RoamSpeed.Clamp(s.ScrollSpeed),
            NaturalScroll = s.NaturalScroll,
            RemoteGame = p.Game,
            RemoteApp = p.GameApp,
        };
    }

    (RoamSide Side, RoamSideSource Source) SideOf(Peer p, RoamPairSettings s)
    {
        if (RoamSides.Parse(s.ManualSide) is { } manual) return (manual, RoamSideSource.Manual);
        var layout = _store.Layout;
        var self = layout.Any(a => SpaceIds.Same(a.Computer, Own)) ? Own : _space.ListState.You ?? Own;
        if (RoamLayout.SideOf(layout, self, p.Id) is { } aimed) return (aimed, RoamSideSource.PointSetup);
        return (RoamSide.Right, RoamSideSource.Default);
    }

    bool IsReady(Peer p, RoamPairSettings s)
        => p.Link is { IsOpen: true } && _platform.IsAvailable && s.Enabled && p.Remote is { Enabled: true } r && r.Screens.Count > 0 && _displays.Count > 0;

    /// <summary>Recomputes the neighbours and the platform mode (under the lock).</summary>
    void Recompute()
    {
        RebuildRoom();
        var neighbors = new List<RoamNeighbor>();
        var inRoom = new List<string>();
        foreach (var p in _peers.Values)
        {
            var s = _store.Get(p.Id);
            if (!IsReady(p, s)) continue;
            var screens = p.Remote!.Screens.Select(x => x.Rect).ToList();
            if (UsesRoom(p))
            {
                // §10.3: reached through the room's portals; the side is only where its group sits.
                neighbors.Add(new RoamNeighbor(p.Id, p.Name, RoomSide(p.Id), screens, p.Platform, s.CmdAsCtrl, InRoom: true,
                    Game: GameOf(p), PointerSpeed: RoamSpeed.Clamp(s.PointerSpeed), ScrollSpeed: RoamSpeed.Clamp(s.ScrollSpeed), NaturalScroll: s.NaturalScroll));
                inRoom.Add(p.Id);
                continue;
            }
            var (side, _) = SideOf(p, s);
            if (neighbors.Any(n => !n.InRoom && n.Side == side)) continue;
            neighbors.Add(new RoamNeighbor(p.Id, p.Name, side, screens, p.Platform, s.CmdAsCtrl,
                Game: GameOf(p), PointerSpeed: RoamSpeed.Clamp(s.PointerSpeed), ScrollSpeed: RoamSpeed.Clamp(s.ScrollSpeed), NaturalScroll: s.NaturalScroll));
        }
        // The controller's room: this PC and the ready computers placed with it.
        _controller.SetRoom(inRoom.Count == 0 ? RoomMap.Empty
            : new RoomMap(_roomMap.Screens.Where(x => IsSelf(x.Computer) || inRoom.Any(id => SpaceIds.Same(id, x.Computer)))), Own);
        _controller.SetNeighbors(neighbors);
        UpdateMode();
        After(() => Changed?.Invoke());
    }

    void UpdateMode()
    {
        var want = _controller.Mode == RoamMode.Away ? RoamInputMode.Capture
            : _controlledBy is not null ? RoamInputMode.Controlled
            : _controller.Neighbors.Count > 0 ? RoamInputMode.Watch
            : RoamInputMode.Off;
        if (_platform.Mode != want) _platform.SetMode(want);
    }

    // ---------------- the phone: screenLayout (§9.2) ----------------

    void OnPhoneMessage(object? sender, LinkMessageEventArgs e)
    {
        if (e.Type == "screenLayout") ApplyLayout(RoamLayout.Parse(e.Data));
    }

    /// <summary>A screenLayout from the phone. A new Point setup clears learned and manual sides.</summary>
    public void ApplyLayout(IReadOnlyList<ScreenAim> layout)
    {
        Locked(() =>
        {
            if (!_store.SetLayout(layout)) return;
            WriteLog($"Roam: new screen layout ({layout.Count} screen aims)");
            _store.ClearSides();
            _undo = null;
            foreach (var p in _peers.Values) SendHello(p);
            Recompute();
        });
    }

    // ---------------- the peer link ----------------

    void OnLinkUp(PeerLink link)
    {
        Locked(() =>
        {
            if (!_peers.TryGetValue(link.PeerId, out var p)) _peers[link.PeerId] = p = new Peer { Id = link.PeerId };
            StopPump(p);
            p.Link = link;
            p.Name = link.PeerName;
            p.Platform = link.PeerPlatform;
            p.Remote = null;
            p.GotHello = false;
            p.LastRemoteManual = null;
            p.LastRemoteCmd = null;
            p.SentRoom = false;
            p.LastSentRoom = null; // §10.2: the room goes out on every peer hello
            p.Game = RoamGameMode.Normal;
            p.GameApp = "";
            p.Outbox.Clear();
            p.Pump = new CancellationTokenSource();
            var ct = p.Pump.Token;
            _ = Task.Run(() => PumpAsync(p, link, ct), CancellationToken.None);
            link.MessageReceived += OnPeerMessage;
            link.PongReceived += OnPong;
            SendHello(p);
            SendTargetState(p); // §12.4: what this PC's app in front is doing
            Recompute(); // sends the room (§10.2) right after the hello
        });
    }

    void OnLinkDown(PeerLink link)
    {
        Locked(() =>
        {
            link.MessageReceived -= OnPeerMessage;
            link.PongReceived -= OnPong;
            if (!_peers.TryGetValue(link.PeerId, out var p) || !ReferenceEquals(p.Link, link)) return;
            _controller.LinkLost(p.Id);
            if (_controlledBy is { } by && SpaceIds.Same(by, p.Id)) StopControlled();
            StopPump(p);
            p.Link = null;
            p.Remote = null;
            p.SentRoom = false;
            p.LastSentRoom = null;
            p.Outbox.Clear();
            Recompute();
        });
    }

    static void StopPump(Peer p)
    {
        try { p.Pump?.Cancel(); } catch { }
        p.Pump = null;
    }

    void OnPong(PeerLink link) => Locked(() => _controller.Pong(link.PeerId));

    void Enqueue(Peer p, string type, JsonObject data)
    {
        p.Outbox.Enqueue(type, data);
        Wake(p);
    }

    static void Wake(Peer p)
    {
        try { p.Signal.Release(); } catch (SemaphoreFullException) { }
    }

    Peer? PeerFor(string id) => _peers.TryGetValue(id, out var p) ? p : null;

    void SendHello(Peer p)
    {
        if (p.Link is not { IsOpen: true }) return;
        var s = _store.Get(p.Id);
        var screens = _displays.Select(d =>
        {
            var r = d.Dips;
            return new RoamScreen(d.Id, r.X, r.Y, r.W, r.H)
            {
                Name = d.Name,
                MmW = d.MmW,
                MmH = d.MmH,
                BuiltIn = d.BuiltIn,
                PxW = (int)Math.Round(d.Pixels.W),
                PxH = (int)Math.Round(d.Pixels.H),
                Scale = d.SafeScale,
            };
        }).ToList();
        var hello = new CtlHello(s.Enabled && _platform.IsAvailable, _platform.Platform, screens, RoamSides.Parse(s.ManualSide), s.CmdAsCtrl);
        Enqueue(p, RoamWire.Hello, hello.ToWire());
    }

    /// <summary>
    /// Sends one peer's queue in order (§12.3): the next item goes the moment the previous frame is written; moves offered
    /// meanwhile collapse into the newest position waiting at the end of the queue.
    /// </summary>
    async Task PumpAsync(Peer p, PeerLink link, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            RoamOutboxItem? item;
            lock (_gate)
            {
                if (!p.Outbox.TryTake(out item)) item = null;
            }
            if (item is null)
            {
                try { await p.Signal.WaitAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }
            try
            {
                if (item.Run is { } run)
                {
                    await run().ConfigureAwait(false);
                    continue;
                }
                var data = item.Deferred is { } d ? await d.ConfigureAwait(false) : item.Data ?? new JsonObject();
                await link.SendAsync(item.Type, data, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) { WriteLog($"Roam send {item.Type}: {ex.Message}"); }
        }
    }

    void OnPeerMessage(object? sender, LinkMessageEventArgs e)
    {
        if (sender is PeerLink roomLink && e.Type == RoomLayout.Type)
        {
            OnRoomLayout(roomLink, e.Data);
            return;
        }
        if (sender is not PeerLink link || !e.Type.StartsWith("ctl", StringComparison.Ordinal)) return;
        Locked(() =>
        {
            if (!_peers.TryGetValue(link.PeerId, out var p) || !ReferenceEquals(p.Link, link)) return;
            HandleCtl(p, e.Type, e.Data);
        });
    }

    void HandleCtl(Peer p, string type, JsonElement d)
    {
        var controlled = _controlledBy is { } by && SpaceIds.Same(by, p.Id);
        switch (type)
        {
            case RoamWire.Hello:
                ApplyHello(p, CtlHello.Parse(d));
                break;
            case RoamWire.Enter:
            {
                var s = _store.Get(p.Id);
                if (!s.Enabled || !_platform.IsAvailable || _controller.Mode == RoamMode.Away || (_controlledBy is not null && !controlled))
                {
                    Enqueue(p, RoamWire.Release, RoamWire.ReleaseData()); // not now: the cursor goes back
                    break;
                }
                var first = _controlledBy is null;
                _controlledBy = p.Id;
                ApplyClipboard(RoamClip.Parse(d));
                var x = d.Double("x") ?? 0;
                var y = d.Double("y") ?? 0;
                _remoteCursor = (x, y);
                UpdateMode();
                Inject(x, y);
                if (first) StartControlled();
                WriteLog($"Roam: {p.Name} is using this PC");
                After(() => _space.SetRoamInUse(true));
                After(() => Changed?.Invoke());
                break;
            }
            case RoamWire.Move:
                if (!controlled) break;
                _remoteCursor = (d.Double("x") ?? _remoteCursor.X, d.Double("y") ?? _remoteCursor.Y);
                Inject(_remoteCursor.X, _remoteCursor.Y);
                if (_ghost is { } g) SetGhost(g with { X = _remoteCursor.X, Y = _remoteCursor.Y });
                break;
            case RoamWire.Rel:
                // §12.4: relative motion for a game here (no MOUSEEVENTF_ABSOLUTE).
                if (!controlled) break;
                _injector.Relative(d.Int("dx") ?? 0, d.Int("dy") ?? 0);
                break;
            case RoamWire.Button:
            {
                if (!controlled || RoamButtons.Parse(d.Str("b")) is not { } b) break;
                var down = d.Bool("down") ?? false;
                // A file drag from the controller: its button went down over there; the drop is the controller's to send.
                if (_ghost is not null && b == RoamButton.Left) break;
                if (down) _injectedButtons.Add(b);
                else _injectedButtons.Remove(b);
                _injector.Button(b, down);
                break;
            }
            case RoamWire.Wheel:
            {
                if (!controlled) break;
                // §12.3: fractions are carried over on the input thread (sub-notch, smooth scrolling).
                var (v, h) = RoamWheel.ToWindows(d.Double("dx") ?? 0, d.Double("dy") ?? 0, exact: true);
                _injector.Wheel(h, v);
                break;
            }
            case RoamWire.Key:
            {
                if (!controlled || d.Int("usage") is not { } usage) break;
                _stats.Received();
                var down = d.Bool("down") ?? false;
                if (down) _injectedKeys.Add(usage);
                else _injectedKeys.Remove(usage);
                _injector.Key(usage, down); // §12.5 key repeat: every repeated down is typed (Windows doesn't repeat injected keys)
                break;
            }
            case RoamWire.TargetState:
                OnTargetState(p, d);
                break;
            case RoamWire.ScreenLock:
                OnScreenLock(p);
                break;
            case RoamWire.Leave:
                if (!controlled) break;
                ApplyClipboard(RoamClip.Parse(d));
                WriteLog($"Roam: {p.Name} left");
                StopControlled();
                break;
            case RoamWire.Release:
                _controller.Released(p.Id);
                break;
            case RoamWire.DragBegin:
                if (!controlled) break;
                SetGhost(new RoamGhost(d.Str("id") ?? "", d.Str("name") ?? "", Math.Max(1, d.Int("count") ?? 1), _remoteCursor.X, _remoteCursor.Y));
                break;
            case RoamWire.DragCancel:
                if (_ghost is { } ghost && (d.Str("id") is not { } id || id == ghost.Id)) SetGhost(null);
                break;
        }
    }

    /// <summary>
    /// ctlHello. The pair's side and ⌘-as-Ctrl are shared: a change the other computer made is applied (mirrored for the
    /// side); when both had a different choice before linking, the computer whose id sorts lower wins on both.
    /// </summary>
    void ApplyHello(Peer p, CtlHello hello)
    {
        p.Remote = hello;
        p.Platform = hello.Platform;
        var s = _store.Get(p.Id);
        var remoteManual = hello.ManualSide is { } m ? RoamSides.Wire(m) : null;
        var mirrored = hello.ManualSide is { } mm ? RoamSides.Wire(RoamSides.Mirror(mm)) : null;
        var theirsWins = SpaceIds.Compare(p.Id, Own) < 0;
        if (!p.GotHello)
        {
            p.GotHello = true;
            if (mirrored is not null && mirrored != s.ManualSide && (s.ManualSide is null || theirsWins)) s = s with { ManualSide = mirrored };
            if (hello.CmdAsCtrl != s.CmdAsCtrl && theirsWins) s = s with { CmdAsCtrl = hello.CmdAsCtrl };
        }
        else
        {
            if (remoteManual != p.LastRemoteManual) s = s with { ManualSide = mirrored };
            if (hello.CmdAsCtrl != p.LastRemoteCmd) s = s with { CmdAsCtrl = hello.CmdAsCtrl };
        }
        p.LastRemoteManual = remoteManual;
        p.LastRemoteCmd = hello.CmdAsCtrl;
        _store.Set(p.Id, s);
        if (!hello.Enabled && _controlledBy is { } by && SpaceIds.Same(by, p.Id)) StopControlled();
        Recompute();
    }

    void Inject(double x, double y)
    {
        if (_displays.Count == 0) return;
        var (px, py) = RoamGeometry.DipToPixel(x, y, _displays);
        _injector.Move(px, py);
    }

    /// <summary>Not controlled any more: lift every key and button still down from the other computer.</summary>
    void StopControlled()
    {
        foreach (var k in _injectedKeys.ToList()) _injector.Key(k, false);
        foreach (var b in _injectedButtons.ToList()) _injector.Button(b, false);
        _injectedKeys.Clear();
        _injectedButtons.Clear();
        if (_controlledBy is not null) EndControlled();
        _controlledBy = null;
        SetGhost(null);
        UpdateMode();
        After(() => _space.SetRoamInUse(null));
        After(() => Changed?.Invoke());
    }

    void SetGhost(RoamGhost? ghost)
    {
        if (ghost == _ghost) return;
        _ghost = ghost;
        After(() => GhostChanged?.Invoke(ghost));
    }

    // ---------------- local input ----------------

    void OnInput(RoamInput input)
    {
        Locked(() =>
        {
            if (input.Kind == RoamInputKind.LocalPhysical)
            {
                // §9.1: someone touched this PC's own mouse/keyboard while another computer drove it.
                if (_controlledBy is { } by && PeerFor(by) is { } p)
                {
                    WriteLog($"Roam: local input — releasing {p.Name}");
                    Enqueue(p, RoamWire.Release, RoamWire.ReleaseData());
                    StopControlled();
                }
                return;
            }
            switch (input.Kind)
            {
                case RoamInputKind.Key when _controller.Mode == RoamMode.Away:
                    _stats.Captured();
                    break;
                case RoamInputKind.UnmappedKey:
                    _stats.Unmapped(RoamKeyStats.WindowsCode(input.Dx, input.Dy, input.Down));
                    return;
            }
            _controller.Handle(input);
        });
    }

    void OnTick()
    {
        Locked(() =>
        {
            if (_controller.Mode != RoamMode.Away)
            {
                SetTimer(false);
                return;
            }
            var target = _controller.Target!;
            if (_controller.Tick())
            {
                WriteLog($"Roam: no answer from {target.Name} for 2 s — the cursor came home");
                return;
            }
            if (PeerFor(target.PeerId) is { Link: { } link } p && _clock() - p.LastPing >= RoamWatchdog.PingEvery)
            {
                p.LastPing = _clock();
                After(() => _ = link.SendAsync("ping"));
            }
        });
    }

    void SetTimer(bool on)
    {
        if (on == _timerOn || _disposed) return;
        _timerOn = on;
        try { _timer.Change(on ? _options.Tick : Timeout.InfiniteTimeSpan, on ? _options.Tick : Timeout.InfiniteTimeSpan); } catch { }
    }

    // ---------------- clipboard (§9.4) ----------------

    /// <summary>
    /// What a crossing carries (§9.4, §12.5): the clipboard's text, or — with no text — its image as PNG (≤ 8 MB); null when
    /// nothing changed since the last crossing either way.
    /// </summary>
    async Task<RoamClip?> CrossingClipboardAsync()
    {
        var cb = Clipboard();
        if (cb is null) return null;
        string? text;
        byte[]? png = null;
        try
        {
            text = await Within(cb.GetTextAsync(), _options.ClipboardTimeout).ConfigureAwait(false);
            if (string.IsNullOrEmpty(text)) png = await Within(cb.GetImagePngAsync(), _options.ClipboardTimeout * 3).ConfigureAwait(false);
        }
        catch { return null; }
        if (RoamClip.Pick(text, png) is not { } clip) return null;
        var sig = clip.Signature;
        lock (_gate)
        {
            if (sig == _lastCrossText) return null; // unchanged since the last crossing
            _lastCrossText = sig;
        }
        return clip;
    }

    static async Task<T?> Within<T>(Task<T?> read, TimeSpan timeout) where T : class
        => await Task.WhenAny(read, Task.Delay(timeout)).ConfigureAwait(false) == read ? await read.ConfigureAwait(false) : null;

    void ApplyClipboard(RoamClip? clip)
    {
        if (clip is null) return;
        _lastCrossText = clip.Signature;
        var cb = Clipboard();
        if (cb is null) return;
        if (clip.Text is { } text) After(() => _ = cb.SetTextAsync(text));
        else if (clip.Png is { } png) After(() => _ = cb.SetImageAsync(png));
    }

    // ---------------- the controller's requests ----------------

    sealed class Host : IRoamControllerHost
    {
        readonly RoamEngine _e;
        public Host(RoamEngine e) => _e = e;

        public void Enter(RoamNeighbor peer, double x, double y)
        {
            if (_e.PeerFor(peer.PeerId) is not { } p) return;
            _e.WriteLog($"Roam: going to {peer.Name}");
            p.Outbox.EnqueueDeferred(RoamWire.Enter, EnterData(x, y));
            Wake(p);
            p.LastPing = _e._clock();
            _e.SetTimer(true);
            _e.After(() => _e._space.SetRoamInUse(false));
        }

        async Task<JsonObject> EnterData(double x, double y) => RoamWire.EnterData(x, y, await _e.CrossingClipboardAsync().ConfigureAwait(false));

        async Task<JsonObject> LeaveData() => RoamWire.LeaveData(await _e.CrossingClipboardAsync().ConfigureAwait(false));

        public void Send(string peerId, string type, JsonObject data)
        {
            if (_e.PeerFor(peerId) is not { } p) return;
            if (type == RoamWire.Key) _e._stats.Sent();
            _e.Enqueue(p, type, data);
        }

        public void Rel(string peerId, int dx, int dy)
        {
            if (_e.PeerFor(peerId) is not { } p) return;
            p.Outbox.OfferRel(dx, dy);
            Wake(p);
        }

        public void Pill(RoamPill? pill) => _e.ShowPill(pill);

        public void Move(string peerId, double x, double y)
        {
            if (_e.PeerFor(peerId) is not { } p) return;
            p.Outbox.OfferMove(x, y);
            Wake(p);
        }

        public void Leave(RoamNeighbor peer)
        {
            if (_e.PeerFor(peer.PeerId) is not { } p) return;
            p.Outbox.EnqueueDeferred(RoamWire.Leave, LeaveData());
            Wake(p);
        }

        public void Capture(double parkX, double parkY, bool hide)
        {
            _e._platform.SetMode(RoamInputMode.Capture);
            _e._platform.Park(parkX, parkY, hide);
        }

        public void Uncapture(double x, double y)
        {
            _e._platform.Unpark(x, y);
            _e.UpdateMode();
            _e.SetTimer(false);
            _e.After(() => _e._space.SetRoamInUse(null));
            _e.WriteLog("Roam: back on this PC");
        }

        public void Hint(RoamEdgeHint? hint) => _e.After(() => _e.HintChanged?.Invoke(hint));

        public void ProbeDrag(RoamEdgeHint? at) => _e.After(() => _e.ProbeChanged?.Invoke(at));

        public void Drop(RoamNeighbor peer, double x, double y, IReadOnlyList<string> files)
        {
            if (_e.PeerFor(peer.PeerId) is not { } p || files.Count == 0) return;
            _e.WriteLog($"Roam: dropped {files.Count} file(s) on {peer.Name}");
            // After the button-up is out, start the transfer (flickStart + at, then §6.9) without holding the queue.
            p.Outbox.EnqueueAction("files", () =>
            {
                _ = _e._space.SendFilesDroppedAsync(peer.PeerId, files, x, y);
                return Task.CompletedTask;
            });
            Wake(p);
        }

        public void FinishLocalDrag(bool cancel) => _e._platform.FinishParkedDrag(cancel);

        public void Learned(RoamSide side, RoamNeighbor peer, string? screenId)
        {
            if (peer.InRoom && _e.LearnInRoom(side, peer)) return;
            var before = _e._store.Get(peer.PeerId);
            _e._undo = (peer.PeerId, before.ManualSide);
            _e._store.Set(peer.PeerId, before with { ManualSide = RoamSides.Wire(side) });
            if (_e.PeerFor(peer.PeerId) is { } p) _e.SendHello(p);
            _e.WriteLog($"Roam: {peer.Name} is on the {RoamSides.EdgeWord(side)} now (learned)");
            _e.Recompute();
            var notice = new RoamLearnedNotice(peer.PeerId, peer.Name, side);
            _e.After(() => _e.SideLearned?.Invoke(notice));
        }

        public void StateChanged()
        {
            _e.UpdateMode();
            _e.After(() => _e.Changed?.Invoke());
        }
    }

    void WriteLog(string line)
    {
        try { Log?.Invoke(line); } catch { }
    }

    public void Dispose()
    {
        Locked(() =>
        {
            if (_disposed) return;
            _controller.ReleaseNow();
            if (_controlledBy is not null) StopControlled();
            foreach (var p in _peers.Values) StopPump(p);
            _disposed = true;
        });
        try { _timer.Dispose(); } catch { }
        StopRound2();
        try { _platform.SetMode(RoamInputMode.Off); } catch { }
        try { _platform.Dispose(); } catch { }
    }
}
