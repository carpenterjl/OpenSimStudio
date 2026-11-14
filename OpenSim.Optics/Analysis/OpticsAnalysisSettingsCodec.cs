using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenSim.Optics.Analysis;

/// <summary>
/// Writes an analysis's settings record into the project file as JSON and reads it back.
/// Reading is forgiving in the direction files age: a property the record no longer has is
/// ignored, one it has gained keeps its default, and a value that does not parse leaves the
/// whole record at its defaults with a note naming the analysis — never a failed load.
/// </summary>
public static class OpticsAnalysisSettingsCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The options settings are written and read with; the window's settings editor
    /// rebuilds a record from edited values through the same options.</summary>
    public static JsonSerializerOptions SerializerOptions => Options;

    public static JsonElement Save(object settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return JsonSerializer.SerializeToElement(settings, settings.GetType(), Options);
    }

    /// <summary>The settings saved for <paramref name="analysis"/>, or its defaults when nothing
    /// was saved or the saved value cannot be read (then <paramref name="note"/> says so).</summary>
    public static object Load(IOpticsAnalysis analysis, JsonElement? saved, out string? note)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        note = null;
        if (saved is not { ValueKind: JsonValueKind.Object } element)
        {
            if (saved is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) })
                note = $"The saved settings of '{analysis.Name}' are not a settings record; its defaults are used.";
            return analysis.CreateDefaultSettings();
        }
        try
        {
            return element.Deserialize(analysis.SettingsType, Options) ?? analysis.CreateDefaultSettings();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            note = $"The saved settings of '{analysis.Name}' could not be read ({ex.Message}); its defaults are used.";
            return analysis.CreateDefaultSettings();
        }
    }

    /// <summary>A copy made through the saved form: what a settings record becomes after a
    /// save and a load.</summary>
    public static object Clone(IOpticsAnalysis analysis, object settings) =>
        Load(analysis, Save(settings), out _);
}
