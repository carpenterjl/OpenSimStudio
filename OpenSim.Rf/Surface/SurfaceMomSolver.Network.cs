using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Layered;

namespace OpenSim.Rf.Surface;

/// <summary>A sheet solved with several ports at one frequency.</summary>
/// <param name="Admittance">Y[i, j]: current through port i per volt across port j with
/// every other port's gap closed.</param>
/// <param name="EdgeCurrents">For each port, the RWG coefficients with one volt across it
/// and the other gaps closed.</param>
public sealed record SurfaceMultiPortSolution(double FrequencyHz, Complex[,] Admittance, Complex[][] EdgeCurrents);

/// <summary>
/// Ohmic loss of sheet metal as an impedance boundary: the tangential field on the sheet is
/// Z_s·J instead of zero, which adds Z_s·∫f_m·f_n dS (the RWG Gram matrix) to the moment
/// matrix, and the power turned to heat is ½·Re Z_s·∫|J|² dS.
/// </summary>
public static class SheetLoss
{
    /// <summary>
    /// Impedance per square of a copper sheet of finite thickness carrying its current on
    /// both faces (a strip or a plate in free space), or on one (a patch or a trace close
    /// over its ground plane, where the current is on the face toward the plane):
    /// (k/σ)·coth(k·t) for one face, half of (k/σ)·coth(k·t/2) for two, k = (1 + j)/δ. Both
    /// are 1/(σt) at DC.
    /// </summary>
    public static Complex CopperSheet(double frequencyHz, double conductivity, double thickness, bool bothFaces)
    {
        if (!(conductivity > 0 && thickness > 0 && frequencyHz > 0))
            throw new ArgumentException("Conductivity, thickness and frequency must be positive.");
        Complex k = Complex.Sqrt(new Complex(0, 2 * Math.PI * frequencyHz * RfConstants.Mu0 * conductivity));
        Complex Coth(Complex x) => x.Magnitude < 1e-3 ? 1 / x + x / 3 : 1 / Complex.Tanh(x);
        return bothFaces
            ? 0.5 * k / conductivity * Coth(k * thickness / 2)
            : k / conductivity * Coth(k * thickness);
    }

    /// <summary>Surface impedance (1 + j)·√(πfµ₀/σ) of a round conductor whose skin depth is
    /// well inside it, per square of its surface.</summary>
    public static Complex RoundWire(double frequencyHz, double conductivity)
    {
        if (!(conductivity > 0 && frequencyHz > 0))
            throw new ArgumentException("Conductivity and frequency must be positive.");
        double rs = Math.Sqrt(Math.PI * frequencyHz * RfConstants.Mu0 / conductivity);
        return new Complex(rs, rs);
    }

    /// <summary>The value of every basis on a triangle at its three edge midpoints (a rule
    /// exact for the quadratic f_m·f_n), as (basis, vector) lists per midpoint.</summary>
    private static IEnumerable<(double Weight, (int Basis, Vector3D Value)[] Bases)> Midpoints(SurfaceStructure s, int t)
    {
        var (a, b, c) = s.Triangles[t];
        Vector3D va = s.Vertices[a], vb = s.Vertices[b], vc = s.Vertices[c];
        double area = s.TriangleAreas[t];
        foreach (var mid in new[] { 0.5 * (va + vb), 0.5 * (vb + vc), 0.5 * (vc + va) })
        {
            var supports = s.TriangleSupports[t];
            var values = new (int, Vector3D)[supports.Count];
            for (int i = 0; i < supports.Count; i++)
            {
                var (basis, sign, opposite) = supports[i];
                values[i] = (basis, sign * s.Edges[basis].Length / (2 * area) * (mid - s.Vertices[opposite]));
            }
            yield return (area / 3, values);
        }
    }

