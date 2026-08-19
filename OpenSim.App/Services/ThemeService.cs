using System;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace OpenSim.App.Services;

/// <summary>The two visual themes the design system ships. Values are persisted by name.</summary>
public enum ThemeKind { Dark, Light }

/// <summary>
/// Runtime theme switching. Both token dictionaries carry the same brush keys, so
/// applying a theme is one merged-dictionary swap; every style binds brushes with
/// DynamicResource and re-resolves live. The choice persists to
/// %AppData%/OpenSimStudio/ui.json (the materials.json / recent.json precedent).
/// </summary>
public sealed class ThemeService
{
    private readonly string _file;

    public ThemeService() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenSimStudio"))
    { }

    internal ThemeService(string directory) => _file = Path.Combine(directory, "ui.json");

    public ThemeKind Current { get; private set; } = ThemeKind.Dark;

    /// <summary>Fires after a theme is applied, for chrome that redraws imperatively.</summary>
    public event Action<ThemeKind>? ThemeChanged;

    private sealed record UiSettings(string? Theme);

    /// <summary>Reads the persisted choice (default Dark) and applies it. Call at startup.</summary>
    public void ApplySaved()
    {
        var kind = ThemeKind.Dark;
        try
        {
            if (File.Exists(_file))
            {
                var settings = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(_file));
                if (Enum.TryParse<ThemeKind>(settings?.Theme, ignoreCase: true, out var parsed))
                    kind = parsed;
            }
        }
        catch (Exception)
        {
            // A malformed ui.json must never block startup; the default theme applies.
        }
        Apply(kind);
    }

    public void Toggle() => Apply(Current == ThemeKind.Dark ? ThemeKind.Light : ThemeKind.Dark);

    public void Apply(ThemeKind kind)
    {
        var uri = new Uri(
            kind == ThemeKind.Dark ? "Themes/Tokens.Dark.xaml" : "Themes/Tokens.Light.xaml",
            UriKind.Relative);

        // The token dictionary is always merged FIRST (App.xaml order contract);
        // Controls.xaml and Icons.xaml follow and are theme-independent.
        var merged = Application.Current.Resources.MergedDictionaries;
        merged[0] = new ResourceDictionary { Source = uri };

        Current = kind;
        Save();
        ThemeChanged?.Invoke(kind);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(new UiSettings(Current.ToString())));
        }
        catch (Exception)
        {
            // Persisting the preference is best-effort; the in-session theme still applies.
        }
    }
}
