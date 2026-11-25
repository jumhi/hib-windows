// H.I.B Phone Remote on Windows (docs/SPACE.md §15.3): Media Foundation H.264. The GPU's hardware encoder MFT first
// (asynchronous, event driven), the Microsoft software encoder if no hardware one takes the format; NV12 in system
// memory in, Annex-B access units out. Low latency, CBR, no B-frames, a keyframe every 2 s (GOP) and on request.
// ICodecAPI isn't wrapped by Vortice, so its SetValue is called through the vtable.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace HIB.Core.Remote.Windows;

[SupportedOSPlatform("windows")]
public sealed unsafe class MfH264Encoder : IDisposable
{
    const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    const int MF_E_NO_EVENTS_AVAILABLE = unchecked((int)0xC00D3E80);
    const uint MFT_ENUM_FLAG_SYNCMFT = 0x1, MFT_ENUM_FLAG_HARDWARE = 0x4, MFT_ENUM_FLAG_LOCALMFT = 0x8, MFT_ENUM_FLAG_SORTANDFILTER = 0x40;
    const int MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100, MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES = 0x200;

    static readonly Guid IidICodecAPI = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
    static readonly Guid CodecApiLowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    static readonly Guid CodecApiRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    static readonly Guid CodecApiMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    static readonly Guid CodecApiGopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    static readonly Guid CodecApiBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    static readonly Guid CodecApiForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
    static readonly Guid MtMpegSequenceHeader = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");

    static int s_started;

    readonly IMFTransform _mft;
    readonly IMFMediaEventGenerator? _events;
    readonly nint _codecApi;
    readonly int _w, _h;
    readonly bool _providesSamples;
    readonly int _outSize;
    int _fps;
    int _pendingInput; // async: NeedInput events not yet used
    byte[]? _sequenceHeader;

    MfH264Encoder(IMFTransform mft, IMFMediaEventGenerator? events, nint codecApi, int w, int h, int fps, bool hardware, string name)
    {
        _mft = mft;
        _events = events;
        _codecApi = codecApi;
        _w = w;
        _h = h;
        _fps = fps;
        IsHardware = hardware;
        Name = name;
        var info = mft.GetOutputStreamInfo(0);
        _providesSamples = (info.Flags & (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;
        _outSize = Math.Max(info.Size, w * h * 3 / 2);
    }

    public bool IsHardware { get; }
    public string Name { get; }
    public int Width => _w;
    public int Height => _h;

    static void Startup()
    {
        if (Interlocked.Exchange(ref s_started, 1) == 0) MediaFactory.MFStartup(false).CheckError();
    }

    /// <summary>A hardware encoder if one takes this size, else the software one. Null when neither works.</summary>
    public static MfH264Encoder? Create(int w, int h, int fps, int bitrate, Action<string>? log = null)
    {
        Startup();
        var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 };
        var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
        foreach (var hardware in new[] { true, false })
        {
            var flags = hardware ? MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER : MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_LOCALMFT | MFT_ENUM_FLAG_SORTANDFILTER;
            IMFActivateCollection? list = null;
            try { list = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, flags, input, output); }
            catch (Exception ex) { log?.Invoke($"[remote] encoder list: {ex.Message}"); }
            if (list is null) continue;
            using (list)
            {
                foreach (var activate in list)
                {
                    string name = hardware ? "hardware H.264" : "software H.264";
                    try { name = (hardware ? "hardware H.264 (" : "software H.264 (") + activate.FriendlyName + ")"; } catch { }
                    try
                    {
                        var mft = activate.ActivateObject<IMFTransform>();
                        var enc = Configure(mft, w, h, fps, bitrate, hardware, name);
                        if (enc is not null) return enc;
                        mft.Dispose();
                    }
                    catch (Exception ex) { log?.Invoke($"[remote] {name} refused {w}x{h}: {ex.Message}"); }
                }
            }
        }
        return null;
    }

