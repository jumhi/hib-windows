// H.I.B Phone Remote on Windows (docs/SPACE.md §15.3): one display through DXGI Desktop Duplication, cropped, scaled
// and (for H.264) turned into NV12 on the GPU by the D3D11 video processor, then read back small. When the video
// processor isn't there (basic display driver, remote session), the whole BGRA image is read back and PixelConvert does
// it on the CPU. The cursor is never in the picture (Desktop Duplication leaves it out); the phone draws it.
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HIB.Core.Remote.Windows;

public enum AcquireResult { NewImage, NoChange, Lost, Blocked }

[SupportedOSPlatform("windows")]
public sealed class DesktopDuplicationCapture : IDisposable
{
    const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
    const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
    const int DXGI_ERROR_ACCESS_DENIED = unchecked((int)0x887A002B);
    const int DXGI_ERROR_SESSION_DISCONNECTED = unchecked((int)0x887A0028);
    const int E_ACCESSDENIED = unchecked((int)0x80070005);

    readonly ID3D11Device _device;
    readonly ID3D11DeviceContext _context;
    readonly IDXGIOutputDuplication _dupl;
    ID3D11VideoDevice? _videoDevice;
    ID3D11VideoContext? _videoContext;
    ID3D11Texture2D? _desktop;     // our copy of the last desktop image
    // The scaler for the current crop/size/format.
    (PixelBox Src, int W, int H, Format Format)? _vpKey;
    ID3D11VideoProcessorEnumerator? _vpEnum;
    ID3D11VideoProcessor? _vp;
    ID3D11Texture2D? _vpOut;
    ID3D11VideoProcessorOutputView? _vpOutView;
    ID3D11VideoProcessorInputView? _vpInView;
    ID3D11Texture2D? _staging;
    (int W, int H, Format Format)? _stagingKey;
    byte[] _cpu = Array.Empty<byte>();

    DesktopDuplicationCapture(ID3D11Device device, ID3D11DeviceContext context, IDXGIOutputDuplication dupl, string deviceName,
        PixelBox bounds, RemoteRotation rotation, ID3D11VideoDevice? vd, ID3D11VideoContext? vc)
    {
        _device = device;
        _context = context;
        _dupl = dupl;
        DeviceName = deviceName;
        Bounds = bounds;
        Rotation = rotation;
        _videoDevice = vd;
        _videoContext = vc;
    }

    /// <summary>\\.\DISPLAYn — the same as Avalonia's Screen.DisplayName (§7.1).</summary>
    public string DeviceName { get; }
    /// <summary>The display in desktop pixels (as the user sees it).</summary>
    public PixelBox Bounds { get; }
    public RemoteRotation Rotation { get; }
    public bool HasVideoProcessor => _videoDevice is not null;

