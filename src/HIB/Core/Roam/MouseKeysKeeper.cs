// H.I.B Roam (docs/SPACE.md §12.1): a PC with no mouse of its own hides the pointer, so while another computer drives
// it the user sees nothing move. While controlled without a mouse, turn Mouse Keys on (Deskflow's MSWindowsComputer
// trick: MKF_AVAILABLE | MKF_MOUSEKEYSON | MKF_REPLACENUMBERS makes Windows believe a mouse is there) and give the user
// their own setting back on release, on exit — and, when H.I.B died while it was on, at the next start. The setting
// to restore is written to disk before Mouse Keys is turned on, so a crash can't lose it.
using System;
using System.IO;
using System.Text.Json;

namespace HIB.Core.Roam;

public sealed class MouseKeysKeeper
{
    public const string FileName = "roam-mousekeys.json";

    readonly IRoamSystem _system;
    readonly string _path;
    readonly Action<string>? _log;
    readonly object _gate = new();
    MouseKeysState? _saved;
    bool _controlled;

    public MouseKeysKeeper(IRoamSystem system, string dataDir, Action<string>? log = null)
    {
        _system = system;
        _path = Path.Combine(dataDir, FileName);
        _log = log;
    }

    /// <summary>True while H.I.B holds Mouse Keys on (the user's own setting is saved).</summary>
    public bool Active { get { lock (_gate) return _saved is not null; } }

    /// <summary>The setting H.I.B gives back (while <see cref="Active"/>).</summary>
    public MouseKeysState? Saved { get { lock (_gate) return _saved; } }

    /// <summary>At start: H.I.B died while it held Mouse Keys on — put the user's setting back. True when it did.</summary>
    public bool RecoverAfterCrash()
    {
        lock (_gate)
        {
            if (_saved is not null || Read() is not { } left) return false;
            var ok = _system.SetMouseKeys(left);
            Delete();
            _log?.Invoke(ok ? "Roam: put Mouse Keys back as it was before H.I.B last closed"
                            : "Roam: couldn't put Mouse Keys back after the last run");
            return ok;
        }
    }

    /// <summary>Another computer starts (true) or stops (false) driving this one.</summary>
    public void SetControlled(bool on)
    {
        lock (_gate)
        {
            _controlled = on;
            if (on) Ensure();
            else RestoreLocked();
        }
    }

    /// <summary>A device came or went (WM_DEVICECHANGE): a mouse may have been unplugged while controlled.</summary>
    public void DevicesChanged()
    {
        lock (_gate)
        {
            if (_controlled) Ensure();
        }
    }

    /// <summary>Gives the user's setting back now (release, quitting).</summary>
    public void Restore()
    {
        lock (_gate) RestoreLocked();
    }

    void Ensure()
    {
        if (_saved is { } held)
        {
            // Someone (or Windows) turned it off meanwhile: on again, the saved setting stays the user's.
            if (_system.GetMouseKeys() is { IsOn: false }) _system.SetMouseKeys(held.Forced());
            return;
        }
        if (_system.MousePresent) return; // a real mouse (or the user's own Mouse Keys): the pointer shows
        if (_system.GetMouseKeys() is not { } before)
        {
            _log?.Invoke("Roam: couldn't read Mouse Keys; the pointer may stay hidden on this PC");
            return;
        }
        if (before.IsOn) return;
        Write(before); // first, so a crash still restores it
        if (!_system.SetMouseKeys(before.Forced()))
        {
            Delete();
            _log?.Invoke("Roam: couldn't turn Mouse Keys on to show the pointer");
            return;
        }
        _saved = before;
        _log?.Invoke("Roam: no mouse on this PC — Mouse Keys on so the pointer shows while it's being used");
    }

    void RestoreLocked()
    {
        if (_saved is not { } s) return;
        var ok = _system.SetMouseKeys(s);
        _saved = null;
        Delete();
        _log?.Invoke(ok ? "Roam: Mouse Keys back as it was" : "Roam: couldn't put Mouse Keys back");
    }

    sealed record Stored(uint Flags, uint MaxSpeed, uint TimeToMaxSpeed);

    MouseKeysState? Read()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var s = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path));
            return s is null ? null : new MouseKeysState(s.Flags, s.MaxSpeed, s.TimeToMaxSpeed);
        }
        catch
        {
            Delete();
            return null;
        }
    }

    void Write(MouseKeysState s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Stored(s.Flags, s.MaxSpeed, s.TimeToMaxSpeed)));
            File.Move(tmp, _path, overwrite: true);
        }
        catch { /* best effort */ }
    }

    void Delete()
    {
        try { File.Delete(_path); } catch { }
    }
}