    static MfH264Encoder? Configure(IMFTransform mft, int w, int h, int fps, int bitrate, bool hardware, string name)
    {
        IMFMediaEventGenerator? events = null;
        var attrs = mft.Attributes;
        var isAsync = false;
        try { isAsync = attrs.GetUInt32(TransformAttributeKeys.TransformAsync) != 0; } catch { }
        if (isAsync)
        {
            attrs.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
            events = mft.QueryInterface<IMFMediaEventGenerator>();
        }
        try { attrs.Set(CodecApiLowLatency, 1u); } catch { } // MF_LOW_LATENCY has the same GUID

        // The codec settings go in before the media types (some encoders lock them after).
        var codecApi = mft.QueryInterfaceOrNull(IidICodecAPI);
        if (codecApi != 0)
        {
            SetCodecValue(codecApi, CodecApiLowLatency, true);
            SetCodecValue(codecApi, CodecApiRateControlMode, 0u); // CBR
            SetCodecValue(codecApi, CodecApiMeanBitRate, (uint)bitrate);
            SetCodecValue(codecApi, CodecApiGopSize, (uint)Math.Max(1, fps * 2));
            SetCodecValue(codecApi, CodecApiBPictureCount, 0u);
        }

        var ok = false;
        foreach (var profile in new uint[] { 77 /* Main */, 66 /* Baseline */ })
        {
            using var outType = MediaFactory.MFCreateMediaType();
            outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            outType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            outType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
            outType.Set(MediaTypeAttributeKeys.FrameSize, Pack(w, h));
            outType.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
            outType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            outType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u); // progressive
            outType.Set(MediaTypeAttributeKeys.Mpeg2Profile, profile);
            try
            {
                mft.SetOutputType(0, outType, 0);
                ok = true;
                break;
            }
            catch { }
        }
        if (!ok) return Fail();

        using (var inType = MediaFactory.MFCreateMediaType())
        {
            inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            inType.Set(MediaTypeAttributeKeys.FrameSize, Pack(w, h));
            inType.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
            inType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            inType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
            try { mft.SetInputType(0, inType, 0); }
            catch { return Fail(); }
        }

        mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        return new MfH264Encoder(mft, events, codecApi, w, h, fps, hardware, name);

