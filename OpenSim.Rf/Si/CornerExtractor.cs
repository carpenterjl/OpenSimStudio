using System.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Network;
using OpenSim.Rf.Surface;

namespace OpenSim.Rf.Si;

/// <summary>
/// A trace's corner as the lumped network it adds to the line's own path length (FU-29): a
/// symmetric T of series L/2, shunt C, series L/2, between reference planes at the corner point on
/// the centerline. Negative L is a corner shorter, electrically, than its path.
/// </summary>
public sealed record CornerNetwork(double SeriesInductanceHenries, double ShuntCapacitanceFarads,
    double ExtractionFrequencyHz, double LineOhms)
{
    /// <summary>How far the de-embedded corner is from a symmetric T: |A − D| and |A − (1 + ZY/2)|
    /// over the corner's departure from the identity — the solve's own noise in the reading.</summary>
    public double FitResidual { get; init; }

    /// <summary>The T's chain (ABCD) matrix at <paramref name="frequencyHz"/>.</summary>
    public Complex[,] Abcd(double frequencyHz)
    {
        double omega = 2 * Math.PI * frequencyHz;
        Complex z = new(0, omega * SeriesInductanceHenries), y = new(0, omega * ShuntCapacitanceFarads);
        Complex a = 1 + z * y / 2;
        return new[,] { { a, z * (1 + z * y / 4) }, { y, a } };
    }
}

/// <summary>
/// FU-29 — a trace corner solved, not looked up: the full-wave layered moment method
/// (<see cref="SurfaceMomSolver"/> on the stack's own Green's function) on a bent strip and on two
/// straight lengths of the same strip, all between the same gap ports. The straight pair calibrates
/// the line and takes the ports off (<see cref="LineFromTwoLengths"/>, the gap's shunt across the
/// terminals); the bent strip, de-embedded the same way and with its two arms removed up to the corner
/// point, leaves the corner alone, read as a symmetric T: from its chain matrix, Y = C₂₁ and Z from
/// B = Z(1 + ZY/4), L = Im Z/ω and C = Im Y/ω.
///
/// <para>The corner is the one a board draws: two straight draws of width w meeting at the corner
/// point with round ends, so the copper there is the union of the two strips and a disc of radius
/// w/2. The arms run 4·max(w, h) from each port to the corner, h the distance to the nearer plane, and
/// 4·max(w, h) beyond each port (a gap close to an open end biases the calibration — FU-30); the
/// straight strips are two and four arms long. The frequency puts the long strip at λ/8, where the
/// corner is lumped and its reactances still readable against the line's.</para>
///
/// <para>The readings are differences of a few per cent between solves, so the strips are meshed
/// alike: rows of cells w/2 long and four graded cells across, laid out from the ports, the bent
/// strip's arms exactly as the straight lines are — only the patch of copper where the arms meet
/// differs. (An unstructured mesh of each strip put 10 fF and 30 pH into a corner of 0°; this one
/// gives a 0° corner of exactly nothing.) On a 0.3 mm trace over 0.2 mm of εr 4.2 a right angle reads
/// −75 pH and −5.1 fF — electrically shorter than its centerline path — and arms half as long again at
/// 1.3 times the frequency read the same to 6 % and 1 % (CornerTests).</para>
/// </summary>
public static class CornerExtractor
{
    private const double ReferenceOhms = 50;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<CornerNetwork>> Solved = new();

    /// <summary><see cref="Extract"/>, once per corner shape for the life of the process: a board has
    /// many corners and few shapes (a width, a layer's cross-section, an angle).</summary>
    public static CornerNetwork Cached(LayeredStackup stackup, int metalInterface, bool topGround,
        double width, double deflectionDegrees)
    {
        string key = string.Join("|", stackup.Layers.Select(l => FormattableString.Invariant(
                $"{l.RelativePermittivity:r},{l.LossTangent:r},{l.ThicknessMeters:r}")))
            + FormattableString.Invariant($"|{metalInterface}|{topGround}|{width:r}|{deflectionDegrees:r}");
        return Solved.GetOrAdd(key, _ => new Lazy<CornerNetwork>(() =>
            Extract(stackup, metalInterface, topGround, width, deflectionDegrees))).Value;
    }