    internal static void AddTo(ComplexDenseMatrix z, SurfaceStructure surface, Complex sheetImpedance)
    {
        if (sheetImpedance == Complex.Zero) return;
        for (int t = 0; t < surface.Triangles.Count; t++)
            foreach (var (weight, bases) in Midpoints(surface, t))
                foreach (var (m, fm) in bases)
                    foreach (var (n, fn) in bases)
                        z[m, n] += sheetImpedance * weight * Vector3D.Dot(fm, fn);
    }

    /// <summary>∫|J|² dS over the sheet [A²] for the given RWG coefficients.</summary>
    public static double CurrentSquared(SurfaceStructure surface, IReadOnlyList<Complex> edgeCurrents)
    {
        double total = 0;
        for (int t = 0; t < surface.Triangles.Count; t++)
            foreach (var (weight, bases) in Midpoints(surface, t))
            {
                Complex jx = Complex.Zero, jy = Complex.Zero, jz = Complex.Zero;
                foreach (var (n, fn) in bases)
                {
                    jx += edgeCurrents[n] * fn.X;
                    jy += edgeCurrents[n] * fn.Y;
                    jz += edgeCurrents[n] * fn.Z;
                }
                total += weight * (jx.Magnitude * jx.Magnitude + jy.Magnitude * jy.Magnitude + jz.Magnitude * jz.Magnitude);
            }
        return total;
    }

    /// <summary>Power turned to heat in the sheet [W] for peak-amplitude currents.</summary>
    public static double OhmicPower(SurfaceStructure surface, IReadOnlyList<Complex> edgeCurrents, Complex sheetImpedance) =>
        0.5 * sheetImpedance.Real * CurrentSquared(surface, edgeCurrents);
}

public sealed partial class SurfaceMomSolver
{
    /// <summary>
    /// Impedance per square of the sheet metal against frequency (see <see cref="SheetLoss"/>);
    /// null is a perfect conductor. Used by the delta-gap solves, the multi-port solve and the
    /// probe-fed solve.
    /// </summary>
    public Func<double, Complex>? SheetImpedance { get; init; }

    /// <summary>
    /// Surface impedance of round conductors (probe tubes, wires) against frequency [Ω per
    /// square]; null is a perfect conductor. A round conductor of radius a carries it over its
    /// circumference: Z_s/(2πa) per metre of its current (<see cref="SheetLoss.RoundWire"/>).
    /// </summary>
    public Func<double, Complex>? WireSurfaceImpedance { get; init; }

    /// <summary>Several delta-gap ports on a sheet in free space (or over its image plane).</summary>
    public SurfaceMultiPortSolution SolveMultiPort(SurfaceStructure surface, double frequencyHz,
        IReadOnlyList<SurfacePort> ports)
    {
        if (frequencyHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(frequencyHz), "Frequency must be positive.");
        double omega = 2 * Math.PI * frequencyHz;
        var z = AssembleImpedanceMatrix(surface, omega / RfConstants.SpeedOfLight, omega, MaxDegreeOfParallelism);
        return SolveMultiPortAssembled(surface, ports, frequencyHz, z);
    }

    /// <summary>Several delta-gap ports on a sheet on a grounded slab.</summary>
    public SurfaceMultiPortSolution SolveMultiPort(SurfaceStructure surface, LayeredKernelTable kernel,
        IReadOnlyList<SurfacePort> ports)
    {
        foreach (var port in ports) ValidateLayeredSurface(surface, port);
        var z = AssembleLayeredImpedanceMatrix(surface, kernel, 2 * Math.PI * kernel.FrequencyHz, MaxDegreeOfParallelism);
        return SolveMultiPortAssembled(surface, ports, kernel.FrequencyHz, z);
    }

