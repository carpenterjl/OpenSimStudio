using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Numerics;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Controls;
using OpenSim.App.Rendering;
using OpenSim.Core.PostProcessing;
using OpenSim.Rf;
using OpenSim.Rf.Network;
using OpenSim.Rf.Si;
using OpenSim.Rf.Surface;
using Vector3D = OpenSim.Core.Numerics.Vector3D;

namespace OpenSim.App.ViewModels;

/// <summary>What closes a port of a multi-port antenna model, as the panel's pickers name it.</summary>
public static class PortLoads
{
    public const string Port = "Port";
    public const string Short = "Short";
    public const string Open = "Open";
    public const string Resistor = "R [Ω]";
    public const string Inductor = "L [nH]";
    public const string Capacitor = "C [pF]";
    public static IReadOnlyList<string> All { get; } = new[] { Port, Short, Open, Resistor, Inductor, Capacitor };
}

/// <summary>One port's closure as the solve reads it: left a port, shorted, left open, or closed by a
/// lumped part (value in the picker's unit; Q 0 = ideal).</summary>
internal readonly record struct PortClosure(string Name, string Load, double Value, double Q)
{
    public bool IsPort => Load == PortLoads.Port;
    public bool IsOpen => Load == PortLoads.Open;

    /// <summary>The closing impedance at <paramref name="frequencyHz"/>; null for a port or an open.</summary>
    public Complex? Impedance(double frequencyHz) => Part() is { } part ? part.Impedance(frequencyHz)
        : Load == PortLoads.Short ? Complex.Zero : null;

    public LumpedPart? Part() => Load switch
    {
        PortLoads.Resistor => LumpedPart.Resistor(Value),
        PortLoads.Inductor => LumpedPart.Inductor(Value * 1e-9, Q > 0 ? Q : null),
        PortLoads.Capacitor => LumpedPart.Capacitor(Value * 1e-12, Q > 0 ? Q : null),
        _ => null
    };

    public string Describe() => Part() is { } part ? $"'{Name}' closed by {part.Describe()}"
        : Load == PortLoads.Short ? $"'{Name}' shorted" : Load == PortLoads.Open ? $"'{Name}' open" : $"'{Name}' a port";

    /// <summary>Refuses a part without a positive value.</summary>
    public string? Problem() => Part() is not null && !(Value > 0)
        ? $"the {Load} on '{Name}' needs a value above 0" : null;
}

/// <summary>
/// The multi-port side of the antenna panel (Feature 8, Feature 12; FU-47): pins, sheet-port lines and
/// a printed antenna's feed are ports of one model. Each port is left a port, shorted, left open or
/// closed by a lumped part; the network the sweep shows is the one over the ports left open to the
/// outside — one port goes to the S11, Smith chart and matching network as before, several to the
/// N-port S-matrix, its reciprocity and passivity checks and a Touchstone .sNp. The far field drives
/// one port with 1 V and closes the others as set, a port left a port in the reference impedance (the
/// embedded pattern).
/// </summary>
public partial class AntennaViewModel
{
    /// <summary>A model with ports: its full impedance matrix at a frequency (every port, in
    /// <see cref="PortNames"/> order), and the fields of a port excitation.</summary>
    private abstract class PortedModel
    {
        public required SurfaceStructure Surface { get; init; }
        public required IReadOnlyList<string> PortNames { get; init; }
        /// <summary>A short account for the result line: unknowns and medium.</summary>
        public required string Description { get; init; }
        public required IReadOnlyList<string> Assumptions { get; init; }
        public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
        /// <summary>The ground's height for the far-field grid check (image or slab), when there is one.</summary>
        public double? GroundZ { get; init; }

        public abstract Complex[,] Impedance(double frequencyHz);

        /// <summary>The far field for the given port voltages and currents, the sheet currents that
        /// produce it, the power the ports deliver, and the power ledger's text.</summary>
        public abstract (FarFieldPattern Pattern, SurfaceMomSolution Sheet, double InputWatts, string Ledger) Radiate(
            double frequencyHz, Complex[] volts, Complex[] currents);

