using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.NonSequential;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-70: the non-sequential ray tracing core (docs/Optics-Conventions.md §69). Gate: the energy
/// ledger closes; a glass sphere's first-surface reflectance and internal paths against closed
/// forms derived here (Fresnel's equations, the sphere's chord geometry); a Standard Lens object
/// traces as its sequential twin; the Monte Carlo loop is the same to the bit at any thread
/// count. Indices, radii and thicknesses are arbitrary test inputs (constant-index materials),
/// not reference data.
/// </summary>
public sealed class NonSequentialTests
{
    private const double Mm = 1e-3;

    private static void Close(double expected, double actual, double tolerance, string what)
    {
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what}: expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {tolerance:R}).");
    }

    private static NscObject Glass(NscGeometry g, double n, NscPlacement placement = default, int insideOf = 0) => new()
    {
        Geometry = g, Material = NscMaterial.Of(ParaxialMedium.Constant(n)), Placement = placement, InsideOf = insideOf,
    };

    // ================================================================= gate: the glass sphere

    /// <summary>
    /// A ray at height h parallel to the axis meets a glass sphere (radius R, index n, in air).
    /// Derived closed forms, in the y–z plane with the centre at the origin:
    /// <list type="bullet">
    /// <item>Entry at P(ψ₀), P(ψ) = R(0, sin ψ, −cos ψ), with sin θi = h/R = sin ψ₀, θi the angle of
    /// incidence; refraction angle sin θt = sin θi / n.</item>
    /// <item>Inside, every chord joins two points of the circle and makes the same angle θt with
    /// the normal at both ends (the triangle centre–Pj–Pj+1 is isosceles), so each chord
    /// subtends the central angle β = π − 2θt and the internal hit points are P(ψ₀ + jβ). Every
    /// incidence inside is at θt, so the same Fresnel coefficients apply at each.</item>
    /// <item>Fresnel's amplitude reflection coefficients from the continuity of the tangential
    /// fields: rs = (n₁cos θ₁ − n₂cos θ₂)/(n₁cos θ₁ + n₂cos θ₂), rp = (n₂cos θ₁ − n₁cos θ₂)/(n₂cos θ₁ +
    /// n₁cos θ₂); R = r², the same from either side (rs′ = −rs), and T = 1 − R for this lossless
    /// interface.</item>
    /// <item>The plane of incidence is the same plane for every hit, so s and p never mix: an
    /// unpolarized ray (half its power in each) leaves after m internal reflections with power
    /// ½[(1 − Rs)²Rsᵐ + (1 − Rp)²Rpᵐ]; the first-surface reflection carries ½(Rs + Rp).</item>
    /// <item>Leaving at P(ψ_(m+1)), the direction follows from Snell's law as conservation of the
    /// tangential part: d_out = n·u_t + √(1 − n²|u_t|²)·n̂, with u the chord direction, n̂ the
    /// outward normal and u_t = u − (u·n̂)n̂.</item>
    /// </list>
    /// Tolerances: every quantity is a few dozen roundings of numbers of order 1 (intensities) or
    /// R (positions), about 1e-15 relative; 1e-13 leaves two orders of margin.
    /// </summary>
    [Fact]
    public void Gate_AGlassSphere_ReflectsAndCarriesItsInternalPaths_AsTheClosedForms()
    {
        double radius = 10 * Mm, n = 1.5, h = 6 * Mm;
        var tracer = new NscTracer(new NscScene { Objects = new[] { Glass(new NscSphere(radius), n) } }, 0.55,
            new NscTraceSettings { Splitting = NscSplitting.Split, MinimumRelativeIntensity = 1e-9 });
        var tree = tracer.TraceSingle(new NscLaunchRay { Position = new Vector3D(0, h, -50 * Mm), Direction = Vector3D.UnitZ });
        Assert.Empty(tree.Errors);

        double sinI = h / radius, cosI = Math.Sqrt(1 - sinI * sinI);
        double sinT = sinI / n, cosT = Math.Sqrt(1 - sinT * sinT);
        double rs = (cosI - n * cosT) / (cosI + n * cosT), rp = (n * cosI - cosT) / (n * cosI + cosT);
        double Rs = rs * rs, Rp = rp * rp;
        double psi0 = Math.Asin(sinI), beta = Math.PI - 2 * Math.Asin(sinT);
        Vector3D P(int j) => new(0, radius * Math.Sin(psi0 + j * beta), -radius * Math.Cos(psi0 + j * beta));

        const double tolI = 1e-13;
        // The entry.
        var first = tree.Segments[1];
        Assert.Equal(1, first.Hit);
        Close(P(0).Y, first.Position.Y, 1e-13 * radius, "entry y");
        Close(P(0).Z, first.Position.Z, 1e-13 * radius, "entry z");
        Assert.True(first.Events.HasFlag(NscEvents.Reflected) && first.Events.HasFlag(NscEvents.Transmitted));

        // The internal hits: one segment inside the sphere at each level L ≥ 2, ending at P(L − 1).
        var inside = tree.Segments.Where(s => s.Inside == 1).OrderBy(s => s.Level).ToList();
        Assert.True(inside.Count >= 4, $"only {inside.Count} internal segments");
        foreach (var s in inside)
        {
            var expected = P(s.Level - 1);
            Close(0, s.Position.X, 1e-13 * radius, $"internal hit x, level {s.Level}");
            Close(expected.Y, s.Position.Y, 1e-13 * radius, $"internal hit y, level {s.Level}");
            Close(expected.Z, s.Position.Z, 1e-13 * radius, $"internal hit z, level {s.Level}");
            Close(2 * radius * cosT, s.PathLength, 1e-13 * radius, $"chord length 2R·cos θt, level {s.Level}");
        }

        // The escaping rays: level 2 is the first-surface reflection; level m + 3 left after m internal reflections.
        var escaped = tree.Segments.Where(s => s.Hit == 0 && s.Number > 0).OrderBy(s => s.Level).ToList();
        var ghost = Assert.Single(escaped, s => s.Level == 2);
        Assert.True(ghost.Events.HasFlag(NscEvents.Ghost));
        Close((Rs + Rp) / 2, ghost.Intensity, tolI, "first-surface reflectance");
        var normal0 = P(0) / radius;
        var reflected = Vector3D.UnitZ - normal0 * (2 * Vector3D.Dot(Vector3D.UnitZ, normal0));
        Close(reflected.Y, ghost.Direction.Y, 1e-13, "reflected M");
        Close(reflected.Z, ghost.Direction.Z, 1e-13, "reflected N");

        int checkedPaths = 0;
        foreach (var s in escaped.Where(s => s.Level >= 3))
        {
            int m = s.Level - 3;
            double expected = 0.5 * ((1 - Rs) * (1 - Rs) * Math.Pow(Rs, m) + (1 - Rp) * (1 - Rp) * Math.Pow(Rp, m));
            Close(expected, s.Intensity, tolI, $"power out after {m} internal reflections");
            var exit = P(m + 1);
            var u = (exit - P(m)) / (exit - P(m)).Length;
            var nHat = exit / radius;
            var ut = u - nHat * Vector3D.Dot(u, nHat);
            var dOut = ut * n + nHat * Math.Sqrt(1 - n * n * ut.LengthSquared);
            Close(dOut.Y, s.Direction.Y, 1e-13, $"exit M after {m}");
            Close(dOut.Z, s.Direction.Z, 1e-13, $"exit N after {m}");
            checkedPaths++;
        }
        Assert.True(checkedPaths >= 4);

        // Nothing is absorbed (to rounding) and the ray's ledger closes.
        var ledger = tree.Ledger;
        Close(1, ledger.Emitted, 0, "emitted");
        Close(0, ledger.Absorbed, 1e-14, "absorbed by a lossless sphere");
        Close(0, ledger.Residual, 1e-14, "ledger residual");
        Assert.True(ledger.Thresholded > 0);
    }

    [Fact]
    public void ASphere_WithoutSplitting_SendsTheTransmittedRayOn_AndListsTheReflectionAsNotTraced()
    {
        double radius = 10 * Mm, n = 1.5;
        var tracer = new NscTracer(new NscScene { Objects = new[] { Glass(new NscSphere(radius), n) } }, 0.55, new NscTraceSettings());
        var tree = tracer.TraceSingle(new NscLaunchRay { Position = new Vector3D(0, 0, -50 * Mm), Direction = Vector3D.UnitZ });
        // Normal incidence: R = ((n − 1)/(n + 1))² at each face (derived from Fresnel at θ = 0).
        double r = (n - 1) / (n + 1), R = r * r;
        Assert.Equal(4, tree.Segments.Count);   // source, entry, exit, escape
        Close(2 * radius, tree.Segments[2].PathLength, 1e-13 * radius, "diameter");
        Close((1 - R) * (1 - R), tree.Segments[3].Intensity, 1e-14, "transmitted power");
        Close(R + (1 - R) * R, tree.Ledger.NotTraced, 1e-14, "reflections not traced");
        Close(0, tree.Ledger.Residual, 1e-15, "residual");
    }

    [Fact]
    public void SimpleRaySplitting_KeepsEachRaysWholeEnergy_ThroughALosslessSphere()
    {
        var tracer = new NscTracer(new NscScene { Objects = new[] { Glass(new NscSphere(10 * Mm), 1.7) } }, 0.55,
            new NscTraceSettings { Splitting = NscSplitting.Simple, Seed = 7 });
        int reflectedFirst = 0;
        for (int k = 0; k < 200; k++)
        {
            var tree = tracer.TraceSingle(new NscLaunchRay { Position = new Vector3D(0, (k % 19) * 0.5 * Mm, -50 * Mm), Direction = Vector3D.UnitZ }, k);
            var leaf = Assert.Single(tree.Segments, s => s.Hit == 0 && s.Number > 0);
            Close(1, leaf.Intensity, 1e-13, "the escaping ray carries all the energy");
            Close(0, tree.Ledger.Residual, 1e-14, "residual");
            if (tree.Segments[1].Events.HasFlag(NscEvents.Reflected)) reflectedFirst++;
        }
        Assert.InRange(reflectedFirst, 1, 100);   // some reflect (R ≈ 7–10 %), most do not
    }

    // ================================================================= gate: the sequential twin

    private static SequentialSurface S(double radius, double conic, double thickness, double n, bool stop = false) => new()
    {
        Curvature = OpticsConventions.CurvatureFromRadius(radius), Conic = conic, Thickness = thickness,
        Medium = ParaxialMedium.Constant(n), IsStop = stop,
    };

    /// <summary>
    /// A Standard Lens object and the same lens as two sequential surfaces trace the same rays to
    /// the same exit point and direction. The object's faces use the sequential conic's equation,
    /// normal and vertex-plane transfer, so the two differ only by the order of a few roundings
    /// (the non-sequential ray starts at its launch point, not on the vertex plane, and is offset
    /// off each face by about γ₃₂ times the coordinates, 4e-15 × 0.05 m). Expected agreement is
    /// ~1e-16 m in position and ~1e-15 in direction; 1e-13 m and 1e-12 leave a wide margin and
    /// still catch any wrong root, sign or normal (which would be off by micrometres or more).
    /// The tilted case places the lens and its target with tilts and a reference object and maps
    /// the result back into the lens's frame.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gate_AStandardLensObject_TracesAsItsSequentialTwin(bool tilted)
    {
        double r1 = 50 * Mm, k1 = 0, t = 5 * Mm, r2 = -80 * Mm, k2 = -0.5, n = 1.5168, gap = 40 * Mm, clear = 12 * Mm;
        var lens = new SequentialLens
        {
            Surfaces = new[]
            {
                new SequentialSurface { Thickness = 10 * Mm, Medium = ParaxialMedium.Air },
                S(r1, k1, t, n, stop: true), S(r2, k2, gap, 1), new SequentialSurface(),
            }
        };
        var system = new OpticalSystemData
        {
            Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 0.01 },
            Fields = new FieldSet { Type = FieldType.Angle, Points = new[] { new FieldPoint() } },
        };
        var sequential = SequentialTrace.Create(lens, system);

        var placement = tilted ? new NscPlacement(3 * Mm, -2 * Mm, 7 * Mm, 3, -2, 10) : default;
        var scene = new NscScene
        {
            Objects = new[]
            {
                Glass(new NscStandardLens(r1, k1, clear, clear + 0.5 * Mm, t, r2, k2, clear, clear + 0.5 * Mm), n, placement),
                new NscObject { Geometry = new NscRectangle(0.1, 0.1), Placement = new NscPlacement(Z: t + gap), ReferenceObject = tilted ? 1 : 0, IsDetectingStandIn = true },
            }
        };
        var tracer = new NscTracer(scene, sequential.AirMicrometers, new NscTraceSettings { UsePolarization = false });
        var frame = tracer.GlobalPlacement(1);

        int count = 0;
        for (int i = -2; i <= 2; i++)
            for (int j = -2; j <= 2; j++)
                foreach (double angle in new[] { 0.0, 3.0, -5.0 })
                {
                    var start = new Vector3D(i * 3 * Mm, j * 3 * Mm, -10 * Mm);
                    var a = angle * Math.PI / 180;
                    var dir = new Vector3D(Math.Sin(a) * 0.6, Math.Sin(a) * 0.8, Math.Cos(a));
                    var seq = sequential.Trace(sequential.NewRay(start, dir));
                    Assert.True(seq.IsOk, $"sequential ray failed: {seq.Status}");

                    var tree = tracer.TraceSingle(new NscLaunchRay { Position = frame.Apply(start), Direction = frame.ApplyDirection(dir) });
                    Assert.Empty(tree.Errors);
                    Assert.Equal(new[] { 1, 2 }, tree.Segments.Skip(1).Take(2).Select(s => s.Face));   // front, then back
                    var end = Assert.Single(tree.Segments, s => s.Hit == 2);
                    var p = frame.InverseApply(end.Position);
                    var d = frame.InverseApplyDirection(end.Direction);
                    // The sequential result is in the image surface's frame: z = t + gap from surface 1.
                    Close(seq.Position.X, p.X, 1e-13, "exit x");
                    Close(seq.Position.Y, p.Y, 1e-13, "exit y");
                    Close(t + gap, p.Z, 1e-13, "exit z");
                    Close(seq.Direction.X, d.X, 1e-12, "exit L");
                    Close(seq.Direction.Y, d.Y, 1e-12, "exit M");
                    Close(seq.Direction.Z, d.Z, 1e-12, "exit N");
                    Close(1, tree.Ledger.Detected, 0, "no energy accounting: all the energy reaches the target");
                    count++;
                }
        Assert.Equal(75, count);
    }

    // ================================================================= gate: the ledger

    private static NscScene Clutter() => new()
    {
        Objects = new[]
        {
            Glass(new NscStandardLens(40 * Mm, 0, 10 * Mm, 11 * Mm, 6 * Mm, -40 * Mm, 0, 10 * Mm, 11 * Mm), 1.5, new NscPlacement(Z: 20 * Mm)),
            Glass(new NscSphere(5 * Mm), 1.6, new NscPlacement(Z: 45 * Mm)),
            new NscObject { Geometry = new NscRectangle(20 * Mm, 20 * Mm), Material = NscMaterial.Mirror, Placement = new NscPlacement(Z: 70 * Mm, TiltXDegrees: -30) },
            new NscObject { Geometry = new NscRectangle(50 * Mm, 50 * Mm), Placement = new NscPlacement(Z: -5 * Mm), IsDetectingStandIn = true },
            new NscObject { Geometry = new NscRectangle(60 * Mm, 60 * Mm), Placement = new NscPlacement(Y: -60 * Mm, Z: 60 * Mm, TiltXDegrees: 90), IsDetectingStandIn = true },
        }
    };

    /// <summary>The ledger closes to rounding on a scene that sends energy everywhere: detectors,
    /// mirror losses, escapes, thresholds and the intersection limit. Each destination is a
    /// compensated sum, so the residual is a few rounding units of the emitted 1 W; 1e-13 bounds it.</summary>
    [Theory]
    [InlineData(NscSplitting.Split)]
    [InlineData(NscSplitting.Simple)]
    [InlineData(NscSplitting.None)]
    public void Gate_TheEnergyLedger_Closes(NscSplitting splitting)
    {
        var tracer = new NscTracer(Clutter(), 0.55, new NscTraceSettings
        {
            Splitting = splitting, MinimumRelativeIntensity = 1e-3, MaxIntersectionsPerRay = 9,
        });
        var rays = NscSourceStandIn.PointCone(Vector3D.Zero, Vector3D.UnitZ, 25, 2000, 1.0);
        var run = tracer.Run(rays);
        var l = run.Ledger;
        Assert.Equal(2000, l.Rays);
        Close(1.0, l.Emitted, 1e-15, "emitted");
        Close(0, l.Residual, 1e-13, "emitted − accounted");
        Assert.True(l.Detected > 0 && l.Escaped > 0 && l.Absorbed > 0, string.Join("; ", l.Rows("W")));
        Assert.Equal(l.Detected, l.DetectedByObject.Values.Sum(), 12);
        if (splitting == NscSplitting.Split) Assert.True(l.Thresholded > 0);
        if (splitting == NscSplitting.None) Assert.True(l.NotTraced > 0);
        else Assert.Equal(0, l.NotTraced);
        Assert.Equal(0, l.Errors);
    }

    [Fact]
    public void AMirrorShell_WithNoLosses_KeepsTheRaysUntilTheIntersectionLimit()
    {
        var scene = new NscScene
        {
            Objects = new[] { new NscObject { Geometry = new NscSphere(20 * Mm, isVolume: false), Material = NscMaterial.Mirror } }
        };
        var tracer = new NscTracer(scene, 0.55, new NscTraceSettings { UsePolarization = false, MaxIntersectionsPerRay = 25 });
        var run = tracer.Run(NscSourceStandIn.PointCone(new Vector3D(0, 3 * Mm, 0), Vector3D.UnitZ, 60, 100, 2.0, NscSampling.Random, 3));
        Close(2.0, run.Ledger.LimitTerminated, 1e-14, "all the energy reaches the limit");
        Assert.Equal(100, run.Ledger.LimitTerminatedRays);
        Close(0, run.Ledger.Residual, 1e-14, "residual");
    }

    // ================================================================= determinism

    [Fact]
    public void TheMonteCarloRun_IsBitwiseTheSame_AtAnyThreadCount()
    {
        var rays = NscSourceStandIn.PointCone(Vector3D.Zero, Vector3D.UnitZ, 25, 1500, 1.0, NscSampling.Random, 11);
        NscEnergyLedger Run(int? threads) => new NscTracer(Clutter(), 0.55, new NscTraceSettings
        {
            Splitting = NscSplitting.Simple, Seed = 5, MaxDegreeOfParallelism = threads, MinimumRelativeIntensity = 1e-4,
        }).Run(rays).Ledger;
        var reference = Run(1);
        foreach (int? threads in new int?[] { 2, 7, null })
        {
            var other = Run(threads);
            foreach (var (a, b, name) in new[]
            {
                (reference.Detected, other.Detected, "detected"), (reference.Absorbed, other.Absorbed, "absorbed"),
                (reference.Escaped, other.Escaped, "escaped"), (reference.Thresholded, other.Thresholded, "thresholded"),
                (reference.LimitTerminated, other.LimitTerminated, "limit"), (reference.Emitted, other.Emitted, "emitted"),
            })
                Assert.True(BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b), $"{name} differs at {threads} threads: {a:R} against {b:R}");
            Assert.Equal(reference.Segments, other.Segments);
            Assert.Equal(reference.DetectedByObject.Select(p => BitConverter.DoubleToInt64Bits(p.Value)), other.DetectedByObject.Select(p => BitConverter.DoubleToInt64Bits(p.Value)));
        }
    }

    // ================================================================= nesting, touching, errors

    /// <summary>Two flat plates touching at z = 5 mm. With equal indices the contact is no
    /// interface; with different ones the ray refracts from one glass straight into the other,
    /// conserving n·sin θ (derived: Snell's law across the contact).</summary>
    [Theory]
    [InlineData(1.5, 1.5)]
    [InlineData(1.5, 1.8)]
    public void TouchingPlates_AreOneContact_WithSnellsLawBetweenTheTwoGlasses(double na, double nb)
    {
        NscGeometry Plate() => new NscStandardLens(0, 0, 10 * Mm, 10 * Mm, 5 * Mm, 0, 0, 10 * Mm, 10 * Mm);
        var tracer = new NscTracer(new NscScene
        {
            Objects = new[] { Glass(Plate(), na), Glass(Plate(), nb, new NscPlacement(Z: 5 * Mm)) }
        }, 0.55, new NscTraceSettings { UsePolarization = false });
        var dir = new Vector3D(0, Math.Sin(0.3), Math.Cos(0.3));
        var tree = tracer.TraceSingle(new NscLaunchRay { Position = new Vector3D(0, 0, -5 * Mm), Direction = dir });
        Assert.Empty(tree.Errors);
        var s = tree.Segments;
        Assert.Equal(0, s[1].Inside);
        Assert.Equal(1, s[2].Inside);
        Assert.Equal(2, s[2].Hit);              // the contact: object 2 (the later) decides
        Assert.Equal(2, s[3].Inside);
        double sinAir = dir.Y;
        Close(sinAir / na, s[2].Direction.Y, 1e-15, "n·sin θ in glass A");
        Close(sinAir / nb, s[3].Direction.Y, 1e-15, "n·sin θ in glass B");
        Close(sinAir, s[4].Direction.Y, 1e-15, "back in air");
    }

    [Fact]
    public void TheLaterObjectWins_WhereObjectsOverlap()
    {
        // A dense sphere (object 1) inside a larger one (object 2): where they overlap the later
        // object's index holds, so the small sphere's boundary is no interface at all.
        var tracer = new NscTracer(new NscScene
        {
            Objects = new[] { Glass(new NscSphere(3 * Mm), 1.9), Glass(new NscSphere(10 * Mm), 1.5) }
        }, 0.55, new NscTraceSettings { UsePolarization = false });
        var dir = new Vector3D(0, 0.1, 1).Normalized();
        var tree = tracer.TraceSingle(new NscLaunchRay { Position = new Vector3D(0, -2 * Mm, -30 * Mm), Direction = dir });
        Assert.Empty(tree.Errors);
        var crossings = tree.Segments.Where(s => s.Hit == 1).ToList();
        Assert.Equal(2, crossings.Count);
        foreach (var c in crossings) Assert.Contains("same index", c.Fate);
        Assert.All(tree.Segments.Skip(1), s => Assert.True(s.Index is 1.0 or 1.5, $"segment {s.Number} in index {s.Index}"));

        // A bubble (blank, index 1) listed after the glass sphere it sits in is air.
        var bubble = new NscTracer(new NscScene
        {
            Objects = new[] { Glass(new NscSphere(10 * Mm), 1.5), new NscObject { Geometry = new NscSphere(3 * Mm), InsideOf = 1 } }
        }, 0.55, new NscTraceSettings { UsePolarization = false });
        var bubbleTree = bubble.TraceSingle(new NscLaunchRay { Position = new Vector3D(0, 1 * Mm, -30 * Mm), Direction = Vector3D.UnitZ });
        Assert.Empty(bubbleTree.Errors);
        Assert.Contains(bubbleTree.Segments, s => s.Inside == 2 && s.Index == 1.0);
    }

    [Fact]
    public void ARayStartingInsideAnObject_MustSaySo()
    {
        var tracer = new NscTracer(new NscScene { Objects = new[] { Glass(new NscSphere(10 * Mm), 1.5) } }, 0.55, new NscTraceSettings());
        var ray = new NscLaunchRay { Position = Vector3D.Zero, Direction = Vector3D.UnitZ };
        var e = Assert.Throws<ArgumentException>(() => tracer.TraceSingle(ray));
        Assert.Contains("Inside Of", e.Message);
        var tree = tracer.TraceSingle(ray with { InsideOf = 1 });
        Assert.Equal(1, tree.Segments[1].Inside);
        Close(10 * Mm, tree.Segments[1].PathLength, 1e-17, "centre to surface");
        Assert.Throws<ArgumentException>(() => tracer.TraceSingle(ray with { Position = new Vector3D(0, 0, -20 * Mm), InsideOf = 1 }));
    }

    /// <summary>Five nested spheres with a nesting limit of four: entering the fifth is a trace
    /// error. With Ignore Errors off the run stops naming it; on, the energy is counted as lost
    /// to errors and the ledger still closes; the single-ray trace marks the segment Z.</summary>
    [Fact]
    public void TraceErrors_StopTheRun_OrAreCounted_WhenIgnored()
    {
        var objects = Enumerable.Range(0, 5).Select(i => Glass(new NscSphere((10 - i) * Mm), 1.4 + 0.05 * i)).ToArray();
        var scene = new NscScene { Objects = objects };
        var rays = NscSourceStandIn.CollimatedDisk(new Vector3D(0, 0, -20 * Mm), Vector3D.UnitZ, 1 * Mm, 10, 1.0);
        var strict = new NscTracer(scene, 0.55, new NscTraceSettings { MaxNestedObjects = 4 });
        var e = Assert.Throws<InvalidOperationException>(() => strict.Run(rays));
        Assert.Contains("nested", e.Message);
        Assert.Contains("object 5", e.Message);

        var lenient = new NscTracer(scene, 0.55, new NscTraceSettings { MaxNestedObjects = 4, IgnoreErrors = true });
        var run = lenient.Run(rays);
        Assert.Equal(10, run.Ledger.ErrorRays);
        Assert.True(run.Ledger.Errors > 0);
        Close(0, run.Ledger.Residual, 1e-14, "residual");
        Assert.NotEmpty(run.Errors);

        var tree = strict.TraceSingle(rays[0]);
        Assert.Contains(tree.Segments, s => s.Events.HasFlag(NscEvents.Error));
        Assert.Single(tree.Errors);
    }

    [Fact]
    public void AMirrorRectangle_ReflectsByTheLawOfReflection_AndAbsorbIsAbsorbed()
    {
        var mirror = new NscObject { Geometry = new NscRectangle(10 * Mm, 10 * Mm), Material = NscMaterial.Mirror, Placement = new NscPlacement(Z: 10 * Mm, TiltXDegrees: 45) };
        var tracer = new NscTracer(new NscScene { Objects = new[] { mirror } }, 0.55, new NscTraceSettings { UsePolarization = false });
        var tree = tracer.TraceSingle(new NscLaunchRay { Position = Vector3D.Zero, Direction = Vector3D.UnitZ });
        // The rectangle's normal is Rx(45°)·z = (0, −sin 45°, cos 45°), so +z reflects to
        // d − 2(d·n)n = (0, 1, 0) (derived).
        var n = new Vector3D(0, -Math.Sqrt(0.5), Math.Sqrt(0.5));
        var expected = Vector3D.UnitZ - n * (2 * Vector3D.Dot(Vector3D.UnitZ, n));
        Close(expected.Y, tree.Segments[2].Direction.Y, 1e-15, "reflected M");
        Close(expected.Z, tree.Segments[2].Direction.Z, 1e-15, "reflected N");
        Close(10 * Mm, tree.Segments[1].Position.Z, 1e-17, "hit z");

        var absorber = new NscTracer(new NscScene { Objects = new[] { mirror with { Material = NscMaterial.Absorb } } }, 0.55, new NscTraceSettings());
        var absorbed = absorber.TraceSingle(new NscLaunchRay { Position = Vector3D.Zero, Direction = Vector3D.UnitZ, Intensity = 0.25 });
        Close(0.25, absorbed.Ledger.Absorbed, 0, "absorbed");
        Assert.Equal(2, absorbed.Segments.Count);
    }

    [Fact]
    public void AnUncoatedMirror_WithEnergyAccounted_ReflectsLessThanAll()
    {
        // OpticStudio's uncoated mirror is aluminium 0.7 − 7.0i (help, "Refraction and
        // Reflection From NSC Objects"); its reflectance at normal incidence is
        // |(1 − N)/(1 + N)|² with N = 0.7 + 7i in this program's convention (derived, Fresnel at θ = 0).
        var mirror = new NscObject { Geometry = new NscRectangle(10 * Mm, 10 * Mm), Material = NscMaterial.Mirror, Placement = new NscPlacement(Z: 10 * Mm) };
        var tracer = new NscTracer(new NscScene { Objects = new[] { mirror } }, 0.55, new NscTraceSettings());
        var tree = tracer.TraceSingle(new NscLaunchRay { Position = Vector3D.Zero, Direction = Vector3D.UnitZ });
        var N = new System.Numerics.Complex(0.7, 7.0);
        double R = System.Numerics.Complex.Abs((1 - N) / (1 + N));
        R *= R;
        Close(R, tree.Ledger.Escaped, 1e-14, "reflected power");
        Close(1 - R, tree.Ledger.Absorbed, 1e-14, "absorbed power");
    }

    [Fact]
    public void Refusals_NameWhatIsNotBuilt()
    {
        Assert.Throws<NotSupportedException>(() => new NscStandardLens(10 * Mm, 0, -5 * Mm, 5 * Mm, 3 * Mm, 0, 0, 5 * Mm, 5 * Mm));
        Assert.Throws<ArgumentException>(() => new NscStandardLens(5 * Mm, 0, 4 * Mm, 4 * Mm, 1 * Mm, -5 * Mm, 0, 4 * Mm, 4 * Mm));   // faces cross
        var shell = new NscScene { Objects = new[] { new NscObject { Geometry = new NscSphere(1, isVolume: false) } } };
        Assert.Contains(shell.Validate(), p => p.Contains("MIRROR or ABSORB"));
        var detectorVolume = new NscScene { Objects = new[] { new NscObject { Geometry = new NscSphere(1), IsDetectingStandIn = true } } };
        Assert.Contains(detectorVolume.Validate(), p => p.Contains("OP-79"));
        var forward = new NscScene { Objects = new[] { new NscObject { Geometry = new NscSphere(1), ReferenceObject = 1 } } };
        Assert.Throws<ArgumentException>(() => new NscTracer(forward, 0.55));
    }

    [Fact]
    public void TheStandardLens_InsideTest_AgreesWithRayCrossings()
    {
        // From a point found inside, every ray leaves through exactly one face crossing more than
        // it enters (watertight); from a point outside, crossings come in pairs.
        var lens = new NscStandardLens(30 * Mm, -0.5, 9 * Mm, 10 * Mm, 6 * Mm, -50 * Mm, 0, 8 * Mm, 11 * Mm);
        var rng = new SplitMix64(3);
        var hits = new List<NscLocalHit>();
        int insideCount = 0;
        for (int k = 0; k < 400; k++)
        {
            var p = new Vector3D((rng.NextDouble() - 0.5) * 24 * Mm, (rng.NextDouble() - 0.5) * 24 * Mm, (rng.NextDouble() - 0.3) * 10 * Mm);
            var d = new Vector3D(rng.NextNormal(), rng.NextNormal(), rng.NextNormal()).Normalized();
            hits.Clear();
            lens.Intersect(p, d, 0, double.PositiveInfinity, hits);
            bool inside = lens.IsInside(p);
            if (inside) insideCount++;
            Assert.True(hits.Count % 2 == (inside ? 1 : 0), $"point {p}, direction {d}: {hits.Count} crossings, inside = {inside}");
            int exits = hits.Count(h => Vector3D.Dot(h.Normal, d) > 0), entries = hits.Count - exits;
            Assert.Equal(inside ? 1 : 0, exits - entries);
        }
        Assert.InRange(insideCount, 20, 380);
    }

    // ================================================================= the analysis and the saved scene

    private static OpticsAnalysisContext ContextWithSphere(string type = "Sphere") => new("P", new OpticsSettings
    {
        NonSequential = new OpticsNonSequentialSettings
        {
            Objects = new List<OpticsNscObjectSettings>
            {
                new() { Type = type, Parameters = new List<double> { 10, 1 }, Material = "1.5" },
                new() { Type = "Rectangle", Parameters = new List<double> { 50, 50 }, Z = 30, DetectingStandIn = true },
            }
        }
    });

    [Fact]
    public void TheNscSingleRayTrace_ListsTheSegments_AndTheLedger()
    {
        var analysis = new NscSingleRayTraceAnalysis();
        Assert.True(OpticsAnalysisRegistry.CreateDefault().TryGet(NscSingleRayTraceAnalysis.SavedId, out _));
        var result = analysis.Compute(ContextWithSphere(), new NscSingleRayTraceSettings { Y = 2, Z = -20, SplitRays = true }, default);
        var segments = Assert.Single(result.Sections, s => s.Heading == "Segments");
        Assert.True(segments.TableRows.Count > 4);
        Assert.Equal("0", segments.TableRows[0][0]);
        Assert.Contains(result.Sections, s => s.Heading == "Energy");
        Assert.DoesNotContain(result.Sections, s => s.Heading == "Ray trace errors");

        var empty = analysis.Compute(new OpticsAnalysisContext("P", new OpticsSettings()), new NscSingleRayTraceSettings(), default);
        Assert.Contains(empty.Sections, s => s.Heading == "No objects");

        var e = Assert.Throws<ArgumentException>(() => analysis.Compute(ContextWithSphere("Cylinder Volume"), new NscSingleRayTraceSettings(), default));
        Assert.Contains("OP-72", e.Message);
    }
}