    /// <summary>
    /// Duplicates the output whose device name is <paramref name="displayId"/> (or whose bounds are <paramref name="bounds"/>;
    /// else the main one). Throws <see cref="UnauthorizedAccessException"/> while the secure desktop is up.
    /// </summary>
    public static DesktopDuplicationCapture Open(string displayId, PixelBox bounds)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        IDXGIAdapter1? pickAdapter = null;
        IDXGIOutput? pickOutput = null;
        IDXGIAdapter1? mainAdapter = null;
        IDXGIOutput? mainOutput = null;
        try
        {
            for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
            {
                var keep = false;
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    var d = output.Description;
                    var r = d.DesktopCoordinates;
                    var box = new PixelBox(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                    var match = pickOutput is null && d.AttachedToDesktop &&
                                ((!string.IsNullOrEmpty(displayId) && string.Equals(d.DeviceName, displayId, StringComparison.OrdinalIgnoreCase))
                                 || (!bounds.IsEmpty && box == bounds));
                    if (match)
                    {
                        pickOutput = output;
                        pickAdapter = adapter;
                        keep = true;
                        continue;
                    }
                    if (mainOutput is null && d.AttachedToDesktop && box.X == 0 && box.Y == 0)
                    {
                        mainOutput = output;
                        mainAdapter = adapter;
                        keep = true;
                        continue;
                    }
                    output.Dispose();
                }
                if (!keep) adapter.Dispose();
            }
            var useAdapter = pickAdapter ?? mainAdapter ?? throw new InvalidOperationException("No display to capture");
            var useOutput = pickOutput ?? mainOutput!;
            var desc = useOutput.Description;
            var dc = desc.DesktopCoordinates;
            var outBounds = new PixelBox(dc.Left, dc.Top, dc.Right - dc.Left, dc.Bottom - dc.Top);
            var rotation = desc.Rotation switch
            {
                ModeRotation.Rotate90 => RemoteRotation.Cw90,
                ModeRotation.Rotate180 => RemoteRotation.Cw180,
                ModeRotation.Rotate270 => RemoteRotation.Cw270,
                _ => RemoteRotation.None,
            };

            var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
            ID3D11Device? device;
            ID3D11DeviceContext? context;
            var video = true;
            var hr = D3D11.D3D11CreateDevice(useAdapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport, levels, out device, out context);
            if (hr.Failure)
            {
                video = false;
                D3D11.D3D11CreateDevice(useAdapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels, out device, out context).CheckError();
            }
            IDXGIOutputDuplication dupl;
            try
            {
                using var output1 = useOutput.QueryInterface<IDXGIOutput1>();
                dupl = output1.DuplicateOutput(device!);
            }
            catch (SharpGenException ex) when (ex.ResultCode.Code is E_ACCESSDENIED or DXGI_ERROR_ACCESS_DENIED or DXGI_ERROR_SESSION_DISCONNECTED)
            {
                context?.Dispose();
                device?.Dispose();
                throw new UnauthorizedAccessException("The secure desktop is up", ex);
            }
            ID3D11VideoDevice? vd = null;
            ID3D11VideoContext? vc = null;
            if (video)
            {
                try
                {
                    vd = device!.QueryInterfaceOrNull<ID3D11VideoDevice>();
                    vc = context!.QueryInterfaceOrNull<ID3D11VideoContext>();
                }
                catch { }
                if (vd is null || vc is null)
                {
                    vd?.Dispose();
                    vc?.Dispose();
                    vd = null;
                    vc = null;
                }
            }
            return new DesktopDuplicationCapture(device!, context!, dupl, desc.DeviceName, outBounds, rotation, vd, vc);
        }
        finally
        {
            pickOutput?.Dispose();
            mainOutput?.Dispose();
            pickAdapter?.Dispose();
            if (!ReferenceEquals(mainAdapter, pickAdapter)) mainAdapter?.Dispose(); // one adapter can hold both
        }
    }

    /// <summary>Waits up to <paramref name="timeoutMs"/> for a new desktop image (pointer-only updates are NoChange).</summary>
    public AcquireResult Acquire(int timeoutMs)
    {
        var hr = _dupl.AcquireNextFrame((uint)Math.Max(0, timeoutMs), out var info, out var resource);
        if (hr.Code == DXGI_ERROR_WAIT_TIMEOUT) return AcquireResult.NoChange;
        if (hr.Code == DXGI_ERROR_ACCESS_LOST) return AcquireResult.Lost;
        if (hr.Code is E_ACCESSDENIED or DXGI_ERROR_ACCESS_DENIED or DXGI_ERROR_SESSION_DISCONNECTED) return AcquireResult.Blocked;
        if (hr.Failure) return AcquireResult.Lost;
        try
        {
            if (info.LastPresentTime == 0 && _desktop is not null) return AcquireResult.NoChange; // the pointer moved, nothing else
            using var tex = resource.QueryInterface<ID3D11Texture2D>();
            var d = tex.Description;
            if (_desktop is null || _desktop.Description.Width != d.Width || _desktop.Description.Height != d.Height || _desktop.Description.Format != d.Format)
            {
                ReleaseScaler();
                _desktop?.Dispose();
                _desktop = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = d.Width,
                    Height = d.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = d.Format,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                });
            }
            _context.CopyResource(_desktop, tex);
            return AcquireResult.NewImage;
        }
        finally
        {
            resource?.Dispose();
            _dupl.ReleaseFrame();
        }
    }

    public bool HasImage => _desktop is not null;

    /// <summary>
    /// The crop (display pixels as seen) as an outW × outH picture: NV12 (Y plane then UV, tightly packed) or BGRA.
    /// Null when there is no image yet.
    /// </summary>
    public byte[]? Render(PixelBox crop, int outW, int outH, bool nv12)
    {
        if (_desktop is null || outW <= 0 || outH <= 0) return null;
        var format = nv12 ? Format.NV12 : Format.B8G8R8A8_UNorm;
        if (_videoDevice is not null && _videoContext is not null)
        {
            try { return RenderGpu(crop, outW, outH, format); }
            catch
            {
                // This driver's video processor can't do it: the CPU from now on.
                ReleaseScaler();
                _videoContext.Dispose();
                _videoDevice.Dispose();
                _videoContext = null;
                _videoDevice = null;
            }
        }
        return RenderCpu(crop, outW, outH, nv12);
    }

    byte[] RenderGpu(PixelBox crop, int outW, int outH, Format format)
    {
        var dd = _desktop!.Description;
        var src = RemoteRotationMath.ToSource(crop, Bounds.W, Bounds.H, Rotation);
        var key = (src, outW, outH, format);
        if (_vpKey != key)
        {
            ReleaseScaler();
            var content = new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputFrameRate = new Rational(60, 1),
                InputWidth = dd.Width,
                InputHeight = dd.Height,
                OutputFrameRate = new Rational(60, 1),
                OutputWidth = (uint)outW,
                OutputHeight = (uint)outH,
                Usage = VideoUsage.OptimalQuality,
            };
            _vpEnum = _videoDevice!.CreateVideoProcessorEnumerator(content);
            _vp = _videoDevice.CreateVideoProcessor(_vpEnum, 0);
            _vpOut = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)outW,
                Height = (uint)outH,
                MipLevels = 1,
                ArraySize = 1,
                Format = format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
            });
            _vpOutView = _videoDevice.CreateVideoProcessorOutputView(_vpOut, _vpEnum,
                new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });
            _vpInView = _videoDevice.CreateVideoProcessorInputView(_desktop, _vpEnum,
                new VideoProcessorInputViewDescription { FourCC = 0, ViewDimension = VideoProcessorInputViewDimension.Texture2D });
            var vc = _videoContext!;
            vc.VideoProcessorSetStreamFrameFormat(_vp, 0, VideoFrameFormat.Progressive);
            vc.VideoProcessorSetStreamSourceRect(_vp, 0, true, new Vortice.RawRect(src.X, src.Y, src.Right, src.Bottom));
            vc.VideoProcessorSetStreamDestRect(_vp, 0, true, new Vortice.RawRect(0, 0, outW, outH));
            vc.VideoProcessorSetOutputTargetRect(_vp, true, new Vortice.RawRect(0, 0, outW, outH));
            if (Rotation != RemoteRotation.None)
            {
                vc.VideoProcessorSetStreamRotation(_vp, 0, true, Rotation switch
                {
                    RemoteRotation.Cw90 => VideoProcessorRotation.Rotation90,
                    RemoteRotation.Cw180 => VideoProcessorRotation.Rotation180,
                    _ => VideoProcessorRotation.Rotation270,
                });
            }
            _vpKey = key;
        }
        var stream = new VideoProcessorStream { Enable = true, InputSurface = _vpInView! };
        _videoContext!.VideoProcessorBlt(_vp!, _vpOutView!, 0, 1, new[] { stream }).CheckError();
        return ReadBack(_vpOut!, outW, outH, format);
    }

    byte[] ReadBack(ID3D11Texture2D texture, int w, int h, Format format)
    {
        if (_stagingKey != (w, h, format))
        {
            _staging?.Dispose();
            _staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w,
                Height = (uint)h,
                MipLevels = 1,
                ArraySize = 1,
                Format = format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
            });
            _stagingKey = (w, h, format);
        }
        _context.CopyResource(_staging!, texture);
        var map = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var pitch = (int)map.RowPitch;
            if (format == Format.NV12)
            {
                var result = new byte[w * h * 3 / 2];
                // Y rows, then the interleaved UV plane, which starts RowPitch × Height into the mapping.
                for (var y = 0; y < h; y++) Marshal.Copy(map.DataPointer + y * pitch, result, y * w, w);
                var uvBase = map.DataPointer + pitch * h;
                for (var y = 0; y < h / 2; y++) Marshal.Copy(uvBase + y * pitch, result, w * h + y * w, w);
                return result;
            }
            var bgra = new byte[w * h * 4];
            for (var y = 0; y < h; y++) Marshal.Copy(map.DataPointer + y * pitch, bgra, y * w * 4, w * 4);
            return bgra;
        }
        finally { _context.Unmap(_staging!, 0); }
    }

    byte[] RenderCpu(PixelBox crop, int outW, int outH, bool nv12)
    {
        var dd = _desktop!.Description;
        var w = (int)dd.Width;
        var h = (int)dd.Height;
        if (dd.Format != Format.B8G8R8A8_UNorm) throw new NotSupportedException("Desktop format " + dd.Format);
        if (_stagingKey != (w, h, dd.Format))
        {
            _staging?.Dispose();
            _staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w,
                Height = (uint)h,
                MipLevels = 1,
                ArraySize = 1,
                Format = dd.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
            });
            _stagingKey = (w, h, dd.Format);
        }
        _context.CopyResource(_staging!, _desktop);
        var map = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        var bgra = new byte[outW * outH * 4];
        try
        {
            var pitch = (int)map.RowPitch;
            if (_cpu.Length < pitch * h) _cpu = new byte[pitch * h];
            Marshal.Copy(map.DataPointer, _cpu, 0, pitch * h);
        }
        finally { _context.Unmap(_staging!, 0); }
        PixelConvert.CropScaleBgra(_cpu, w, h, (int)map.RowPitch, crop, Bounds.W, Bounds.H, Rotation, bgra, outW, outH);
        if (!nv12) return bgra;
        var result = new byte[outW * outH * 3 / 2];
        PixelConvert.BgraToNv12(bgra, outW, outH, result);
        return result;
    }

    void ReleaseScaler()
    {
        _vpInView?.Dispose();
        _vpOutView?.Dispose();
        _vpOut?.Dispose();
        _vp?.Dispose();
        _vpEnum?.Dispose();
        _vpInView = null;
        _vpOutView = null;
        _vpOut = null;
        _vp = null;
        _vpEnum = null;
        _vpKey = null;
    }

    public void Dispose()
    {
        ReleaseScaler();
        _staging?.Dispose();
        _desktop?.Dispose();
        _videoContext?.Dispose();
        _videoDevice?.Dispose();
        _dupl.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
