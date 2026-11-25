// H.I.B Phone Remote on Windows (docs/SPACE.md §15.3): one thread per /screen session runs capture → scale → encode.
// Control calls (Start, SetRate, RequestKeyframe, Pause) only set fields, so the session never waits on this thread
// and this thread never holds a lock while it raises Frame / Event.
//  - Paced to the ladder's fps; a picture goes out when the desktop changed, the cursor moved, a keyframe is due, or
//    at least once a second (so a still screen still shows the cursor and answers keyframe requests).
//  - ACCESS_LOST (mode change, display unplugged, desktop switch) re-opens the duplication; a different size →
//    SourceChanged (the session sends screenOpened, then config + keyframe follow). The secure desktop → Blocked,
//    retried every second → Recovered.
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace HIB.Core.Remote.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsScreenPipelineFactory : IScreenPipelineFactory
{
    public WindowsScreenPipelineFactory()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    }

    public bool IsAvailable => true;
    public IScreenPipeline Create() => new WindowsScreenPipeline();
}

[SupportedOSPlatform("windows")]
public sealed partial class WindowsScreenPipeline : IScreenPipeline
{
    readonly Thread _thread;
    readonly AutoResetEvent _wake = new(false);
    volatile ScreenPipelineConfig? _config;
    volatile bool _paused = true;
    volatile bool _stop;
    volatile int _keyRequested;
    volatile int _bitrate;
    volatile int _fps = 30;

    public WindowsScreenPipeline()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "H.I.B Remote capture", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public event Action<PipelineFrame>? Frame;
    public event Action<ScreenPipelineEvent>? Event;
    public string? EncoderName { get; private set; }

    public void Start(ScreenPipelineConfig config)
    {
        _bitrate = config.Bitrate;
        _fps = Math.Max(1, config.Fps);
        _config = config;
        _keyRequested = 1;
        _paused = false;
        _wake.Set();
    }

    public void SetRate(int bitrate, int fps)
    {
        _bitrate = bitrate;
        _fps = Math.Max(1, fps);
    }

    public void RequestKeyframe() => _keyRequested = 1;

    public void Pause()
    {
        _paused = true;
        _wake.Set();
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
    }

    // ---------------- the thread ----------------

