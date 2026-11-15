using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.PhysicalOptics;

/// <summary>What one physical optics propagation is asked to do, in meters (OP-49).</summary>
public sealed record PopRequest
{
    /// <summary>The surface the beam starts before (1 or more).</summary>
    public int StartSurface { get; init; } = 1;

    /// <summary>The surface the beam ends just after; null for the image surface.</summary>
    public int? EndSurface { get; init; }

    /// <summary>0-based wavelength row; null for the primary.</summary>
    public int? WavelengthIndex { get; init; }

    /// <summary>The field number (1 = the first) whose chief ray the beam follows.</summary>
    public int Field { get; init; } = 1;

    /// <summary>"Surf to Beam": the distance from the start surface to the beam, negative when
    /// the beam starts to the left of it, meters.</summary>
    public double SurfaceToBeam { get; init; }

    public PopBeamDefinition Beam { get; init; } = new();

    public int SamplingX { get; init; } = 256;
    public int SamplingY { get; init; } = 256;

    /// <summary>The array widths, meters; null for the help's Auto width.</summary>
    public double? WidthX { get; init; }
    public double? WidthY { get; init; }

    public PopTransfer Transfer { get; init; } = PopTransfer.RayBased;

    /// <summary>Keep the field after every surface (for "Save Beam At All Surfaces").</summary>
    public bool KeepSurfaceFields { get; init; }

    /// <summary>The lens unit a ZMM file's lengths are in.</summary>
    public LensUnit Unit { get; init; } = LensUnit.Millimeter;
}

/// <summary>The result of a propagation.</summary>
public sealed record PopRun
{
    /// <summary>The field just after the end surface.</summary>
    public required PopField Field { get; init; }

    /// <summary>The field at the start (after normalisation, before any propagation).</summary>
    public required PopField Initial { get; init; }

    public required int StartSurface { get; init; }
    public required int EndSurface { get; init; }
    public required double AirWavelength { get; init; }

    /// <summary>The operators applied, in order.</summary>
    public required IReadOnlyList<PopStep> Steps { get; init; }

    /// <summary>Possible inaccuracies found (aliasing, guard band, small waist), one line each.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Choices made where the help leaves room, one line each.</summary>
    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>The field after each surface from the start to the end surface, when asked.</summary>
    public IReadOnlyList<(int Surface, PopField Field)> SurfaceFields { get; init; } = Array.Empty<(int, PopField)>();
}

