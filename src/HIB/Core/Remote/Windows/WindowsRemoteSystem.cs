// H.I.B Phone Remote on Windows (docs/SPACE.md §15.1, §15.4): the system parts, kept thin — the decisions live in
// RemoteSessionLogic (RemoteAwakePolicy, RemoteUnlockAvailability).
//  - Locked? WTSQuerySessionInformation(WTSSessionInfoEx).SessionFlags (Windows 10+: 0 = locked, 1 = unlocked).
//  - Mains power: GetSystemPowerStatus.
//  - Keep awake: SetThreadExecutionState is per thread, so one small thread owns the assertion.
//  - Managed PC: NetGetJoinInformation (domain) and NetGetAadJoinInformation (Entra).
//  - Focus: UI Automation (UiaFocusWatcher).
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace HIB.Core.Remote.Windows;

[SupportedOSPlatform("windows")]
public sealed unsafe partial class WindowsRemoteSystem : IRemoteSystem
{
    readonly BlockingCollection<uint> _awake = new();
    readonly Thread _thread;

    public WindowsRemoteSystem()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        _thread = new Thread(AwakeLoop) { IsBackground = true, Name = "H.I.B Remote awake" };
        _thread.Start();
    }

    public bool? IsLocked()
    {
        try
        {
            if (!WTSQuerySessionInformationW(0, 0xFFFFFFFF /* WTS_CURRENT_SESSION */, 25 /* WTSSessionInfoEx */, out var buf, out var bytes)) return null;
            try
            {
                // WTSINFOEXW { DWORD Level; union { WTSINFOEX_LEVEL1_W } } — the union is 8-byte aligned (LARGE_INTEGERs).
                if (bytes < 20 || *(uint*)buf != 1) return null;
                var flags = *(int*)((byte*)buf + 16); // SessionId @8, SessionState @12, SessionFlags @16
                return flags switch
                {
                    0 => true,  // WTS_SESSIONSTATE_LOCK
                    1 => false, // WTS_SESSIONSTATE_UNLOCK
                    _ => null,
                };
            }
            finally { WTSFreeMemory(buf); }
        }
        catch { return null; }
    }

    public bool OnMainsPower
    {
        get
        {
            try
            {
                SYSTEM_POWER_STATUS s;
                return !GetSystemPowerStatus(&s) || RemoteAwakePolicy.OnMains(s.ACLineStatus, s.BatteryFlag);
            }
            catch { return true; }
        }
    }

    public void SetAwake(RemoteAwake need)
    {
        if (!_awake.IsAddingCompleted) _awake.Add(RemoteAwakePolicy.ExecutionState(need));
    }

    void AwakeLoop()
    {
        try
        {
            foreach (var flags in _awake.GetConsumingEnumerable()) SetThreadExecutionState(flags);
        }
        catch { }
        finally { SetThreadExecutionState(0x80000000u); }
    }

    public (bool Domain, bool Entra) JoinState()
    {
        var domain = false;
        bool entra = false;
        try
        {
            if (NetGetJoinInformation(null, out var name, out var status) == 0)
            {
                domain = RemoteUnlockAvailability.IsDomainJoined(status);
                if (name != 0) NetApiBufferFree(name);
            }
        }
        catch { }
        try
        {
            // Windows 10+: null info = not joined. The first field of DSREG_JOIN_INFO is its DSREG_JOIN_TYPE.
            if (NetGetAadJoinInformation(null, out var info) == 0 && info != 0)
            {
                entra = RemoteUnlockAvailability.IsEntraJoined(*(int*)info);
                NetFreeAadJoinInformation(info);
            }
        }
        catch { }
        return (domain, entra);
    }

    public IRemoteFocusSource? CreateFocusSource() => new UiaFocusWatcher();

    public void Dispose()
    {
        try { _awake.CompleteAdding(); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [LibraryImport("wtsapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool WTSQuerySessionInformationW(nint server, uint session, int infoClass, out nint buffer, out uint bytes);
    [LibraryImport("wtsapi32.dll")] private static partial void WTSFreeMemory(nint memory);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetSystemPowerStatus(SYSTEM_POWER_STATUS* status);
    [LibraryImport("kernel32.dll")] private static partial uint SetThreadExecutionState(uint flags);
    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int NetGetJoinInformation(string? server, out nint name, out int status);
    [LibraryImport("netapi32.dll")] private static partial int NetApiBufferFree(nint buffer);
    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int NetGetAadJoinInformation(string? tenantId, out nint info);
    [LibraryImport("netapi32.dll")] private static partial void NetFreeAadJoinInformation(nint info);
}
