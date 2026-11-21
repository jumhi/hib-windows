// H.I.B Space engine (docs/SPACE.md §1–§3): the device directory, activity, the direct peer link,
// Flick in every direction and "Play on…" — behind the SpaceDirectory contract the UI codes against.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HIB.Core.Link;
using HIB.Core.Pairing;
using HIB.Core.Phone;

namespace HIB.Core.Space;

/// <summary>Plays the phone's audio on this computer (implemented with scrcpy by the app's FeatureHost).</summary>
public interface IPhoneAudioPlayer
{
    bool IsActive { get; }
    bool IsStarting { get; }
    bool CanStart { get; }
    string? LastError { get; }
    void Start();
    void Stop();
    event Action? StateChanged;
}

public sealed class SpaceOptions
{
    /// <summary>Direct computer ↔ computer links (SPACE.md §3.3).</summary>
    public bool PeerLinks { get; set; } = true;
    public PeerNetworkOptions Peer { get; set; } = new();
    /// <summary>Keyboard/mouse activity source (tests inject a fake).</summary>
    public Func<ActivitySample> ActivitySampler { get; set; } = DesktopActivity.Sample;
    public TimeSpan ActivityCheck { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan ActivityHeartbeat { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>One computer as the phone's <c>desktopsList</c> describes it (§3.2).</summary>
public sealed record SpaceDesktopEntry(string Id, string Name, string Platform, string Type, bool Connected,
    bool? InUse, int? IdleSeconds, IReadOnlyList<string> Ips, int? Port);

/// <summary>The last <c>desktopsList</c> (§3.2): who the phone says we are, what it sees, who plays.</summary>
public sealed record SpaceListState
{
    public string? You { get; init; }
    public string? PhoneName { get; init; }
    public string? PhoneModel { get; init; }
    public int? PhoneBattery { get; init; }
    public bool? PhoneCharging { get; init; }
    /// <summary>The phone sent the "inUse" field (an extended phone): trust it over local guesses.</summary>
    public bool HasInUse { get; init; }
    public string? InUse { get; init; }
    public bool HasPlayingOn { get; init; }
    public string? PlayingOn { get; init; }
    public IReadOnlyList<SpaceDesktopEntry> Desktops { get; init; } = Array.Empty<SpaceDesktopEntry>();

    public static SpaceListState Parse(JsonElement d, Func<string, bool> isSelf)
    {
        var phone = d.Obj("phone");
        var desktops = new List<SpaceDesktopEntry>();
        if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("desktops", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var id = e.Str("id") ?? e.Str("name");
                if (string.IsNullOrWhiteSpace(id) || isSelf(id)) continue;
                desktops.Add(new SpaceDesktopEntry(id, e.Str("name") ?? id, (e.Str("platform") ?? "mac").ToLowerInvariant(),
                    e.Str("type") ?? "", e.Bool("connected") ?? false, e.Bool("inUse"), e.Int("idleSeconds"),
                    e.StrList("ips"), e.Int("port")));
            }
        }
        bool Has(string name) => d.ValueKind == JsonValueKind.Object && d.TryGetProperty(name, out _);
        return new SpaceListState
        {
            You = d.Str("you"),
            PhoneName = phone?.Str("name"),
            PhoneModel = phone?.Str("model"),
            PhoneBattery = phone?.Int("battery"),
            PhoneCharging = phone?.Bool("charging"),
            HasInUse = Has("inUse"),
            InUse = d.Str("inUse"),
            HasPlayingOn = Has("playingOn"),
            PlayingOn = d.Str("playingOn"),
            Desktops = desktops,
        };
    }
}

/// <summary>
/// The real <see cref="SpaceDirectory"/>: parses the extended desktopsList, keeps the space key (DPAPI) and the
/// remembered peers, runs the direct peer links, reports this computer's activity, routes files and text
/// (direct → through the phone → "isn't reachable"), Flicks to the phone with a preview, and plays phone audio here.
/// </summary>
public sealed class SpaceEngine : SpaceDirectory, IDisposable
{
    readonly LinkServer _server;
    readonly FileReceiver _files;
    readonly SpaceOptions _options;
    readonly SpaceKeyStore _keys;
    readonly SpacePeerStore _peers;
    readonly PeerNetwork _net;
    readonly object _gate = new();
    readonly Dictionary<PeerLink, FileSender> _peerSenders = new();
    /// <summary>Outgoing transfer id → device id, so phone-link progress can say where it goes.</summary>
    readonly ConcurrentDictionary<string, string> _transferDevice = new();
    /// <summary>When each device was last connected (this run); remembered computers fall back to space.json.</summary>
    readonly ConcurrentDictionary<string, DateTimeOffset> _lastSeen = new(StringComparer.OrdinalIgnoreCase);
    SpaceListState _list = new();
    ActivitySample _own;
    DateTime _lastHeartbeat = DateTime.MinValue;
    IPhoneAudioPlayer? _audio;
    CancellationTokenSource? _cts;

    public SpaceEngine(LinkServer link, PhoneEcosystem ecosystem, FileSender sender, FileReceiver files, PhoneHub hub,
        string dataDir, SpaceOptions? options = null)
        : base(link, ecosystem, sender)
    {
        _server = link;
        _files = files;
        _options = options ?? new SpaceOptions();
        _keys = new SpaceKeyStore(dataDir);
        _peers = new SpacePeerStore(dataDir);
        _own = new ActivitySample(false, -1);
        _net = new PeerNetwork(_keys.Load, OwnHello, () => _peers.All, _options.Peer);
        _net.Log += WriteLog;
        _net.HelloReceived += OnPeerHello;
        _net.LinkUp += OnLinkUp;
        _net.LinkDown += OnLinkDown;
        _net.ActivityChanged += _ => RaiseChanged();

        link.MessageReceived += OnPhoneMessage;
        hub.FlickStarted += RaiseFlickArrived;
        sender.Progress += OnPhoneSendProgress;
        link.ConnectionChanged += (_, _) => _lastSeen[SpaceIds.Phone] = DateTimeOffset.Now;

        if (_options.PeerLinks)
        {
            _server.PeerHandler = _net.HandleIncomingAsync;
            _server.PeerAccepts = () => _net.Running && _keys.Load() is not null;
        }
    }

    public event Action<string>? Log;

    /// <summary>The phone paired last (shown as an Offline phone before it connects this run).</summary>
    public Func<string?>? RememberedPhoneName { get; set; }

    // ---------------- lifecycle ----------------

    /// <summary>Starts the dialler (remembered peers link up without the phone) and the activity reports.</summary>
    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        if (_options.PeerLinks) _net.Start();
        _ = ActivityLoopAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts = null;
        _net.Dispose();
    }

    /// <summary>The app attaches the scrcpy audio bridge here (SPACE.md §3.5).</summary>
    public void AttachAudio(IPhoneAudioPlayer player)
    {
        _audio = player;
        player.StateChanged += RaiseChanged;
        RaiseChanged();
    }

    // ---------------- state for the UI ----------------

    public SpaceListState ListState { get { lock (_gate) return _list; } }

    /// <summary>The direct links that are up right now.</summary>
    public IReadOnlyList<PeerLink> PeerLinks => _net.Links;

    /// <summary>Remembered computers (id, ips, port…), saved with the space key.</summary>
    public IReadOnlyList<KnownPeer> KnownPeers => _peers.All;

    public PeerNetwork Network => _net;

    public ActivitySample OwnActivity { get { lock (_gate) return _own; } }

    public override bool HasSpaceKey => _keys.Load() is not null;

    public override string? LastTargetId => _peers.LastTarget;

    public override bool CanPlayHere => _audio?.CanStart == true;

    /// <summary>The phone-audio player the app attached (scrcpy), or null.</summary>
    public IPhoneAudioPlayer? Audio => _audio;

    bool IsSelf(string id)
        => string.Equals(id, ThisDeviceName, StringComparison.OrdinalIgnoreCase)
           || (_list.You is { Length: > 0 } you && SpaceIds.Same(id, you));

    string? Normalize(string? id) => id is null ? null : IsSelf(id) ? ThisDeviceName : id;

    public override string? InUseId
    {
        get
        {
            SpaceListState list;
            ActivitySample own;
            lock (_gate) { list = _list; own = _own; }
            if (Link.IsConnected && list.HasInUse) return Normalize(list.InUse);
            // No (extended) phone: newest activity among this computer and the directly linked ones.
            string? best = null;
            var bestAt = DateTime.MinValue;
            if (own.InUse && own.IdleSeconds >= 0)
            {
                best = ThisDeviceName;
                bestAt = DateTime.UtcNow - TimeSpan.FromSeconds(own.IdleSeconds);
            }
            foreach (var l in _net.Links)
            {
                if (!l.PeerInUse || l.PeerLastActiveAt is not { } at) continue;
                if (best is null || at > bestAt)
                {
                    best = l.PeerId;
                    bestAt = at;
                }
            }
            return best;
        }
    }

    public override string? PlayingOn
    {
        get
        {
            if (_audio is { } a && (a.IsActive || a.IsStarting)) return ThisDeviceName;
            SpaceListState list;
            lock (_gate) list = _list;
            if (!Link.IsConnected || !list.HasPlayingOn) return null;
            var p = Normalize(list.PlayingOn);
            return p == ThisDeviceName ? null : p; // the phone thinks so, but nothing plays here
        }
    }

    public override IReadOnlyList<SpaceDevice> Devices
    {
        get
        {
            SpaceListState list;
            lock (_gate) list = _list;
            var phoneUp = Link.IsConnected;
            var inUse = InUseId;
            var playing = PlayingOn;
            var result = new List<SpaceDevice>();

            var phone = Link.Device;
            var remembered = RememberedPhoneName?.Invoke();
            var phoneName = phone?.Name ?? list.PhoneName ?? remembered;
            if (phone is not null || phoneUp || list.PhoneName is not null || remembered is not null)
            {
                int? battery = phone is { BatteryLevel: >= 0 } ? phone.BatteryLevel : list.PhoneBattery is >= 0 ? list.PhoneBattery : null;
                result.Add(new SpaceDevice(SpaceIds.Phone, phoneName ?? "Phone", SpaceDeviceKind.Phone,
                    list.PhoneModel ?? "Android phone", phoneUp, false,
                    phoneUp ? SpaceRoute.PhoneLink : SpaceRoute.Unavailable, battery, false)
                {
                    LastSeen = phoneUp ? DateTimeOffset.Now : _lastSeen.TryGetValue(SpaceIds.Phone, out var ps) ? ps : null,
                });
            }

            var links = _net.Links.ToDictionary(l => l.PeerId, StringComparer.Ordinal);
            var listed = phoneUp ? list.Desktops.ToDictionary(d => d.Id, StringComparer.Ordinal) : new Dictionary<string, SpaceDesktopEntry>(StringComparer.Ordinal);
            var order = new List<string>();
            void Add(string id)
            {
                if (!IsSelf(id) && !order.Contains(id)) order.Add(id);
            }
            foreach (var d in list.Desktops) Add(d.Id);
            foreach (var id in links.Keys) Add(id);
            // Remembered computers show as Offline when there's no phone to ask (or the phone still lists them).
            foreach (var p in _peers.All)
            {
                if (!phoneUp || listed.ContainsKey(p.Id) || links.ContainsKey(p.Id)) Add(p.Id);
            }

            var computers = new List<SpaceDevice>();
            foreach (var id in order)
            {
                listed.TryGetValue(id, out var entry);
                entry ??= list.Desktops.FirstOrDefault(d => d.Id == id) is { } stale ? stale with { Connected = false } : null;
                links.TryGetValue(id, out var link);
                var known = _peers.Get(id);
                var platform = entry?.Platform ?? link?.Hello?.Platform ?? known?.Platform ?? "mac";
                var kind = SpaceIds.KindFor(platform);
                var name = entry?.Name ?? link?.Hello?.Name ?? known?.Name ?? id;
                var type = entry?.Type is { Length: > 0 } t ? t : link?.Hello?.Type is { Length: > 0 } ht ? ht : known?.Type is { Length: > 0 } kt ? kt : SpaceIds.DefaultModel(kind);
                var viaPhone = phoneUp && entry is { Connected: true };
                var direct = link is not null;
                var route = direct ? SpaceRoute.Direct : viaPhone ? SpaceRoute.ViaPhone : SpaceRoute.Unavailable;
                var online = direct || viaPhone;
                DateTimeOffset? seen = online ? DateTimeOffset.Now
                    : _lastSeen.TryGetValue(id, out var ls) ? ls
                    : known is { LastSeen: var kls } && kls != default ? kls : null;
                computers.Add(new SpaceDevice(id, name, kind, type, online, inUse is not null && SpaceIds.Same(inUse, id),
                    route, null, playing is not null && SpaceIds.Same(playing, id)) { LastSeen = seen });
            }
            result.AddRange(computers.OrderByDescending(c => c.Online));
            return result;
        }
    }

    /// <summary>
    /// SPACE.md §1.4 from a computer (explicit pick and aim are the caller's): the computer in use (never this
    /// one), else the last one used, else the phone, else any reachable computer.
    /// </summary>
    public override SpaceDevice? DefaultTarget
    {
        get
        {
            var devices = Devices;
            var inUse = InUseId;
            if (inUse is not null && devices.FirstOrDefault(d => !d.IsPhone && d.Id == inUse && d.CanReceive) is { } used) return used;
            var last = _peers.LastTarget;
            if (last is not null && devices.FirstOrDefault(d => d.Id == last && d.CanReceive) is { } lastUsed) return lastUsed;
            return devices.FirstOrDefault(d => d.IsPhone && d.CanReceive) ?? devices.FirstOrDefault(d => d.CanReceive);
        }
    }

    // ---------------- the phone ----------------

    void OnPhoneMessage(object? sender, LinkMessageEventArgs e)
    {
        switch (e.Type)
        {
            case "desktopsList":
                OnDesktopsList(e.Data);
                break;
            case "device":
                // Right after the handshake (macInfo is already queued ahead of this).
                _ = Task.Delay(250).ContinueWith(_ => SendActivityToPhone(OwnActivity), TaskScheduler.Default);
                break;
        }
    }

    /// <summary>Applies a <c>desktopsList</c> (also used by tests).</summary>
    public void OnDesktopsList(JsonElement data)
    {
        var state = SpaceListState.Parse(data, id => string.Equals(id, ThisDeviceName, StringComparison.OrdinalIgnoreCase)
                                                    || (data.Str("you") is { Length: > 0 } you && SpaceIds.Same(id, you)));
        lock (_gate) _list = state;

        if (data.Obj("space") is { } space && LenientJson.Base64(space.Str("key")) is { Length: LinkCrypto.KeySize } key)
        {
            if (_keys.Save(key))
            {
                WriteLog("Space key received from the phone");
                _net.DropAll("Space key changed");
            }
        }
        foreach (var d in state.Desktops)
        {
            if (d.Connected) _lastSeen[d.Id] = DateTimeOffset.Now;
            if (d.Ips.Count > 0 && d.Port is > 0)
                _peers.Upsert(d.Id, d.Name, d.Platform, d.Type, d.Ips, d.Port, seen: d.Connected);
            else if (_peers.Get(d.Id) is not null)
                _peers.Upsert(d.Id, d.Name, d.Platform, d.Type, null, null, seen: d.Connected);
        }
        _net.ResetBackoff();
        RaiseChanged();
    }

    /// <summary>Stores a space key directly (tests, or a future manual "join" flow).</summary>
    public void SetSpaceKey(byte[] key)
    {
        if (_keys.Save(key)) _net.DropAll("Space key changed");
        RaiseChanged();
    }

    /// <summary>Remembers a computer so the dialler links to it (tests, manual add).</summary>
    public void RememberPeer(string id, string name, string platform, IEnumerable<string> ips, int port)
    {
        _peers.Upsert(id, name, platform, null, ips, port);
        _net.ResetBackoff(id);
        RaiseChanged();
    }

    // ---------------- the peer link ----------------

    PeerHello OwnHello()
    {
        var ips = new List<string>();
        try { ips.AddRange(NetworkInfo.GetLocalIPv4().Select(a => a.Address)); } catch { }
        return new PeerHello(ThisDeviceName, ThisDeviceName, SpaceIds.ThisPlatform, SpaceIds.ThisType, _server.Port, ips, 1);
    }

    void OnPeerHello(PeerLink link)
    {
        if (link.Hello is not { } h) return;
        // Remember where it answered from first, then what it says about itself.
        _peers.Upsert(h.Id, h.Name, h.Platform, h.Type, h.Ips, h.Port > 0 ? h.Port : null, link.RemoteIp, seen: true);
        _net.ResetBackoff(h.Id);
    }

    void OnLinkUp(PeerLink link)
    {
        var sender = new FileSender(link)
        {
            NotConnectedText = $"{link.PeerName} isn't linked",
            LostText = $"The link to {link.PeerName} dropped",
        };
        sender.Log += WriteLog;
        sender.Progress += p => RaiseSendProgress(link.PeerId, p);
        _lastSeen[link.PeerId] = DateTimeOffset.Now;
        lock (_gate) _peerSenders[link] = sender;
        _files.Attach(link);
        link.MessageReceived += OnPeerMessage;
        _ = link.SendAsync("peerActivity", ActivityData(OwnActivity));
        RaiseChanged();
    }

    void OnLinkDown(PeerLink link)
    {
        _lastSeen[link.PeerId] = DateTimeOffset.Now;
        _peers.Touch(link.PeerId);
        lock (_gate) _peerSenders.Remove(link);
        _files.Detach(link);
        link.MessageReceived -= OnPeerMessage;
        RaiseChanged();
    }

    void OnPhoneSendProgress(SendProgress p)
    {
        var device = _transferDevice.TryGetValue(p.Id, out var d) ? d
            // Sent by someone else straight through Core.Sender: guess from the target's name.
            : Devices.FirstOrDefault(x => !x.IsPhone && string.Equals(x.Name, p.TargetName, StringComparison.OrdinalIgnoreCase))?.Id ?? SpaceIds.Phone;
        if (p.Done) _transferDevice.TryRemove(p.Id, out _);
        RaiseSendProgress(device, p);
    }

    FileSender? SenderFor(PeerLink link)
    {
        lock (_gate) return _peerSenders.TryGetValue(link, out var s) ? s : null;
    }

    void OnPeerMessage(object? sender, LinkMessageEventArgs e)
    {
        if (sender is not PeerLink link) return;
        switch (e.Type)
        {
            case "flickStart":
                var d = e.Data;
                var id = d.Str("id");
                if (string.IsNullOrEmpty(id)) return;
                var info = new FlickInfo(id, (d.Str("kind") ?? "file").ToLowerInvariant(), d.Str("name") ?? "", d.Str("mime"),
                    d.Long("size") ?? 0, LenientJson.Base64(d.Str("thumbnail")), d.Str("text"))
                {
                    Angle = d.Double("angle") ?? 0,
            ExitXRatio = d.Double("exitXRatio") ?? 0.5,
            Velocity = d.Double("velocity") ?? 1800,
                    From = d.Str("from") is { Length: > 0 } from ? from : link.PeerName,
                    At = HIB.Core.Roam.RoamWire.At(d),
                };
                RaiseFlickArrived(info);
                if (info.IsTransferless) _ = link.SendAsync("flickLanded", new { id });
                break;
            case "clipboardUpdate":
            case "clipboardImageUpdate":
                break; // never on the peer link (the phone already shares the clipboard)
        }
    }

    // ---------------- activity (§3.1) ----------------

    static object ActivityData(ActivitySample a) => new { inUse = a.InUse, idleSeconds = a.IdleSeconds };

    void SendActivityToPhone(ActivitySample a)
    {
        if (Link.IsConnected) _ = Link.SendAsync("desktopActivity", ActivityData(a));
    }

    async Task ActivityLoopAsync(CancellationToken ct)
    {
        try { Sample(force: true); } catch { }
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_options.ActivityCheck, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try { Sample(force: false); } catch (Exception ex) { WriteLog("Activity: " + ex.Message); }
        }
    }

