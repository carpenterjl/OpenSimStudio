using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Controls.Spreadsheet;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.LensEditing;
using S = OpenSim.App.ViewModels.OpticsLensEditorSchema;

namespace OpenSim.App.ViewModels;

/// <summary>
/// The Lens Data Editor (plan item OP-31): the sequential surfaces on OP-10's spreadsheet
/// (<see cref="SheetDocument"/>, schema <see cref="OpticsLensEditorSchema"/>), saved as
/// <see cref="OpticsSurfaceSettings"/> with the project and the lens file, and the editing tools
/// of <c>OpenSim.Optics.Sequential.LensEditing</c>. Every edit — typed, pasted, a tool's — lands
/// on the sheet's undo stack, and each tool is one step. After every change the editor
/// publishes the system (the analyses recompute), checks the lens, evaluates its solves and
/// pickups and shows the solved values (TC-6; a failing solve's message goes on its cell), and
/// shows the automatic semi-diameters it sized.
/// <para>
/// Row 0 is the object and the last row the image; both are locked (they cannot be deleted or
/// moved, and no row can be inserted before the object or after the image, as the OpticStudio
/// help says). A surface's data the sheet has no cell for (the parameters of an unknown type
/// past the last column, its extra data, flags on columns its row type lacks) is kept with
/// the row and written back unchanged.
/// </para>
/// </summary>
public sealed partial class OpticsLensEditorViewModel : ObservableObject
{
    private readonly Action _publish;
    private readonly Func<LensEditContext> _context;
    private readonly Dictionary<long, OpticsSurfaceSettings> _base = new();
    private bool _applying;

    /// <param name="publish">Called after every change, to publish the system.</param>
    /// <param name="context">The system data, active catalogs and coating names the tools and
    /// the check read.</param>
    public OpticsLensEditorViewModel(Action publish, Func<LensEditContext> context)
    {
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        Document = NewDocument(Array.Empty<string>());
        Load(DefaultSurfaces(), new List<string>(), publish: false);
    }

    /// <summary>The sheet the grid shows. It is replaced when a file brings a surface type the
    /// current schema has no row type for.</summary>
    [ObservableProperty] private SheetDocument _document = null!;

    /// <summary>The row the grid's current cell is on (the view sets it); tools that act on
    /// "the current surface" read it.</summary>
    [ObservableProperty] private int _currentRow = 1;

    /// <summary>What the last command did, or why it did nothing.</summary>
    [ObservableProperty] private string _status = "";

    /// <summary>What is wrong with the lens (one line each), empty when it can be traced.</summary>
    [ObservableProperty] private IReadOnlyList<string> _problems = Array.Empty<string>();

    /// <summary>The last System Check report.</summary>
    [ObservableProperty] private string _systemCheckReport = "";

    /// <summary>The last File Comparator listing.</summary>
    [ObservableProperty] private string _comparisonReport = "";

    // Tool inputs.
    [ObservableProperty] private int _firstSurface = 1;
    [ObservableProperty] private int _lastSurface = 2;
    [ObservableProperty] private double _scaleFactor = 2;
    [ObservableProperty] private double _targetFocalLength = 100;
    [ObservableProperty] private int _reflectAtSurface = 1;
    [ObservableProperty] private string _goToText = "";
    [ObservableProperty] private bool _differencesOnly = true;

    public bool HasProblems => Problems.Count > 0;

    /// <summary>The lens check for the panel: "The lens can be traced." or the problems.</summary>
    public string ProblemsText => Problems.Count == 0 ? "The lens can be traced." : string.Join(Environment.NewLine, Problems);

