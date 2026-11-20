// H.I.B Space (docs/SPACE.md): the phone and every computer as one list of devices.
//
// CONTRACT shared by the UI (Views/ViewModels) and the engine (peer link, activity, Flick).
// The UI only uses the public members below. The engine may add members and replace the
// implementation, but must keep these signatures.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HIB.Core.Link;
using HIB.Core.Phone;

namespace HIB.Core.Space;

public enum SpaceDeviceKind { Phone, Mac, Windows }

/// <summary>How this computer reaches a device.</summary>
public enum SpaceRoute
{
    /// <summary>Not reachable right now.</summary>
    Unavailable,
    /// <summary>The phone itself, over the phone link.</summary>
    PhoneLink,
    /// <summary>Another computer, over the direct peer link (SPACE.md §3.3).</summary>
    Direct,
    /// <summary>Another computer, relayed by the phone (PROTOCOL.md §6.22).</summary>
    ViaPhone,
}

public sealed record SpaceDevice(
    string Id,
    string Name,
    SpaceDeviceKind Kind,
    string Model,
    bool Online,
    bool InUse,
    SpaceRoute Route,
    int? Battery,
    bool IsPlayingAudio)
{
    public bool IsPhone => Kind == SpaceDeviceKind.Phone;

    /// <summary>When this device was last connected (now while online); null when never seen.</summary>
    public DateTimeOffset? LastSeen { get; init; }
    public bool CanReceive => Route != SpaceRoute.Unavailable;

    /// <summary>"Connected", "In use", "Offline".</summary>
    public string StatusText => !Online ? "Offline" : InUse ? "In use" : "Connected";

    /// <summary>"Direct", "Through phone", "" (phone) or "Not reachable".</summary>
    public string RouteText => Route switch
    {
        SpaceRoute.Direct => "Direct",
        SpaceRoute.ViaPhone => "Through phone",
        SpaceRoute.PhoneLink => "",
        _ => "Not reachable",
    };
}

/// <summary>
/// Every other device in the space (never this computer), phone first.
/// Minimal implementation: phone link + phone relay. The engine adds the direct peer link,
/// activity ("in use"), play coordination and Flick to the phone.
/// </summary>
public class SpaceDirectory
{
    protected readonly ILinkSession Link;
    protected readonly PhoneEcosystem Ecosystem;
    protected readonly FileSender Sender;

    public SpaceDirectory(ILinkSession link, PhoneEcosystem ecosystem, FileSender sender)
    {
        Link = link;
        Ecosystem = ecosystem;
        Sender = sender;
        ecosystem.Changed += RaiseChanged;
        link.ConnectionChanged += (_, _) => RaiseChanged();
        link.DeviceUpdated += (_, _) => RaiseChanged();
    }

    /// <summary>Raised (on any thread) whenever <see cref="Devices"/> or a device's state changes.</summary>
    public event Action? Changed;

    protected void RaiseChanged() => Changed?.Invoke();

    /// <summary>This computer's own id/name.</summary>
    public string ThisDeviceName => Link.DesktopName;

    /// <summary>The phone first, then other computers (online first). Never this computer.</summary>
    public virtual IReadOnlyList<SpaceDevice> Devices
    {
        get
        {
            var list = new List<SpaceDevice>();
            var phone = Link.Device;
            if (phone is not null || Link.IsConnected)
            {
                list.Add(new SpaceDevice("phone", phone?.Name ?? "Phone", SpaceDeviceKind.Phone, "Android phone",
                    Link.IsConnected, false, Link.IsConnected ? SpaceRoute.PhoneLink : SpaceRoute.Unavailable,
                    phone is { BatteryLevel: >= 0 } ? phone.BatteryLevel : null, false));
            }
            foreach (var d in Ecosystem.AllDesktops.OrderByDescending(d => d.Connected))
            {
                var kind = d.Platform == "windows" ? SpaceDeviceKind.Windows : SpaceDeviceKind.Mac;
                var route = d.Connected && Link.IsConnected ? SpaceRoute.ViaPhone : SpaceRoute.Unavailable;
                list.Add(new SpaceDevice(d.Id, d.Name, kind, kind == SpaceDeviceKind.Windows ? "Windows PC" : "Mac",
                    d.Connected, false, route, null, false));
            }
            return list;
        }
    }

