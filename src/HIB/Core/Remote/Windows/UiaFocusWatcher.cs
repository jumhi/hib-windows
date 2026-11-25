// H.I.B Phone Remote on Windows (docs/SPACE.md §15.1 remoteFocus): UI Automation's focus-changed event says when the
// keyboard focus moves; the watcher then reads the focused element's control type, IsPassword, Value (read-only?) and
// Text pattern, and RemoteFocusRules decides text / secure. Plain COM through vtables (no interop assembly):
//  - CUIAutomation {FF48DBA4-60EF-4201-AA87-54103EEF594E} / IUIAutomation {30CBE57D-D9D0-452A-AB13-7AC5AC4825EE}:
//    slot 8 GetFocusedElement, 39 AddFocusChangedEventHandler, 40 RemoveFocusChangedEventHandler.
//  - IUIAutomationElement: slot 10 GetCurrentPropertyValue.
//  - Our IUIAutomationFocusChangedEventHandler {C270F6B5-5C69-4290-9745-7A7F97169468}: a static native object whose
//    one method only wakes the watcher thread (no UIA calls inside the callback).
// Everything runs on one MTA thread; a 1 s re-read covers apps that don't raise the event.
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace HIB.Core.Remote.Windows;

[SupportedOSPlatform("windows")]
public sealed unsafe partial class UiaFocusWatcher : IRemoteFocusSource
{
    static readonly Guid ClsidCUIAutomation = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");
    static readonly Guid IidIUIAutomation = new("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");
    static readonly Guid IidFocusHandler = new("C270F6B5-5C69-4290-9745-7A7F97169468");
    static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");

    const int UIA_ControlTypePropertyId = 30003, UIA_IsPasswordPropertyId = 30019, UIA_IsTextPatternAvailablePropertyId = 30040,
        UIA_IsValuePatternAvailablePropertyId = 30043, UIA_ValueIsReadOnlyPropertyId = 30046;

    // One handler object for the process (UIA may call it briefly after removal, so it is never freed).
    static nint s_handler;
    static UiaFocusWatcher? s_current;
    static readonly object s_gate = new();

    readonly AutoResetEvent _wake = new(false);
    Thread? _thread;
    volatile bool _stop;

    public event Action<FocusFacts?>? Changed;

    public void Start()
    {
        if (_thread is not null) return;
        lock (s_gate) s_current = this;
        _thread = new Thread(Run) { IsBackground = true, Name = "H.I.B Remote focus" };
        _thread.Start();
    }

    void Run()
    {
        var hrInit = CoInitializeEx(0, 0 /* COINIT_MULTITHREADED */);
        nint uia = 0;
        var handler = Handler();
        var registered = false;
        try
        {
            var clsid = ClsidCUIAutomation;
            var iid = IidIUIAutomation;
            if (CoCreateInstance(&clsid, 0, 1 /* CLSCTX_INPROC_SERVER */, &iid, &uia) < 0 || uia == 0) return;
            var vt = *(nint**)uia;
            registered = ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)vt[39])(uia, 0, handler) >= 0;
            FocusFacts? last = null;
            var first = true;
            while (!_stop)
            {
                var facts = Read(uia);
                if (first || facts != last) Raise(facts);
                first = false;
                last = facts;
                _wake.WaitOne(registered ? 1000 : 300);
            }
        }
        catch { }
        finally
        {
            if (uia != 0)
            {
                var vt = *(nint**)uia;
                if (registered) try { ((delegate* unmanaged[Stdcall]<nint, nint, int>)vt[40])(uia, handler); } catch { }
                ((delegate* unmanaged[Stdcall]<nint, uint>)vt[2])(uia);
            }
            if (hrInit >= 0) CoUninitialize();
        }
    }

    void Raise(FocusFacts? facts)
    {
        try { Changed?.Invoke(facts); } catch { }
    }

    static FocusFacts? Read(nint uia)
    {
        nint element = 0;
        var vt = *(nint**)uia;
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)vt[8])(uia, &element) < 0 || element == 0) return null;
        try
        {
            return new FocusFacts(
                IntProperty(element, UIA_ControlTypePropertyId) ?? 0,
                BoolProperty(element, UIA_IsPasswordPropertyId),
                BoolProperty(element, UIA_IsValuePatternAvailablePropertyId),
                BoolProperty(element, UIA_ValueIsReadOnlyPropertyId),
                BoolProperty(element, UIA_IsTextPatternAvailablePropertyId));
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)element)[2])(element);
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct VARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public int intVal;
        [FieldOffset(8)] public short boolVal;
    }

    static bool GetProperty(nint element, int id, VARIANT* v)
    {
        var vt = *(nint**)element;
        return ((delegate* unmanaged[Stdcall]<nint, int, VARIANT*, int>)vt[10])(element, id, v) >= 0;
    }

    static int? IntProperty(nint element, int id)
    {
        VARIANT v = default;
        if (!GetProperty(element, id, &v)) return null;
        try { return v.vt == 3 /* VT_I4 */ ? v.intVal : null; }
        finally { VariantClear(&v); }
    }

    static bool BoolProperty(nint element, int id)
    {
        VARIANT v = default;
        if (!GetProperty(element, id, &v)) return false;
        try { return v.vt == 11 /* VT_BOOL */ && v.boolVal != 0; }
        finally { VariantClear(&v); }
    }

    // ---------------- the event handler COM object ----------------

    static nint Handler()
    {
        lock (s_gate)
        {
            if (s_handler != 0) return s_handler;
            var vtbl = (nint*)NativeMemory.Alloc((nuint)(4 * sizeof(nint)));
            vtbl[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&QueryInterface;
            vtbl[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;
            vtbl[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;
            vtbl[3] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&HandleFocusChangedEvent;
            var obj = (nint*)NativeMemory.Alloc((nuint)sizeof(nint));
            obj[0] = (nint)vtbl;
            s_handler = (nint)obj;
            return s_handler;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    static int QueryInterface(nint self, Guid* iid, nint* ppv)
    {
        if (*iid == IidIUnknown || *iid == IidFocusHandler)
        {
            *ppv = self;
            return 0;
        }
        *ppv = 0;
        return unchecked((int)0x80004002); // E_NOINTERFACE
    }

    // A static object: reference counts don't matter.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    static uint AddRef(nint self) => 2;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    static uint Release(nint self) => 1;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    static int HandleFocusChangedEvent(nint self, nint sender)
    {
        try
        {
            UiaFocusWatcher? w;
            lock (s_gate) w = s_current;
            w?._wake.Set();
        }
        catch { }
        return 0;
    }

    public void Dispose()
    {
        _stop = true;
        try { _wake.Set(); } catch { }
        lock (s_gate)
        {
            if (ReferenceEquals(s_current, this)) s_current = null;
        }
    }

    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(nint reserved, uint coInit);
    [LibraryImport("ole32.dll")] private static partial void CoUninitialize();
    [LibraryImport("ole32.dll")] private static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* ppv);
    [LibraryImport("oleaut32.dll")] private static partial int VariantClear(VARIANT* v);
}