    /// <summary>The corner network of a strip of width <paramref name="width"/> on interface
    /// <paramref name="metalInterface"/> of <paramref name="stackup"/> (a plane under it; air above, or a
    /// second plane, <paramref name="topGround"/>), turning by <paramref name="deflectionDegrees"/>
    /// (0 = straight on; 90 = a right angle).</summary>
    public static CornerNetwork Extract(LayeredStackup stackup, int metalInterface, bool topGround,
        double width, double deflectionDegrees, double? frequencyHz = null, double? armMeters = null)
    {
        if (!(width > 0)) throw new ArgumentOutOfRangeException(nameof(width));
        if (!(deflectionDegrees >= 0 && deflectionDegrees < 150))
            throw new ArgumentOutOfRangeException(nameof(deflectionDegrees), "A corner turns by 0° to under 150°.");
        var heights = stackup.InterfaceHeights();
        double z = heights[metalInterface];
        double h = topGround ? Math.Min(z, heights[^1] - z) : z;
        double scale = Math.Max(width, h);
        double cell = width / 2;
        // Arms and stubs a whole number of cells, so every port sits on a row of the mesh.
        double arm = cell * Math.Ceiling((armMeters ?? 4 * scale) / cell - 1e-9);
        double stub = cell * Math.Ceiling(4 * scale / cell - 1e-9);

        // The quasi-TEM line from the 2D solve, for the frequency and the branch of β.
        var section = new CoupledLineCrossSection(stackup, metalInterface,
            new[] { TraceCrossSection.Copper(0, width) }, topGround);
        var rlgc = RlgcExtractor.Extract(section);
        double lc = rlgc.InductanceHenriesPerMeter[0, 0] * rlgc.CapacitanceFaradsPerMeter[0, 0];
        double shortLength = 2 * arm, longLength = 4 * arm;
        double f = frequencyHz ?? 1 / (8 * longLength * Math.Sqrt(lc));
        double omega = 2 * Math.PI * f, beta = omega * Math.Sqrt(lc);

        double span = longLength + 2 * stub;
        var table = new MultiLayerKernelTable(stackup, f, rhoMax: 1.2 * span,
            sourceInterface: topGround || metalInterface < stackup.Layers.Count - 1 ? metalInterface : null,
            shielded: topGround);
        var solver = new SurfaceMomSolver();
        Complex[,] Solve((SurfaceStructure Structure, SurfacePort[] Ports) model) => NetworkChecks.AdmittanceToScattering(
            solver.SolveMultiPort(model.Structure, table, model.Ports).Admittance, ReferenceOhms);

        var calibration = LineFromTwoLengths.Calibrate(
            Solve(Strip(width, cell, z, shortLength / 2 + stub, 0, shortLength / 2, shortLength / 2)),
            Solve(Strip(width, cell, z, longLength / 2 + stub, 0, longLength / 2, longLength / 2)),
            shortLength, longLength, ReferenceOhms, beta, PortModel.ShuntAtTerminals);
        var corner = calibration.DeEmbed(Solve(Strip(width, cell, z, arm + stub, deflectionDegrees * Math.PI / 180, arm, arm)),
            ReferenceOhms, shiftMeters: arm);

        // B = Z(1 + ZY/4) is first order in the corner's series element, A − 1 = ZY/2 only second;
        // so Z comes from B (the root of (Y/4)Z² + Z − B = 0 next to B), and A and D measure the fit.
        Complex y = corner[1, 0], b = corner[0, 1];
        Complex zSeries = (y * b).Magnitude < 1e-12 ? b : 2 * (Complex.Sqrt(1 + b * y) - 1) / y;
        Complex aFit = 1 + zSeries * y / 2;
        double departure = Math.Max(Math.Max(b.Magnitude / calibration.CharacteristicOhms.Magnitude,
            y.Magnitude * calibration.CharacteristicOhms.Magnitude), 1e-12);
        double residual = Math.Max((corner[0, 0] - corner[1, 1]).Magnitude,
            (0.5 * (corner[0, 0] + corner[1, 1]) - aFit).Magnitude) / departure;
        return new CornerNetwork(zSeries.Imaginary / omega, y.Imaginary / omega, f, calibration.CharacteristicOhms.Real)
        {
            FitResidual = residual
        };
    }

    /// <summary>Cells across a strip (cosine-graded toward its edges, as the stripline check had them).</summary>
    private const int Across = 4;