    /// <summary>
    /// Roam (SPACE.md §9.6, "in use follows the cursor"): true while another computer drives this one, false while this one
    /// drives another, null for the normal keyboard/mouse sample. Reported to the phone and peers at once.
    /// </summary>
    public void SetRoamInUse(bool? inUse)
    {
        lock (_gate)
        {
            if (_roamInUse == inUse) return;
            _roamInUse = inUse;
        }
        try { Sample(force: true); } catch (Exception ex) { WriteLog("Activity: " + ex.Message); }
    }

    bool? _roamInUse;

    void Sample(bool force)
    {
        var s = _options.ActivitySampler();
        bool? roam;
        lock (_gate) roam = _roamInUse;
        if (roam is { } r) s = r ? new ActivitySample(true, 0) : new ActivitySample(false, Math.Max(s.IdleSeconds, DesktopActivity.InUseWindowSeconds));
        bool flipped;
        bool heartbeat;
        lock (_gate)
        {
            flipped = s.InUse != _own.InUse;
            _own = s;
            heartbeat = force || DateTime.UtcNow - _lastHeartbeat >= _options.ActivityHeartbeat;
            if (flipped || heartbeat) _lastHeartbeat = DateTime.UtcNow;
        }
        if (!flipped && !heartbeat) return;
        SendActivityToPhone(s);
        foreach (var l in _net.Links) _ = l.SendAsync("peerActivity", ActivityData(s));
        if (flipped) RaiseChanged();
    }

