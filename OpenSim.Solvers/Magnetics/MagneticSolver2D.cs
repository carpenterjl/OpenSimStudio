using System.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Core.Numerics;

namespace OpenSim.Solvers.Magnetics;

/// <summary>
/// 2D finite-element magnetics on a <see cref="MagneticModel2D"/>: magnetostatics (nonlinear B–H
/// by Newton's method) and time-harmonic eddy currents (linear materials), planar or axisymmetric.
///
/// <para>Unknown u is A_z (planar) or ψ = r·A_φ (axisymmetric; 2πψ is the flux through the circle
/// of radius r). With w = 1 (planar) or 1/r (axisymmetric, taken at each element's centroid as FEMM
/// does), the weak form is ∫ν·w·∇u·∇v + jω∫σ·w·u·v = ∫J·v. B = (∂u/∂y, −∂u/∂x) planar, (−∂ψ/∂z,
/// ∂ψ/∂r)/r axisymmetric.</para>
///
/// <para>A solid conductor carries J = σ·w·(g − jωu) with g one unknown per conductor (the applied
/// field per metre in a planar model, V/2π for a ring) fixed by its total current. The system is
/// factored once; one solve per conductor gives the response to its g, and a small dense system
/// imposes the currents. In a planar model, a conductive region that belongs to no conductor
/// carries no net current (it is an isolated bar); in an axisymmetric one it is a closed ring and
/// its induced current is free.</para>
/// </summary>
public sealed class MagneticSolver2D
{
    public static IReadOnlyList<string> Assumptions { get; } = new[]
    {
        "2D: planar (per metre of depth, current along z) or axisymmetric (current around the axis).",
        "Linear triangles on a graded rectilinear grid — or, when any region is a polygon, on a constrained Delaunay mesh conforming to every outline, seeded from the same grading; the axisymmetric 1/r weight is taken at each element's centroid.",
        "The vector potential is zero on the outer boundary except on sides declared as symmetry planes; the domain must be large enough that this does not matter.",
        "Time-harmonic solves give a B–H material an energy-equivalent effective permeability at each element's own amplitude (fundamental only; harmonics of B are not resolved); core loss is Steinmetz on the peak |B| of each element, or iGSE over a given flux waveform.",
        "Stranded windings carry uniform current density (no eddy currents in the field solve); with a strand diameter given, the strands' skin and proximity loss is reported from the local field (round strands, proximity term derived for d ≲ δ).",
        "Forces are the Maxwell stress tensor over a one-element shell of plain air around the named regions (time-average in a time-harmonic solve)."
    };

    public MagneticSolution2D SolveStatic(MagneticModel2D model, int maxIterations = 60, double tolerance = 1e-9)
    {
        var mesh = new Mesh(model);
        var log = new List<string> { mesh.Describe() };
        int n = mesh.NodeCount;
        // Sources: stranded J, and DC solid conductors with J ∝ σ·w (uniform in a bar, ∝ 1/r in a ring).
        var f = mesh.StrandedLoad();
        foreach (var c in model.Conductors)
        {
            var (b, s) = mesh.ConductorVector(c);
            for (int i = 0; i < n; i++) f[i] += c.Current.Real * b[i] / s;
        }

        var u = new double[n];
        bool nonlinear = mesh.Elements.Any(e => e.Material.IsNonlinear);
        int iterations = 0;
        double fNorm = Math.Sqrt(f.Sum(x => x * x));
        double lastResidual = double.MaxValue;
        for (; iterations < maxIterations; iterations++)
        {
            var (nu, dnu) = mesh.Reluctivities(u);
            var residual = mesh.Residual(u, nu, f);
            double rNorm = Math.Sqrt(residual.Sum(x => x * x));
            if (iterations > 0 && rNorm <= tolerance * Math.Max(fNorm, 1e-30)) { lastResidual = rNorm; break; }
            var solver = mesh.Assemble(nu, nonlinear ? dnu : null, nonlinear ? u : null, omega: 0);
            solver.Factor();
            var delta = solver.Solve(residual.Select(r => new Complex(-r, 0)).ToArray());
            if (!nonlinear)
            {
                for (int i = 0; i < n; i++) u[i] += delta[i].Real;
                iterations++;
                lastResidual = 0;
                break;
            }
            // Damped Newton: halve the step until the residual falls.
            double step = 1;
            double[] trial = new double[n];
            for (int halving = 0; halving < 8; halving++)
            {
                for (int i = 0; i < n; i++) trial[i] = u[i] + step * delta[i].Real;
                var (nuT, _) = mesh.Reluctivities(trial);
                double tNorm = Math.Sqrt(mesh.Residual(trial, nuT, f).Sum(x => x * x));
                if (tNorm < rNorm || halving == 7) break;
                step /= 2;
            }
            Array.Copy(trial, u, n);
            lastResidual = rNorm;
        }
        if (nonlinear)
        {
            log.Add($"Newton: {iterations} iteration(s), residual {lastResidual / Math.Max(fNorm, 1e-30):e2} of the load.");
            if (iterations >= maxIterations)
                log.Add($"WARNING: Newton did not reach {tolerance:e1} in {maxIterations} iterations.");
        }
        var values = u.Select(x => new Complex(x, 0)).ToArray();
        var gValues = model.Conductors.Select(c => Complex.Zero).ToArray();
        return new MagneticSolution2D(model, mesh, values, 0, gValues, log);
    }

