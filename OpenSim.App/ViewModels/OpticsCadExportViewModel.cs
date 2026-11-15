using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Core.Model;
using OpenSim.Optics.CadExport;
using OpenSim.Optics.Materials;

namespace OpenSim.App.ViewModels;

// OP-96: the Export CAD Files dialog.

/// <summary>
/// Export CAD Files (plan item OP-96; OpticStudio 2025 R2 help, "CAD Files"): the file type, the
/// surface range, the edge style, the mirror substrate, the tolerance and the rays; Export writes the
/// project's lens through <see cref="CadExporter"/> off the UI thread and lists what was written. The
/// dialog only reads the lens; nothing in the project changes. The settings are not saved with the
/// project.
/// </summary>
public sealed partial class OpticsCadExportViewModel : ObservableObject
{
    private readonly Func<OpticsSettings> _system;
    private readonly Func<IReadOnlyList<OpticalCatalog>> _catalogs;

    public OpticsCadExportViewModel(Func<OpticsSettings> system, Func<IReadOnlyList<OpticalCatalog>> catalogs)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
        _selectedFormat = Formats[0];
        _selectedEdgeStyle = EdgeStyles[0];
        _selectedTolerance = Tolerances[0];
        _selectedRayPattern = RayPatterns[1];
    }

    public IReadOnlyList<InteractiveChoice<CadExportFormat>> Formats { get; } = new[]
    {
        new InteractiveChoice<CadExportFormat>("STEP (AP214 solids)", CadExportFormat.Step),
        new InteractiveChoice<CadExportFormat>("IGES 5.3 (surfaces and linework)", CadExportFormat.Iges),
        new InteractiveChoice<CadExportFormat>("STL (triangles)", CadExportFormat.Stl),
        new InteractiveChoice<CadExportFormat>("DXF (2-D cross-section)", CadExportFormat.Dxf),
    };

    public IReadOnlyList<InteractiveChoice<CadEdgeStyle>> EdgeStyles { get; } = new[]
    {
        new InteractiveChoice<CadEdgeStyle>("Squared to next surface", CadEdgeStyle.Squared),
        new InteractiveChoice<CadEdgeStyle>("Tapered to next surface", CadEdgeStyle.Tapered),
        new InteractiveChoice<CadEdgeStyle>("Flat to next surface", CadEdgeStyle.FlatToNext),
    };

    public IReadOnlyList<InteractiveChoice<double>> Tolerances { get; } =
        CadExportSettings.ToleranceChoices.Select(t => new InteractiveChoice<double>(t.ToString("0E+0", CultureInfo.InvariantCulture), t)).ToArray();

    public IReadOnlyList<InteractiveChoice<CadRayPattern>> RayPatterns { get; } = new[]
    {
        new InteractiveChoice<CadRayPattern>("None", CadRayPattern.None),
        new InteractiveChoice<CadRayPattern>("Y fan", CadRayPattern.YFan),
        new InteractiveChoice<CadRayPattern>("X fan", CadRayPattern.XFan),
        new InteractiveChoice<CadRayPattern>("XY fan", CadRayPattern.XYFan),
        new InteractiveChoice<CadRayPattern>("Ring", CadRayPattern.Ring),
        new InteractiveChoice<CadRayPattern>("Grid", CadRayPattern.Grid),
    };

    [ObservableProperty] private InteractiveChoice<CadExportFormat> _selectedFormat;
    [ObservableProperty] private int _firstSurface = 1;

    /// <summary>0: the last surface before the image.</summary>
    [ObservableProperty] private int _lastSurface;

    [ObservableProperty] private InteractiveChoice<CadEdgeStyle> _selectedEdgeStyle;

    /// <summary>Lens units; 0 exports bare mirror surfaces (refused for STEP).</summary>
    [ObservableProperty] private double _mirrorSubstrateThickness;

    [ObservableProperty] private InteractiveChoice<double> _selectedTolerance;
    [ObservableProperty] private InteractiveChoice<CadRayPattern> _selectedRayPattern;
    [ObservableProperty] private int _numberOfRays = 7;

    /// <summary>0: every field.</summary>
    [ObservableProperty] private int _field;

    /// <summary>0: every wavelength.</summary>
    [ObservableProperty] private int _wavelength;

    [ObservableProperty] private bool _deleteVignetted;
    [ObservableProperty] private bool _stlBinary = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isRunning;

    public bool IsIdle => !IsRunning;

    [ObservableProperty] private string _status = "Writes the lens elements, mirrors and rays as a CAD file; the lens is not changed.";

    /// <summary>What the last export wrote, one line each.</summary>
    [ObservableProperty] private string _details = "";

    public CadExportResult? LastResult { get; private set; }

    /// <summary>The settings the form shows.</summary>
    public CadExportSettings Settings => new()
    {
        Format = SelectedFormat.Value,
        FirstSurface = FirstSurface,
        LastSurface = LastSurface,
        EdgeStyle = SelectedEdgeStyle.Value,
        MirrorSubstrateThickness = MirrorSubstrateThickness,
        Tolerance = SelectedTolerance.Value,
        RayPattern = SelectedRayPattern.Value,
        NumberOfRays = NumberOfRays,
        Field = Field,
        Wavelength = Wavelength,
        DeleteVignetted = DeleteVignetted,
        StlBinary = StlBinary,
    };

    /// <summary>The save dialog's filter for the selected format.</summary>
    public string FileFilter => SelectedFormat.Value switch
    {
        CadExportFormat.Step => "STEP AP214 (*.step;*.stp)|*.step;*.stp",
        CadExportFormat.Iges => "IGES 5.3 (*.igs;*.iges)|*.igs;*.iges",
        CadExportFormat.Stl => "STL (*.stl)|*.stl",
        _ => "DXF (*.dxf)|*.dxf",
    };

    /// <summary>The file name the save dialog proposes.</summary>
    public string DefaultFileName
    {
        get
        {
            string title = _system().SystemData?.Title is { Length: > 0 } t ? t : "lens";
            foreach (char c in Path.GetInvalidFileNameChars()) title = title.Replace(c, '_');
            return title + CadExporter.Extension(SelectedFormat.Value);
        }
    }

    /// <summary>Writes the file; returns the result, or null when the export was refused (the
    /// reason is in <see cref="Status"/>).</summary>
    public async Task<CadExportResult?> ExportToAsync(string path, DateTime? timestamp = null)
    {
        var system = _system();
        var catalogs = _catalogs();
        var settings = Settings;
        IsRunning = true;
        Status = "Exporting…";
        try
        {
            var result = await Task.Run(() => CadExporter.Export(system, catalogs, settings, path, timestamp));
            LastResult = result;
            Status = $"Wrote {path}.";
            Details = string.Join(Environment.NewLine, result.Notes);
            return result;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            LastResult = null;
            Status = $"Not exported: {ex.Message}";
            Details = "";
            return null;
        }
        finally { IsRunning = false; }
    }
}