    /// <summary>Several delta-gap ports on a sheet in a multi-layer stackup.</summary>
    public SurfaceMultiPortSolution SolveMultiPort(SurfaceStructure surface, MultiLayerKernelTable kernel,
        IReadOnlyList<SurfacePort> ports)
    {
        foreach (var port in ports) ValidateLayeredSurface(surface, port);
        var z = AssembleLayeredImpedanceMatrix(surface, kernel, 2 * Math.PI * kernel.FrequencyHz, MaxDegreeOfParallelism);
        return SolveMultiPortAssembled(surface, ports, kernel.FrequencyHz, z);
    }

    /// <summary>The coefficient with which each edge of a port is driven (rhs = c·V·l, port
    /// current Σ c·l·I): a finite gap's own coefficients, or for a delta gap +1 where the edge's
    /// T⁺→T⁻ crossing runs along the port direction (a grounded rim edge crosses into the
    /// plane) and −1 otherwise.</summary>
    private static double[] PortSigns(SurfaceStructure surface, SurfacePort port)
    {
        if (port.Coefficients is { } given)
        {
            if (given.Count != port.EdgeBases.Count)
                throw new ArgumentException(
                    $"The port has {port.EdgeBases.Count} edges but {given.Count} coefficients.", nameof(port));
            foreach (int e in port.EdgeBases)
                if (e < 0 || e >= surface.BasisCount)
                    throw new ArgumentOutOfRangeException(nameof(port), $"Port edge {e} is outside 0..{surface.BasisCount - 1}.");
            return given.ToArray();
        }
        var signs = new double[port.EdgeBases.Count];
        for (int i = 0; i < signs.Length; i++)
        {
            int e = port.EdgeBases[i];
            if (e < 0 || e >= surface.BasisCount)
                throw new ArgumentOutOfRangeException(nameof(port), $"Port edge {e} is outside 0..{surface.BasisCount - 1}.");
            var plusCentroid = surface.TriangleCentroids[surface.Edges[e].PlusTriangle];
            var crossing = surface.Edges[e].MinusTriangle >= 0
                ? surface.TriangleCentroids[surface.Edges[e].MinusTriangle] - plusCentroid
                : ThinWireMomSolver.Mirror(plusCentroid, surface.Ground!.SurfaceZ) - plusCentroid;
            signs[i] = Vector3D.Dot(crossing, port.Direction) >= 0 ? 1.0 : -1.0;
        }
        return signs;
    }

    private SurfaceMultiPortSolution SolveMultiPortAssembled(SurfaceStructure surface,
        IReadOnlyList<SurfacePort> ports, double frequencyHz, ComplexDenseMatrix z)
    {
        if (ports.Count == 0) throw new ArgumentException("At least one port is needed.", nameof(ports));
        var signs = ports.Select(p => PortSigns(surface, p)).ToArray();
        var used = new HashSet<int>();
        foreach (var port in ports)
            foreach (int e in port.EdgeBases)
                if (!used.Add(e))
                    throw new ArgumentException($"Mesh edge {e} belongs to two ports.", nameof(ports));

        if (SheetImpedance is { } sheet) SheetLoss.AddTo(z, surface, sheet(frequencyHz));
        var lu = ComplexLu.Factor(z, MaxDegreeOfParallelism);
        var y = new Complex[ports.Count, ports.Count];
        var currents = new Complex[ports.Count][];
        for (int q = 0; q < ports.Count; q++)
        {
            var rhs = new Complex[surface.BasisCount];
            for (int i = 0; i < ports[q].EdgeBases.Count; i++)
                rhs[ports[q].EdgeBases[i]] = signs[q][i] * surface.Edges[ports[q].EdgeBases[i]].Length;
            currents[q] = lu.Solve(rhs);
            for (int p = 0; p < ports.Count; p++)
            {
                Complex current = Complex.Zero;
                for (int i = 0; i < ports[p].EdgeBases.Count; i++)
                    current += signs[p][i] * surface.Edges[ports[p].EdgeBases[i]].Length * currents[q][ports[p].EdgeBases[i]];
                y[p, q] = current;
            }
        }
        return new SurfaceMultiPortSolution(frequencyHz, y, currents);
    }
}