    public MagneticSolution2D SolveTimeHarmonic(MagneticModel2D model, double frequencyHz, int maxIterations = 60, double tolerance = 1e-6)
    {
        if (!(frequencyHz > 0)) throw new ArgumentOutOfRangeException(nameof(frequencyHz));
        var mesh = new Mesh(model);
        var log = new List<string> { mesh.Describe() };
        double omega = 2 * Math.PI * frequencyHz;

        // Every conductor, plus (planar only) each passive conductive region as a conductor carrying
        // no net current.
        var conductors = model.Conductors.ToList();
        if (model.Geometry == MagneticGeometry.Planar)
        {
            var claimed = model.Conductors.SelectMany(c => c.Regions).Concat(model.Coils.SelectMany(c => c.Regions.Select(r => r.Region))).ToHashSet();
            foreach (var r in model.Regions)
                if (r.Material.Conductivity > 0 && !claimed.Contains(r.Name))
                    conductors.Add(new SolidConductor("(isolated) " + r.Name, new[] { r.Name }, Complex.Zero));
        }

        var fs = mesh.StrandedLoad();
        bool nonlinear = mesh.Elements.Any(e => e.Material.IsNonlinear);
        var nu = new double[mesh.Elements.Count];
        for (int k = 0; k < nu.Length; k++)
        {
            var material = mesh.Elements[k].Material;
            nu[k] = material.Curve is { } curve ? curve.Reluctivity(0).Nu
                : 1 / (MagneticConstants.Mu0 * material.RelativePermeability);
        }

        // B–H materials get an energy-equivalent effective reluctivity at each element's own
        // amplitude, by damped fixed-point iteration over the linearised solve. An H-driven core
        // (its B set by the ampere-turns, not a flux) makes the bare map ν ← ν_eff(B̂(ν)) steeper
        // than −1 on the knee, so the damping halves whenever the change grows instead of falling.
        Complex[] u;
        Complex[] g;
        var nuEff = nonlinear ? new double[nu.Length] : null;
        int iterations = 0;
        double damping = 0.5, previousWorst = double.MaxValue;
        while (true)
        {
            (u, g) = SolveLinearised(mesh, conductors, nu, fs, omega);
            iterations++;
            if (!nonlinear) break;
            double worst = 0;
            for (int k = 0; k < nu.Length; k++)
            {
                var e = mesh.Elements[k];
                if (e.Material.Curve is not { } curve) continue;
                nuEff![k] = curve.EffectiveReluctivity(Amplitude(e, u));
                worst = Math.Max(worst, Math.Abs(nuEff[k] - nu[k]) / nu[k]);
            }
            if (worst <= tolerance || iterations >= maxIterations)
            {
                log.Add($"Effective permeability (energy-equivalent, fundamental only): {iterations} iteration(s), largest ν change {worst:e2}.");
                if (worst > tolerance)
                    log.Add($"WARNING: the effective-permeability iteration did not reach {tolerance:e1} in {maxIterations} iterations.");
                break;
            }
            // Shrink the damping when the change grows, creep back up while it falls, hunting the
            // stable range of the map's local slope.
            damping = worst > previousWorst ? Math.Max(damping / 2, 0.02) : Math.Min(damping * 1.2, 0.5);
            previousWorst = worst;
            for (int k = 0; k < nu.Length; k++)
                if (mesh.Elements[k].Material.Curve is not null)
                    nu[k] += damping * (nuEff![k] - nu[k]);
        }

        var gModel = new Complex[model.Conductors.Count];
        Array.Copy(g, gModel, gModel.Length);
        return new MagneticSolution2D(model, mesh, u, frequencyHz, gModel, log, conductors, g, nonlinear ? nu : null);
    }

    /// <summary>One linear time-harmonic solve at the given per-element reluctivities.</summary>
    private static (Complex[] U, Complex[] G) SolveLinearised(Mesh mesh, List<SolidConductor> conductors,
        double[] nu, double[] fs, double omega)
    {
        int n = mesh.NodeCount;
        var solver = mesh.Assemble(nu, null, null, omega);
        solver.Factor();
        var aF = solver.Solve(fs.Select(x => new Complex(x, 0)).ToArray());
        int m = conductors.Count;
        var g = new Complex[m];
        var u = aF;
        if (m > 0)
        {
            var bVectors = new double[m][];
            var sValues = new double[m];
            var responses = new Complex[m][];
            for (int k = 0; k < m; k++)
            {
                (bVectors[k], sValues[k]) = mesh.ConductorVector(conductors[k]);
                responses[k] = solver.Solve(bVectors[k].Select(x => new Complex(x, 0)).ToArray());
            }
            // S_j g_j − jω b_jᵀ(a_f + Σ g_k a_k) = I_j.
            var system = new ComplexDenseMatrix(m, m);
            var rhs = new Complex[m];
            var jw = new Complex(0, omega);
            for (int j = 0; j < m; j++)
            {
                rhs[j] = conductors[j].Current + jw * Dot(bVectors[j], aF);
                for (int k = 0; k < m; k++)
                    system[j, k] = (j == k ? sValues[j] : 0) - jw * Dot(bVectors[j], responses[k]);
            }
            g = ComplexLu.Factor(system).Solve(rhs);
            u = new Complex[n];
            for (int i = 0; i < n; i++)
            {
                Complex v = aF[i];
                for (int k = 0; k < m; k++) v += g[k] * responses[k][i];
                u[i] = v;
            }
        }
        return (u, g);

        static Complex Dot(double[] b, Complex[] x)
        {
            Complex s = Complex.Zero;
            for (int i = 0; i < b.Length; i++) if (b[i] != 0) s += b[i] * x[i];
            return s;
        }
    }

    /// <summary>The peak |B| over a cycle of an element's (possibly elliptical) field.</summary>
    internal static double Amplitude(Mesh.Element e, Complex[] u)
    {
        Complex gx = e.Bx[0] * u[e.N0] + e.Bx[1] * u[e.N1] + e.Bx[2] * u[e.N2];
        Complex gy = e.By[0] * u[e.N0] + e.By[1] * u[e.N1] + e.By[2] * u[e.N2];
        double sum = gx.Magnitude * gx.Magnitude + gy.Magnitude * gy.Magnitude;
        return Math.Sqrt(0.5 * (sum + (gx * gx + gy * gy).Magnitude)) / e.Rho;
    }

    /// <summary>The mesh (graded rectilinear grid, or constrained Delaunay when any region is a
    /// polygon), its triangles and the per-element assembly data.</summary>
    internal sealed class Mesh
    {
        public readonly MagneticModel2D Model;
        public readonly double[] Xs, Ys;
        public readonly bool XFast;
        public readonly bool Unstructured;
        public readonly int NodeCount, HalfBandwidth;
        public readonly List<Element> Elements = new();
        public readonly bool[] Pinned;
        private readonly double[] _nodeX, _nodeY;
        private readonly Dictionary<string, int> _regionIndex;
        private readonly double[] _regionArea; // geometric area per region (meshed)

        public sealed class Element
        {
            public int N0, N1, N2;
            public double Area, Weight, Rho;      // Weight = ∫w dA; Rho = 1 planar, r_c axisymmetric
            public double[] Bx = new double[3], By = new double[3]; // ∂N/∂x, ∂N/∂y
            public int Region;                     // −1 = background
            public required MagneticMaterial Material;
            public double Cx, Cy;
            public double Sigma;                   // 0 inside stranded windings
        }

