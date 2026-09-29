using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace AltStableCompanion.App;

/// <summary>What a bound property needs, and no toolkit.</summary>
internal abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    /// <summary>For a property worked out from another one.</summary>
    protected void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A button's action.</summary>
internal sealed class Command(Action run) : ICommand
{
    private bool _enabled = true;

    public event EventHandler? CanExecuteChanged;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool CanExecute(object? parameter) => _enabled;

    public void Execute(object? parameter)
    {
        if (_enabled) run();
    }
}