    /// <summary>The device Flick/Send/Play go to when the user didn't pick one (SPACE.md §1.4).</summary>
    public virtual SpaceDevice? DefaultTarget =>
        Devices.FirstOrDefault(d => d.InUse && d.CanReceive && !d.IsPhone)
        ?? Devices.FirstOrDefault(d => d.CanReceive);

    /// <summary>Id of the computer playing the phone's audio, or null.</summary>
    public virtual string? PlayingOn => null;

    public SpaceDevice? Find(string id) => Devices.FirstOrDefault(d => d.Id == id);

    /// <summary>Sends files to a device (direct link, phone relay or the phone itself).</summary>
    public virtual async Task<IReadOnlyList<SendResult>> SendFilesAsync(string deviceId, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        var target = Find(deviceId);
        var results = new List<SendResult>();
        foreach (var path in paths)
        {
            if (target is null || !target.CanReceive)
            {
                results.Add(new SendResult(Guid.NewGuid().ToString(), System.IO.Path.GetFileName(path), false,
                    $"{target?.Name ?? deviceId} isn't reachable"));
                continue;
            }
            results.Add(await Sender.SendAsync(path, target.IsPhone ? null : target.Id, target.Name, ct).ConfigureAwait(false));
        }
        return results;
    }

    /// <summary>Flicks text or a link to a device (SPACE.md §3.3/§3.4).</summary>
    public virtual Task<bool> SendTextAsync(string deviceId, string text) => Task.FromResult(false);

    /// <summary>Starts/stops playing the phone's audio on this computer (SPACE.md §3.5).</summary>
    public virtual Task<bool> PlayHereAsync(bool on) => Task.FromResult(false);

    // ---------------- additions (engine; safe defaults here) ----------------

    /// <summary>This computer as a device (for the small "This PC" card). Route is always Unavailable.</summary>
    public virtual SpaceDevice ThisDevice => new(ThisDeviceName, ThisDeviceName, SpaceDeviceKind.Windows, "Windows PC",
        true, ThisDeviceInUse, SpaceRoute.Unavailable, null, IsPlayingHere) { LastSeen = DateTimeOffset.Now };

    /// <summary>Id of the computer in use right now (may be <see cref="ThisDeviceName"/>), or null.</summary>
    public virtual string? InUseId => null;

    /// <summary>True when this computer is the one in use (the "This PC" card's "In use").</summary>
    public virtual bool ThisDeviceInUse => InUseId is { Length: > 0 } id && string.Equals(id, ThisDeviceName, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the phone's audio plays on this computer (<see cref="PlayingOn"/> is this one).</summary>
    public virtual bool IsPlayingHere => PlayingOn is { Length: > 0 } p && string.Equals(p, ThisDeviceName, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the phone audio can be started here (phone linked over ADB, scrcpy present).</summary>
    public virtual bool CanPlayHere => false;

    /// <summary>True once the phone has given this computer the space key (direct links possible).</summary>
    public virtual bool HasSpaceKey => false;

    /// <summary>The device "Send to…" used last (target rule step 4), or null.</summary>
    public virtual string? LastTargetId => null;

    /// <summary>
    /// Every incoming Flick (from the phone, from another computer through the phone, or over a
    /// direct peer link). Raised on a background thread. Files then arrive on <c>HibCore.Files</c>.
    /// </summary>
    public event Action<FlickInfo>? FlickArrived;

    /// <summary>
    /// Progress of every outgoing file, with the id of the device it goes to ("phone" or a computer's id),
    /// whatever the route (phone link, direct, through phone). Raised on a background thread.
    /// </summary>
    public event Action<string, SendProgress>? SendProgress;

    protected void RaiseFlickArrived(FlickInfo flick) => FlickArrived?.Invoke(flick);
    protected void RaiseSendProgress(string deviceId, SendProgress progress) => SendProgress?.Invoke(deviceId, progress);
}
