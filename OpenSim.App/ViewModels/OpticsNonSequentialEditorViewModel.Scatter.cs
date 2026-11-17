using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Core.Model;
using OpenSim.Optics.NonSequential.Scattering;

namespace OpenSim.App.ViewModels;

// OP-80: the Coat/Scatter and Scatter To fields of the current object and the project's ABg
// catalog (docs/Optics-Conventions.md §74). The fields show the current object's saved values
// (Read loads them; changing the current object reloads them); Apply writes them into the object
// and publishes. Apply is not a step on the sheet's undo stack.
public sealed partial class OpticsNonSequentialEditorViewModel
{
    /// <summary>The scatter model names (help, "Scattering Models").</summary>
    public IReadOnlyList<string> ScatterModels => ProjectNscScatter.ModelNames;

    /// <summary>The Scatter To methods.</summary>
    public IReadOnlyList<string> ScatterToMethods => ProjectNscScatter.ScatterToNames;

    /// <summary>The face the Coat/Scatter fields edit.</summary>
    [ObservableProperty] private int _scatterFace;
    [ObservableProperty] private string _scatterModel = "None";
    [ObservableProperty] private int _scatterRays = 1;
    [ObservableProperty] private double _scatterFraction;
    [ObservableProperty] private double _scatterSigma;
    /// <summary>ABg: catalog name; ABg File and BSDF: full path of the file.</summary>
    [ObservableProperty] private string _scatterReflect = "";
    [ObservableProperty] private string _scatterTransmit = "";
    [ObservableProperty] private double _scatterAngle;
    [ObservableProperty] private bool _thinWindowScattering;

    [ObservableProperty] private string _scatterToMethod = "None";
    /// <summary>Object numbers separated by spaces.</summary>
    [ObservableProperty] private string _scatterToList = "";
    /// <summary>One target per line: object, size (lens units), limit (sr); object 0 is "None".</summary>
    [ObservableProperty] private string _scatterTargets = "";

    /// <summary>The ABg catalog as text, one row per line: NAME wavelength angle A B g.</summary>
    [ObservableProperty] private string _abgCatalogText = "";

    partial void OnCurrentRowChanged(int value) => LoadScatterFields();

    /// <summary>Loads the current object's scatter fields and the catalog.</summary>
    [RelayCommand]
    private void ReadScatter() => LoadScatterFields();

