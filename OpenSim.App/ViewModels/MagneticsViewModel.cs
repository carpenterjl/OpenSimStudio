using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Controls;
using OpenSim.App.Services;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Magnetics;
using OpenSim.Solvers.Magnetics;

namespace OpenSim.App.ViewModels;

/// <summary>A board net offered as a winding, with its selection toggle.</summary>
public partial class WindingNetRow : ObservableObject
{
    public WindingNetRow(CopperNet net) => Net = net;
    public CopperNet Net { get; }
    public string Label => Net.Label;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>One turn of the winding section, editable: a ring of copper at radii r0..r1 and heights
/// z0..z1 (z = 0 at the board's top copper surface, negative downward).</summary>
public partial class TurnRow : ObservableObject
{
    [ObservableProperty] private string _winding = "W";
    [ObservableProperty] private int _layer = 1;
    [ObservableProperty] private double _r0Mm;
    [ObservableProperty] private double _r1Mm;
    [ObservableProperty] private double _z0Um;
    [ObservableProperty] private double _z1Um;

    public static TurnRow From(WindingTurn t) => new()
    {
        Winding = t.Net, Layer = t.LayerOrder, R0Mm = t.R0 * 1e3, R1Mm = t.R1 * 1e3, Z0Um = t.Z0 * 1e6, Z1Um = t.Z1 * 1e6
    };

    public WindingTurn ToTurn() => new(Winding.Trim(), Layer, R0Mm * 1e-3, R1Mm * 1e-3, Z0Um * 1e-6, Z1Um * 1e-6);
}

/// <summary>A winding's lead-out trace for the impedance matrix: length and width; the thickness is
/// the winding's copper.</summary>
public partial class LeadOutRow : ObservableObject
{
    public LeadOutRow(string winding) => Winding = winding;
    public string Winding { get; }
    [ObservableProperty] private double _lengthMm;
    [ObservableProperty] private double _widthMm = 1;
}

/// <summary>
/// Planar magnetics (Feature 13, FU-39, FU-40) — the solve the command line's <c>magnetics</c>
/// command runs (<see cref="PlanarMagneticsStudy"/>): the windings' copper cut along a ray from the
/// winding axis (or averaged over several angles), each crossing one ring of an axisymmetric model,
/// with an optional pot or ER core; the static or time-harmonic field; the driven winding's
/// inductance, AC resistance and mutuals, and the winding impedance matrix. The turns table can also
/// be filled by hand, so a section needs no board. The flux-density picture samples the solved field.
/// </summary>
public partial class MagneticsViewModel : ObservableObject
{
    public const string NoCore = "None (air)";
    public const string PotCore = "Pot core (axisymmetric)";
    public const string ErCoreKind = "ER core (legs as an area-preserving wall)";

    private readonly ILogService _log;
    private PcbBoard? _board;
    private string? _boardFileName;
    private Func<NetMeshOptions>? _meshOptions;
    private PlanarMagneticsResult? _solved;
    private string? _axis;
    private IReadOnlyList<string> _cutNotes = Array.Empty<string>();

    public MagneticsViewModel(ILogService log) => _log = log;

    // ---- Cut from the board ----
    public ObservableCollection<WindingNetRow> Nets { get; } = new();
    [ObservableProperty] private bool _hasBoard;
    [ObservableProperty] private double _centerXMm;
    [ObservableProperty] private double _centerYMm;
    [ObservableProperty] private double _cutAngleDegrees;
    /// <summary>1 = one cut at the angle; more = the turns at their mean radii over that many angles.</summary>
    [ObservableProperty] private int _cutAngles = 1;
    [ObservableProperty] private double _maxRadiusMm = 30;
    [ObservableProperty] private string _cutResult = "";

    // ---- The section ----
    public ObservableCollection<TurnRow> Turns { get; } = new();
    public ObservableCollection<string> Windings { get; } = new();
    [ObservableProperty] private string? _driveWinding;

