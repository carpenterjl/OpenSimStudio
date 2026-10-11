using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Si;
using Xunit;
using Xunit.Abstractions;
using Layer = OpenSim.Rf.Layered.LayeredStackup.Layer;

namespace OpenSim.Tests.Si;

/// <summary>
/// FU-29: traces on different interfaces of one stack — broadside coupling — by
/// <see cref="BroadsideExtractor"/>, against the coplanar extraction where both lie on one
/// interface and against a spatial image BEM in air.
/// </summary>
public class BroadsideCouplingTests
{
    private readonly ITestOutputHelper _out;
    public BroadsideCouplingTests(ITestOutputHelper output) => _out = output;

    private static double Worst(double[,] a, double[,] b)
    {
        double worst = 0, scale = 0;
        for (int i = 0; i < a.GetLength(0); i++)
            for (int j = 0; j < a.GetLength(1); j++) scale = Math.Max(scale, Math.Abs(b[i, j]));
        for (int i = 0; i < a.GetLength(0); i++)
            for (int j = 0; j < a.GetLength(1); j++) worst = Math.Max(worst, Math.Abs(a[i, j] - b[i, j]) / scale);
        return worst;
    }

    [Fact]
    public void OnOneInterface_ItIsTheCoplanarExtraction()
    {
        // A pair on the buried interface of a two-layer stack, under a top plane: the coplanar
        // extraction's spectral kernel is 1/(Y↓ + Y↑) at that node, which the nodal solve is.
        var stack = new LayeredStackup(new[] { new Layer(4.2, 0.02, 0.2e-3), new Layer(3.6, 0.01, 0.25e-3) });
        var a = TraceCrossSection.Copper(-0.2e-3, 0.15e-3);
        var b = TraceCrossSection.Copper(0.2e-3, 0.15e-3);
        var coplanar = RlgcExtractor.Extract(new CoupledLineCrossSection(stack, 0, new[] { a, b }, topGround: true));
        var broadside = BroadsideExtractor.Extract(stack, true, new[] { new LayeredTrace(a, 0), new LayeredTrace(b, 0) });
        double c = Worst(broadside.CapacitanceFaradsPerMeter, coplanar.CapacitanceFaradsPerMeter);
        double loss = Worst(broadside.CapacitanceLossFaradsPerMeter, coplanar.CapacitanceLossFaradsPerMeter);
        double l = Worst(broadside.InductanceHenriesPerMeter, coplanar.InductanceHenriesPerMeter);
        _out.WriteLine($"C {c:e2}, C″ {loss:e2}, L {l:e2} apart");
        Assert.InRange(c, 0, 1e-9);
        Assert.InRange(loss, 0, 1e-9);
        Assert.InRange(l, 0, 1e-9);
    }

    [Fact]
    public void TwoHeightsInAir_AreTheImageSolution()
    {
        // Strips 0.3 and 0.2 mm wide at 0.2 and 0.35 mm over a ground, in air, 0.1 mm apart
        // laterally: the potential of a line charge over a ground is its log and its image's,
        // −(1/2πε₀)[ln|r − r′| − ln|r − r̄′|], and between horizontal panels each is a closed-form
        // log moment with the height difference (or sum) as its depth.
        double z1 = 0.2e-3, z2 = 0.35e-3;
        var stack = new LayeredStackup(new[] { new Layer(1, 0, z1), new Layer(1, 0, z2 - z1) });
        var a = TraceCrossSection.Copper(0, 0.3e-3);
        var b = TraceCrossSection.Copper(0.35e-3, 0.2e-3);
        int perTrace = 48;
        var spectral = BroadsideExtractor.Extract(stack, false, new[] { new LayeredTrace(a, 0), new LayeredTrace(b, 1) }, perTrace);

        var panels = new List<(double Start, double End, double Z, int Conductor)>();
        foreach (var (trace, z, conductor) in new[] { (a, z1, 0), (b, z2, 1) })
        {
            double half = trace.WidthMeters / 2, previous = trace.CenterMeters - half;
            for (int j = 1; j <= perTrace; j++)
            {
                double edge = trace.CenterMeters - half * Math.Cos(Math.PI * j / perTrace);
                panels.Add((previous, edge, z, conductor));
                previous = edge;
            }
        }
        int m = panels.Count;
        double eps0 = 8.8541878128e-12;
        var matrix = new ComplexDenseMatrix(m, m);
        for (int i = 0; i < m; i++)
            for (int j = 0; j < m; j++)
            {
                var (p, q) = (panels[i], panels[j]);
                double direct = RlgcExtractor.LogMoment(p.Start, p.End, q.Start, q.End, Math.Abs(p.Z - q.Z));
                double image = RlgcExtractor.LogMoment(p.Start, p.End, q.Start, q.End, p.Z + q.Z);
                matrix[i, j] = -(direct - image) / (2 * Math.PI * eps0);
            }
        var lu = ComplexLu.Factor(matrix);
        var c = new double[2, 2];
        for (int k = 0; k < 2; k++)
        {
            var rhs = new Complex[m];
            for (int i = 0; i < m; i++) rhs[i] = panels[i].Conductor == k ? panels[i].End - panels[i].Start : 0;
            var charge = lu.Solve(rhs);
            for (int i = 0; i < m; i++) c[panels[i].Conductor, k] += charge[i].Real * (panels[i].End - panels[i].Start);
        }
        double worst = Worst(spectral.CapacitanceFaradsPerMeter, c);
        _out.WriteLine($"C11 {c[0, 0] * 1e12:f4}, C22 {c[1, 1] * 1e12:f4}, C12 {c[0, 1] * 1e12:f4} pF/m by images; spectral {worst:e2} apart");
        Assert.InRange(worst, 0, 1e-5);
    }

    [Fact]
    public void BetweenTwoPlanesInOneDielectric_LTimesCIsTheMediums()
    {
        // A broadside pair between two planes, all one dielectric: the line is TEM, so L·C is
        // µ₀ε₀εr times the identity, whatever the geometry.
        double er = 3.8;
        var stack = new LayeredStackup(new[] { new Layer(er, 0, 0.2e-3), new Layer(er, 0, 0.12e-3), new Layer(er, 0, 0.2e-3) });
        var result = BroadsideExtractor.Extract(stack, true, new[]
        {
            new LayeredTrace(TraceCrossSection.Copper(0, 0.12e-3), 0),
            new LayeredTrace(TraceCrossSection.Copper(0.05e-3, 0.12e-3), 1),
        });
        var l = result.InductanceHenriesPerMeter;
        var c = result.CapacitanceFaradsPerMeter;
        double target = 4e-7 * Math.PI * 8.8541878128e-12 * er;
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                double lc = l[i, 0] * c[0, j] + l[i, 1] * c[1, j];
                Assert.InRange(Math.Abs(lc - (i == j ? target : 0)) / target, 0, 1e-9);
            }
        Assert.True(-c[0, 1] > 0.2 * c[0, 0], "a broadside pair 0.12 mm apart couples strongly");
    }
}