        public Mesh(MagneticModel2D model)
        {
            model.Validate();
            Model = model;
            Xs = model.GridLines(true);
            Ys = model.GridLines(false);
            _regionIndex = model.Regions.Select((r, i) => (r.Name, i)).ToDictionary(p => p.Name, p => p.i);
            _regionArea = new double[model.Regions.Count];
            bool axi = model.Geometry == MagneticGeometry.Axisymmetric;
            var coilRegions = model.Coils.SelectMany(c => c.Regions.Select(r => _regionIndex[r.Region])).ToHashSet();
            Unstructured = model.Regions.Any(r => r.Shape is not null);
            int nx = Xs.Length, ny = Ys.Length;

            if (Unstructured)
            {
                var (nodes, triangles) = TriangulateUnstructured(model, Xs, Ys);
                XFast = true;
                NodeCount = nodes.Count;
                HalfBandwidth = 0;
                _nodeX = new double[NodeCount];
                _nodeY = new double[NodeCount];
                for (int i = 0; i < NodeCount; i++) { _nodeX[i] = nodes[i].X; _nodeY[i] = nodes[i].Y; }
                foreach (var (a, b, c) in triangles)
                {
                    double cx = (_nodeX[a] + _nodeX[b] + _nodeX[c]) / 3;
                    double cy = (_nodeY[a] + _nodeY[b] + _nodeY[c]) / 3;
                    AddElement(a, b, c, model.RegionAt(cx, cy), axi, coilRegions);
                }
                Pinned = new bool[NodeCount];
                double hMin = Math.Min(model.MinElement ?? model.MaxElement, model.MaxElement);
                double diag = Math.Sqrt(Math.Pow(model.DomainX1 - model.DomainX0, 2) + Math.Pow(model.DomainY1 - model.DomainY0, 2));
                double tol = Math.Min(2e-6 * diag, 0.3 * hMin);
                for (int n = 0; n < NodeCount; n++)
                {
                    bool left = Math.Abs(_nodeX[n] - model.DomainX0) <= tol;
                    bool right = Math.Abs(_nodeX[n] - model.DomainX1) <= tol;
                    bool bottom = Math.Abs(_nodeY[n] - model.DomainY0) <= tol;
                    bool top = Math.Abs(_nodeY[n] - model.DomainY1) <= tol;
                    Pinned[n] = (left && (!model.NaturalSides.HasFlag(DomainSides.Left) || (axi && model.DomainX0 == 0)))
                        || (right && !model.NaturalSides.HasFlag(DomainSides.Right))
                        || (bottom && !model.NaturalSides.HasFlag(DomainSides.Bottom))
                        || (top && !model.NaturalSides.HasFlag(DomainSides.Top));
                }
                return;
            }

            XFast = nx <= ny;
            NodeCount = nx * ny;
            HalfBandwidth = (XFast ? nx : ny) + 1;
            _nodeX = new double[NodeCount];
            _nodeY = new double[NodeCount];
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < ny; j++)
                {
                    int node = Node(i, j);
                    _nodeX[node] = Xs[i];
                    _nodeY[node] = Ys[j];
                }
            for (int i = 0; i + 1 < nx; i++)
                for (int j = 0; j + 1 < ny; j++)
                {
                    double cx = 0.5 * (Xs[i] + Xs[i + 1]), cy = 0.5 * (Ys[j] + Ys[j + 1]);
                    int region = model.RegionAt(cx, cy);
                    int p00 = Node(i, j), p10 = Node(i + 1, j), p11 = Node(i + 1, j + 1), p01 = Node(i, j + 1);
                    var tris = (i + j) % 2 == 0
                        ? new[] { (p00, p10, p11), (p00, p11, p01) }
                        : new[] { (p00, p10, p01), (p10, p11, p01) };
                    foreach (var (a, b, c) in tris)
                        AddElement(a, b, c, region, axi, coilRegions);
                }