    // ---- The core ----
    public IReadOnlyList<string> CoreKinds { get; } = new[] { NoCore, PotCore, ErCoreKind };
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPotCore), nameof(IsErCore), nameof(HasCore))]
    private string? _coreKind = NoCore;
    public bool HasCore => CoreKind is PotCore or ErCoreKind;
    public bool IsPotCore => CoreKind == PotCore;
    public bool IsErCore => CoreKind == ErCoreKind;
    [ObservableProperty] private double _postRadiusMm = 3;
    [ObservableProperty] private double _windowRadiusMm = 10;   // pot
    [ObservableProperty] private double _outerRadiusMm = 12;    // pot
    [ObservableProperty] private double _windowWidthMm = 6;     // ER
    [ObservableProperty] private double _legWidthMm = 3;        // ER
    [ObservableProperty] private double _depthMm = 12;          // ER
    [ObservableProperty] private double _plateThicknessMm = 2;
    [ObservableProperty] private double _windowHeightMm = 2;
    [ObservableProperty] private double _gapMm = 0.2;
    [ObservableProperty] private double _coreMu = 2000;

    // ---- The solve ----
    /// <summary>0 = magnetostatic; above, time-harmonic (solid turns show skin and proximity).</summary>
    [ObservableProperty] private double _frequencyKhz;
    [ObservableProperty] private double _strandDiameterMm;
    [ObservableProperty] private int _strandsPerTurn = 1;
    [ObservableProperty] private double _copperConductivity = 5.8e7;
    public ObservableCollection<LeadOutRow> LeadOuts { get; } = new();

    // ---- Results ----
    [ObservableProperty] private string _result = "";
    [ObservableProperty] private string _matrixResult = "";
    [ObservableProperty] private string _assumptions = "";
    [ObservableProperty] private ImageSource? _fieldImage;
    [ObservableProperty] private bool _hasResult;
    public ObservableCollection<string> ResultLines { get; } = new();

    public void LoadBoard(PcbBoard board, Func<NetMeshOptions> meshOptions, string? sourceFileName)
    {
        _board = board;
        _boardFileName = sourceFileName;
        _meshOptions = meshOptions;
        Nets.Clear();
        foreach (var net in board.Nets.OrderBy(n => n.Name ?? $"Net {n.Id}", StringComparer.OrdinalIgnoreCase))
            Nets.Add(new WindingNetRow(net));
        HasBoard = true;
        CutResult = "";
    }

    public void Clear()
    {
        _board = null;
        _boardFileName = null;
        _meshOptions = null;
        Nets.Clear();
        HasBoard = false;
        CutResult = "";
    }

    private List<CopperNet> SelectedNets() => Nets.Where(n => n.IsSelected).Select(n => n.Net).ToList();

    /// <summary>Puts the axis at the middle of the selected windings' copper and the cut radius just
    /// past its farthest corner.</summary>
    [RelayCommand]
    private void CenterOnWindings()
    {
        var nets = SelectedNets();
        if (nets.Count == 0) { CutResult = "Tick the winding nets first."; return; }
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var island in nets.SelectMany(n => n.Islands))
        {
            var (x0, y0, x1, y1) = island.Bounds();
            minX = Math.Min(minX, x0); minY = Math.Min(minY, y0); maxX = Math.Max(maxX, x1); maxY = Math.Max(maxY, y1);
        }
        CenterXMm = 0.5 * (minX + maxX) * 1e3;
        CenterYMm = 0.5 * (minY + maxY) * 1e3;
        MaxRadiusMm = Math.Ceiling(0.5 * Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY)) * 1e3 * 1.05);
        CutResult = $"Axis at ({CenterXMm:g5}, {CenterYMm:g5}) mm, the middle of the selected copper; cut out to {MaxRadiusMm:g4} mm. "
            + "A spiral's axis is its own centre — check it against the layout.";
    }

    /// <summary>Cuts the selected windings and fills the turns table with what the cut crossed.</summary>
    [RelayCommand]
    private void CutFromBoard()
    {
        if (_board is null) { CutResult = "Import a board first (PCB panel)."; return; }
        var nets = SelectedNets();
        if (nets.Count == 0) { CutResult = "Tick the winding nets first."; return; }
        if (!(MaxRadiusMm > 0)) { CutResult = "The cut radius must be positive."; return; }
        try
        {
            var stackup = _meshOptions?.Invoke().Stackup ?? BoardStackup.FromBoard(_board);
            var center = new Point2(CenterXMm * 1e-3, CenterYMm * 1e-3);
            IReadOnlyList<WindingTurn> turns;
            if (CutAngles > 1)
            {
                var averaged = PlanarWindingSection.CutAveraged(_board, nets, center, MaxRadiusMm * 1e-3, stackup, CutAngles);
                turns = averaged.Turns;
                _cutNotes = averaged.Notes;
                _axis = $"({CenterXMm:g5}, {CenterYMm:g5}) mm, cut averaged over {CutAngles} angles";
            }
            else
            {
                turns = PlanarWindingSection.Cut(_board, nets, center, CutAngleDegrees * Math.PI / 180, MaxRadiusMm * 1e-3, stackup);
                _cutNotes = Array.Empty<string>();
                _axis = $"({CenterXMm:g5}, {CenterYMm:g5}) mm, cut at {CutAngleDegrees:g4}°";
            }
            Turns.Clear();
            foreach (var t in turns) Turns.Add(TurnRow.From(t));
            RefreshWindings();
            var counts = turns.GroupBy(t => t.Net).Select(g => $"{g.Key}: {g.Count()} turn(s)");
            CutResult = turns.Count == 0
                ? "The cut crossed no copper of the selected nets: check the axis and the radius."
                : $"{turns.Count} turn(s) — {string.Join(", ", counts)}." + (_cutNotes.Count > 0 ? " " + string.Join(" ", _cutNotes) : "");
            _log.Append("Magnetics: " + CutResult);
        }
        catch (Exception ex)
        {
            CutResult = "Not cut: " + ex.Message;
        }
    }

    [RelayCommand]
    private void AddTurn()
    {
        // A new turn continues the last one outward: the same width and layer, 0.2 mm of space between.
        var last = Turns.LastOrDefault();
        var row = last is null
            ? new TurnRow { Winding = "W", Layer = 1, R0Mm = 5, R1Mm = 6, Z0Um = -35, Z1Um = 0 }
            : new TurnRow
            {
                Winding = last.Winding, Layer = last.Layer, R0Mm = last.R1Mm + 0.2,
                R1Mm = last.R1Mm + 0.2 + (last.R1Mm - last.R0Mm), Z0Um = last.Z0Um, Z1Um = last.Z1Um
            };
        Turns.Add(row);
        _axis = null;
        RefreshWindings();
    }

    [RelayCommand]
    private void RemoveTurn(TurnRow? row)
    {
        if (row is not null) Turns.Remove(row);
        RefreshWindings();
    }

    [RelayCommand]
    private void ClearTurns()
    {
        Turns.Clear();
        _axis = null;
        _cutNotes = Array.Empty<string>();
        RefreshWindings();
    }

    /// <summary>The winding names in the table, the drive kept when it still exists, and a lead-out
    /// row per winding.</summary>
    private void RefreshWindings()
    {
        var names = Turns.Select(t => t.Winding.Trim()).Where(n => n.Length > 0).Distinct().ToList();
        string? drive = DriveWinding;
        Windings.Clear();
        foreach (var n in names) Windings.Add(n);
        DriveWinding = drive is not null && names.Contains(drive) ? drive : names.FirstOrDefault();
        var keep = LeadOuts.ToDictionary(l => l.Winding);
        LeadOuts.Clear();
        foreach (var n in names) LeadOuts.Add(keep.TryGetValue(n, out var row) ? row : new LeadOutRow(n));
    }

    private AxisymmetricCore? Core()
    {
        var material = new MagneticMaterial("core", CoreMu);
        return CoreKind switch
        {
            PotCore => new AxisymmetricCore(PostRadiusMm * 1e-3, WindowRadiusMm * 1e-3, OuterRadiusMm * 1e-3,
                PlateThicknessMm * 1e-3, WindowHeightMm * 1e-3, GapMm * 1e-3, material),
            ErCoreKind => new ErCore(PostRadiusMm * 1e-3, WindowWidthMm * 1e-3, LegWidthMm * 1e-3, DepthMm * 1e-3,
                PlateThicknessMm * 1e-3, WindowHeightMm * 1e-3, GapMm * 1e-3, material).ToAxisymmetric(),
            _ => null
        };
    }

    private bool TryTurns(out List<WindingTurn> turns, out string? failure)
    {
        turns = new List<WindingTurn>();
        failure = null;
        RefreshWindings();
        foreach (var row in Turns)
        {
            var t = row.ToTurn();
            if (t.Net.Length == 0) { failure = "every turn needs its winding's name"; return false; }
            if (!(t.R0 >= 0 && t.R1 > t.R0)) { failure = $"turn {t.Name}: r1 must exceed r0 (both from the axis)"; return false; }
            if (!(t.Z1 > t.Z0)) { failure = $"turn {t.Name}: z1 must lie above z0"; return false; }
            turns.Add(t);
        }
        if (turns.Count == 0) { failure = "no turns: cut them from the board or add them to the table"; return false; }
        return true;
    }

    [RelayCommand]
    private async Task Solve()
    {
        _solved = null;
        HasResult = false;
        FieldImage = null;
        ResultLines.Clear();
        MatrixResult = "";
        if (!TryTurns(out var turns, out string? failure)) { Result = $"Not solvable: {failure}."; return; }
        AxisymmetricCore? core;
        try { core = Core(); }
        catch (Exception ex) { Result = "Not solvable: " + ex.Message; return; }
        var input = new PlanarMagneticsInput
        {
            Turns = turns, Drive = DriveWinding, Windings = Windings.ToList(), FrequencyHz = FrequencyKhz * 1e3, Core = core,
            StrandDiameter = StrandDiameterMm * 1e-3, StrandsPerTurn = Math.Max(1, StrandsPerTurn), CopperConductivity = CopperConductivity
        };
        Result = "Solving…";
        try
        {
            var (solved, field, view) = await Task.Run(() =>
            {
                var r = PlanarMagneticsStudy.Run(input);
                var (map, rect) = SampleField(r);
                return (r, map, rect);
            });
            _solved = solved;
            HasResult = true;
            foreach (var (k, v) in solved.Lines()) ResultLines.Add($"{k}: {v}");
            Result = $"{(input.FrequencyHz > 0 ? $"Time-harmonic at {input.FrequencyHz:g4} Hz" : "Magnetostatic")}, 1 A in '{solved.Drive}' "
                + $"({(solved.SolidTurns ? "solid" : "stranded")} turns, {solved.Solution.ElementCount:N0} elements); "
                + $"largest |B| {solved.Solution.MaxFluxDensity() * 1e3:g4} mT.";
            Assumptions = "Assumptions: " + string.Join(" ", PlanarWindingSection.Assumptions.Concat(MagneticSolver2D.Assumptions))
                + " " + string.Join(" ", solved.Built.Notes);
            var outlines = solved.Built.Model.Regions.Select(g => new MapOutline(g.X0, g.Y0, g.X1, g.Y1)).ToList();
            FieldImage = FieldMapPlot.Render(field, view.X0, view.X1, view.Y0, view.Y1, outlines, logarithmic: true, decades: 3,
                "r [mm]", "z [mm]", "T");
            _log.Append("Magnetics: " + Result);
            foreach (var line in ResultLines) _log.Append("Magnetics: " + line);
        }
        catch (Exception ex)
        {
            Result = "Not solved: " + ex.Message;
        }
    }

    /// <summary>The peak |B| on a raster over the windings and the core (r from the axis to a little
    /// past the outermost copper or the core's wall), at equal scale in r and z.</summary>
    private static (double[,] Map, (double X0, double X1, double Y0, double Y1) View) SampleField(PlanarMagneticsResult r)
    {
        var turns = r.Input.Turns;
        var core = r.Input.Core;
        double rMax = turns.Max(t => t.R1), zMin = turns.Min(t => t.Z0), zMax = turns.Max(t => t.Z1);
        double zMid = 0.5 * (zMin + zMax);
        double outer = core?.OuterRadius ?? rMax;
        double half = core is null ? 0.5 * rMax + (zMax - zMin) : core.WindowHeight / 2 + core.PlateThickness + 0.25 * outer;
        var view = (X0: 0.0, X1: 1.2 * outer, Y0: zMid - half, Y1: zMid + half);
        int columns = 200;
        int rows = Math.Clamp((int)Math.Round(columns * (view.Y1 - view.Y0) / (view.X1 - view.X0)), 40, 400);
        var points = new List<(double, double)>(rows * columns);
        for (int i = 0; i < rows; i++)
            for (int j = 0; j < columns; j++)
                points.Add((view.X0 + (j + 0.5) * (view.X1 - view.X0) / columns, view.Y0 + (i + 0.5) * (view.Y1 - view.Y0) / rows));
        var b = r.Solution.SampleFluxDensity(points);
        var map = new double[rows, columns];
        for (int i = 0; i < rows; i++)
            for (int j = 0; j < columns; j++)
            {
                var (bx, by) = b[i * columns + j];
                map[i, j] = MagneticSolution2D.PeakMagnitude(bx, by);
            }
        return (map, view);
    }

    /// <summary>The winding impedance matrix: one solid-turn time-harmonic solve per winding.</summary>
    [RelayCommand]
    private async Task SolveMatrix()
    {
        MatrixResult = "";
        if (!(FrequencyKhz > 0)) { MatrixResult = "The impedance matrix is an AC quantity: set a frequency above 0."; return; }
        if (!TryTurns(out var turns, out string? failure)) { MatrixResult = $"Not solvable: {failure}."; return; }
        AxisymmetricCore? core;
        try { core = Core(); }
        catch (Exception ex) { MatrixResult = "Not solvable: " + ex.Message; return; }
        double f = FrequencyKhz * 1e3, sigma = CopperConductivity;
        var leadOuts = new List<LeadOut>();
        foreach (var row in LeadOuts.Where(l => l.LengthMm > 0 && l.WidthMm > 0))
        {
            var first = turns.FirstOrDefault(t => t.Net == row.Winding);
            if (first is null) continue;
            leadOuts.Add(new LeadOut(row.Winding, row.LengthMm * 1e-3, row.WidthMm * 1e-3, first.Z1 - first.Z0, sigma));
        }
        MatrixResult = $"Solving {turns.Select(t => t.Net).Distinct().Count()} winding(s), one time-harmonic solve each…";
        try
        {
            var matrix = await Task.Run(() => PlanarWindingSection.ImpedanceMatrix(turns, core, f, sigma, leadOuts: leadOuts));
            var lines = new List<string> { $"Winding impedance matrix at {f:g4} Hz (1 A in each winding in turn, the others open):" };
            foreach (var a in matrix.Nets)
                foreach (var b in matrix.Nets)
                {
                    var z = matrix.Z(a, b);
                    string line = $"Z({a},{b}) = {z.Real * 1e3:g4} {(z.Imaginary < 0 ? "−" : "+")} j{Math.Abs(z.Imaginary) * 1e3:g4} mΩ — "
                        + $"L {matrix.InductanceHenries(a, b) * 1e9:g5} nH";
                    if (a != b) line += $", k {matrix.CouplingCoefficient(a, b):g4}";
                    else line += $", R {z.Real * 1e3:g4} mΩ";
                    lines.Add(line);
                }
            lines.AddRange(matrix.Notes);
            MatrixResult = string.Join(Environment.NewLine, lines);
            foreach (var line in lines) _log.Append("Magnetics: " + line);
        }
        catch (Exception ex)
        {
            MatrixResult = "Not solved: " + ex.Message;
        }
    }

    [RelayCommand]
    private void SaveReport()
    {
        if (_solved is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = Path.GetFileNameWithoutExtension(_boardFileName ?? "winding") + "_magnetics.html",
            Filter = "HTML|*.html|Markdown|*.md|All files|*.*",
            Title = "Save the magnetics report"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var report = _solved.Report($"{Path.GetFileNameWithoutExtension(_boardFileName ?? "Winding")} — planar magnetics",
                _axis, _cutNotes);
            bool markdown = Path.GetExtension(dialog.FileName).Equals(".md", StringComparison.OrdinalIgnoreCase);
            File.WriteAllText(dialog.FileName, markdown ? report.ToMarkdown() : report.ToHtml());
            _log.Append($"Magnetics: saved {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception ex)
        {
            Result = "Not saved: " + ex.Message;
        }
    }
}