        MfH264Encoder? Fail()
        {
            events?.Dispose();
            if (codecApi != 0) Marshal.Release(codecApi);
            return null;
        }
    }

    static ulong Pack(int hi, int lo) => ((ulong)(uint)hi << 32) | (uint)lo;

    public void SetBitrate(int bitrate)
    {
        if (_codecApi != 0) SetCodecValue(_codecApi, CodecApiMeanBitRate, (uint)bitrate);
    }

    public void SetFps(int fps) => _fps = Math.Max(1, fps);

    /// <summary>One NV12 picture in; zero or more access units out (each with IsKey).</summary>
    public List<(byte[] Data, bool IsKey)> Encode(byte[] nv12, long ptsHns, bool forceKey)
    {
        if (forceKey && _codecApi != 0) SetCodecValue(_codecApi, CodecApiForceKeyFrame, 1u);
        var outputs = new List<(byte[], bool)>();
        using var sample = MediaFactory.MFCreateSample();
        using (var buffer = MediaFactory.MFCreateMemoryBuffer(nv12.Length))
        {
            buffer.Lock(out var ptr, out _, out _);
            try { Marshal.Copy(nv12, 0, ptr, nv12.Length); }
            finally { buffer.Unlock(); }
            buffer.CurrentLength = nv12.Length;
            sample.AddBuffer(buffer);
        }
        sample.SampleTime = ptsHns;
        sample.SampleDuration = 10_000_000L / Math.Max(1, _fps);

        if (_events is null)
        {
            _mft.ProcessInput(0, sample, 0);
            Drain(outputs);
            return outputs;
        }

        // Asynchronous hardware MFT: input only after NeedInput; outputs on HaveOutput.
        var until = Environment.TickCount64 + 200;
        while (_pendingInput == 0 && Environment.TickCount64 < until)
        {
            if (!PumpEvent(outputs)) Thread.Sleep(1);
        }
        if (_pendingInput == 0) return outputs; // the encoder is behind: skip this picture
        _pendingInput--;
        _mft.ProcessInput(0, sample, 0);
        // Low-latency encoders answer within a few ms; wait a little for this picture's output.
        until = Environment.TickCount64 + 30;
        var before = outputs.Count;
        while (Environment.TickCount64 < until && outputs.Count == before)
        {
            if (!PumpEvent(outputs)) Thread.Sleep(1);
        }
        while (PumpEvent(outputs)) { }
        return outputs;
    }

    /// <summary>Handles one queued event (non-blocking). False when there was none.</summary>
    bool PumpEvent(List<(byte[], bool)> outputs)
    {
        IMFMediaEvent ev;
        try { ev = _events!.GetEvent(1 /* MF_EVENT_FLAG_NO_WAIT */); }
        catch (SharpGenException ex) when (ex.ResultCode.Code == MF_E_NO_EVENTS_AVAILABLE) { return false; }
        using (ev)
        {
            switch (ev.EventType)
            {
                case MediaEventTypes.TransformNeedInput:
                    _pendingInput++;
                    break;
                case MediaEventTypes.TransformHaveOutput:
                    TakeOutput(outputs);
                    break;
            }
        }
        return true;
    }

    void Drain(List<(byte[], bool)> outputs)
    {
        for (var i = 0; i < 16; i++)
        {
            if (!TakeOutput(outputs)) break;
        }
    }

    /// <summary>One ProcessOutput. False when the encoder needs more input.</summary>
    bool TakeOutput(List<(byte[], bool)> outputs)
    {
        var buffer = new OutputDataBuffer { StreamID = 0 };
        IMFSample? ours = null;
        if (!_providesSamples)
        {
            ours = MediaFactory.MFCreateSample();
            using var mem = MediaFactory.MFCreateMemoryBuffer(_outSize);
            ours.AddBuffer(mem);
            buffer.Sample = ours;
        }
        try
        {
            var hr = _mft.ProcessOutput(ProcessOutputFlags.None, 1, ref buffer, out _);
            if (hr.Code == MF_E_TRANSFORM_NEED_MORE_INPUT) return false;
            if (hr.Code == MF_E_TRANSFORM_STREAM_CHANGE)
            {
                using var t = _mft.GetOutputAvailableType(0, 0);
                _mft.SetOutputType(0, t, 0);
                return true;
            }
            hr.CheckError();
            var sample = buffer.Sample;
            if (sample is null) return true;
            using var contiguous = sample.ConvertToContiguousBuffer();
            contiguous.Lock(out var ptr, out _, out var length);
            byte[] data;
            try
            {
                data = new byte[length];
                Marshal.Copy(ptr, data, 0, length);
            }
            finally { contiguous.Unlock(); }
            var key = false;
            try { key = sample.GetUInt32(new Guid("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a") /* MFSampleExtension_CleanPoint */) != 0; } catch { }
            data = WithSequenceHeader(data, ref key);
            outputs.Add((data, key));
            if (!ReferenceEquals(sample, ours)) sample.Dispose();
            return true;
        }
        finally
        {
            ours?.Dispose();
            buffer.Events?.Dispose();
        }
    }

    /// <summary>An IDR without SPS/PPS gets the output type's sequence header in front (so a config frame can be made).</summary>
    byte[] WithSequenceHeader(byte[] au, ref bool key)
    {
        var (config, _, isKey) = AnnexB.Split(au);
        key |= isKey;
        if (!isKey || config is not null) return au;
        if (_sequenceHeader is null)
        {
            try
            {
                using var t = _mft.GetOutputCurrentType(0);
                if (t.GetBlobSize(MtMpegSequenceHeader) > 0) _sequenceHeader = t.GetBlob(MtMpegSequenceHeader);
            }
            catch { }
        }
        if (_sequenceHeader is null) return au;
        var joined = new byte[_sequenceHeader.Length + au.Length];
        _sequenceHeader.CopyTo(joined, 0);
        au.CopyTo(joined, _sequenceHeader.Length);
        return joined;
    }

    // ICodecAPI::SetValue is vtable slot 9 (IUnknown 3 + IsSupported, IsModifiable, GetParameterRange,
    // GetParameterValues, GetDefaultValue, GetValue).
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct VARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint uintVal;
        [FieldOffset(8)] public short boolVal;
    }

    static void SetCodecValue(nint codecApi, Guid api, uint value)
    {
        var v = new VARIANT { vt = 19 /* VT_UI4 */, uintVal = value };
        CallSetValue(codecApi, api, &v);
    }

    static void SetCodecValue(nint codecApi, Guid api, bool value)
    {
        var v = new VARIANT { vt = 11 /* VT_BOOL */, boolVal = value ? (short)-1 : (short)0 };
        CallSetValue(codecApi, api, &v);
    }

    static void CallSetValue(nint codecApi, Guid api, VARIANT* v)
    {
        try
        {
            var vt = *(nint**)codecApi;
            ((delegate* unmanaged[Stdcall]<nint, Guid*, VARIANT*, int>)vt[9])(codecApi, &api, v); // best effort
        }
        catch { }
    }

    public void Dispose()
    {
        try { _mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero); } catch { }
        try { _mft.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); } catch { }
        if (_codecApi != 0) Marshal.Release(_codecApi);
        _events?.Dispose();
        _mft.Dispose();
    }
}