            Pinned = new bool[NodeCount];
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < ny; j++)
                {
                    bool left = i == 0, right = i == nx - 1, bottom = j == 0, top = j == ny - 1;
                    bool pin = (left && (!model.NaturalSides.HasFlag(DomainSides.Left) || (axi && Xs[0] == 0)))
                        || (right && !model.NaturalSides.HasFlag(DomainSides.Right))
                        || (bottom && !model.NaturalSides.HasFlag(DomainSides.Bottom))
                        || (top && !model.NaturalSides.HasFlag(DomainSides.Top));
                    Pinned[Node(i, j)] = pin;
                }
        }

        private void AddElement(int a, int b, int c, int region, bool axi, HashSet<int> coilRegions)
        {
            var material = region >= 0 ? Model.Regions[region].Material : Model.Background;
            var e = new Element
            {
                N0 = a, N1 = b, N2 = c, Region = region, Material = material,
                Sigma = coilRegions.Contains(region) ? 0 : material.Conductivity
            };
            double xa = _nodeX[a], ya = _nodeY[a];
            double xb = _nodeX[b], yb = _nodeY[b];
            double xc = _nodeX[c], yc = _nodeY[c];
            double twoA = (xb - xa) * (yc - ya) - (xc - xa) * (yb - ya);
            if (twoA == 0) throw new InvalidOperationException("The mesh produced a degenerate triangle.");
            e.Area = Math.Abs(twoA) / 2;
            e.Bx[0] = (yb - yc) / twoA; e.Bx[1] = (yc - ya) / twoA; e.Bx[2] = (ya - yb) / twoA;
            e.By[0] = (xc - xb) / twoA; e.By[1] = (xa - xc) / twoA; e.By[2] = (xb - xa) / twoA;
            e.Cx = (xa + xb + xc) / 3;
            e.Cy = (ya + yb + yc) / 3;
            e.Rho = axi ? e.Cx : 1;
            e.Weight = axi ? e.Area / e.Cx : e.Area;
            Elements.Add(e);
            if (region >= 0) _regionArea[region] += e.Area;
        }

        /// <summary>
        /// The constrained Delaunay mesh of a model with polygonal regions: every region outline
        /// (rectangles at their grid-line crossings, polygon rings subdivided at the local graded
        /// spacing) and the domain boundary are constraint polylines; the seed points are the
        /// graded grid's interior crossings, except within half a step of a polygon edge. Input
        /// points are snapped back to their exact coordinates after triangulation (the CDT jitters
        /// them by far less than an element).
        /// </summary>
        private static (List<Point2> Nodes, List<(int A, int B, int C)> Triangles)
            TriangulateUnstructured(MagneticModel2D model, double[] xs, double[] ys)
        {
            double spanX = model.DomainX1 - model.DomainX0, spanY = model.DomainY1 - model.DomainY0;
            double diag = Math.Sqrt(spanX * spanX + spanY * spanY);
            double tol = 1e-9 * diag;
            double hMin = Math.Min(model.MinElement ?? model.MaxElement, model.MaxElement);

            var basePoints = new List<Point2>();
            var pointIndex = new Dictionary<(long, long), int>();
            int AddPoint(double x, double y)
            {
                var key = ((long)Math.Round(x / tol), (long)Math.Round(y / tol));
                if (pointIndex.TryGetValue(key, out int k)) return k;
                pointIndex[key] = basePoints.Count;
                basePoints.Add(new Point2(x, y));
                return basePoints.Count - 1;
            }

            var constraints = new List<(int, int)>();
            void Chain(IReadOnlyList<int> idx)
            {
                for (int k = 0; k < idx.Count; k++)
                {
                    int u = idx[k], v = idx[(k + 1) % idx.Count];
                    if (u != v) constraints.Add((u, v));
                }
            }

            double LocalSpacing(double x, double y)
            {
                int i = Array.BinarySearch(xs, x);
                if (i < 0) i = ~i - 1;
                i = Math.Clamp(i, 0, xs.Length - 2);
                int j = Array.BinarySearch(ys, y);
                if (j < 0) j = ~j - 1;
                j = Math.Clamp(j, 0, ys.Length - 2);
                return Math.Clamp(Math.Min(xs[i + 1] - xs[i], ys[j + 1] - ys[j]), hMin, model.MaxElement);
            }

            // Domain boundary, at the graded grid's own crossings.
            var boundary = new List<int>();
            foreach (double x in xs) boundary.Add(AddPoint(x, model.DomainY0));
            for (int j = 1; j < ys.Length; j++) boundary.Add(AddPoint(model.DomainX1, ys[j]));
            for (int i = xs.Length - 2; i >= 0; i--) boundary.Add(AddPoint(xs[i], model.DomainY1));
            for (int j = ys.Length - 2; j >= 1; j--) boundary.Add(AddPoint(model.DomainX0, ys[j]));
            Chain(boundary);

            // Region outlines. Rectangles subdivide at the grid lines crossing their edges;
            // polygon rings at the local graded spacing (their vertices are grid lines, so the
            // spacing is MinElement near every vertex).
            var polygonEdges = new List<(Point2 A, Point2 B)>();
            foreach (var region in model.Regions)
            {
                if (region.Shape is { } shape)
                {
                    foreach (var ring in Polygon2.OrientedRings(shape))
                    {
                        var idx = new List<int>();
                        for (int k = 0; k < ring.Count; k++)
                        {
                            var p0 = ring[k];
                            var p1 = ring[(k + 1) % ring.Count];
                            polygonEdges.Add((p0, p1));
                            idx.Add(AddPoint(p0.X, p0.Y));
                            var d = p1 - p0;
                            double length = d.Length;
                            if (length <= 0) continue;
                            if (Math.Abs(d.Y) <= tol || Math.Abs(d.X) <= tol)
                            {
                                // Axis-aligned: subdivide at the grid lines crossing it, exactly as
                                // a rectangle's edge, so a shared edge gets the same points.
                                bool alongX = Math.Abs(d.Y) <= tol;
                                var lines = alongX ? xs : ys;
                                double a0 = alongX ? p0.X : p0.Y, a1 = alongX ? p1.X : p1.Y;
                                var inside = lines.Where(v => v > Math.Min(a0, a1) + tol && v < Math.Max(a0, a1) - tol);
                                foreach (double v in a0 < a1 ? inside : inside.Reverse())
                                    idx.Add(alongX ? AddPoint(v, p0.Y) : AddPoint(p0.X, v));
                                continue;
                            }
                            double at = 0;
                            while (true)
                            {
                                double h = LocalSpacing(p0.X + d.X * at / length, p0.Y + d.Y * at / length);
                                if (at + 1.5 * h >= length) break;
                                at += h;
                                idx.Add(AddPoint(p0.X + d.X * at / length, p0.Y + d.Y * at / length));
                            }
                        }
                        Chain(idx);
                    }
                }
                else
                {
                    var onX = xs.Where(x => x >= region.X0 - tol && x <= region.X1 + tol).ToList();
                    var onY = ys.Where(y => y >= region.Y0 - tol && y <= region.Y1 + tol).ToList();
                    var idx = new List<int>();
                    foreach (double x in onX) idx.Add(AddPoint(x, region.Y0));
                    foreach (double y in onY.Skip(1)) idx.Add(AddPoint(region.X1, y));
                    for (int i = onX.Count - 2; i >= 0; i--) idx.Add(AddPoint(onX[i], region.Y1));
                    for (int j = onY.Count - 2; j >= 1; j--) idx.Add(AddPoint(region.X0, onY[j]));
                    Chain(idx);
                }
            }

            // Interior seeds from the graded grid, kept clear of polygon edges.
            double DistanceToEdge(Point2 p, Point2 a, Point2 b)
            {
                var d = b - a;
                double dd = Point2.Dot(d, d);
                double t = dd > 0 ? Math.Clamp(Point2.Dot(p - a, d) / dd, 0, 1) : 0;
                return (p - (a + d * t)).Length;
            }
            for (int i = 1; i + 1 < xs.Length; i++)
                for (int j = 1; j + 1 < ys.Length; j++)
                {
                    var p = new Point2(xs[i], ys[j]);
                    double clearance = 0.45 * LocalSpacing(p.X, p.Y);
                    bool near = false;
                    foreach (var (a, b) in polygonEdges)
                    {
                        if (p.X < Math.Min(a.X, b.X) - clearance || p.X > Math.Max(a.X, b.X) + clearance
                            || p.Y < Math.Min(a.Y, b.Y) - clearance || p.Y > Math.Max(a.Y, b.Y) + clearance) continue;
                        if (DistanceToEdge(p, a, b) < clearance) { near = true; break; }
                    }
                    if (!near) AddPoint(p.X, p.Y);
                }

            var cdt = new Cdt2D();
            cdt.Triangulate(basePoints, constraints);

            // Compact to the nodes the triangles actually use; snap input points back to their
            // exact coordinates (recovery-split extras keep theirs). A triangle of three collinear
            // constraint points (the CDT's jitter bends a straight chain, so a sliver forms along
            // the hull) snaps flat — it covers no area and is dropped.
            var newIndex = new int[cdt.Points.Count];
            Array.Fill(newIndex, -1);
            var nodes = new List<Point2>();
            var triangles = new List<(int, int, int)>();
            Point2 Snapped(int old) => old < basePoints.Count ? basePoints[old] : cdt.Points[old];
            int Map(int old)
            {
                if (newIndex[old] >= 0) return newIndex[old];
                newIndex[old] = nodes.Count;
                nodes.Add(Snapped(old));
                return newIndex[old];
            }
            foreach (var (a, b, c) in cdt.Triangles())
            {
                var pa = Snapped(a);
                var pb = Snapped(b);
                var pc = Snapped(c);
                double twoA = Point2.Cross(pb - pa, pc - pa);
                double scale = Math.Max(Point2.Dot(pb - pa, pb - pa),
                    Math.Max(Point2.Dot(pc - pa, pc - pa), Point2.Dot(pc - pb, pc - pb)));
                if (Math.Abs(twoA) <= 1e-12 * scale) continue;
                triangles.Add((Map(a), Map(b), Map(c)));
            }
            return (nodes, triangles);
        }

        public int Node(int i, int j) => XFast ? i + Xs.Length * j : j + Ys.Length * i;

        public (double X, double Y) Coord(int node) => (_nodeX[node], _nodeY[node]);

        /// <summary>Node positions, for the sparse solver's elimination order.</summary>
        public IReadOnlyList<Point2> NodePositions()
        {
            var p = new Point2[NodeCount];
            for (int i = 0; i < NodeCount; i++) p[i] = new Point2(_nodeX[i], _nodeY[i]);
            return p;
        }

        public string Describe() => Unstructured
            ? $"Unstructured mesh (constrained Delaunay): {NodeCount} nodes, {Elements.Count} triangles."
            : $"Grid {Xs.Length} × {Ys.Length} lines, {NodeCount} nodes, {Elements.Count} triangles.";

        public int[] Nodes(Element e) => new[] { e.N0, e.N1, e.N2 };

        /// <summary>|B| of an element from nodal values.</summary>
        public double FluxDensity(Element e, IReadOnlyList<double> u)
        {
            double gx = e.Bx[0] * u[e.N0] + e.Bx[1] * u[e.N1] + e.Bx[2] * u[e.N2];
            double gy = e.By[0] * u[e.N0] + e.By[1] * u[e.N1] + e.By[2] * u[e.N2];
            return Math.Sqrt(gx * gx + gy * gy) / e.Rho;
        }

        public (double[] Nu, double[] DNu) Reluctivities(IReadOnlyList<double> u)
        {
            var nu = new double[Elements.Count];
            var dnu = new double[Elements.Count];
            for (int k = 0; k < Elements.Count; k++)
            {
                var e = Elements[k];
                if (e.Material.Curve is { } curve)
                    (nu[k], dnu[k]) = curve.Reluctivity(FluxDensity(e, u));
                else
                    nu[k] = 1 / (MagneticConstants.Mu0 * e.Material.RelativePermeability);
            }
            return (nu, dnu);
        }

        /// <summary>K(ν)·u − f, with pinned rows zero.</summary>
        public double[] Residual(IReadOnlyList<double> u, double[] nu, double[] f)
        {
            var r = new double[NodeCount];
            for (int k = 0; k < Elements.Count; k++)
            {
                var e = Elements[k];
                var nodes = Nodes(e);
                for (int a = 0; a < 3; a++)
                {
                    double sum = 0;
                    for (int b = 0; b < 3; b++)
                        sum += (e.Bx[a] * e.Bx[b] + e.By[a] * e.By[b]) * u[nodes[b]];
                    r[nodes[a]] += nu[k] * e.Weight * sum;
                }
            }
            for (int i = 0; i < NodeCount; i++) r[i] = Pinned[i] ? 0 : r[i] - f[i];
            return r;
        }

        /// <summary>K(ν) + jωM, plus the Newton term when <paramref name="dnu"/> is given.</summary>
        public ILinearSolver Assemble(double[] nu, double[]? dnu, IReadOnlyList<double>? u, double omega)
        {
            var s = Unstructured
                ? (ILinearSolver)new SparseLdlSolver(NodePositions())
                : new BandedSolver(NodeCount, HalfBandwidth);
            var h = new double[3, 3];
            var hu = new double[3];
            for (int k = 0; k < Elements.Count; k++)
            {
                var e = Elements[k];
                var nodes = Nodes(e);
                for (int a = 0; a < 3; a++)
                    for (int b = 0; b < 3; b++)
                        h[a, b] = e.Weight * (e.Bx[a] * e.Bx[b] + e.By[a] * e.By[b]);
                double sigma = e.Sigma;
                if (dnu is not null && u is not null && dnu[k] != 0)
                    for (int a = 0; a < 3; a++)
                    {
                        hu[a] = 0;
                        for (int b = 0; b < 3; b++) hu[a] += h[a, b] * u[nodes[b]];
                    }
                for (int a = 0; a < 3; a++)
                    for (int b = 0; b < 3; b++)
                    {
                        double re = nu[k] * h[a, b];
                        if (dnu is not null && u is not null && dnu[k] != 0)
                            re += 2 * dnu[k] / (e.Rho * e.Rho * e.Weight) * hu[a] * hu[b];
                        double im = omega > 0 && sigma > 0 ? omega * sigma * e.Weight * (a == b ? 2 : 1) / 12 : 0;
                        s.Add(nodes[a], nodes[b], re, im);
                    }
            }
            for (int i = 0; i < NodeCount; i++) if (Pinned[i]) s.Pin(i);
            return s;
        }

        /// <summary>∫J·N_i for the stranded windings.</summary>
        public double[] StrandedLoad()
        {
            var f = new double[NodeCount];
            var density = new Dictionary<int, double>();
            foreach (var coil in Model.Coils)
            {
                double plus = coil.Regions.Where(r => r.Direction > 0).Sum(r => _regionArea[_regionIndex[r.Region]]);
                double minus = coil.Regions.Where(r => r.Direction < 0).Sum(r => _regionArea[_regionIndex[r.Region]]);
                foreach (var (name, dir) in coil.Regions)
                {
                    double area = dir > 0 ? plus : minus;
                    density[_regionIndex[name]] = dir * coil.Turns * coil.Current / area;
                }
            }
            foreach (var e in Elements)
            {
                if (e.Region < 0 || !density.TryGetValue(e.Region, out double j)) continue;
                f[e.N0] += j * e.Area / 3;
                f[e.N1] += j * e.Area / 3;
                f[e.N2] += j * e.Area / 3;
            }
            for (int i = 0; i < NodeCount; i++) if (Pinned[i]) f[i] = 0;
            return f;
        }

        /// <summary>b_i = ∫σ·w·N_i over the conductor, and S = ∫σ·w.</summary>
        public (double[] B, double S) ConductorVector(SolidConductor conductor)
        {
            var regions = conductor.Regions.Select(r => _regionIndex[r]).ToHashSet();
            var b = new double[NodeCount];
            double s = 0;
            foreach (var e in Elements)
            {
                if (!regions.Contains(e.Region)) continue;
                double v = e.Material.Conductivity * e.Weight;
                s += v;
                b[e.N0] += v / 3;
                b[e.N1] += v / 3;
                b[e.N2] += v / 3;
            }
            for (int i = 0; i < NodeCount; i++) if (Pinned[i]) b[i] = 0;
            if (!(s > 0)) throw new InvalidOperationException($"Conductor '{conductor.Name}' covers no elements.");
            return (b, s);
        }

        public int RegionIndex(string name) => _regionIndex[name];
        public double RegionArea(string name) => _regionArea[_regionIndex[name]];
    }
}