    // ---------------- sending (§3.3, §3.4) ----------------

    bool PhoneRelays(string id)
    {
        if (!Link.IsConnected) return false;
        SpaceListState list;
        lock (_gate) list = _list;
        if (list.Desktops.FirstOrDefault(d => SpaceIds.Same(d.Id, id)) is { } entry) return entry.Connected;
        return Ecosystem.Targets.Any(t => !t.IsPhone && SpaceIds.Same(t.Id, id));
    }

    Dictionary<string, object?> FlickStartData(string id, string kind, string name, string mime, long size, byte[]? thumbnail, bool toPeer,
        (double X, double Y)? at = null)
    {
        var d = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["kind"] = kind,
            ["name"] = name,
            ["mime"] = mime,
            ["size"] = size,
            ["from"] = ThisDeviceName,
            ["fromPlatform"] = SpaceIds.ThisPlatform,
        };
        if (thumbnail is { Length: > 0 }) d["thumbnail"] = Convert.ToBase64String(thumbnail);
        if (toPeer)
        {
            d["exitXRatio"] = 0.5;
            d["velocity"] = 1800.0;
            d["angle"] = 0.0;
        }
        if (at is { } p) d["at"] = new Dictionary<string, object?> { ["x"] = Math.Round(p.X, 2), ["y"] = Math.Round(p.Y, 2) };
        return d;
    }

    static Task<byte[]?> ThumbnailAsync(string path, string kind)
        => kind == "image" ? Task.Run(() => FlickThumbnail.TryMakeJpeg(path)) : Task.FromResult<byte[]?>(null);

    public override async Task<IReadOnlyList<SendResult>> SendFilesAsync(string deviceId, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        var results = new List<SendResult>();
        var target = Find(deviceId);
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (IsSelf(deviceId))
            {
                results.Add(new SendResult(Guid.NewGuid().ToString(), name, false, "That's this computer"));
                continue;
            }
            if (Directory.Exists(path))
            {
                results.Add(new SendResult(Guid.NewGuid().ToString(), name, false, "Folders can't be sent"));
                continue;
            }
            if (target is null || !target.CanReceive)
            {
                results.Add(new SendResult(Guid.NewGuid().ToString(), name, false, $"{target?.Name ?? deviceId} isn't reachable"));
                continue;
            }
            results.Add(await SendOneAsync(target, path, ct).ConfigureAwait(false));
        }
        if (target is not null && results.Any(r => r.Ok)) _peers.LastTarget = target.Id;
        return results;
    }

    /// <summary>
    /// Roam file drag (SPACE.md §9.5): the files dropped on another computer at (<paramref name="x"/>, <paramref name="y"/>)
    /// in its points. Direct link only (the drop happened over it); each flickStart carries <c>"at"</c>.
    /// </summary>
    public async Task<IReadOnlyList<SendResult>> SendFilesDroppedAsync(string deviceId, IReadOnlyList<string> paths, double x, double y, CancellationToken ct = default)
    {
        var results = new List<SendResult>();
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (Directory.Exists(path))
            {
                results.Add(new SendResult(Guid.NewGuid().ToString(), name, false, "Folders can't be sent"));
                continue;
            }
            var target = Find(deviceId);
            if (target is null || IsSelf(deviceId) || _net.Get(deviceId) is null)
            {
                results.Add(new SendResult(Guid.NewGuid().ToString(), name, false, $"{target?.Name ?? deviceId} isn't reachable"));
                continue;
            }
            results.Add(await SendOneAsync(target, path, ct, (x, y)).ConfigureAwait(false));
        }
        if (results.Any(r => r.Ok)) _peers.LastTarget = deviceId;
        return results;
    }

    async Task<SendResult> SendOneAsync(SpaceDevice target, string path, CancellationToken ct, (double X, double Y)? at = null)
    {
        var id = Guid.NewGuid().ToString();
        var name = Path.GetFileName(path);
        if (!File.Exists(path)) return new SendResult(id, name, false, "File not found");
        var size = new FileInfo(path).Length;
        var mime = FileSender.MimeFor(name);
        var kind = SpaceIds.KindForMime(mime);

        if (target.IsPhone)
        {
            // §3.4: flickStart with a preview first; an old phone ignores it and still gets the file.
            var thumb = await ThumbnailAsync(path, kind).ConfigureAwait(false);
            _transferDevice[id] = SpaceIds.Phone;
            await Link.SendAsync("flickStart", FlickStartData(id, kind, name, mime, size, thumb, toPeer: false), ct).ConfigureAwait(false);
            return await Sender.SendAsync(path, null, target.Name, id, new Dictionary<string, object?> { ["flickId"] = id }, ct).ConfigureAwait(false);
        }

        if (_net.Get(target.Id) is { } link && SenderFor(link) is { } direct)
        {
            var thumb = await ThumbnailAsync(path, kind).ConfigureAwait(false);
            if (await link.SendAsync("flickStart", FlickStartData(id, kind, name, mime, size, thumb, toPeer: true, at), ct).ConfigureAwait(false))
            {
                var r = await direct.SendAsync(path, null, target.Name, id, new Dictionary<string, object?>
                {
                    ["flickId"] = id,
                    ["relayFrom"] = ThisDeviceName,
                    ["relayFromPlatform"] = SpaceIds.ThisPlatform,
                }, ct).ConfigureAwait(false);
                if (r.Ok || ct.IsCancellationRequested || link.IsOpen || !PhoneRelays(target.Id)) return r;
                WriteLog($"Direct link to {target.Name} dropped mid-transfer — retrying through the phone");
            }
        }

        if (PhoneRelays(target.Id))
        {
            var relayId = Guid.NewGuid().ToString();
            _transferDevice[relayId] = target.Id;
            return await Sender.SendAsync(path, target.Id, target.Name, relayId, null, ct).ConfigureAwait(false);
        }

        return new SendResult(id, name, false, $"{target.Name} isn't reachable");
    }

    public override async Task<bool> SendTextAsync(string deviceId, string text)
    {
        if (string.IsNullOrEmpty(text) || IsSelf(deviceId)) return false;
        if (text.Length > 100_000) text = text[..100_000];
        var id = Guid.NewGuid().ToString();
        var kind = SpaceIds.TextKind(text);
        var data = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["kind"] = kind,
            ["name"] = "",
            ["text"] = kind == "link" ? text.Trim() : text,
            ["from"] = ThisDeviceName,
            ["fromPlatform"] = SpaceIds.ThisPlatform,
        };
        bool ok;
        if (deviceId == SpaceIds.Phone)
        {
            ok = Link.IsConnected && await Link.SendAsync("flickStart", data).ConfigureAwait(false);
            // Phones without the Space update ignore flickStart: the shared clipboard still gets it there (like the Mac).
            if (ok && !ListState.HasInUse) await Link.SendAsync("clipboardUpdate", new { text }).ConfigureAwait(false);
        }
        else if (_net.Get(deviceId) is { } link)
        {
            data["exitXRatio"] = 0.5;
            data["velocity"] = 1800.0;
            data["angle"] = 0.0;
            ok = await link.SendAsync("flickStart", data).ConfigureAwait(false);
        }
        else if (PhoneRelays(deviceId))
        {
            // No direct link: the phone shares the clipboard with every computer (§6.22), as the Mac does.
            ok = await Link.SendAsync("clipboardUpdate", new { text }).ConfigureAwait(false);
        }
        else
        {
            ok = false;
        }
        if (ok) _peers.LastTarget = deviceId;
        return ok;
    }

    // ---------------- Play on… (§3.5) ----------------

    public override async Task<bool> PlayHereAsync(bool on)
    {
        var a = _audio;
        if (a is null) return false;
        if (!on)
        {
            a.Stop();
            RaiseChanged();
            return true;
        }
        if (a.IsActive) return true;
        a.Start();
        RaiseChanged();
        // The bridge sends phoneAudioState {active:true} itself once scrcpy streams; the phone then stops the others.
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < until)
        {
            if (a.IsActive) break;
            if (!a.IsStarting && a.LastError is not null) break;
            await Task.Delay(100).ConfigureAwait(false);
        }
        RaiseChanged();
        return a.IsActive;
    }

    void WriteLog(string line)
    {
        try { Log?.Invoke(line); } catch { }
    }
}
