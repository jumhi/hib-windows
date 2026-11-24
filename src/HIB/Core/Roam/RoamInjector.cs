// H.I.B Roam (docs/SPACE.md §12.3): the target applies what the controller sends on one dedicated, high-priority input
// thread — never the UI thread, never a dispatcher queue, never the network thread. The queue is a ring of small structs
// (no allocation per event); a move waiting behind another move is replaced by the newer one, relative motion waiting
// behind relative motion is added up, and nothing else is ever merged or reordered. Before a key or a click, the UIPI
// guard looks at the window in front (§12.2), and the key path is counted (§12.2).
using System;
using System.Threading;

namespace HIB.Core.Roam;

public sealed class RoamInjector : IDisposable
{
    enum Kind : byte { Move, Rel, Button, Wheel, Key, Barrier }

    struct Op
    {
        public Kind Kind;
        public double X, Y;
        public int Code;
        public bool Down;
        public ManualResetEventSlim? Done;
    }

    readonly IRoamPlatform _platform;
    readonly RoamKeyStats _stats;
    readonly Func<RoamForeground?> _front;
    readonly UipiGuard _uipi = new();
    readonly RoamDelta _wheel = new();
    readonly object _lock = new();
    readonly Thread _thread;
    Op[] _ring = new Op[256];
    int _head, _count;
    bool _stop;

    public RoamInjector(IRoamPlatform platform, RoamKeyStats stats, Func<RoamForeground?> front)
    {
        _platform = platform;
        _stats = stats;
        _front = front;
        _thread = new Thread(Loop) { IsBackground = true, Name = "H.I.B Roam inject", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    /// <summary>The app in front runs as administrator: tell the user (once per app). Raised on the input thread.</summary>
    public event Action<string>? AdminAppInFront;

    // ---------------- producers (any thread) ----------------

    public void Move(double px, double py) => Enqueue(new Op { Kind = Kind.Move, X = px, Y = py });

    public void Relative(int dx, int dy)
    {
        if (dx != 0 || dy != 0) Enqueue(new Op { Kind = Kind.Rel, X = dx, Y = dy });
    }

    public void Button(RoamButton b, bool down) => Enqueue(new Op { Kind = Kind.Button, Code = (int)b, Down = down });

    /// <summary>ctlWheel in 120ths of a notch; fractions are kept for the next one (§12.3 smooth scrolling).</summary>
    public void Wheel(double dx, double dy)
    {
        if (dx != 0 || dy != 0) Enqueue(new Op { Kind = Kind.Wheel, X = dx, Y = dy });
    }

    public void Key(int usage, bool down) => Enqueue(new Op { Kind = Kind.Key, Code = usage, Down = down });

    /// <summary>Waits until everything queued so far was applied (tests, quitting).</summary>
    public bool Drain(TimeSpan timeout)
    {
        if (Thread.CurrentThread == _thread) return true;
        lock (_lock)
        {
            if (_stop) return true;
        }
        using var done = new ManualResetEventSlim();
        Enqueue(new Op { Kind = Kind.Barrier, Done = done });
        return done.Wait(timeout);
    }

    void Enqueue(Op op)
    {
        lock (_lock)
        {
            if (_stop) return;
            if (_count > 0)
            {
                ref var tail = ref _ring[(_head + _count - 1) % _ring.Length];
                if (op.Kind == Kind.Move && tail.Kind == Kind.Move)
                {
                    tail.X = op.X;
                    tail.Y = op.Y;
                    return;
                }
                if (op.Kind == Kind.Rel && tail.Kind == Kind.Rel)
                {
                    tail.X += op.X;
                    tail.Y += op.Y;
                    return;
                }
            }
            if (_count == _ring.Length) Grow();
            _ring[(_head + _count) % _ring.Length] = op;
            _count++;
            Monitor.Pulse(_lock);
        }
    }

    void Grow()
    {
        var bigger = new Op[_ring.Length * 2];
        for (var i = 0; i < _count; i++) bigger[i] = _ring[(_head + i) % _ring.Length];
        _ring = bigger;
        _head = 0;
    }

    /// <summary>How many are waiting (tests).</summary>
    public int Pending { get { lock (_lock) return _count; } }

    // ---------------- the input thread ----------------

    void Loop()
    {
        while (true)
        {
            Op op;
            lock (_lock)
            {
                while (_count == 0 && !_stop) Monitor.Wait(_lock);
                if (_count == 0) return;
                op = _ring[_head];
                _ring[_head] = default;
                _head = (_head + 1) % _ring.Length;
                _count--;
            }
            try { Apply(op); }
            catch { /* the input thread never dies */ }
        }
    }

    void Apply(in Op op)
    {
        switch (op.Kind)
        {
            case Kind.Move:
                _platform.InjectMove(op.X, op.Y);
                break;
            case Kind.Rel:
                _platform.InjectRelative((int)op.X, (int)op.Y);
                break;
            case Kind.Button:
                if (op.Down) CheckFront();
                _platform.InjectButton((RoamButton)op.Code, op.Down);
                break;
            case Kind.Wheel:
            {
                var (h, v) = _wheel.Add(op.X, op.Y);
                if (h != 0 || v != 0) _platform.InjectWheel(Math.Clamp(h, -RoamWheel.Notch * 100, RoamWheel.Notch * 100),
                    Math.Clamp(v, -RoamWheel.Notch * 100, RoamWheel.Notch * 100));
                break;
            }
            case Kind.Key:
                if (RoamKeys.ToWindows(op.Code) is null)
                {
                    _stats.Unmapped(RoamKeyStats.UsageCode(op.Code));
                    break;
                }
                if (op.Down) CheckFront();
                if (_platform.InjectKey(op.Code, op.Down)) _stats.Injected();
                else _stats.Failed();
                break;
            case Kind.Barrier:
                op.Done?.Set();
                break;
        }
    }

    void CheckFront()
    {
        RoamForeground? front;
        try { front = _front(); }
        catch { return; }
        if (_uipi.Check(front) is { } app) AdminAppInFront?.Invoke(app);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_stop) return;
            _stop = true;
            Monitor.PulseAll(_lock);
        }
        _thread.Join(TimeSpan.FromSeconds(1));
    }
}