/// <summary>
/// Physical optics propagation through a sequential lens (OP-49; docs/Optics-Conventions.md
/// §66): the initial beam is built in the space before the start surface, propagated to each
/// surface by <see cref="PopPropagator"/>, carried across it by <see cref="PopSurfaceTransfer"/>,
/// and checked for aliasing after every step by <see cref="PopDiagnostics"/>.
/// <para>
/// Built: a scalar field along the axis of an axial lens (no tilt, decenter or coordinate break
/// up to the end surface), on-axis fields. Refused by name: off-axis chief rays, tilted systems,
/// polarized propagation, separate x and y pilot beams, ABCD and diffractive surfaces,
/// birefringent media, the DLL beam, fiber coupling (OP-50), non-sequential surfaces.
/// </para>
/// </summary>
public static class PopEngine
{
    public static PopRun Run(SequentialLens lens, OpticalSystemData system, PopRequest request,
        IReadOnlyList<double>? semiDiameters = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(request);
        int image = lens.ImageIndex;
        int start = request.StartSurface, end = request.EndSurface ?? image;
        if (start < 1 || start > image)
            throw new ArgumentException($"Physical optics: the start surface must be 1 to {image} (it is {start}); the beam starts in the space before it.");
        if (end < start || end > image)
            throw new ArgumentException($"Physical optics: the end surface must be {start} (the start surface) to {image} (it is {end}).");
        int fields = system.Fields.Points.Count;
        if (request.Field < 1 || request.Field > fields)
            throw new ArgumentException($"Physical optics: the field must be 1 to {fields} (it is {request.Field}).");
        var point = system.Fields.Points[request.Field - 1];
        if (point.X != 0 || point.Y != 0)
            throw new NotSupportedException($"Physical optics: field {request.Field} is off axis ({point.X}, {point.Y}); propagation along an off-axis chief ray is not built. Choose an on-axis field.");

        var trace = SequentialTrace.Create(lens, system, request.WavelengthIndex, semiDiameters);
        for (int j = 1; j <= end; j++)
        {
            var step = trace.SurfaceStep(j);
            bool axial = step.Col0 == Vector3D.UnitX && step.Col1 == Vector3D.UnitY && step.Col2 == Vector3D.UnitZ
                && step.Translation.X == 0 && step.Translation.Y == 0;
            if (!axial)
                throw new NotSupportedException($"Physical optics: surface {j} is tilted or decentered (or is a coordinate break that moves the axis); propagation through tilted or decentered systems is not built. The beam must follow the axis of an axial lens up to the end surface.");
        }
        if (trace.Birefringence is not null)
            throw new NotSupportedException("Physical optics: the lens has birefringent media; physical optics through birefringent media (the help's \"Use Rays To Propagate\") is not built.");
        for (int j = start; j <= Math.Min(end, image - 1); j++)
        {
            var s = lens.Surfaces[j];
            if (s.IsIgnored || s.IsCoordinateBreak) continue;
            var shape = Sequential.Composite.CompositeSurfaces.EffectiveShape(s);
            if (shape?.OpdLimitation(s) is { } why)
                throw new NotSupportedException($"Physical optics: surface {j} ({shape.TypeName}) cannot be propagated through: {why}");
        }

        var warnings = new List<string>();
        var notes = new List<string>();
        var steps = new List<PopStep>();
        double air = OpticsConventions.MicrometersToMeters(trace.AirMicrometers);
        double sense = 1;
        for (int j = 1; j < start; j++)
            if (lens.Surfaces[j].IsMirror) sense = -sense;
        double index = trace.Indices[start - 1];

        var beam = request.Beam;
        double widthX, widthY;
        if (request.WidthX is { } wx && request.WidthY is { } wy) { widthX = wx; widthY = wy; }
        else
        {
            if (beam.Type is PopBeamType.Multimode)
                throw new ArgumentException("Physical optics: the Auto width needs the pilot waist before the beam is built, which a multimode beam does not give; set the X and Y widths.");
            double? w = PopBeams.PilotWaist(beam, air);
            widthX = w is { } a ? PopBeams.AutoWidth(a, request.SamplingX) : 1;
            widthY = w is { } b ? PopBeams.AutoWidth(b, request.SamplingY) : 1;
            if (w is { } waist && beam.Type != PopBeamType.File)
                notes.Add($"Physical optics: Auto width, X = ω0·√(π·nx) = {widthX:g6} m and Y = {widthY:g6} m for the pilot waist {waist:g6} m (help, \"Comments about Point Spacing and Sampling\").");
        }
        var field = PopBeams.Create(beam, request.SamplingX, request.SamplingY, widthX, widthY, air, index, request.Unit, notes);
        var initial = field;
        PopDiagnostics.Check(field, "the initial beam", warnings);

        var kept = new List<(int, PopField)>();
        string Gap(int j) => j == start ? $"beam to surface {j}" : $"surface {j - 1} to {j}";
        for (int j = start; j <= end; j++)
        {
            cancellation.ThrowIfCancellationRequested();
            double distance = j == start ? -sense * request.SurfaceToBeam : sense * lens.Surfaces[j - 1].Thickness;
            if (!double.IsFinite(distance))
                throw new ArgumentException($"Physical optics: the distance {Gap(j)} is not finite.");
            if (distance != 0)
            {
                field = PopPropagator.Propagate(field, distance, Gap(j), steps);
                PopDiagnostics.Check(field, Gap(j), warnings);
            }
            cancellation.ThrowIfCancellationRequested();
            field = PopSurfaceTransfer.Apply(field, trace, j, sense, request.Transfer, warnings);
            steps.Add(new PopStep($"surface {j}", "Surface", 0, field.Dx, field.Dy, field.Reference));
            PopDiagnostics.Check(field, $"surface {j}", warnings);
            if (lens.Surfaces[j].IsMirror && j < image) sense = -sense;
            if (request.KeepSurfaceFields) kept.Add((j, field));
        }
        return new PopRun
        {
            Field = field, Initial = initial, StartSurface = start, EndSurface = end, AirWavelength = air,
            Steps = steps, Warnings = warnings.Distinct().ToArray(), Notes = notes, SurfaceFields = kept,
        };
    }
}

