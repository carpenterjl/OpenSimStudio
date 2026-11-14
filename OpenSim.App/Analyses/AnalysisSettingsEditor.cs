using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenSim.Optics.Analysis;

namespace OpenSim.App.Analyses;

/// <summary>How a settings property is edited.</summary>
public enum SettingKind
{
    Number,
    Integer,
    Flag,
    Choice,
    Text,
}

/// <summary>One editable property of an analysis's settings record, as the window's settings
/// form shows it. Text and choice values are held as typed; <see cref="AnalysisSettingsEditor.TryBuild"/>
/// checks them.</summary>
public sealed partial class AnalysisSettingField : ObservableObject
{
    internal AnalysisSettingField(PropertyInfo property, string jsonName, SettingKind kind, bool nullable,
        IReadOnlyList<string> choices)
    {
        Property = property;
        JsonName = jsonName;
        Kind = kind;
        IsNullable = nullable;
        Choices = choices;
        Label = property.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? Words(property.Name);
        Description = property.GetCustomAttribute<DescriptionAttribute>()?.Description;
    }

    internal PropertyInfo Property { get; }
    internal string JsonName { get; }
    internal bool IsNullable { get; }

    public string Name => Property.Name;
    public string Label { get; }
    public string? Description { get; }
    public SettingKind Kind { get; }

    /// <summary>The names a <see cref="SettingKind.Choice"/> offers (an enum's members).</summary>
    public IReadOnlyList<string> Choices { get; }

    /// <summary>The typed value of a number, integer or text field.</summary>
    [ObservableProperty] private string _text = "";

    /// <summary>The value of a <see cref="SettingKind.Flag"/>.</summary>
    [ObservableProperty] private bool _flag;

    /// <summary>The value of a <see cref="SettingKind.Choice"/>.</summary>
    [ObservableProperty] private string? _choice;

    /// <summary>"ShowConventions" → "Show conventions", for a property with no display name.</summary>
    private static string Words(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1])) sb.Append(' ').Append(char.ToLowerInvariant(c));
            else sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>
/// The settings form of an analysis window, made from the settings record by reflection so
/// that a new analysis gets its form by declaring its record and nothing else. Numbers,
/// integers, booleans, enums and strings (and their nullable forms) are edited; any other
/// property is carried through unchanged. A new record is built through the same JSON form the
/// project file stores, so what the form produces is exactly what a save and load would.
/// </summary>
public sealed class AnalysisSettingsEditor
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    private AnalysisSettingsEditor(Type settingsType, IReadOnlyList<AnalysisSettingField> fields)
    {
        SettingsType = settingsType;
        Fields = fields;
    }

    public Type SettingsType { get; }

    public IReadOnlyList<AnalysisSettingField> Fields { get; }

    /// <summary>A form showing <paramref name="settings"/>.</summary>
    public static AnalysisSettingsEditor For(object settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var type = settings.GetType();
        var naming = OpticsAnalysisSettingsCodec.SerializerOptions.PropertyNamingPolicy;
        var fields = new List<AnalysisSettingField>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || property.SetMethod is not { IsPublic: true }) continue;
            var underlying = Nullable.GetUnderlyingType(property.PropertyType);
            var valueType = underlying ?? property.PropertyType;
            bool nullable = underlying is not null || (!valueType.IsValueType);
            SettingKind? kind =
                valueType == typeof(double) || valueType == typeof(float) || valueType == typeof(decimal) ? SettingKind.Number
                : valueType == typeof(int) || valueType == typeof(long) || valueType == typeof(short) ? SettingKind.Integer
                : valueType == typeof(bool) ? SettingKind.Flag
                : valueType.IsEnum ? SettingKind.Choice
                : valueType == typeof(string) ? SettingKind.Text
                : null;
            if (kind is null) continue;
            var field = new AnalysisSettingField(property, naming?.ConvertName(property.Name) ?? property.Name,
                kind.Value, nullable, valueType.IsEnum ? Enum.GetNames(valueType) : Array.Empty<string>());
            Show(field, property.GetValue(settings));
            fields.Add(field);
        }
        return new AnalysisSettingsEditor(type, fields);
    }

    /// <summary>Puts <paramref name="settings"/>' values back into the fields (Reset, Cancel).</summary>
    public void Show(object settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        foreach (var field in Fields) Show(field, field.Property.GetValue(settings));
    }

    /// <summary>A new settings record: <paramref name="original"/> with the fields' values.
    /// False, with <paramref name="error"/> naming the field, when a value does not parse.</summary>
    public bool TryBuild(object original, out object result, out string? error)
    {
        ArgumentNullException.ThrowIfNull(original);
        result = original;
        error = null;
        var options = OpticsAnalysisSettingsCodec.SerializerOptions;
        if (JsonSerializer.SerializeToNode(original, SettingsType, options) is not JsonObject node)
        {
            error = "The settings are not a record.";
            return false;
        }
        foreach (var field in Fields)
        {
            if (!TryValue(field, out var value, out error)) return false;
            node[field.JsonName] = value;
        }
        try
        {
            result = node.Deserialize(SettingsType, options) ?? original;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void Show(AnalysisSettingField field, object? value)
    {
        switch (field.Kind)
        {
            case SettingKind.Flag: field.Flag = value is true; break;
            case SettingKind.Choice: field.Choice = value?.ToString(); break;
            default: field.Text = value is IFormattable f ? f.ToString(null, Ci) : value?.ToString() ?? ""; break;
        }
    }

    private static bool TryValue(AnalysisSettingField field, out JsonNode? value, out string? error)
    {
        value = null;
        error = null;
        string text = field.Text.Trim();
        switch (field.Kind)
        {
            case SettingKind.Flag:
                value = JsonValue.Create(field.Flag);
                return true;
            case SettingKind.Choice:
                if (field.Choice is { } choice && field.Choices.Contains(choice)) { value = JsonValue.Create(choice); return true; }
                if (field.IsNullable && field.Choice is null) return true;
                error = $"{field.Label}: choose one of {string.Join(", ", field.Choices)}.";
                return false;
            case SettingKind.Text:
                value = JsonValue.Create(field.Text);
                return true;
        }
        if (text.Length == 0 && field.IsNullable) return true;
        if (field.Kind == SettingKind.Integer)
        {
            if (long.TryParse(text, NumberStyles.Integer, Ci, out long n)) { value = JsonValue.Create(n); return true; }
            error = $"{field.Label}: '{field.Text}' is not a whole number.";
            return false;
        }
        if (double.TryParse(text, NumberStyles.Float, Ci, out double x) && double.IsFinite(x)) { value = JsonValue.Create(x); return true; }
        error = $"{field.Label}: '{field.Text}' is not a number.";
        return false;
    }
}