    void Run()
    {
        DesktopDuplicationCapture? capture = null;
        MfH264Encoder? encoder = null;
        ScreenPipelineConfig? built = null; // what capture + encoder were made for
        var blocked = false;
        var failures = 0;
        var clock = Stopwatch.StartNew();
        long nextDue = 0;
        long lastSent = -10_000;
        var lastCursor = (X: double.NaN, Y: double.NaN, Visible: false);
        var bitrateApplied = 0;
        var hardwareFailed = false; // a hardware encoder that took the format but failed to encode: software from now on
        var old = SetThreadDpiAwarenessContext(-4); // physical pixels for the cursor, like the desktop coordinates

        try
        {
            while (!_stop)
            {
                var config = _config;
                if (_paused || config is null)
                {
                    capture?.Dispose();
                    capture = null;
                    encoder?.Dispose();
                    encoder = null;
                    built = null;
                    _wake.WaitOne(500);
                    continue;
                }

                // (Re)open the display.
                if (capture is null)
                {
                    try
                    {
                        capture = DesktopDuplicationCapture.Open(config.DisplayId, config.Display);
                        failures = 0;
                        if (blocked)
                        {
                            blocked = false;
                            Raise(new ScreenPipelineEvent(ScreenPipelineEventKind.Recovered));
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        if (!blocked)
                        {
                            blocked = true;
                            Raise(new ScreenPipelineEvent(ScreenPipelineEventKind.Blocked));
                        }
                        _wake.WaitOne(1000);
                        continue;
                    }
                    catch (Exception ex)
                    {
                        if (++failures >= 5)
                        {
                            Raise(new ScreenPipelineEvent(ScreenPipelineEventKind.Failed, Detail: ex.Message));
                            return;
                        }
                        _wake.WaitOne(1000);
                        continue;
                    }
                    if (capture.Bounds != config.Display || (string.IsNullOrEmpty(config.DisplayId) && !string.IsNullOrEmpty(capture.DeviceName)))
                    {
                        // A new resolution, or "main" resolved: the session restarts us with the real bounds.
                        var bounds = capture.Bounds;
                        var name = capture.DeviceName;
                        Raise(new ScreenPipelineEvent(ScreenPipelineEventKind.SourceChanged, name, bounds));
                        // Keep the capture; wait for the matching config.
                        if (WaitForConfig(c => c.Display == bounds)) continue;
                        continue;
                    }
                    built = null;
                }

                if (config.OutW <= 0 || config.OutH <= 0)
                {
                    _wake.WaitOne(200);
                    continue;
                }

                // (Re)make the encoder for a new size or codec; a crop or rate change needs no new encoder.
                if (built is null || built.OutW != config.OutW || built.OutH != config.OutH || built.Codec != config.Codec)
                {
                    encoder?.Dispose();
                    encoder = null;
                    if (config.Codec == ScreenCodec.H264)
                    {
                        try { encoder = MfH264Encoder.Create(config.OutW, config.OutH, _fps, _bitrate, allowHardware: !hardwareFailed); }
                        catch { encoder = null; }
                        if (encoder is null)
                        {
                            Raise(new ScreenPipelineEvent(ScreenPipelineEventKind.EncoderFailed));
                            WaitForConfig(c => c.Codec != ScreenCodec.H264);
                            continue;
                        }
                        EncoderName = encoder.Name;
                        bitrateApplied = _bitrate;
                    }
                    else EncoderName = "JPEG";
                    built = config;
                    _keyRequested = 1;
                }

                // Pace to the fps.
                var fps = config.Codec == ScreenCodec.Jpeg ? Math.Min(10, _fps) : _fps;
                var interval = 1000L / Math.Max(1, fps);
                var now = clock.ElapsedMilliseconds;
                var wait = (int)Math.Clamp(nextDue - now, 0, interval);
                var result = capture.Acquire(wait);
                if (result == AcquireResult.Lost || result == AcquireResult.Blocked)
                {
                    capture.Dispose();
                    capture = null;
                    if (result == AcquireResult.Blocked && !blocked)
                    {
                        blocked = true;
                        Raise(new ScreenPipelineEvent(ScreenPipelineEventKind.Blocked));
                    }
                    continue;
                }
                var changed = result == AcquireResult.NewImage;
                now = clock.ElapsedMilliseconds;
                if (now < nextDue)
                {
                    // Early image: hold it until it's time (the next Acquire returns NoChange and we render then).
                    if (changed) _dirty = true;
                    continue;
                }
                changed |= _dirty;
                _dirty = false;

                var cursor = Cursor();
                var cursorMoved = cursor != lastCursor;
                var key = Interlocked.Exchange(ref _keyRequested, 0) == 1;
                if (!(changed || cursorMoved || key || now - lastSent >= 1000) || !capture.HasImage)
                {
                    if (key) _keyRequested = 1;
                    nextDue = now + interval;
                    continue;
                }
                nextDue = now + interval;
                lastCursor = cursor;
                lastSent = now;

                var crop = config.Crop;
                try
                {
                    var pts = clock.Elapsed.Ticks / 10; // µs
                    if (config.Codec == ScreenCodec.Jpeg)
                    {
                        var bgra = capture.Render(crop, config.OutW, config.OutH, nv12: false);
                        if (bgra is null) continue;
                        var jpeg = JpegFrameEncoder.Encode(bgra, config.OutW, config.OutH, config.OutW * 4);
                        Raise(new PipelineFrame(jpeg, true, pts, cursor.X, cursor.Y, cursor.Visible, config));
                        failures = 0;
                    }
                    else if (encoder is not null)
                    {
                        if (_bitrate != bitrateApplied)
                        {
                            encoder.SetBitrate(_bitrate);
                            bitrateApplied = _bitrate;
                        }
                        encoder.SetFps(fps);
                        var nv12 = capture.Render(crop, config.OutW, config.OutH, nv12: true);
                        if (nv12 is null) continue;
                        System.Collections.Generic.List<(byte[] Data, bool IsKey)> units;
                        try { units = encoder.Encode(nv12, pts * 10, key); }
                        catch
                        {
                            if (encoder.IsHardware) hardwareFailed = true;
                            throw;
                        }
                        foreach (var (data, isKey) in units)
                            Raise(new PipelineFrame(data, isKey, pts, cursor.X, cursor.Y, cursor.Visible, config));
                        failures = 0;
                    }
                }
                catch (Exception ex)
                {
                    // A lost device or a driver hiccup: start the display and encoder over.
                    capture.Dispose();
                    capture = null;
                    encoder?.Dispose();
                    encoder = null;
                    built = null;
                    if (++failures >= 5)
                    {
                        Raise(new ScreenPipelineEvent(ScreenPipelineEventKind.Failed, Detail: ex.Message));
                        return;
                    }
                }
            }
        }
        finally
        {
            capture?.Dispose();
            encoder?.Dispose();
            if (old != 0) SetThreadDpiAwarenessContext(old);
        }
    }

    bool _dirty;

    /// <summary>Waits (up to 3 s) for the session to send a config that satisfies <paramref name="ok"/>.</summary>
    bool WaitForConfig(Func<ScreenPipelineConfig, bool> ok)
    {
        var until = Environment.TickCount64 + 3000;
        while (!_stop && Environment.TickCount64 < until)
        {
            if (_config is { } c && ok(c)) return true;
            _wake.WaitOne(50);
        }
        return false;
    }

    void Raise(PipelineFrame f)
    {
        try { Frame?.Invoke(f); } catch { }
    }

    void Raise(ScreenPipelineEvent e)
    {
        try { Event?.Invoke(e); } catch { }
    }

    // ---------------- the cursor (never drawn into the picture; the phone draws it) ----------------

    unsafe (double X, double Y, bool Visible) Cursor()
    {
        var ci = new CURSORINFO { cbSize = (uint)sizeof(CURSORINFO) };
        if (!GetCursorInfo(&ci)) return (double.NaN, double.NaN, false);
        return (ci.ptScreenPos.X, ci.ptScreenPos.Y, (ci.flags & 0x1 /* CURSOR_SHOWING */) != 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct CURSORINFO
    {
        public uint cbSize, flags;
        public nint hCursor;
        public POINT ptScreenPos;
    }

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static unsafe partial bool GetCursorInfo(CURSORINFO* info);
    [LibraryImport("user32.dll")] private static partial nint SetThreadDpiAwarenessContext(nint context);
}