    private void LoadScatterFields()
    {
        AbgCatalogText = ProjectNscScatter.Catalog(_settings).Format();
        if (Document.RowCount == 0) return;
        var row = ReadRow(Document.Rows[Current]);
        var face = row.Faces?.LastOrDefault(f => (f.Face ?? 0) == ScatterFace);
        ScatterModel = ProjectNscScatter.ModelOf(face?.ScatterModel) is { } m ? ProjectNscScatter.ModelNames[(int)m] : face?.ScatterModel ?? "None";
        ScatterRays = face?.ScatterRays ?? 1;
        ScatterFraction = face?.ScatterFraction ?? 0;
        ScatterSigma = face?.ScatterSigma ?? 0;
        ScatterReflect = face?.ScatterReflect ?? "";
        ScatterTransmit = face?.ScatterTransmit ?? "";
        ScatterAngle = face?.ScatterAngle ?? 0;
        ThinWindowScattering = face?.ThinWindowScattering ?? false;
        ScatterToMethod = string.IsNullOrWhiteSpace(row.ScatterToMethod) ? "None" : row.ScatterToMethod!;
        ScatterToList = row.ScatterToList ?? "";
        ScatterTargets = string.Join(Environment.NewLine, (row.ScatterTargets ?? new List<OpticsNscScatterTargetSettings>())
            .Select(t => string.Join(" ", N(t.Object ?? 0), N(t.Size ?? 0), N(t.Limit ?? 0))));
        static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>Writes the fields into the current object (face <see cref="ScatterFace"/> and its
    /// Scatter To section) and publishes; refusals go to <see cref="Status"/>.</summary>
    [RelayCommand]
    private void ApplyScatter() => Status = ApplyScatterFields() ?? $"Object {Current + 1}: scattering of face {ScatterFace} and Scatter To applied.";

    /// <summary>As <see cref="ApplyScatterCommand"/>; returns why not, or null.</summary>
    public string? ApplyScatterFields()
    {
        if (Document.RowCount == 0) return "There is no object.";
        if (ProjectNscScatter.ModelOf(ScatterModel) is null) return $"The scatter model '{ScatterModel}' is not one of {string.Join(", ", ScatterModels)}.";
        if (!ScatterToMethods.Contains(ScatterToMethod)) return $"The Scatter To method '{ScatterToMethod}' is not one of {string.Join(", ", ScatterToMethods)}.";
        var targets = new List<OpticsNscScatterTargetSettings>();
        int line = 0;
        foreach (var raw in (ScatterTargets ?? "").Split('\n'))
        {
            line++;
            var t = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length == 0) continue;
            if (t.Length != 3
                || !int.TryParse(t[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int obj)
                || !double.TryParse(t[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double size)
                || !double.TryParse(t[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double limit))
                return $"Importance-sampling target line {line}: expected object, size and limit.";
            targets.Add(new OpticsNscScatterTargetSettings { Object = obj, Size = size, Limit = limit });
        }
        if (targets.Count > 6) return $"{targets.Count} importance-sampling targets; at most 6.";

        var sheetRow = Document.Rows[Current];
        var row = ReadRow(sheetRow);
        var faces = row.Faces?.ToList() ?? new List<OpticsNscFaceSettings>();
        int at = faces.FindLastIndex(f => (f.Face ?? 0) == ScatterFace);
        var entry = at >= 0 ? faces[at] : new OpticsNscFaceSettings { Face = ScatterFace };
        bool none = ProjectNscScatter.ModelOf(ScatterModel) == NscScatterModel.None;
        entry = entry with
        {
            ScatterModel = none ? null : ScatterModel,
            ScatterRays = none ? null : ScatterRays,
            ScatterFraction = none ? null : ScatterFraction,
            ScatterSigma = none || ScatterSigma == 0 ? null : ScatterSigma,
            ScatterReflect = none || string.IsNullOrWhiteSpace(ScatterReflect) ? null : ScatterReflect.Trim(),
            ScatterTransmit = none || string.IsNullOrWhiteSpace(ScatterTransmit) ? null : ScatterTransmit.Trim(),
            ScatterAngle = none || ScatterAngle == 0 ? null : ScatterAngle,
            ThinWindowScattering = none || !ThinWindowScattering ? null : true,
        };
        if (at >= 0) faces[at] = entry; else faces.Add(entry);
        bool noTo = ScatterToMethod == "None";
        _base[sheetRow.Id] = row with
        {
            Faces = faces,
            ScatterToMethod = noTo ? null : ScatterToMethod,
            ScatterToList = noTo || string.IsNullOrWhiteSpace(ScatterToList) ? null : ScatterToList.Trim(),
            ScatterTargets = noTo || targets.Count == 0 ? null : targets,
        };
        AfterChange(publish: true);
        return null;
    }

    /// <summary>Parses <see cref="AbgCatalogText"/> into the project's ABg catalog and publishes.</summary>
    [RelayCommand]
    private void ApplyAbgCatalog() => Status = ApplyAbgCatalogText() ?? "ABg catalog applied.";

    /// <summary>As <see cref="ApplyAbgCatalogCommand"/>; returns why not, or null.</summary>
    public string? ApplyAbgCatalogText()
    {
        NscAbgCatalog catalog;
        try { catalog = NscAbgCatalog.Parse(AbgCatalogText); }
        catch (FormatException e) { return e.Message; }
        if (catalog.Validate() is { Count: > 0 } problems) return string.Join(" ", problems);
        _settings = (_settings ?? new OpticsNonSequentialSettings()) with
        {
            AbgCatalog = catalog.Profiles.Count == 0 ? null : ProjectNscScatter.ToSettings(catalog),
        };
        AfterChange(publish: true);
        return null;
    }
}