        /// <summary>The near field of the excitation and the sheet currents behind it, or a null map
        /// with the reason it is not available.</summary>
        public abstract (FieldMap? Map, SurfaceMomSolution? Sheet) NearField(double frequencyHz, Complex[] volts,
            IReadOnlyList<Vector3D> points, out string? refusal);
    }

    // ---- The multi-port result ----
    [ObservableProperty] private bool _hasMultiPort;
    [ObservableProperty] private string _multiPortResult = "";
    [ObservableProperty] private ImageSource? _multiPortImage;
    /// <summary>The ports a far field can drive (those left a port).</summary>
    public ObservableCollection<string> PortChoices { get; } = new();
    /// <summary>The port the far field and near field drive with 1 V; null = the first.</summary>
    [ObservableProperty] private string? _farFieldPort;
    private (List<double> Hz, List<Complex[,]> S, List<string> Names)? _multiPortS;

    /// <summary>The ported modes: pins on a patch or an island, a printed antenna, sheet metal.</summary>
    private bool IsPortedMode => SourceMode is PinnedPatchMode or IslandPinsMode or PrintedMode or SheetMetalMode;

    private void ClearMultiPortResults()
    {
        HasMultiPort = false;
        MultiPortResult = "";
        MultiPortImage = null;
        _multiPortS = null;
    }

    /// <summary>The network over the ports left open to the outside: open ports dropped, shorts and
    /// parts closing theirs (exact for delta-gap ports, <see cref="PortTermination.Terminate"/>).</summary>
    private static Complex[,] Reduce(Complex[,] z, IReadOnlyList<PortClosure> closures, double frequencyHz)
    {
        var present = Enumerable.Range(0, closures.Count).Where(k => !closures[k].IsOpen).ToArray();
        var sub = new Complex[present.Length, present.Length];
        for (int a = 0; a < present.Length; a++)
            for (int b = 0; b < present.Length; b++) sub[a, b] = z[present[a], present[b]];
        var loads = new Dictionary<int, Complex>();
        for (int a = 0; a < present.Length; a++)
            if (closures[present[a]].Impedance(frequencyHz) is { } load) loads[a] = load;
        return loads.Count == 0 ? sub : PortTermination.Terminate(sub, loads);
    }

    private static Complex[] Flatten(Complex[,] z)
    {
        int n = z.GetLength(0);
        var v = new Complex[n * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) v[i * n + j] = z[i, j];
        return v;
    }

