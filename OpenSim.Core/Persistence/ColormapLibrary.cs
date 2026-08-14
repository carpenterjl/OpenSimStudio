using System.IO;
using System.Text.Json;
using OpenSim.Core.PostProcessing;

namespace OpenSim.Core.Persistence;

/// <summary>
/// The colormap library: built-in maps overlaid by an offline-editable user file, exactly
/// like <see cref="MaterialLibrary"/>. User maps ADD to the built-ins or OVERRIDE one by
/// name; deleting an override restores the built-in on the next load. The user file lives
/// at <c>%AppData%/OpenSimStudio/colormaps.json</c>.
/// <para>
/// Colormaps are stored PER USER, never per project: a result set is session-transient
/// and how it is colored is the operator's taste, not a property of the model. Load
/// problems never fail startup and are never silent either — <see cref="LoadWarnings"/>
/// goes to the log panel, and a malformed file is ignored rather than overwritten, so a
/// hand-edited map that trips a typo can still be repaired by hand.
/// </para>
/// </summary>
public sealed class ColormapLibrary
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _userDirectory;
    private readonly List<ColormapDefinition> _colormaps = new();
    private readonly List<string> _loadWarnings = new();
    private readonly HashSet<string> _userNames = new(StringComparer.Ordinal);

    /// <summary>Built-ins first, then user maps, in insertion order.</summary>
    public IReadOnlyList<ColormapDefinition> Colormaps => _colormaps;

    /// <summary>Problems found while loading the user file — log these at startup.</summary>
    public IReadOnlyList<string> LoadWarnings => _loadWarnings;

    public ColormapLibrary() : this(DefaultUserDirectory) { }

    /// <summary>Test seam: a library rooted at a private directory.</summary>
    internal ColormapLibrary(string userDirectory)
    {
        _userDirectory = userDirectory;
        _colormaps.AddRange(ColormapDefinition.Presets);
        LoadUserColormaps();
    }

    private static string DefaultUserDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenSimStudio");

    private string UserFile => Path.Combine(_userDirectory, "colormaps.json");

    /// <summary>True when the named map came from the user file (built-ins are undeletable).</summary>
    public bool IsUserDefined(string name) => _userNames.Contains(name);

    private void LoadUserColormaps()
    {
        if (!File.Exists(UserFile)) return;

        List<ColormapDefinition>? loaded;
        try
        {
            loaded = JsonSerializer.Deserialize<List<ColormapDefinition>>(
                File.ReadAllText(UserFile), JsonOptions);
        }
        catch (JsonException e)
        {
            _loadWarnings.Add($"User colormap file '{UserFile}' is malformed and was ignored: {e.Message}");
            return;
        }
        catch (ArgumentException e)
        {
            // A map whose stops break the invariants throws out of the constructor during
            // deserialization; name the file, keep the built-ins, carry on.
            _loadWarnings.Add($"User colormap file '{UserFile}' holds an invalid colormap " +
                              $"and was ignored: {e.Message}");
            return;
        }
        if (loaded is null) return;

        foreach (var map in loaded)
        {
            Overlay(map);
            _userNames.Add(map.Name);
        }
    }

    /// <summary>Replaces a same-named map or appends.</summary>
    private void Overlay(ColormapDefinition map)
    {
        int existing = _colormaps.FindIndex(m => m.Name == map.Name);
        if (existing >= 0) _colormaps[existing] = map;
        else _colormaps.Add(map);
    }

    /// <summary>Adds or updates a user colormap and persists the user file immediately.</summary>
    public void AddOrUpdate(ColormapDefinition map)
    {
        ArgumentNullException.ThrowIfNull(map);
        Overlay(map);
        _userNames.Add(map.Name);
        Save();
    }

    /// <summary>
    /// Removes a user colormap (persisting immediately). Removing an override of a built-in
    /// name restores the built-in. Refuses to delete a pure built-in loudly.
    /// </summary>
    public void Remove(string name)
    {
        int index = _colormaps.FindIndex(m => m.Name == name);
        if (index < 0)
            throw new InvalidOperationException($"No colormap named '{name}' exists.");
        if (!_userNames.Contains(name))
            throw new InvalidOperationException(
                $"'{name}' is a built-in colormap and cannot be deleted. " +
                "User overrides of built-ins can be deleted (restoring the built-in).");

        var builtIn = ColormapDefinition.Presets.FirstOrDefault(m => m.Name == name);
        if (builtIn is not null) _colormaps[index] = builtIn;
        else _colormaps.RemoveAt(index);
        _userNames.Remove(name);
        Save();
    }

    /// <summary>Writes only the user-defined maps to the AppData file.</summary>
    public void Save()
    {
        Directory.CreateDirectory(_userDirectory);
        var user = _colormaps.Where(m => _userNames.Contains(m.Name)).ToList();
        File.WriteAllText(UserFile, JsonSerializer.Serialize(user, JsonOptions));
    }
}
