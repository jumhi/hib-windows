// H.I.B Phone Remote (docs/SPACE.md §15): what the screen stream and the remote need from the operating system.
// Windows: DXGI Desktop Duplication + Media Foundation H.264 (Windows/), WTS lock state, SetThreadExecutionState,
// UI Automation focus and the domain / Entra join state. Elsewhere (and in tests): nothing, or fakes.
using System;
using System.Collections.Generic;

namespace HIB.Core.Remote;

public enum ScreenCodec { H264, Jpeg }

/// <summary>
/// What to capture and how: the display (desktop pixels; empty = the main display, the pipeline says which with
/// <see cref="ScreenPipelineEvent.SourceChanged"/>), the crop (pixels relative to the display), the encoded size and rate.
/// </summary>
public sealed record ScreenPipelineConfig(string DisplayId, PixelBox Display, PixelBox Crop, int OutW, int OutH, int Fps, int Bitrate, ScreenCodec Codec);

/// <summary>
/// One encoded picture: an H.264 access unit (Annex-B; SPS/PPS may be inside — the session splits them out) or a JPEG,
/// with the cursor in desktop pixels when it was taken and the config it was made with.
/// </summary>
public sealed record PipelineFrame(byte[] Data, bool IsKey, long PtsMicros, double CursorX, double CursorY, bool CursorVisible, ScreenPipelineConfig Config)
{
    public bool IsJpeg => Config.Codec == ScreenCodec.Jpeg;
}

public enum ScreenPipelineEventKind
{
    /// <summary>Capture refused for now (the secure desktop: lock screen, a UAC prompt). The pipeline keeps retrying.</summary>
    Blocked,
    /// <summary>Capture works again after <see cref="Blocked"/>.</summary>
    Recovered,
    /// <summary>The display's bounds are not what the config said (resolution change, or "main" resolved): restart with them.</summary>
    SourceChanged,
    /// <summary>No H.264 encoder works here: the session falls back to JPEG.</summary>
    EncoderFailed,
    /// <summary>Capture can't work at all (no display, no GPU access).</summary>
    Failed,
}

public sealed record ScreenPipelineEvent(ScreenPipelineEventKind Kind, string? DisplayId = null, PixelBox Display = default, string? Detail = null);

/// <summary>Capture + encode for one /screen session. Events fire on the pipeline's own thread.</summary>
public interface IScreenPipeline : IDisposable
{
    /// <summary>Starts (or restarts with) this config; the first frame after it is a keyframe with SPS/PPS.</summary>
    void Start(ScreenPipelineConfig config);
    void SetRate(int bitrate, int fps);
    void RequestKeyframe();
    /// <summary>Stops capturing (the computer is locked); <see cref="Start"/> resumes.</summary>
    void Pause();
    /// <summary>"hardware H.264", "software H.264" or "JPEG", once known (for the log).</summary>
    string? EncoderName { get; }
    event Action<PipelineFrame>? Frame;
    event Action<ScreenPipelineEvent>? Event;
}

public interface IScreenPipelineFactory
{
    /// <summary>False where there is no capture (other systems): remoteHello says screen:false.</summary>
    bool IsAvailable { get; }
    IScreenPipeline Create();
}

public sealed class NullScreenPipelineFactory : IScreenPipelineFactory
{
    public bool IsAvailable => false;
    public IScreenPipeline Create() => throw new PlatformNotSupportedException("No screen capture here");
}

/// <summary>UI Automation keyboard focus (only while a /screen session is open).</summary>
public interface IRemoteFocusSource : IDisposable
{
    /// <summary>The focused element now, after each change (null = nothing readable). Background thread.</summary>
    event Action<FocusFacts?>? Changed;
    void Start();
}

/// <summary>The rest of the operating system the remote touches.</summary>
public interface IRemoteSystem : IDisposable
{
    /// <summary>Is this session locked now? Null when unknown.</summary>
    bool? IsLocked();
    bool OnMainsPower { get; }
    /// <summary>Holds (or drops) the keep-awake assertion.</summary>
    void SetAwake(RemoteAwake need);
    /// <summary>Domain-joined / Entra-joined (managed PC).</summary>
    (bool Domain, bool Entra) JoinState();
    /// <summary>A focus watcher, or null where there is none.</summary>
    IRemoteFocusSource? CreateFocusSource();
}

public sealed class NullRemoteSystem : IRemoteSystem
{
    public bool? IsLocked() => null;
    public bool OnMainsPower => true;
    public void SetAwake(RemoteAwake need) { }
    public (bool Domain, bool Entra) JoinState() => (false, false);
    public IRemoteFocusSource? CreateFocusSource() => null;
    public void Dispose() { }
}

public sealed class PhoneRemoteOptions
{
    /// <summary>Capture + encode. Windows: Desktop Duplication + Media Foundation (HIB_REMOTE=0 turns it off). Elsewhere: none.</summary>
    public Func<IScreenPipelineFactory> Pipelines { get; set; } = RemotePlatforms.DefaultPipelines;
    /// <summary>Lock state, keep-awake, join state, focus. Windows: the real thing. Elsewhere: nothing.</summary>
    public Func<IRemoteSystem> System { get; set; } = RemotePlatforms.DefaultSystem;
    /// <summary>This computer's Tailscale addresses (remoteHello.addrs).</summary>
    public Func<IReadOnlyList<string>> TailscaleAddrs { get; set; } = TailscaleAddresses.Local;
    /// <summary>No screenPing for this long ends the session (§15 Review Focus 1: 5 s).</summary>
    public TimeSpan PingTimeout { get; set; } = PingWatch.DefaultTimeout;
    /// <summary>The first frame must be a valid screenOpen within this long, else 4003.</summary>
    public TimeSpan OpenTimeout { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>How often lock state, power, displays and addresses are looked at.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;
}

public static class RemotePlatforms
{
    static bool Enabled => Environment.GetEnvironmentVariable("HIB_REMOTE") is not ("0" or "false");

    public static IScreenPipelineFactory DefaultPipelines()
    {
        if (OperatingSystem.IsWindows() && Enabled)
        {
            try { return new Windows.WindowsScreenPipelineFactory(); }
            catch { /* fall through */ }
        }
        return new NullScreenPipelineFactory();
    }

    public static IRemoteSystem DefaultSystem()
    {
        if (OperatingSystem.IsWindows() && Enabled)
        {
            try { return new Windows.WindowsRemoteSystem(); }
            catch { /* fall through */ }
        }
        return new NullRemoteSystem();
    }
}