    private static Complex[,] Unflatten(IReadOnlyList<Complex> v, int n)
    {
        var z = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) z[i, j] = v[i * n + j];
        return z;
    }

    /// <summary>Checks the closures: at least one port left, every part with a value.</summary>
    private static string? ClosureProblem(IReadOnlyList<PortClosure> closures)
    {
        if (closures.Count == 0) return "the model has no port";
        foreach (var c in closures) if (c.Problem() is { } problem) return problem;
        if (!closures.Any(c => c.IsPort)) return "every port is closed or open — leave at least one as a port";
        var names = closures.Select(c => c.Name).ToList();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count) return "port names must be distinct";
        return null;
    }

    private void RefreshPortChoices(IReadOnlyList<PortClosure> closures)
    {
        var names = closures.Where(c => c.IsPort).Select(c => c.Name).ToList();
        if (names.SequenceEqual(PortChoices)) return;
        string? keep = FarFieldPort;
        PortChoices.Clear();
        foreach (var n in names) PortChoices.Add(n);
        FarFieldPort = keep is not null && names.Contains(keep) ? keep : names.FirstOrDefault();
    }

    /// <summary>Builds the ported model of the current mode from the panel (UI thread); the solves run off it.</summary>
    private bool TryBuildPortedModel(out PortedModel model, out IReadOnlyList<PortClosure> closures, out string? failure,
        double refine = 1)
    {
        model = null!;
        closures = Array.Empty<PortClosure>();
        bool built = SourceMode switch
        {
            PinnedPatchMode or IslandPinsMode => TryBuildPinModel(out model, out closures, out failure, refine),
            PrintedMode => TryBuildPrintedModel(out model, out closures, out failure, refine),
            SheetMetalMode => TryBuildSheetMetalModel(out model, out closures, out failure, refine),
            _ => Fail(out failure)
        };
        if (!built) return false;
        if (ClosureProblem(closures) is { } problem) { failure = problem; return false; }
        RefreshPortChoices(closures);
        return true;

        static bool Fail(out string? failure) { failure = "pick a geometry with ports"; return false; }
    }

    /// <summary>The sweep of a ported model: every frequency's full matrix reduced to the open ports;
    /// one port feeds the S11 display, several the N-port one.</summary>
    private async Task SolvePortedAsync()
    {
        ClearMultiPortResults();
        if (!TryBuildPortedModel(out var model, out var closures, out string? failure))
        {
            AntennaResult = $"Not solvable: {failure}";
            return;
        }
        var names = closures.Where(c => c.IsPort).Select(c => c.Name).ToList();
        int m = names.Count;
        double frequency = FrequencyMHz * 1e6, fMin = SweepFMinMHz * 1e6, fMax = SweepFMaxMHz * 1e6;
        int points = SweepPoints;
        bool adaptive = AdaptiveSweep && points > 2 && fMax > fMin;
        bool logarithmic = LogarithmicSweep;
        int maxSamples = Math.Max(points, 30);
        AntennaResult = $"Solving ({model.Description})…";
        try
        {
            var (curve, rational, display) = await Task.Run(() =>
            {
                Complex[,] Reduced(double f) => Reduce(model.Impedance(f), closures, f);
                if (adaptive)
                {
                    var found = RationalSweep.Run(f => Flatten(Reduced(f)), fMin, fMax, maxSamples: maxSamples,
                        batch: SweepBatch, logarithmic: logarithmic, maxDegreeOfParallelism: SweepBatch);
                    var sampled = found.FrequenciesHz.Select((f, k) => (f, Unflatten(found.Values[k], m))).ToList();
                    return (sampled, (RationalSweepResult?)found, Reduced(frequency));
                }
                double FrequencyAt(int k) => points == 1 ? fMin : fMin * Math.Pow(fMax / fMin, (double)k / (points - 1));
                var results = new Complex[points][,];
                try { Parallel.For(0, points, k => results[k] = Reduced(FrequencyAt(k))); }
                catch (AggregateException e) { throw e.InnerExceptions[0]; }
                var list = Enumerable.Range(0, points).Select(k => (FrequencyAt(k), results[k])).ToList();
                return (list, (RationalSweepResult?)null, Reduced(frequency));
            });

            string closed = closures.Any(c => !c.IsPort)
                ? " Closed: " + string.Join(", ", closures.Where(c => !c.IsPort).Select(c => c.Describe())) + "."
                : "";
            if (m == 1)
            {
                foreach (var (f, z) in curve) ZinSweep.Add(new AntennaZinPoint(f, z[0, 0].Real, z[0, 0].Imaginary));
                if (rational is not null) ShowNetworkResults(rational);
                else ShowNetworkResultsFromSweep();
                var zin = display[0, 0];
                AntennaResult = $"Zin = {zin.Real:g4} {(zin.Imaginary >= 0 ? "+" : "−")} j{Math.Abs(zin.Imaginary):g4} Ω "
                    + $"at {FrequencyMHz:g4} MHz at port '{names[0]}' ({model.Description})";
                string meshCheck = CheckMeshConvergence ? await PortedMeshCheckAsync(model, closures, zin, frequency) : "";
                AntennaAssumptions = "Assumptions: " + string.Join(" ", model.Assumptions)
                    + (model.Notes.Count > 0 ? " " + string.Join(" ", model.Notes) : "") + closed + meshCheck;
            }
            else
            {
                var dense = rational is null ? curve
                    : DenseFrequencies(rational).Select(f => (f, Unflatten(rational.At(f), m))).ToList();
                ShowMultiPortResults(names, dense, display, frequency,
                    rational is null ? null : AdaptiveNote(rational));
                AntennaResult = $"{m}-port network at {FrequencyMHz:g4} MHz over ports "
                    + string.Join(", ", names.Select((n, k) => $"{k + 1} = '{n}'")) + $" ({model.Description})";
                AntennaAssumptions = "Assumptions: " + string.Join(" ", model.Assumptions)
                    + (model.Notes.Count > 0 ? " " + string.Join(" ", model.Notes) : "") + closed
                    + (CheckMeshConvergence ? " Mesh check: not run for a multi-port result (close the ports down to one to run it)." : "");
            }
            GroundPlaneModel = model.GroundZ is { } gz ? GroundOverlayAt(model.Surface, gz) : null;
            _log.Append($"Antenna: {AntennaResult}");
        }
        catch (Exception ex) { AntennaResult = $"Not solvable: {ex.Message}"; }
    }

    /// <summary>The dense band the adaptive sweep's interpolant is read on (301 points).</summary>
    private static IEnumerable<double> DenseFrequencies(RationalSweepResult sweep)
    {
        double from = sweep.FrequenciesHz[0], to = sweep.FrequenciesHz[^1];
        for (int k = 0; k <= 300; k++)
            yield return sweep.Logarithmic ? from * Math.Pow(to / from, k / 300.0) : from + (to - from) * k / 300;
    }

    /// <summary>The same model 1.5× finer (and 2.25× for three passes), the one-port Zin at the
    /// display frequency compared.</summary>
    private async Task<string> PortedMeshCheckAsync(PortedModel model, IReadOnlyList<PortClosure> closures, Complex zin, double frequency)
    {
        bool threePass = ThreePassMeshCheck;
        var finer = new List<PortedModel>();
        foreach (double refine in threePass ? new[] { 1.5, 2.25 } : new[] { 1.5 })
        {
            if (!TryBuildPortedModel(out var refined, out _, out string? failure, refine))
                return $" Mesh check not run: a finer mesh could not be built ({failure}).";
            finer.Add(refined);
        }
        return await Task.Run(() =>
        {
            try
            {
                var z = finer.Select(f => Reduce(f.Impedance(frequency), closures, frequency)[0, 0]).ToList();
                return " " + (threePass
                    ? MeshConvergence.Describe(zin, z[0], z[1], finer[1].Surface.BasisCount, 1.5)
                    : MeshConvergence.Describe(zin, model.Surface.BasisCount, z[0], finer[0].Surface.BasisCount));
            }
            catch (Exception ex) { return $" Mesh check not run: {ex.Message}"; }
        });
    }

    /// <summary>S over the open ports against the reference impedance: the matrix at the display
    /// frequency, |S_ij| against frequency, and the reciprocity and passivity checks.</summary>
    private void ShowMultiPortResults(IReadOnlyList<string> names, IReadOnlyList<(double Hz, Complex[,] Z)> curve,
        Complex[,] displayZ, double displayHz, string? sweepNote)
    {
        double z0 = ReferenceOhms;
        if (!(z0 > 0)) { MultiPortResult = "Set a reference impedance above 0."; return; }
        int m = names.Count;
        var s = curve.Select(p => NetworkParameters.ImpedanceToScattering(p.Z, z0)).ToList();
        var checks = curve.Select((p, k) => NetworkChecks.Check(p.Hz, s[k])).ToList();
        var sd = NetworkParameters.ImpedanceToScattering(displayZ, z0);
        static double Db(double magnitude) => 20 * Math.Log10(Math.Max(magnitude, 1e-12));
        var lines = new List<string>
        {
            $"S at {displayHz / 1e6:g5} MHz against {z0:g4} Ω (ports " + string.Join(", ", names.Select((n, k) => $"{k + 1} '{n}'")) + "):"
        };
        for (int i = 0; i < m; i++)
            lines.Add(string.Join("   ", Enumerable.Range(0, m).Select(j =>
                $"S{i + 1}{j + 1} {Db(sd[i, j].Magnitude),6:F1} dB ∠{sd[i, j].Phase * 180 / Math.PI,6:F1}°")));
        for (int i = 0; i < m; i++)
            lines.Add($"Z{i + 1}{i + 1} = {displayZ[i, i].Real:g4} {(displayZ[i, i].Imaginary < 0 ? "−" : "+")} "
                + $"j{Math.Abs(displayZ[i, i].Imaginary):g4} Ω (the others open)");
        lines.AddRange(NetworkChecks.Describe(checks));
        if (sweepNote is not null) lines.Add(sweepNote);
        MultiPortResult = string.Join(Environment.NewLine, lines);
        foreach (var line in lines) _log.Append("Antenna: " + line);

        var colors = new[]
        {
            Color.FromRgb(0x3D, 0x8B, 0xFD), Color.FromRgb(0xE0, 0x6C, 0x2B), Color.FromRgb(0x2E, 0xA0, 0x5A),
            Color.FromRgb(0xA0, 0x4C, 0xC8), Color.FromRgb(0xE5, 0x53, 0x4B), Color.FromRgb(0x9A, 0xA0, 0xA6)
        };
        var series = new List<PlotSeries>();
        var mhz = curve.Select(p => p.Hz / 1e6).ToList();
        for (int i = 0; i < m && series.Count < 10; i++)
            for (int j = i; j < m && series.Count < 10; j++)
                series.Add(new PlotSeries($"S{i + 1}{j + 1}", mhz, s.Select(x => Db(x[i, j].Magnitude)).ToList(),
                    colors[series.Count % colors.Length], Dashed: i != j));
        MultiPortImage = CurvePlot.Render(series, logX: false, logY: false, "frequency [MHz]", "|S| [dB]", height: 240);
        _multiPortS = (curve.Select(p => p.Hz).ToList(), s, names.ToList());
        HasMultiPort = true;
    }

    [RelayCommand]
    private void ExportSnp()
    {
        if (_multiPortS is not { } data) return;
        int m = data.Names.Count;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"antenna.s{m}p", Filter = $"Touchstone|*.s{m}p|All files|*.*", Title = "Save S-parameters (Touchstone)"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, TouchstoneWriter.Write(data.Hz, data.S, ReferenceOhms, data.Names));
            _log.Append($"Antenna: saved {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception ex) { MultiPortResult += Environment.NewLine + "Not saved: " + ex.Message; }
    }

    /// <summary>The port excitation the field commands use: the chosen port driven with 1 V; every
    /// other port closed as set — a port left a port in the reference impedance, an open one open.</summary>
    private (Complex[] Volts, Complex[] Currents, string Text) Excite(Complex[,] z, IReadOnlyList<PortClosure> closures,
        int driven, double frequencyHz, double referenceOhms)
    {
        var loads = new Dictionary<int, Complex>();
        for (int k = 0; k < closures.Count; k++)
        {
            if (k == driven || closures[k].IsOpen) continue;
            loads[k] = closures[k].Impedance(frequencyHz) ?? referenceOhms;
        }
        var (v, i) = PortTermination.Drive(z, driven, loads);
        var others = closures.Where((_, k) => k != driven).Select(c => c.IsPort ? $"'{c.Name}' in {referenceOhms:g4} Ω" : c.Describe()).ToList();
        string text = $"port '{closures[driven].Name}' driven with 1 V" + (others.Count > 0 ? ", " + string.Join(", ", others) : "");
        return (v, i, text);
    }

    private bool TryDrivenPort(IReadOnlyList<PortClosure> closures, out int driven, out string? failure)
    {
        string? name = FarFieldPort ?? closures.FirstOrDefault(c => c.IsPort).Name;
        driven = Enumerable.Range(0, closures.Count).FirstOrDefault(k => closures[k].Name == name && closures[k].IsPort, -1);
        failure = driven < 0 ? "pick the port to drive (it must be left a port)" : null;
        return driven >= 0;
    }

    private async Task ShowPortedFarFieldAsync()
    {
        if (!TryBuildPortedModel(out var model, out var closures, out string? failure)
            || !TryDrivenPort(closures, out int driven, out failure))
        {
            FieldResult = $"Not computable: {failure}";
            return;
        }
        FieldResult = $"Computing ({model.Description})…";
        double frequency = FrequencyMHz * 1e6, z0 = ReferenceOhms;
        try
        {
            var (fields, text) = await Task.Run(() =>
            {
                var (v, i, t) = Excite(model.Impedance(frequency), closures, driven, frequency, z0);
                return (model.Radiate(frequency, v, i), t);
            });
            var (center, diagonal) = SurfaceBounds(model.Surface);
            FarFieldLobeModel = SceneBuilder.BuildFarFieldLobe(fields.Pattern, center, scale: 1.25 * diagonal, ColormapKind.Viridis);
            SurfaceCurrentModel = SceneBuilder.BuildSurfaceCurrentModel(model.Surface, fields.Sheet, ColormapKind.Viridis);
            GroundPlaneModel = model.GroundZ is { } gz ? GroundOverlayAt(model.Surface, gz) : null;
            double dbi = 10 * Math.Log10(fields.Pattern.MaxDirectivity);
            FieldResult = $"Far field at {FrequencyMHz:g4} MHz ({text}): P_rad = {fields.Pattern.TotalRadiatedPowerWatts:g4} W, "
                + $"D_max = {fields.Pattern.MaxDirectivity:g4} ({dbi:g3} dBi); lobe radius ∝ radiation intensity"
                + (fields.Ledger.Length > 0 ? "; " + fields.Ledger : "");
            if (FarFieldEvaluator.GridWarning(SurfaceExtent(model.Surface, model.GroundZ), frequency,
                    fields.Pattern.ThetaRadians.Count, fields.Pattern.PhiRadians.Count, hemisphere: model.GroundZ is not null) is { } warning)
                FieldResult += $". Warning: {warning}";
            _log.Append($"Antenna: {FieldResult}");
        }
        catch (Exception ex) { FieldResult = $"Not computable: {ex.Message}"; }
    }

    /// <summary>The near field (a 3-D arrow grid, or the overlay plane) of the driven port.</summary>
    private async Task<(FieldMap Map, PortedModel Model, SurfaceMomSolution? Sheet)?> PortedNearFieldAsync(
        Func<PortedModel, IReadOnlyList<Vector3D>> pointsOf)
    {
        if (!TryBuildPortedModel(out var model, out var closures, out string? failure)
            || !TryDrivenPort(closures, out int driven, out failure))
        {
            FieldResult = $"Not computable: {failure}";
            return null;
        }
        FieldResult = $"Computing ({model.Description})…";
        double frequency = FrequencyMHz * 1e6, z0 = ReferenceOhms;
        var points = pointsOf(model);
        try
        {
            var (map, refusal, sheet) = await Task.Run(() =>
            {
                var (v, _, _) = Excite(model.Impedance(frequency), closures, driven, frequency, z0);
                var (near, currents) = model.NearField(frequency, v, points, out string? why);
                return (near, why, currents);
            });
            if (map is null) { FieldResult = $"Not computable: {refusal}"; return null; }
            return (map, model, sheet);
        }
        catch (Exception ex) { FieldResult = $"Not computable: {ex.Message}"; return null; }
    }

    private async Task ComputePortedNearFieldAsync()
    {
        int n = Math.Clamp(GridResolution, 3, 17);
        double spacing = 0;
        var result = await PortedNearFieldAsync(model =>
        {
            var (center, diagonal) = SurfaceBounds(model.Surface);
            double span = 1.6 * diagonal;
            spacing = span / (n - 1);
            var points = new List<Vector3D>(n * n * n);
            for (int z = 0; z < n; z++)
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                        points.Add(center + new Vector3D(x * spacing - span / 2, y * spacing - span / 2, z * spacing - span / 2));
            return points;
        });
        if (result is not { } r) return;
        VectorFieldModel = SceneBuilder.BuildVectorFieldModel(r.Map, ColormapKind.Viridis, arrowLength: 0.8 * spacing);
        if (r.Sheet is not null) SurfaceCurrentModel = SceneBuilder.BuildSurfaceCurrentModel(r.Model.Surface, r.Sheet, ColormapKind.Viridis);
        GroundPlaneModel = r.Model.GroundZ is { } gz ? GroundOverlayAt(r.Model.Surface, gz) : null;
        FieldResult = $"Near field at {FrequencyMHz:g4} MHz: peak |E| = {r.Map.Magnitude.Max():g4} V/m ({n}³ grid, the driven port at 1 V; "
            + "arrows are the t = 0 snapshot; the sheet is colored by log₁₀|J|). Within about an element of the metal the sheet's "
            + "field is a surface-scale approximation.";
        _log.Append($"Antenna: {FieldResult}");
    }

    private async Task ShowPortedFieldOverlayAsync(int n, FieldScaleMode mode, double opacity, bool magnetic)
    {
        double z = 0;
        var result = await PortedNearFieldAsync(model =>
        {
            var (center, diagonal) = SurfaceBounds(model.Surface);
            z = model.Surface.Vertices.Max(v => v.Z) + OverlayHeightMm * 1e-3;
            return OverlayGridPoints(center, diagonal, z, n);
        });
        if (result is not { } r) return;
        ApplyFieldOverlay(r.Map, magnetic, n, mode, opacity, z, kernelNote: "free-space kernels, the driven port at 1 V");
        if (r.Sheet is not null) SurfaceCurrentModel = SceneBuilder.BuildSurfaceCurrentModel(r.Model.Surface, r.Sheet, ColormapKind.Viridis);
        GroundPlaneModel = r.Model.GroundZ is { } gz ? GroundOverlayAt(r.Model.Surface, gz) : null;
    }

    /// <summary>The ground overlay disk at a given height under a surface.</summary>
    private static System.Windows.Media.Media3D.Model3D GroundOverlayAt(SurfaceStructure surface, double groundZ)
    {
        var (center, diagonal) = SurfaceBounds(surface);
        return SceneBuilder.BuildGroundPlaneModel(center.X, center.Y, groundZ, radius: 1.5 * diagonal);
    }

    /// <summary>Raises the mode flags the panel's sections follow.</summary>
    partial void OnSourceModeChanged(string? value)
    {
        OnPropertyChanged(nameof(IsPinPanelVisible));
        OnPropertyChanged(nameof(IsIslandPinsPanelVisible));
        OnPropertyChanged(nameof(IsPrintedPanelVisible));
        OnPropertyChanged(nameof(IsSheetMetalPanelVisible));
        OnPropertyChanged(nameof(IsPortedPanelVisible));
        ClearMultiPortResults();
    }

    public bool IsPinPanelVisible => SourceMode is PinnedPatchMode or IslandPinsMode;
    public bool IsIslandPinsPanelVisible => SourceMode == IslandPinsMode;
    public bool IsPrintedPanelVisible => SourceMode == PrintedMode;
    public bool IsSheetMetalPanelVisible => SourceMode == SheetMetalMode;
    public bool IsPortedPanelVisible => IsPortedMode;

    /// <summary>Keeps the far-field port choices current as rows change.</summary>
    private void OnPortRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (TryClosures(out var closures)) RefreshPortChoices(closures);
    }

    /// <summary>The current mode's port closures without building the model.</summary>
    private bool TryClosures(out IReadOnlyList<PortClosure> closures)
    {
        closures = SourceMode switch
        {
            PinnedPatchMode or IslandPinsMode => PinClosures(),
            PrintedMode => new[] { new PortClosure("feed", PortLoads.Port, 0, 0) },
            SheetMetalMode => SheetClosures(),
            _ => Array.Empty<PortClosure>()
        };
        return closures.Count > 0;
    }
}