    /// <summary>
    /// A strip of width w through a corner at the origin: arm 1 comes in along +x from −<paramref name="reach"/>,
    /// arm 2 leaves along (cos θ, sin θ) to <paramref name="reach"/>; gap ports <paramref name="port1"/> before
    /// the corner and <paramref name="port2"/> after it, each counting the current flowing into the strip
    /// between them. The arms are rows of cells <paramref name="cell"/> long laid out from the ports, so the
    /// line between a port and the corner is meshed exactly as the straight calibration lines are; they stop
    /// where the inner edges meet, a distance c = (w/2)·tan(θ/2) short of the corner point, and the copper
    /// left between them — the outer side an arc of radius w/2 about the corner point (two draws with round
    /// ends), the inner side the point where the edges cross — is fanned from its centroid. At θ = 0 the patch
    /// is empty and the strip is the straight one.
    /// </summary>
    private static (SurfaceStructure Structure, SurfacePort[] Ports) Strip(double width, double cell, double z,
        double reach, double theta, double port1, double port2)
    {
        double half = width / 2, c = half * Math.Tan(theta / 2);
        var d2 = new Point2(Math.Cos(theta), Math.Sin(theta));
        var n2 = new Point2(-d2.Y, d2.X);
        var across = Enumerable.Range(0, Across + 1).Select(j => -half * Math.Cos(Math.PI * j / Across)).ToArray();

        var vertices = new List<Vector3D>();
        var index = new Dictionary<(long, long), int>();
        int Vertex(Point2 p)
        {
            var key = ((long)Math.Round(p.X * 1e12), (long)Math.Round(p.Y * 1e12));
            if (index.TryGetValue(key, out int i)) return i;
            vertices.Add(new Vector3D(p.X, p.Y, z));
            return index[key] = vertices.Count - 1;
        }
        var triangles = new List<(int, int, int)>();
        void Triangle(int a, int b, int d)
        {
            var (pa, pb, pd) = (vertices[a], vertices[b], vertices[d]);
            double twice = (pb.X - pa.X) * (pd.Y - pa.Y) - (pb.Y - pa.Y) * (pd.X - pa.X);
            if (Math.Abs(twice) < 1e-6 * cell * cell) return;
            triangles.Add(twice > 0 ? (a, b, d) : (a, d, b));
        }
        // Stations along an arm, measured from the corner point: the port, whole cells out to the end, and
        // whole cells in toward the corner with the remainder (if any) in the last one.
        List<double> Stations(double port)
        {
            var s = new List<double>();
            for (double x = reach; x > port + 1e-9 * cell; x -= cell) s.Add(x);
            for (double x = port; x > c + 0.25 * cell; x -= cell) s.Add(x);
            s.Add(c);
            return s.Distinct().OrderByDescending(x => x).ToList();
        }
        void Arm(List<double> stations, Func<double, double, Point2> at)
        {
            for (int i = 0; i + 1 < stations.Count; i++)
                for (int j = 0; j < Across; j++)
                {
                    int a = Vertex(at(stations[i], across[j])), b = Vertex(at(stations[i + 1], across[j]));
                    int e = Vertex(at(stations[i], across[j + 1])), g = Vertex(at(stations[i + 1], across[j + 1]));
                    Triangle(a, b, e);
                    Triangle(b, g, e);
                }
        }
        // Arm 1 at distance s before the corner: (−s, v); arm 2 at distance s after it: s·d₂ + v·n₂.
        Arm(Stations(port1), (s, v) => new Point2(-s, v));
        Arm(Stations(port2), (s, v) => d2 * s + n2 * v);
        if (c > 0)
        {
            var ring = new List<Point2>();
            for (int j = Across; j >= 0; j--) ring.Add(new Point2(-c, across[j]));      // arm 1's end, inner to outer
            ring.Add(new Point2(0, -half));
            int arc = Math.Max(2, (int)Math.Ceiling(theta / (Math.PI / 12)));
            for (int k = 1; k < arc; k++)
            {
                double phi = -Math.PI / 2 + theta * k / arc;
                ring.Add(new Point2(half * Math.Cos(phi), half * Math.Sin(phi)));
            }
            ring.Add(n2 * -half);
            for (int j = 0; j < Across; j++) ring.Add(d2 * c + n2 * across[j]);        // arm 2's start, outer to inner
            var centroid = new Point2(ring.Average(p => p.X), ring.Average(p => p.Y));
            int middle = Vertex(centroid);
            for (int k = 0; k < ring.Count; k++)
                Triangle(middle, Vertex(ring[k]), Vertex(ring[(k + 1) % ring.Count]));
        }
        var structure = new SurfaceStructure(vertices, triangles, null);

        // Each port's current counts positive flowing INTO the strip between them.
        SurfacePort Gap(Point2 from, Point2 to, Point2 into)
        {
            int a = Vertex(from), b = Vertex(to);
            var bases = Enumerable.Range(0, structure.BasisCount).Where(e =>
            {
                var edge = structure.Edges[e];
                Point2 p = new(vertices[edge.V1].X, vertices[edge.V1].Y), q = new(vertices[edge.V2].X, vertices[edge.V2].Y);
                return OnSegment(p, from, to) && OnSegment(q, from, to);
            }).ToList();
            if (bases.Count != Across) throw new InvalidOperationException($"A corner port crosses {bases.Count} edges, not {Across}.");
            return new SurfacePort(bases, new Vector3D(into.X, into.Y, 0));
        }
        bool OnSegment(Point2 p, Point2 a, Point2 b)
        {
            var ab = b - a;
            double t = Point2.Dot(p - a, ab) / Point2.Dot(ab, ab);
            return t >= -1e-9 && t <= 1 + 1e-9 && (a + ab * t - p).Length <= 1e-9 * cell;
        }
        var ports = new[]
        {
            Gap(new Point2(-port1, -half), new Point2(-port1, half), new Point2(1, 0)),
            Gap(d2 * port2 - n2 * half, d2 * port2 + n2 * half, d2 * -1),
        };
        return (structure, ports);
    }
}