/// <summary>The solved field and what a designer reads from it.</summary>
public sealed class MagneticSolution2D
{
    private readonly MagneticSolver2D.Mesh _mesh;
    private readonly Complex[] _u;
    private readonly List<SolidConductor> _allConductors;
    private readonly Complex[] _allG;
    private readonly double[]? _acNu;

    internal MagneticSolution2D(MagneticModel2D model, MagneticSolver2D.Mesh mesh, Complex[] u, double frequencyHz,
        Complex[] conductorG, List<string> log, List<SolidConductor>? allConductors = null, Complex[]? allG = null,
        double[]? acNu = null)
    {
        _acNu = acNu;
        Model = model;
        _mesh = mesh;
        _u = u;
        FrequencyHz = frequencyHz;
        Log = log;
        _allConductors = allConductors ?? model.Conductors.ToList();
        _allG = allG ?? conductorG;
    }

    public MagneticModel2D Model { get; }
    public double FrequencyHz { get; }
    public IReadOnlyList<string> Log { get; }
    public int NodeCount => _mesh.NodeCount;
    public int ElementCount => _mesh.Elements.Count;

    private double Depth(MagneticSolver2D.Mesh.Element e) =>
        Model.Geometry == MagneticGeometry.Axisymmetric ? 2 * Math.PI * e.Cx : 1;

