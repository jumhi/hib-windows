using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace HIB.Features;

/// <summary>
/// Minimal INotifyPropertyChanged base. Change notifications are marshalled through
/// <see cref="HibFeatures.Dispatch"/> so services can update state from background threads while
/// the UI binds to them.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        var handler = PropertyChanged;
        if (handler is null) return;
        HibFeatures.Dispatch(() => handler(this, new PropertyChangedEventArgs(name)));
    }
}

/// <summary>Library-wide hooks the app sets once at startup.</summary>
public static class HibFeatures
{
    /// <summary>
    /// How to run UI-facing callbacks (property changes, events). Default: inline on the calling
    /// thread. Avalonia apps set <c>HibFeatures.UiDispatcher = a => Dispatcher.UIThread.Post(a);</c>.
    /// </summary>
    public static Action<Action>? UiDispatcher { get; set; }

    /// <summary>Optional diagnostic log sink. Never receives API keys or pairing secrets.</summary>
    public static Action<string>? Log { get; set; }

    internal static void Dispatch(Action action)
    {
        var dispatcher = UiDispatcher;
        if (dispatcher is null) action();
        else dispatcher(action);
    }

    internal static void Trace(string message)
    {
        try { Log?.Invoke(message); } catch { }
    }
}