/// <summary>
/// The sampling checks run after every step (OP-49; docs/Optics-Conventions.md §66). The help
/// asks for a guard band ("it is vital that sufficient empty space exist around the beam ... If
/// portions of the beam get too close to the edge of the array, they will 'alias'", "Defining the
/// Initial Beam") and warns of aliasing when the phase changes by more than about π/2 between
/// samples ("Angular Spectrum Propagation"), and of a pilot waist smaller than the wavelength
/// ("Algorithm Assumptions"). The help gives no numbers for the first two; these checks are
/// this program's: more than <see cref="EdgeFraction"/> of the power in the outer eighth of the
/// array on any side (the guard band), or in the outer eighth of the spatial-frequency band on
/// any side (under-sampling: a phase that steps too fast between samples, or a hard edge, puts
/// power there).
/// </summary>
public static class PopDiagnostics
{
    /// <summary>The fraction of the power allowed in an outer band before a warning.</summary>
    public const double EdgeFraction = 1e-3;

    public static void Check(PopField field, string where, ICollection<string> warnings)
    {
        double total = 0, edge = 0;
        int nx = field.Nx, ny = field.Ny;
        for (int r = 0; r < ny; r++)
            for (int c = 0; c < nx; c++)
            {
                var u = field.Samples[r, c];
                double i = u.Real * u.Real + u.Imaginary * u.Imaginary;
                total += i;
                if (OuterBand(r, ny) || OuterBand(c, nx)) edge += i;
            }
        if (!(total > 0))
        {
            warnings.Add($"Physical optics, {where}: the beam has no power left on the grid.");
            return;
        }
        double spatial = edge / total;
        if (spatial > EdgeFraction)
            warnings.Add($"Physical optics, {where}: {spatial:P3} of the power lies in the outer eighth of the array (the guard band); the beam may alias. Widen the array (X and Y width) or increase the sampling.");

        var spectrum = Fft2D.Forward(field.Samples);
        double sTotal = 0, sEdge = 0;
        for (int r = 0; r < ny; r++)
        {
            int pr = PopPropagator.Signed(r, ny);
            for (int c = 0; c < nx; c++)
            {
                int pc = PopPropagator.Signed(c, nx);
                var u = spectrum[r, c];
                double i = u.Real * u.Real + u.Imaginary * u.Imaginary;
                sTotal += i;
                if (Math.Abs(pr) > 3 * ny / 8 || Math.Abs(pc) > 3 * nx / 8) sEdge += i;
            }
        }
        double spectral = sEdge / sTotal;
        if (spectral > EdgeFraction)
            warnings.Add($"Physical optics, {where}: {spectral:P3} of the power lies in the outer eighth of the spatial-frequency band; the field is under-sampled (its phase or edges change too fast between samples). Increase the sampling, or narrow the array.");

        if (field.Pilot.WaistRadius < field.Pilot.MediumWavelength)
            warnings.Add($"Physical optics, {where}: the pilot beam's waist ({field.Pilot.WaistRadius:g4} m) is smaller than the wavelength; the help says the results are then \"probably not accurate\" (\"Suggestions for Use\", \"Algorithm Assumptions\").");
    }

    /// <summary>Index <paramref name="i"/> of <paramref name="n"/> lies in the outer eighth on its side.</summary>
    private static bool OuterBand(int i, int n) => Math.Abs(i - n / 2) > 3 * n / 8;
}