    private Complex Gradient(MagneticSolver2D.Mesh.Element e, bool x)
    {
        var w = x ? e.Bx : e.By;
        return w[0] * _u[e.N0] + w[1] * _u[e.N1] + w[2] * _u[e.N2];
    }

    /// <summary>B at an element (complex amplitudes; real for a static solve).</summary>
    private (Complex Bx, Complex By) FluxDensity(MagneticSolver2D.Mesh.Element e)
    {
        var gx = Gradient(e, true);
        var gy = Gradient(e, false);
        // Planar: B = (∂A/∂y, −∂A/∂x). Axisymmetric: B = (−∂ψ/∂z, ∂ψ/∂r)/r.
        return Model.Geometry == MagneticGeometry.Planar
            ? (gy, -gx)
            : (-gy / e.Rho, gx / e.Rho);
    }

    /// <summary>The peak |B| over a cycle of an element's (possibly elliptical) field.</summary>
    private double PeakB(MagneticSolver2D.Mesh.Element e)
    {
        var (bx, by) = FluxDensity(e);
        double sum = bx.Magnitude * bx.Magnitude + by.Magnitude * by.Magnitude;
        return Math.Sqrt(0.5 * (sum + (bx * bx + by * by).Magnitude));
    }

    /// <summary>B (complex amplitudes) at a point, from the element containing it.</summary>
    public (Complex Bx, Complex By) FluxDensityAt(double x, double y)
    {
        var e = _mesh.Elements.MinBy(el => (el.Cx - x) * (el.Cx - x) + (el.Cy - y) * (el.Cy - y))!;
        return FluxDensity(e);
    }

    /// <summary>The largest peak |B| in a region, or anywhere when <paramref name="region"/> is null.</summary>
    public double MaxFluxDensity(string? region = null)
    {
        int index = region is null ? int.MinValue : _mesh.RegionIndex(region);
        return _mesh.Elements.Where(e => index == int.MinValue || e.Region == index).Select(PeakB).DefaultIfEmpty(0).Max();
    }

    /// <summary>Flux linkage of a stranded coil [Wb; Wb/m planar]: N/A·∫u over its regions, signed
    /// by direction (2π·ψ for a ring).</summary>
    public Complex FluxLinkage(string coilName)
    {
        var coil = Model.Coils.FirstOrDefault(c => c.Name == coilName) ?? throw new ArgumentException($"No coil '{coilName}'.");
        double plus = coil.Regions.Where(r => r.Direction > 0).Sum(r => _mesh.RegionArea(r.Region));
        double minus = coil.Regions.Where(r => r.Direction < 0).Sum(r => _mesh.RegionArea(r.Region));
        Complex total = Complex.Zero;
        foreach (var (name, dir) in coil.Regions)
        {
            int index = _mesh.RegionIndex(name);
            double area = dir > 0 ? plus : minus;
            foreach (var e in _mesh.Elements.Where(el => el.Region == index))
                total += dir * coil.Turns / area * e.Area * (_u[e.N0] + _u[e.N1] + _u[e.N2]) / 3;
        }
        return Model.Geometry == MagneticGeometry.Axisymmetric ? 2 * Math.PI * total : total;
    }

    /// <summary>L = λ/I of a coil [H; H/m planar] — the coil's own current and every other source
    /// acting, so with one source it is the self-inductance.</summary>
    public Complex Inductance(string coilName)
    {
        var coil = Model.Coils.First(c => c.Name == coilName);
        return FluxLinkage(coilName) / coil.Current;
    }

    /// <summary>Voltage of a solid conductor in a time-harmonic solve [V; V/m planar] — defined
    /// for a passive conductor (zero imposed current) too, where it is the open-circuit voltage,
    /// so a transformer's open secondary reads its induced voltage here.</summary>
    public Complex ConductorVoltage(string name)
    {
        int k = _allConductors.FindIndex(c => c.Name == name);
        if (k < 0) throw new ArgumentException($"No conductor '{name}'.");
        if (FrequencyHz == 0) throw new InvalidOperationException("A static solve has no conductor voltage.");
        var g = _allG[k];
        return Model.Geometry == MagneticGeometry.Axisymmetric ? 2 * Math.PI * g : g;
    }

    /// <summary>Impedance V/I of a solid conductor [Ω; Ω/m planar] — its resistance with skin and
    /// proximity effect, and its inductance, in a time-harmonic solve.</summary>
    public Complex ConductorImpedance(string name)
    {
        var current = (_allConductors.FirstOrDefault(c => c.Name == name)
            ?? throw new ArgumentException($"No conductor '{name}'.")).Current;
        return ConductorVoltage(name) / current;
    }

    /// <summary>Current density at an element's three nodes.</summary>
    private Complex[] CurrentDensity(MagneticSolver2D.Mesh.Element e)
    {
        var j = new Complex[3];
        double sigma = e.Sigma;
        if (FrequencyHz > 0 && sigma > 0)
        {
            int k = _allConductors.FindIndex(c => c.Regions.Any(r => _mesh.RegionIndex(r) == e.Region));
            Complex g = k >= 0 ? _allG[k] : Complex.Zero;
            var jw = new Complex(0, 2 * Math.PI * FrequencyHz);
            double w = Model.Geometry == MagneticGeometry.Axisymmetric ? 1 / e.Rho : 1;
            int[] nodes = { e.N0, e.N1, e.N2 };
            for (int a = 0; a < 3; a++) j[a] = sigma * w * (g - jw * _u[nodes[a]]);
        }
        return j;
    }

