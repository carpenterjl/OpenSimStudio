using System.Collections.ObjectModel;

namespace OpenSim.App.Services;

/// <summary>The shared application log shown in the Log pane.</summary>
public interface ILogService
{
    ObservableCollection<string> Entries { get; }

    /// <summary>Appends a timestamped entry; safe to call from any thread.</summary>
    void Append(string message);
}

public sealed class LogService : ILogService
{
    public ObservableCollection<string> Entries { get; } = new();

    public void Append(string message)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            Entries.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            while (Entries.Count > 500)
                Entries.RemoveAt(0);
        });
    }
}