    partial void OnProblemsChanged(IReadOnlyList<string> value)
    {
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(ProblemsText));
    }

    /// <summary>The number of surfaces, object and image included.</summary>
    public int SurfaceCount => Document.RowCount;

    /// <summary>OpticStudio's starting lens: "initially (unless a lens has been loaded) three
    /// surfaces are shown; the object, stop, and image surfaces" (OpticStudio 2025 R2 help, Lens
    /// Data Editor). The object at infinity and the zero stop thickness are this program's choice.</summary>
    public static List<OpticsSurfaceSettings> DefaultSurfaces() => new()
    {
        new OpticsSurfaceSettings { ThicknessIsInfinite = true },
        new OpticsSurfaceSettings { IsStop = true },
        new OpticsSurfaceSettings(),
    };

    // ------------------------------------------------------------------ files

    /// <summary>The surfaces as the files store them.</summary>
    public List<OpticsSurfaceSettings> ToSettings() => Document.Rows.Select(ReadRow).ToList();

    /// <summary>Loads a file's surfaces; null (a file from before OP-31) leaves the editor as it
    /// is. A list without an object and an image surface is refused with a note. Clears the undo
    /// history.</summary>
    public void LoadSettings(List<OpticsSurfaceSettings>? saved, ICollection<string> notes)
    {
        if (saved is null) return;
        if (saved.Count < 2)
        {
            notes.Add($"The file's surface list has {saved.Count} surface{(saved.Count == 1 ? "" : "s")}, without both an object and an image surface; the editor kept its lens.");
            return;
        }
        Load(saved, notes, publish: false);
    }

    /// <summary>A new project's lens: object, stop and image.</summary>
    public void Reset() => Load(DefaultSurfaces(), new List<string>(), publish: false);

    private void Load(IReadOnlyList<OpticsSurfaceSettings> surfaces, ICollection<string> notes, bool publish)
    {
        var otherTypes = surfaces.Where(s => s is not null && !SavedSurfaces.IsStandard(s)).Select(s => s.Type!).ToArray();
        var known = Document.Schema.RowTypes.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (otherTypes.Any(t => !known.Contains(t)))
            Document = NewDocument(otherTypes.Concat(known).ToArray());
        _applying = true;
        try
        {
            var doc = Document;
            if (doc.RowCount > 0)
            {
                foreach (var row in doc.Rows) row.IsLocked = false;
                doc.DeleteRows(Enumerable.Range(0, doc.RowCount));
            }
            _base.Clear();
            var guard = doc.InsertGuard;
            doc.InsertGuard = null;
            doc.InsertRows(0, surfaces.Count);
            doc.InsertGuard = guard;
            for (int j = 0; j < surfaces.Count; j++)
                WriteRow(j, surfaces[j] ?? new OpticsSurfaceSettings(), notes);
            for (int j = 0; j < surfaces.Count; j++)
                WriteFlags(j, surfaces[j] ?? new OpticsSurfaceSettings(), notes);
            LockEnds();
            doc.History.Clear();
        }
        finally { _applying = false; }
        AfterChange(publish);
    }

    private SheetDocument NewDocument(IReadOnlyList<string> otherTypes)
    {
        if (Document is { } old) old.Changed -= OnSheetChanged;
        var doc = new SheetDocument(S.Build(otherTypes));
        doc.InsertGuard = index =>
            index == 0 ? "No surface can be inserted in front of the object surface."
            : index >= doc.RowCount && doc.RowCount > 0 ? "No surface can be inserted behind the image surface."
            : null;
        doc.Changed += OnSheetChanged;
        return doc;
    }

    private void LockEnds()
    {
        var doc = Document;
        for (int i = 0; i < doc.RowCount; i++) doc.Rows[i].IsLocked = i == 0 || i == doc.RowCount - 1;
    }

    private void OnSheetChanged(object? sender, EventArgs e)
    {
        if (_applying) return;
        LockEnds();
        AfterChange(publish: true);
    }

    /// <summary>Re-checks the lens and re-sizes the automatic semi-diameters after a change of
    /// the system data or the catalogs, without publishing (the caller publishes).</summary>
    public void Refresh() => AfterChange(publish: false);

    /// <summary>After any change: check the lens, evaluate its solves and pickups and show the
    /// solved values (a failing cell keeps its value and gets the failure as an error note),
    /// show the automatic semi-diameters, publish.</summary>
    private void AfterChange(bool publish)
    {
        var surfaces = ToSettings();
        var problems = new List<string>();
        ClearNotes();
        try
        {
            var context = _context();
            var solution = context.Solve(surfaces);
            ShowSolves(solution, surfaces, context);
            problems.AddRange(solution.Errors);
            if (solution.Succeeded) ShowAutomaticSemiDiameters(solution.Lens, context);
        }
        catch (ArgumentException ex) { problems.AddRange(ex.Message.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)); }
        // The sizing traces rays through the paraxial launch, which refuses some system data
        // (a real-image-height field, say) with its own message; the check shows it.
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArithmeticException) { problems.Add(ex.Message); }
        Problems = problems;
        OnPropertyChanged(nameof(SurfaceCount));
        if (publish) _publish();
    }

    private void ShowAutomaticSemiDiameters(SequentialLens lens, LensEditContext context)
    {
        var system = context.System();
        if (system.Validate().Count > 0) return;
        var report = SemiDiameterSizing.Compute(lens, system);
        double perUnit = OpticsConventions.MetersPer(context.Unit);
        var doc = Document;
        for (int j = 0; j < doc.RowCount && j < report.Clear.Count; j++)
        {
            var row = doc.Rows[j];
            Show(row, S.SemiDiameterSlot, report.Clear[j] / perUnit);
            Show(row, S.MechanicalSlot, report.Mechanical[j] / perUnit);
        }

        void Show(SheetRow row, int slot, double value)
        {
            var cell = row.Cells[slot];
            if (cell.Definition is null || !IsAutomatic(cell) || !double.IsFinite(value)) return;
            if (cell.Value.IsNumber && cell.Value.Number == value) return;
            doc.SetComputed(row.Index, slot + 1, CellValue.Of(value));
        }
    }

    private static bool IsAutomatic(SheetCell cell) =>
        cell.Flag.Kind == CellFlagKind.Solve && cell.Flag.SolveCode == S.AutomaticSolve;

    private void ClearNotes()
    {
        var doc = Document;
        foreach (var row in doc.Rows)
            foreach (var cell in row.Cells)
                if (cell.Note is not null) doc.SetNote(row.Index, cell.Column, null);
    }

    /// <summary>
    /// The solved value in each cell a solve or pickup drives (TC-6), written as a computed value
    /// (no undo step), in surface order so a material pickup copies its source's text as shown;
    /// a solve that failed leaves its cell's value and puts the engine's message, which names
    /// the surface and the cell, on it as an error note.
    /// </summary>
    private void ShowSolves(ProjectLensSolution solution, IReadOnlyList<OpticsSurfaceSettings> surfaces, LensEditContext context)
    {
        var doc = Document;
        double perUnit = OpticsConventions.MetersPer(context.Unit);
        foreach (var cell in solution.Solves.OrderBy(c => c.Surface))
        {
            if (cell.Surface >= doc.RowCount) continue;
            var row = doc.Rows[cell.Surface];
            int slot = SlotOf(row, cell.Column);
            if (slot < 0) continue;
            if (solution.ErrorOf(cell.Surface, cell.Column) is { } error)
            {
                doc.SetNote(row.Index, slot + 1, error, isError: true);
                continue;
            }
            if (solution.Result is null) continue;
            var sheetCell = row.Cells[slot];
            CellValue value;
            if (cell.Solve is OpenSim.Optics.Sequential.Solves.PickupSolve { Target: OpenSim.Optics.Sequential.Solves.SolveColumn.Material } p)
            {
                if (p.FromSurface >= doc.RowCount) continue;
                value = doc.Rows[p.FromSurface].Cells[S.MaterialSlot].Value;
            }
            else if (SavedSolves.SavedValue(solution.Lens, surfaces[cell.Surface], cell, perUnit) is double v)
                value = CellValue.Of(v);
            else continue;
            if (sheetCell.Value == value || CellText.Validate(sheetCell.Definition!, value) is not null) continue;
            doc.SetComputed(row.Index, slot + 1, value);
        }
    }

    private static int SlotOf(SheetRow row, string? key)
    {
        for (int i = 0; i < row.Cells.Count; i++)
            if (row.Cells[i].Definition is { } d && d.Key == key) return i;
        return -1;
    }

    // ------------------------------------------------------------------ sheet to saved

    private OpticsSurfaceSettings ReadRow(SheetRow row)
    {
        var b = _base.TryGetValue(row.Id, out var known) ? known : new OpticsSurfaceSettings();
        bool sameType = string.Equals(b.Type ?? SavedSurfaces.StandardType, row.Type.Name, StringComparison.OrdinalIgnoreCase);
        bool standard = row.Type.Name == SavedSurfaces.StandardType;
        CellValue Value(int slot) => row.Cells[slot].Value;
        double Number(int slot) => row.Cells[slot].Definition is null ? 0 : Value(slot).Number;
        string Text(int slot) => row.Cells[slot].Definition is null ? "" : Value(slot).Text;
        string Prop(string key) => row.Properties.TryGetValue(key, out var v) ? v.Text : "";
        bool YesProp(string key) => Prop(key) == S.Yes;

        double radius = Number(S.RadiusSlot), thickness = Number(S.ThicknessSlot);
        var s = b with
        {
            Type = sameType ? b.Type : standard ? null : row.Type.Name,
            Comment = Text(S.CommentSlot) is { Length: > 0 } c ? c : null,
            Radius = double.IsInfinity(radius) ? null : radius,
            Thickness = double.IsInfinity(thickness) ? (b.ThicknessIsInfinite ? b.Thickness : 0) : thickness,
            ThicknessIsInfinite = double.IsInfinity(thickness),
            Coating = Text(S.CoatingSlot) is { Length: > 0 } coating ? coating : null,
            SemiDiameter = IsAutomatic(row.Cells[S.SemiDiameterSlot]) ? null : Number(S.SemiDiameterSlot),
            ChipZone = Number(S.ChipZoneSlot),
            MechanicalSemiDiameter = IsAutomatic(row.Cells[S.MechanicalSlot]) ? null : Number(S.MechanicalSlot),
            Conic = Number(S.ConicSlot),
            Tce = Number(S.TceSlot),
            IsStop = YesProp(S.StopProperty),
            IsIgnored = YesProp(S.IgnoreProperty),
            SkipRaysTo = YesProp(S.SkipRaysProperty),
            HideRaysTo = YesProp(S.HideRaysProperty),
            DoNotDraw = YesProp(S.DoNotDrawProperty),
            RowColor = row.Tint,
            Aperture = ReadAperture(row, b.Aperture),
            GridSag = sameType ? b.GridSag : null, // OP-25: a Grid Sag's grid goes with its type
        };

        // The material: kept exactly as the file had it while its text is unchanged.
        string material = Text(S.MaterialSlot);
        if (material != SavedSurfaces.FormatMaterial(b))
            s = SavedSurfaces.ParseMaterial(s, material, out _) ?? s with { Material = material, Index = null, ModelGlass = null };

        // Parameters: the cells this type shows (by their "Par n" keys; a registered type's
        // numbers from 13 on are extra data), and the base's beyond them (same type only).
        bool registered = SurfaceTypes.Find(row.Type.Name) is not null;
        var parameters = new SortedDictionary<int, double>();
        var extra = new SortedDictionary<int, double>();
        if (sameType && b.Parameters is { } bp)
            foreach (var p in bp) parameters[p.Number] = p.Value;
        if (sameType && b.ExtraData is { } be)
            foreach (var p in be) extra[p.Number] = p.Value;
        foreach (var cell in row.Cells)
        {
            if (cell.Definition is not { } d || SavedSurfaces.ParameterNumber(d.Key) is not int n || !cell.Value.IsNumber) continue;
            var target = registered && n >= SurfaceTypes.FirstExtraData ? extra : parameters;
            double v = cell.Value.Number;
            if (v != 0 || target.ContainsKey(n)) target[n] = v;
        }
        static List<OpticsSurfaceParameterSettings>? List(SortedDictionary<int, double> d) =>
            d.Count > 0 ? d.Select(p => new OpticsSurfaceParameterSettings { Number = p.Key, Value = p.Value }).ToList() : null;
        s = s with { Parameters = List(parameters), ExtraData = List(extra) };

        // Tilts and decenters, and a coordinate break's return: kept exactly as the file had them
        // while the flyout shows the same values.
        var before = ReadTilt(row, after: false);
        var after = ReadTilt(row, after: true);
        var coordinateReturn = ReadReturn(row);
        s = s with
        {
            TiltBefore = sameType && Equals(Normalised(b.TiltBefore, after: false), before) ? b.TiltBefore : before,
            TiltAfter = sameType && Equals(Normalised(b.TiltAfter, after: true), after) ? b.TiltAfter : after,
            CoordinateReturn = sameType && Equals(NormalisedReturn(b.CoordinateReturn), coordinateReturn) ? b.CoordinateReturn : coordinateReturn,
        };

        // Flags: the sheet's, and the base's on columns this row type has no cell for.
        var flags = new List<OpticsCellFlagSettings>();
        foreach (var cell in row.Cells)
        {
            if (cell.Definition is not { } d || cell.Flag.Kind == CellFlagKind.Fixed || IsAutomatic(cell)) continue;
            var f = cell.Flag;
            switch (f.Kind)
            {
                case CellFlagKind.Variable:
                    flags.Add(new OpticsCellFlagSettings { Column = d.Key, Kind = SavedSurfaces.VariableFlag });
                    break;
                case CellFlagKind.Solve:
                    flags.Add(new OpticsCellFlagSettings
                    {
                        Column = d.Key, Kind = SavedSurfaces.SolveFlag, Solve = f.SolveCode,
                        SolveParameters = f.SolveParameters.Count > 0 ? f.SolveParameters.ToList() : null
                    });
                    break;
                case CellFlagKind.Pickup when Document.FindRow(f.Pickup!.RowId) is { } source:
                    flags.Add(new OpticsCellFlagSettings
                    {
                        Column = d.Key, Kind = SavedSurfaces.PickupFlag, PickupSurface = source.Index,
                        PickupColumn = source.Cells[f.Pickup.Column - 1].Definition?.Key,
                        PickupScale = f.Pickup.Scale, PickupOffset = f.Pickup.Offset
                    });
                    break;
            }
        }
        if (sameType && b.Flags is { } bf)
            flags.AddRange(bf.Where(f => !row.Type.Columns.Any(c => c is not null && c.Key == f.Column)));
        return s with { Flags = flags.Count > 0 ? flags : null };
    }

    // ---- Tilt/decenter and coordinate return (TC-6): flyout text <-> saved names.

    private static readonly (string Display, TiltAfterKind Kind)[] AfterModes =
    {
        (S.AfterExplicit, TiltAfterKind.Explicit), (S.AfterPickUpThis, TiltAfterKind.PickUpThisSurface),
        (S.AfterReverseThis, TiltAfterKind.ReverseThisSurface), (S.AfterPickUp, TiltAfterKind.PickUpSurface),
        (S.AfterReverse, TiltAfterKind.ReverseSurface),
    };

    private static readonly (string Display, CoordinateReturnMode Mode)[] ReturnModes =
    {
        (S.ReturnNone, CoordinateReturnMode.None), (S.ReturnOrientation, CoordinateReturnMode.OrientationOnly),
        (S.ReturnXY, CoordinateReturnMode.OrientationXY), (S.ReturnXYZ, CoordinateReturnMode.OrientationXYZ),
    };

    private static string OrderDisplay(string? saved) =>
        saved == SavedSurfaces.OrderName(TiltDecenterOrder.TiltThenDecenter) ? S.TiltThenDecenter : S.DecenterThenTilt;

    private static string ModeDisplay(string? saved) =>
        AfterModes.FirstOrDefault(m => SavedSurfaces.AfterModeName(m.Kind) == saved).Display ?? S.AfterExplicit;

    private static string ReturnDisplay(string? saved) =>
        ReturnModes.FirstOrDefault(m => SavedSurfaces.ReturnModeName(m.Mode) == saved).Display ?? S.ReturnNone;

    /// <summary>The saved tilt the flyout's values give: null for a "before" (or an explicit
    /// "after") of zeros; the pick-up and reverse modes keep only their mode and surface.</summary>
    private static OpticsTiltDecenterSettings? TiltFrom(double dx, double dy, double tx, double ty, double tz, string order,
        string mode, int surface, bool after)
    {
        var kind = after ? AfterModes.FirstOrDefault(m => m.Display == mode).Kind : TiltAfterKind.Explicit;
        if (kind == TiltAfterKind.Explicit)
        {
            if (dx == 0 && dy == 0 && tx == 0 && ty == 0 && tz == 0) return null;
            return new OpticsTiltDecenterSettings
            {
                DecenterX = dx, DecenterY = dy, TiltX = tx, TiltY = ty, TiltZ = tz,
                Order = order == S.TiltThenDecenter ? SavedSurfaces.OrderName(TiltDecenterOrder.TiltThenDecenter) : null,
            };
        }
        return new OpticsTiltDecenterSettings
        {
            Mode = SavedSurfaces.AfterModeName(kind),
            Surface = kind is TiltAfterKind.PickUpSurface or TiltAfterKind.ReverseSurface ? surface : 0,
        };
    }

    private static OpticsTiltDecenterSettings? Normalised(OpticsTiltDecenterSettings? t, bool after) => t is null ? null
        : TiltFrom(t.DecenterX, t.DecenterY, t.TiltX, t.TiltY, t.TiltZ, OrderDisplay(t.Order), ModeDisplay(t.Mode), t.Surface, after);

    private static OpticsTiltDecenterSettings? ReadTilt(SheetRow row, bool after)
    {
        if (row.Type.FindProperty(S.BeforeDecenterXProperty) is null) return null;
        double N(string key) => row.Properties.TryGetValue(key, out var v) && v.IsNumber ? v.Number : 0;
        string T(string key) => row.Properties.TryGetValue(key, out var v) ? v.Text : "";
        return after
            ? TiltFrom(N(S.AfterDecenterXProperty), N(S.AfterDecenterYProperty), N(S.AfterTiltXProperty), N(S.AfterTiltYProperty), N(S.AfterTiltZProperty),
                T(S.AfterOrderProperty), T(S.AfterModeProperty), (int)N(S.AfterSurfaceProperty), after: true)
            : TiltFrom(N(S.BeforeDecenterXProperty), N(S.BeforeDecenterYProperty), N(S.BeforeTiltXProperty), N(S.BeforeTiltYProperty), N(S.BeforeTiltZProperty),
                T(S.BeforeOrderProperty), S.AfterExplicit, 0, after: false);
    }

    private static OpticsCoordinateReturnSettings? ReturnFrom(string mode, int surface)
    {
        var m = ReturnModes.FirstOrDefault(r => r.Display == mode).Mode;
        return m == CoordinateReturnMode.None ? null : new OpticsCoordinateReturnSettings { Mode = SavedSurfaces.ReturnModeName(m), Surface = surface };
    }

    private static OpticsCoordinateReturnSettings? NormalisedReturn(OpticsCoordinateReturnSettings? r) =>
        r is null ? null : ReturnFrom(ReturnDisplay(r.Mode), r.Surface);

    private static OpticsCoordinateReturnSettings? ReadReturn(SheetRow row)
    {
        if (row.Type.FindProperty(S.ReturnModeProperty) is null) return null;
        string mode = row.Properties.TryGetValue(S.ReturnModeProperty, out var v) ? v.Text : S.ReturnNone;
        int surface = row.Properties.TryGetValue(S.ReturnSurfaceProperty, out var n) && n.IsNumber ? (int)n.Number : 0;
        return ReturnFrom(mode, surface);
    }

    private static OpticsSurfaceApertureSettings? ReadAperture(SheetRow row, OpticsSurfaceApertureSettings? old)
    {
        string type = row.Properties.TryGetValue(S.ApertureProperty, out var t) ? t.Text : S.NoAperture;
        if (type == S.NoAperture) return null;
        double N(string key) => row.Properties.TryGetValue(key, out var v) && v.IsNumber ? v.Number : 0;
        string vertices = row.Properties.TryGetValue(S.VerticesProperty, out var vt) ? vt.Text : "";
        return new OpticsSurfaceApertureSettings
        {
            Type = type,
            IsObscuration = row.Properties.TryGetValue(S.ObscurationProperty, out var o) && o.Text == S.Yes,
            DecenterX = N(S.ApertureDecenterXProperty),
            DecenterY = N(S.ApertureDecenterYProperty),
            MinRadius = N(S.MinRadiusProperty),
            MaxRadius = N(S.MaxRadiusProperty),
            HalfWidthX = N(S.HalfWidthXProperty),
            HalfWidthY = N(S.HalfWidthYProperty),
            ArmWidth = N(S.ArmWidthProperty),
            ArmCount = (int)N(S.ArmCountProperty),
            Vertices = vertices == FormatVertices(old?.Vertices) ? old?.Vertices : ParseVertices(vertices),
        };
    }

    /// <summary>"x y; x y" in lens units, shortest round-trip numbers.</summary>
    public static string FormatVertices(IReadOnlyList<OpticsPointSettings>? vertices) =>
        vertices is null ? "" : string.Join("; ", vertices.Select(v => string.Create(CultureInfo.InvariantCulture, $"{v.X:R} {v.Y:R}")));

    /// <summary>Reads "x y; x y"; a pair that is not two numbers is left out.</summary>
    public static List<OpticsPointSettings>? ParseVertices(string text)
    {
        var points = new List<OpticsPointSettings>();
        foreach (var pair in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var xy = pair.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (xy.Length == 2 && double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                && double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) && double.IsFinite(x) && double.IsFinite(y))
                points.Add(new OpticsPointSettings { X = x, Y = y });
        }
        return points.Count > 0 ? points : null;
    }

    // ------------------------------------------------------------------ saved to sheet

    /// <summary>Writes a surface's type, values and properties into row <paramref name="index"/>
    /// (flags come after every row is written, in <see cref="WriteFlags"/>, so a pickup's source
    /// exists). Each edit is an undo step inside the caller's group.</summary>
    private void WriteRow(int index, OpticsSurfaceSettings s, ICollection<string> notes)
    {
        var doc = Document;
        var row = doc.Rows[index];
        string where = SavedSurfaces.Where(index, doc.RowCount);
        var type = doc.Schema.FindRowType(s.Type ?? SavedSurfaces.StandardType) ?? doc.Schema.DefaultRowType;
        Check(doc.SetRowType(index, type));
        _base[row.Id] = s;

        void Set(int slot, CellValue value)
        {
            var cell = row.Cells[slot];
            if (cell.Definition is null) return;
            if (cell.Value != value)
            {
                var result = doc.SetValue(index, slot + 1, value);
                if (!result.Succeeded) { notes.Add($"{where}: {result.Message}"); return; }
            }
            if (cell.Flag.Kind != CellFlagKind.Fixed) Check(doc.SetFlag(index, slot + 1, CellFlag.Fixed));
        }

        Set(S.CommentSlot, CellValue.Of(s.Comment ?? ""));
        Set(S.RadiusSlot, CellValue.Of(SavedSurfaces.IsPlane(s) && s.Radius is not 0.0 ? double.PositiveInfinity : s.Radius ?? double.PositiveInfinity));
        Set(S.ThicknessSlot, CellValue.Of(s.ThicknessIsInfinite ? double.PositiveInfinity : s.Thickness));
        Set(S.MaterialSlot, CellValue.Of(SavedSurfaces.FormatMaterial(s)));
        Set(S.CoatingSlot, CellValue.Of(s.Coating ?? ""));
        Set(S.SemiDiameterSlot, CellValue.Of(s.SemiDiameter ?? 0));
        Set(S.ChipZoneSlot, CellValue.Of(s.ChipZone));
        Set(S.MechanicalSlot, CellValue.Of(s.MechanicalSemiDiameter ?? 0));
        Set(S.ConicSlot, CellValue.Of(s.Conic));
        Set(S.TceSlot, CellValue.Of(s.Tce));
        for (int slot = S.FirstParameterSlot; slot < row.Cells.Count; slot++)
            if (row.Cells[slot].Definition is { } d && SavedSurfaces.ParameterNumber(d.Key) is int n)
                Set(slot, CellValue.Of(SavedSurfaces.Value(s, n)));
        // Set made every written cell Fixed; the semi-diameters' automatic flag is a value, not a
        // stored flag, so it is restored here (the other flags come in WriteFlags).
        if (s.SemiDiameter is null) Check(doc.SetFlag(index, S.SemiDiameterSlot + 1, S.Automatic));
        if (s.MechanicalSemiDiameter is null) Check(doc.SetFlag(index, S.MechanicalSlot + 1, S.Automatic));

        void Prop(string key, string text)
        {
            if (row.Type.FindProperty(key) is null) return;   // a coordinate break has no stop, aperture or tilt
            var result = doc.SetProperty(index, key, text);
            if (!result.Succeeded) notes.Add($"{where}: {result.Message}");
        }
        string YesNo(bool b) => b ? S.Yes : S.No;
        string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        Prop(S.StopProperty, YesNo(s.IsStop));
        Prop(S.IgnoreProperty, YesNo(s.IsIgnored));
        Prop(S.SkipRaysProperty, YesNo(s.SkipRaysTo));
        Prop(S.HideRaysProperty, YesNo(s.HideRaysTo));
        Prop(S.DoNotDrawProperty, YesNo(s.DoNotDraw));
        var a = s.Aperture;
        string apertureType = a is null ? S.NoAperture : a.Type ?? "";
        if (!S.ApertureChoices.Contains(apertureType))
        {
            notes.Add($"{where}: the aperture type '{a!.Type}' is not one this version knows; the surface has no aperture.");
            apertureType = S.NoAperture;
        }
        Prop(S.ApertureProperty, apertureType);
        var ap = a ?? new OpticsSurfaceApertureSettings();
        Prop(S.ObscurationProperty, YesNo(ap.IsObscuration));
        Prop(S.MinRadiusProperty, Num(ap.MinRadius));
        Prop(S.MaxRadiusProperty, Num(ap.MaxRadius));
        Prop(S.HalfWidthXProperty, Num(ap.HalfWidthX));
        Prop(S.HalfWidthYProperty, Num(ap.HalfWidthY));
        Prop(S.ArmWidthProperty, Num(ap.ArmWidth));
        Prop(S.ArmCountProperty, ap.ArmCount.ToString(CultureInfo.InvariantCulture));
        Prop(S.ApertureDecenterXProperty, Num(ap.DecenterX));
        Prop(S.ApertureDecenterYProperty, Num(ap.DecenterY));
        Prop(S.VerticesProperty, FormatVertices(ap.Vertices));
        var tb = s.TiltBefore ?? new OpticsTiltDecenterSettings();
        Prop(S.BeforeDecenterXProperty, Num(tb.DecenterX));
        Prop(S.BeforeDecenterYProperty, Num(tb.DecenterY));
        Prop(S.BeforeTiltXProperty, Num(tb.TiltX));
        Prop(S.BeforeTiltYProperty, Num(tb.TiltY));
        Prop(S.BeforeTiltZProperty, Num(tb.TiltZ));
        Prop(S.BeforeOrderProperty, OrderDisplay(tb.Order));
        var ta = s.TiltAfter ?? new OpticsTiltDecenterSettings();
        Prop(S.AfterModeProperty, ModeDisplay(ta.Mode));
        Prop(S.AfterSurfaceProperty, ta.Surface.ToString(CultureInfo.InvariantCulture));
        Prop(S.AfterDecenterXProperty, Num(ta.DecenterX));
        Prop(S.AfterDecenterYProperty, Num(ta.DecenterY));
        Prop(S.AfterTiltXProperty, Num(ta.TiltX));
        Prop(S.AfterTiltYProperty, Num(ta.TiltY));
        Prop(S.AfterTiltZProperty, Num(ta.TiltZ));
        Prop(S.AfterOrderProperty, OrderDisplay(ta.Order));
        var cr = s.CoordinateReturn ?? new OpticsCoordinateReturnSettings();
        Prop(S.ReturnModeProperty, ReturnDisplay(cr.Mode));
        Prop(S.ReturnSurfaceProperty, cr.Surface.ToString(CultureInfo.InvariantCulture));
        if (row.Tint != s.RowColor) doc.SetTint(index, s.RowColor is >= 0 ? s.RowColor : null);
    }

    /// <summary>Sets row <paramref name="index"/>'s flags from the surface's saved flags; a flag
    /// the row cannot take is left out with a note.</summary>
    private void WriteFlags(int index, OpticsSurfaceSettings s, ICollection<string> notes)
    {
        var doc = Document;
        var row = doc.Rows[index];
        string where = SavedSurfaces.Where(index, doc.RowCount);
        foreach (var f in s.Flags ?? new List<OpticsCellFlagSettings>())
        {
            int slot = Array.FindIndex(row.Cells.ToArray(), c => c.Definition is { } d && d.Key == f.Column);
            if (slot < 0) continue;   // kept with the row's base (a column this type has no cell for)
            CellFlag? flag = f.Kind switch
            {
                SavedSurfaces.VariableFlag => CellFlag.Variable,
                SavedSurfaces.SolveFlag when f.Solve is { Length: > 0 } code => CellFlag.Solve(code, (f.SolveParameters ?? new List<double>()).ToArray()),
                SavedSurfaces.PickupFlag => PickupFlag(f),
                _ => null,
            };
            string? error = flag is null ? $"the flag '{f.Kind}' could not be read" : doc.ValidateFlag(index, slot + 1, flag);
            if (error is not null) { notes.Add($"{where}, {f.Column}: {error}; the cell is fixed."); continue; }
            Check(doc.SetFlag(index, slot + 1, flag!));
        }

        CellFlag? PickupFlag(OpticsCellFlagSettings f)
        {
            if (f.PickupSurface < 0 || f.PickupSurface >= doc.RowCount) return null;
            var source = doc.Rows[f.PickupSurface];
            int sourceSlot = Array.FindIndex(source.Cells.ToArray(), c => c.Definition is { } d && d.Key == f.PickupColumn);
            return sourceSlot < 0 ? null : CellFlag.PickupFrom(source, sourceSlot + 1, f.PickupScale, f.PickupOffset);
        }
    }

    private static void Check(SheetEditResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException(result.Message);
    }

    // ------------------------------------------------------------------ editing

    /// <summary>
    /// Replaces the lens with <paramref name="surfaces"/> as one undo step named
    /// <paramref name="description"/>: rows are inserted or deleted before the image as needed,
    /// then every row is rewritten. If any value is refused the whole step is rolled back and
    /// the reason returned; null on success.
    /// </summary>
    public string? Apply(IReadOnlyList<OpticsSurfaceSettings> surfaces, string description)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        if (surfaces.Count < 2) return $"{description}: the lens needs an object and an image surface.";
        var otherTypes = surfaces.Where(s => !SavedSurfaces.IsStandard(s)).Select(s => s.Type!).ToArray();
        if (otherTypes.FirstOrDefault(t => Document.Schema.FindRowType(t) is null) is { } missing)
            return $"{description}: the surface type '{missing}' has no row type in this editor.";
        var doc = Document;
        var notes = new List<string>();
        var savedBase = new Dictionary<long, OpticsSurfaceSettings>(_base);
        _applying = true;
        try
        {
            using var group = doc.History.BeginGroup(description);
            try
            {
                foreach (var row in doc.Rows) row.IsLocked = false;
                int current = doc.RowCount;
                if (surfaces.Count > current) Check(doc.InsertRows(current - 1, surfaces.Count - current));
                else if (surfaces.Count < current) Check(doc.DeleteRows(Enumerable.Range(1, current - surfaces.Count)));
                for (int j = 0; j < surfaces.Count; j++) WriteRow(j, surfaces[j], notes);
                for (int j = 0; j < surfaces.Count; j++) WriteFlags(j, surfaces[j], notes);
                if (notes.Count > 0) throw new InvalidOperationException(string.Join(" ", notes));
            }
            catch (InvalidOperationException ex)
            {
                group.Rollback();
                _base.Clear();
                foreach (var (k, v) in savedBase) _base[k] = v;
                return $"{description} was not applied: {ex.Message}";
            }
            finally { LockEnds(); }
        }
        finally { _applying = false; }
        AfterChange(publish: true);
        return null;
    }

    /// <summary>Runs a tool on the current surfaces and applies its result as one undo step;
    /// the status says what it did or why not.</summary>
    private bool RunTool(string name, Func<IReadOnlyList<OpticsSurfaceSettings>, LensEditResult> tool)
    {
        LensEditResult result;
        try { result = tool(ToSettings()); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or ArithmeticException)
        {
            Status = ex.Message;
            return false;
        }
        if (Apply(result.Surfaces, name) is { } refusal)
        {
            Status = refusal;
            return false;
        }
        Status = $"{name}: {string.Join(" ", result.Notes)}";
        return true;
    }

    [RelayCommand]
    private void InsertSurface()
    {
        int at = Math.Clamp(CurrentRow, 1, Document.RowCount - 1);
        Report(Document.InsertRows(at), $"Surface {at} inserted.");
        CurrentRow = at;
    }

    [RelayCommand]
    private void InsertSurfaceAfter()
    {
        int at = Math.Clamp(CurrentRow + 1, 1, Document.RowCount - 1);
        Report(Document.InsertRows(at), $"Surface {at} inserted.");
        CurrentRow = at;
    }

    [RelayCommand]
    private void DeleteSurface()
    {
        if (CurrentRow < 0 || CurrentRow >= Document.RowCount) return;
        int at = CurrentRow;
        if (Report(Document.DeleteRows(new[] { at }), $"Surface {at} deleted."))
            CurrentRow = Math.Min(at, Document.RowCount - 2);
    }

    [RelayCommand]
    private void Undo()
    {
        string? what = Document.History.UndoDescription;
        Status = Document.Undo() ? $"Undone: {what}." : "There is nothing to undo.";
    }

    [RelayCommand]
    private void Redo()
    {
        string? what = Document.History.RedoDescription;
        Status = Document.Redo() ? $"Redone: {what}." : "There is nothing to redo.";
    }

    /// <summary>Makes the current surface the stop and clears the flag elsewhere, as one step.</summary>
    [RelayCommand]
    private void MakeStop()
    {
        int at = CurrentRow;
        if (at <= 0 || at >= Document.RowCount - 1) { Status = "The object and image surfaces cannot be the stop."; return; }
        var surfaces = ToSettings().Select((s, j) => s with { IsStop = j == at }).ToList();
        Status = Apply(surfaces, "Make surface stop") ?? $"Surface {at} is the stop.";
    }

    /// <summary>OpticStudio's Go to Surface: the first surface after the current one whose number
    /// is the text, or whose type or comment contains it; the search wraps.</summary>
    [RelayCommand]
    private void GoToSurface()
    {
        string q = GoToText.Trim();
        if (q.Length == 0) return;
        var doc = Document;
        for (int k = 1; k <= doc.RowCount; k++)
        {
            int j = (CurrentRow + k) % doc.RowCount;
            var row = doc.Rows[j];
            bool match = int.TryParse(q, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n == j
                : row.Type.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                  || row.Cells[S.CommentSlot].Value.Text.Contains(q, StringComparison.OrdinalIgnoreCase);
            if (!match) continue;
            CurrentRow = j;
            Status = $"Surface {j}.";
            return;
        }
        Status = $"No surface matches '{q}'.";
    }

    [RelayCommand] private void ReverseElements() => RunTool("Reverse Elements", s => OpenSim.Optics.Sequential.LensEditing.ReverseElements.Apply(s, FirstSurface, LastSurface));
    [RelayCommand] private void ScaleLens() => RunTool("Scale Lens", s => OpenSim.Optics.Sequential.LensEditing.ScaleLens.Apply(s, ScaleFactor, FirstSurface, LastSurface));
    [RelayCommand] private void MakeFocal() => RunTool("Make Focal", s => OpenSim.Optics.Sequential.LensEditing.MakeFocal.Apply(s, TargetFocalLength, _context()));
    [RelayCommand] private void MakeDoublePass() => RunTool("Make Double Pass", s => OpenSim.Optics.Sequential.LensEditing.MakeDoublePass.Apply(s, ReflectAtSurface));
    [RelayCommand] private void RemoveAllApertures() => RunTool("Remove All Apertures", ApertureTools.RemoveAll);
    [RelayCommand] private void ConvertToCircularApertures() => RunTool("Convert Semi-Diameters to Circular Apertures", s => ApertureTools.ToCircular(s, _context()));
    [RelayCommand] private void ConvertToFloatingApertures() => RunTool("Convert Semi-Diameters to Floating Apertures", ApertureTools.ToFloating);
    [RelayCommand] private void ConvertToMaximumApertures() => RunTool("Convert Semi-Diameters to Maximum Apertures", s => ApertureTools.ToMaximum(s, _context()));
    [RelayCommand] private void RemoveAllVariables() => RunTool("Remove All Variables", VariableTools.RemoveAllVariables);
    [RelayCommand] private void SetAllRadiiVariable() => RunTool("Set All Radii Variable", VariableTools.SetAllRadiiVariable);
    [RelayCommand] private void SetAllThicknessesVariable() => RunTool("Set All Thickness Variable", VariableTools.SetAllThicknessesVariable);

    /// <summary>System Check: errors and warnings, one line each.</summary>
    [RelayCommand]
    private void RunSystemCheck()
    {
        var findings = SystemCheck.Run(ToSettings(), _context());
        int errors = findings.Count(f => f.Severity == CheckSeverity.Error);
        SystemCheckReport = findings.Count == 0 ? "System Check found no errors or warnings."
            : $"{errors} error{(errors == 1 ? "" : "s")}, {findings.Count - errors} warning{(findings.Count - errors == 1 ? "" : "s")}:"
              + Environment.NewLine + string.Join(Environment.NewLine, findings);
        Status = "System Check done.";
    }

    /// <summary>File Comparator: this lens against a lens file (*.osslens) or a project's optics.</summary>
    public void CompareWithFile(string path)
    {
        OpticsSettings? other;
        try
        {
            other = string.Equals(Path.GetExtension(path), OpticsFileSerializer.FileExtension, StringComparison.OrdinalIgnoreCase)
                ? new OpticsFileSerializer().Load(path).Optics
                : new ProjectSerializer().Load(path).Optics;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Status = $"File Comparator: '{path}' could not be read: {ex.Message}";
            return;
        }
        if (other is null) { Status = $"File Comparator: '{path}' holds no optical system."; return; }
        var context = _context();
        var mine = new OpticsSettings { SystemData = context.SystemData, Surfaces = ToSettings() };
        var lines = FileComparator.Compare(mine, other, DifferencesOnly);
        ComparisonReport = FileComparator.Format(lines, "This lens", Path.GetFileName(path));
        Status = $"File Comparator: {lines.Count(l => l.Differs)} difference{(lines.Count(l => l.Differs) == 1 ? "" : "s")}.";
    }

    /// <summary>The lens-unit change: the surfaces are converted so the lens keeps its size, as
    /// the system data's lengths are. The undo history is cleared, since undoing an edit made
    /// in the old unit would bring back numbers in the wrong unit.</summary>
    public void ConvertLensUnit(double factor)
    {
        LensEditResult result;
        try { result = OpenSim.Optics.Sequential.LensEditing.ScaleLens.ConvertUnits(ToSettings(), factor); }
        catch (ArgumentException ex) { Status = ex.Message; return; }
        if (Apply(result.Surfaces, "Change lens unit") is { } refusal) { Status = refusal; return; }
        Document.History.Clear();
        Status = "Surfaces converted to the new lens unit." + (result.Notes.Count > 0 ? " " + string.Join(" ", result.Notes) : "");
    }

    private bool Report(SheetEditResult result, string success)
    {
        Status = result.Succeeded ? success : result.Message ?? "";
        return result.Succeeded;
    }
}