    /// <summary>Time-average ohmic loss ½∫|J|²/σ dV [W; W/m planar] in the named regions (all
    /// conductive regions when null), eddy and conductor currents (not the stranded windings').</summary>
    public double OhmicLoss(IEnumerable<string>? regions = null)
    {
        var set = regions?.Select(_mesh.RegionIndex).ToHashSet();
        double total = 0;
        foreach (var e in _mesh.Elements)
        {
            if (e.Region < 0 || !(e.Sigma > 0)) continue;
            if (set is not null && !set.Contains(e.Region)) continue;
            var j = CurrentDensity(e);
            double sum = 0;
            for (int a = 0; a < 3; a++)
                for (int b = 0; b < 3; b++)
                    sum += (a == b ? 2 : 1) * (j[a] * Complex.Conjugate(j[b])).Real;
            total += 0.5 / e.Sigma * e.Area / 12 * sum * Depth(e);
        }
        return total;
    }

    /// <summary>Steinmetz core loss [W; W/m planar] of every element whose material has
    /// coefficients, at <paramref name="frequencyHz"/> (the solve's own for a time-harmonic one;
    /// required for a static solve, whose B is taken as the peak).</summary>
    public double CoreLoss(double? frequencyHz = null)
    {
        double f = frequencyHz ?? FrequencyHz;
        if (!(f > 0)) throw new ArgumentException("Core loss needs a frequency.");
        double total = 0;
        foreach (var e in _mesh.Elements)
            if (e.Material.Steinmetz is { } law)
                total += law.LossDensity(f, PeakB(e)) * e.Area * Depth(e);
        return total;
    }

    /// <summary>iGSE core loss [W; W/m planar] for a non-sinusoidal periodic flux: each element's
    /// B(t) is <paramref name="waveform"/> (one period of (phase ∈ [0, 1), relative value),
    /// piecewise linear) scaled to the element's own peak |B| from the solve, priced by
    /// <see cref="SteinmetzCoefficients.IgseLossDensity"/>. Equals <see cref="CoreLoss"/> for a
    /// sinusoidal waveform; minor loops are not separated out.</summary>
    public double CoreLossIgse(IReadOnlyList<(double Phase, double Value)> waveform, double? frequencyHz = null)
    {
        double f = frequencyHz ?? FrequencyHz;
        if (!(f > 0)) throw new ArgumentException("Core loss needs a frequency.");
        double total = 0;
        foreach (var e in _mesh.Elements)
            if (e.Material.Steinmetz is { } law)
                total += law.IgseLossDensity(f, PeakB(e), waveform) * e.Area * Depth(e);
        return total;
    }

    /// <summary>Strand-level copper loss of a stranded winding in a time-harmonic solve,
    /// homogenised from the local field: proximity loss (σ·ω²·d²/32)·(|B_x|² + |B_y|²) per unit
    /// copper volume (round strands, derived for d ≲ δ) over the winding's regions, plus the
    /// transport loss ½|I|²·R_dc with the exact round-wire skin factor on each strand's share.
    /// Needs <see cref="StrandedCoil.StrandDiameter"/>; watts (planar: per metre). A passive
    /// winding reports its proximity loss alone.</summary>
    public StrandedWindingLoss StrandLoss(string coilName)
    {
        var coil = Model.Coils.FirstOrDefault(c => c.Name == coilName) ?? throw new ArgumentException($"No coil '{coilName}'.");
        if (!(coil.StrandDiameter > 0)) throw new InvalidOperationException($"Coil '{coilName}' has no strand diameter.");
        if (FrequencyHz == 0) throw new InvalidOperationException("Strand loss needs a time-harmonic solve (at DC the loss is I²·R_dc).");
        double omega = 2 * Math.PI * FrequencyHz;
        double sigma = coil.StrandConductivity;
        double d = coil.StrandDiameter;
        double strandArea = Math.PI * d * d / 4;
        double plus = coil.Regions.Where(r => r.Direction > 0).Sum(r => _mesh.RegionArea(r.Region));
        double minus = coil.Regions.Where(r => r.Direction < 0).Sum(r => _mesh.RegionArea(r.Region));
        var notes = new List<string>();
        double skin = RoundWire.SkinResistanceFactor(d, sigma, FrequencyHz);
        double delta = 1 / Math.Sqrt(Math.PI * FrequencyHz * MagneticConstants.Mu0 * sigma);
        if (d > delta)
            notes.Add($"Strand diameter {d * 1e3:g3} mm exceeds the skin depth {delta * 1e3:g3} mm: the proximity term (derived for d ≲ δ) underestimates.");
        double rdc = 0, proximity = 0;
        foreach (var (name, dir) in coil.Regions)
        {
            int index = _mesh.RegionIndex(name);
            double signArea = dir > 0 ? plus : minus;
            double fill = coil.Turns * coil.StrandsPerTurn * strandArea / signArea;
            if (fill >= 1)
                throw new InvalidOperationException($"Coil '{coilName}': the strands do not fit in region '{name}' (fill {fill:g3}).");
            foreach (var e in _mesh.Elements)
            {
                if (e.Region != index) continue;
                double vol = e.Area * Depth(e);
                rdc += coil.Turns * coil.Turns / (signArea * signArea) / (sigma * fill) * vol;
                var (bx, by) = FluxDensity(e);
                proximity += sigma * omega * omega * d * d / 32
                    * (bx.Magnitude * bx.Magnitude + by.Magnitude * by.Magnitude) * fill * vol;
            }
        }
        double transport = 0.5 * coil.Current * coil.Current * rdc * skin;
        double? rac = coil.Current != 0 ? rdc * skin + 2 * proximity / (coil.Current * coil.Current) : null;
        return new StrandedWindingLoss(transport, proximity, rdc, skin, rac, notes);
    }

    /// <summary>Magnetic force on the named region(s) by the Maxwell stress tensor, integrated
    /// over a shell of air around them (the weighted-stress form, the weight falling from 1 to 0
    /// over <paramref name="shellRings"/> rings of elements — reading the stress several elements
    /// out, where the linear elements' field error is smaller than right at the body): every
    /// element of the shell must be plain air — non-magnetic (µr = 1, no B–H curve),
    /// non-conducting and carrying no winding — so another body closer than the shell needs a
    /// smaller <paramref name="shellRings"/>. Planar: (Fx, Fy) in N per metre of depth.
    /// Axisymmetric: (0, Fz) in N — the radial resultant is zero by symmetry. Static solves give
    /// the force, time-harmonic ones the time average. A region touching the domain boundary
    /// relies on that side being a symmetry plane for the stress the missing shell would
    /// carry.</summary>
    public (double Fx, double Fy) Force(params string[] regions) => Force(4, regions);

    /// <summary>See <see cref="Force(string[])"/>.</summary>
    public (double Fx, double Fy) Force(int shellRings, params string[] regions)
    {
        if (regions.Length == 0) throw new ArgumentException("Name at least one region.", nameof(regions));
        if (shellRings < 1) throw new ArgumentOutOfRangeException(nameof(shellRings));
        var set = regions.Select(_mesh.RegionIndex).ToHashSet();
        // Hop distance from the regions' nodes, level by level over the elements.
        var distance = new int[_mesh.NodeCount];
        Array.Fill(distance, int.MaxValue);
        foreach (var e in _mesh.Elements)
            if (set.Contains(e.Region)) { distance[e.N0] = 0; distance[e.N1] = 0; distance[e.N2] = 0; }
        for (int level = 1; level <= shellRings; level++)
            foreach (var e in _mesh.Elements)
            {
                if (Math.Min(distance[e.N0], Math.Min(distance[e.N1], distance[e.N2])) != level - 1) continue;
                if (distance[e.N0] > level) distance[e.N0] = level;
                if (distance[e.N1] > level) distance[e.N1] = level;
                if (distance[e.N2] > level) distance[e.N2] = level;
            }
        var w = new double[_mesh.NodeCount];
        for (int i = 0; i < w.Length; i++)
            w[i] = distance[i] >= shellRings ? 0 : 1 - (double)distance[i] / shellRings;
        var coilRegions = Model.Coils.SelectMany(c => c.Regions).Select(r => _mesh.RegionIndex(r.Region)).ToHashSet();
        bool ac = FrequencyHz > 0;
        double fx = 0, fy = 0;
        foreach (var e in _mesh.Elements)
        {
            double wx = e.Bx[0] * w[e.N0] + e.Bx[1] * w[e.N1] + e.Bx[2] * w[e.N2];
            double wy = e.By[0] * w[e.N0] + e.By[1] * w[e.N1] + e.By[2] * w[e.N2];
            if (wx == 0 && wy == 0) continue;
            if (set.Contains(e.Region)) continue;
            if (e.Material.RelativePermeability != 1 || e.Material.Curve is not null
                || e.Material.Conductivity > 0 || coilRegions.Contains(e.Region))
                throw new InvalidOperationException(
                    $"The force contour crosses '{(e.Region >= 0 ? Model.Regions[e.Region].Name : "the background")}', which is magnetic, conducting or carries current: leave a shell of plain air around the region(s).");
            var (bxC, byC) = FluxDensity(e);
            double txx, txy; // times µ0; tyy = −txx
            if (ac)
            {
                txx = 0.25 * (bxC.Magnitude * bxC.Magnitude - byC.Magnitude * byC.Magnitude);
                txy = 0.5 * (bxC * Complex.Conjugate(byC)).Real;
            }
            else
            {
                double bx = bxC.Real, by = byC.Real;
                txx = 0.5 * (bx * bx - by * by);
                txy = bx * by;
            }
            double vol = e.Area * Depth(e) / MagneticConstants.Mu0;
            fx -= (txx * wx + txy * wy) * vol;
            fy -= (txy * wx - txx * wy) * vol;
        }
        return Model.Geometry == MagneticGeometry.Axisymmetric ? (0, fy) : (fx, fy);
    }

    /// <summary>Stored magnetic energy [J; J/m planar]: ∫H·dB for a static solve (the curve's
    /// exact integral), the time-average ¼∫ν|B|² for a time-harmonic one (with a B–H material's
    /// converged effective ν).</summary>
    public double Energy()
    {
        double total = 0;
        for (int k = 0; k < _mesh.Elements.Count; k++)
        {
            var e = _mesh.Elements[k];
            double density;
            if (FrequencyHz > 0)
            {
                var (bx, by) = FluxDensity(e);
                double nu = _acNu?[k] ?? 1 / (MagneticConstants.Mu0 * e.Material.RelativePermeability);
                density = 0.25 * nu * (bx.Magnitude * bx.Magnitude + by.Magnitude * by.Magnitude);
            }
            else
            {
                double b = PeakB(e);
                density = e.Material.Curve is { } curve
                    ? curve.EnergyDensity(b)
                    : 0.5 * b * b / (MagneticConstants.Mu0 * e.Material.RelativePermeability);
            }
            total += density * e.Area * Depth(e);
        }
        return total;
    }

    /// <summary>The potential u at a node nearest a point (A_z, or ψ = r·A_φ).</summary>
    public Complex PotentialAt(double x, double y)
    {
        int best = 0;
        double bestD = double.MaxValue;
        for (int i = 0; i < _mesh.NodeCount; i++)
        {
            var (px, py) = _mesh.Coord(i);
            double d = (px - x) * (px - x) + (py - y) * (py - y);
            if (d < bestD) { bestD = d; best = i; }
        }
        return _u[best];
    }

    public string Describe()
    {
        var lines = new List<string>(Log);
        foreach (var coil in Model.Coils)
        {
            var l = Inductance(coil.Name);
            lines.Add(Model.Geometry == MagneticGeometry.Planar
                ? $"Coil '{coil.Name}': flux linkage {FluxLinkage(coil.Name).Magnitude:g5} Wb/m, L = {l.Real * 1e9:g5} nH/m"
                : $"Coil '{coil.Name}': flux linkage {FluxLinkage(coil.Name).Magnitude:g5} Wb, L = {l.Real * 1e6:g5} µH");
        }
        if (FrequencyHz > 0)
            foreach (var c in Model.Conductors)
            {
                var z = ConductorImpedance(c.Name);
                lines.Add($"Conductor '{c.Name}': R = {z.Real:g5} Ω{(Model.Geometry == MagneticGeometry.Planar ? "/m" : "")}, "
                    + $"L = {z.Imaginary / (2 * Math.PI * FrequencyHz) * 1e9:g5} nH{(Model.Geometry == MagneticGeometry.Planar ? "/m" : "")}");
            }
        lines.Add($"Stored energy {Energy():g5} J{(Model.Geometry == MagneticGeometry.Planar ? "/m" : "")}; largest |B| {MaxFluxDensity():g4} T.");
        return string.Join(System.Environment.NewLine, lines);
    }
}

/// <summary>Strand-level copper loss of a stranded winding (watts; planar: per metre), from
/// <see cref="MagneticSolution2D.StrandLoss"/>: the transport loss ½|I|²·R_dc·<see cref="SkinFactor"/>
/// and the proximity loss of the local field. <see cref="AcResistanceOhms"/> =
/// R_dc·F_skin + 2·P_prox/|I|² counts only this winding's own loss against its own current, so it
/// is null for a passive winding (whose proximity loss loads whichever winding drives the
/// field).</summary>
public sealed record StrandedWindingLoss(double TransportWatts, double ProximityWatts, double DcResistanceOhms,
    double SkinFactor, double? AcResistanceOhms, IReadOnlyList<string> Notes)
{
    public double TotalWatts => TransportWatts + ProximityWatts;
}
