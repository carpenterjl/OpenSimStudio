using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Surface;

namespace OpenSim.Rf.Layered;

/// <summary>
/// FU-34 — the far field and the surface-wave power of sheet currents on SEVERAL interfaces of
/// one grounded stackup (<see cref="SurfaceMomSolver.SolveLevels"/>). Each level radiates through
/// the stack with its own region-0 amplitude (<see cref="TransmissionLineGreens.RadiationAmplitude"/>
/// with that level as the source interface), and the levels add COHERENTLY — the field, not the
/// power. The surface wave likewise: per mode, the power is the bilinear form of the levels'
/// current transforms with the mode's residues between every pair of levels (the per-height
/// residues of <see cref="TransmissionLineGreens.PoleFieldResidues"/>, which on the diagonal are
/// each level's own). With one level both reduce to <see cref="LayeredFarField"/>'s.
/// </summary>
public static class LevelsFarField
{
    /// <summary>The far-field pattern of the edge currents of a multi-level sheet.</summary>
    public static FarFieldPattern Compute(SurfaceStructure surface, LayeredStackup stackup, double frequencyHz,
        IReadOnlyList<Complex> edgeCurrents, int thetaCount = 32, int phiCount = 64)
    {
        var (levels, currents) = SplitByLevel(surface, stackup, edgeCurrents);
        double k0 = 2 * Math.PI * frequencyHz / RfConstants.SpeedOfLight;
        double omega = 2 * Math.PI * frequencyHz;
        double eta = Math.Sqrt(RfConstants.Mu0 / RfConstants.Eps0);
        var (uNodes, uWeights) = GaussLegendre.Rule(thetaCount, 0, 1);
        var theta = uNodes.Select(Math.Acos).ToArray();
        var phi = Enumerable.Range(0, phiCount).Select(i => 2 * Math.PI * i / phiCount).ToArray();
        double phiWeight = 2 * Math.PI / phiCount;
        var intensity = new double[thetaCount, phiCount];
        double total = 0;
        for (int ti = 0; ti < thetaCount; ti++)
        {
            double cosTheta = uNodes[ti], sinTheta = Math.Sin(theta[ti]);
            double kRho = k0 * sinTheta;
            var kz0 = new Complex(k0 * cosTheta, 0);
            var amplitudes = levels.Select(m =>
            {
                var (gA, w) = TransmissionLineGreens.RadiationAmplitude(stackup, k0, kRho, kz0, m);
                return (gA, ThetaFactor: cosTheta + Complex.ImaginaryOne * k0 * sinTheta * sinTheta * w);
            }).ToArray();
            double amplitude = omega * k0 * cosTheta / (4 * Math.PI);
            for (int pi = 0; pi < phiCount; pi++)
            {
                var (sinPhi, cosPhi) = Math.SinCos(phi[pi]);
                Complex eTheta = Complex.Zero, ePhi = Complex.Zero;
                for (int l = 0; l < levels.Length; l++)
                {
                    var (jx, jy) = LayeredFarField.SpectralCurrent(surface, currents[l], kRho * cosPhi, kRho * sinPhi);
                    var jPar = cosPhi * jx + sinPhi * jy;
                    var jPerp = -sinPhi * jx + cosPhi * jy;
                    eTheta += amplitude * amplitudes[l].gA * amplitudes[l].ThetaFactor * jPar;
                    ePhi += amplitude * amplitudes[l].gA * jPerp;
                }
                double u = (eTheta.Magnitude * eTheta.Magnitude + ePhi.Magnitude * ePhi.Magnitude) / (2 * eta);
                intensity[ti, pi] = u;
                total += uWeights[ti] * phiWeight * u;
            }
        }
        double maxDirectivity = 0;
        foreach (double u in intensity) maxDirectivity = Math.Max(maxDirectivity, 4 * Math.PI * u / total);
        return new FarFieldPattern(theta, phi, intensity, total, maxDirectivity);
    }

    /// <summary>The power the extracted surface-wave modes carry away from a multi-level sheet:
    /// per mode k_p, Re Σ_{l,l′} [ωk_p/16π·Res_A(l,l′)·∮J̃_l*·J̃_l′ − k_p³/16πω·Res_Φ(l,l′)·∮J̃r_l*·J̃r_l′],
    /// the single-level formula's bilinear form over the levels (J̃r the radial component).</summary>
    public static double SurfaceWavePowerWatts(SurfaceStructure surface, LayeredStackup stackup, double frequencyHz,
        IReadOnlyList<Complex> edgeCurrents, int alphaCount = 64)
    {
        var (levels, currents) = SplitByLevel(surface, stackup, edgeCurrents);
        double k0 = 2 * Math.PI * frequencyHz / RfConstants.SpeedOfLight;
        double omega = 2 * Math.PI * frequencyHz;
        var heights = stackup.InterfaceHeights();
        double power = 0;
        foreach (var pole in SurfaceWavePoles.Find(stackup, k0))
        {
            double kp = pole.KRho.Real;
            var pk = new Complex(kp, 0);
            int count = levels.Length;
            var resA = new Complex[count, count];
            var resPhi = new Complex[count, count];
            for (int o = 0; o < count; o++)
                for (int s = 0; s < count; s++)
                {
                    var r = TransmissionLineGreens.PoleFieldResidues(stackup, k0, pk, pole.IsTm, levels[s], heights[levels[o]]);
                    resA[o, s] = r.GA;
                    resPhi[o, s] = r.Phi;
                }
            var jx = new Complex[count, alphaCount];
            var jy = new Complex[count, alphaCount];
            for (int l = 0; l < count; l++)
                for (int i = 0; i < alphaCount; i++)
                {
                    var (sin, cos) = Math.SinCos(2 * Math.PI * i / alphaCount);
                    (jx[l, i], jy[l, i]) = LayeredFarField.SpectralCurrent(surface, currents[l], kp * cos, kp * sin);
                }
            double dAlpha = 2 * Math.PI / alphaCount;
            Complex sum = Complex.Zero;
            for (int o = 0; o < count; o++)
                for (int s = 0; s < count; s++)
                {
                    Complex all = Complex.Zero, radial = Complex.Zero;
                    for (int i = 0; i < alphaCount; i++)
                    {
                        var (sin, cos) = Math.SinCos(2 * Math.PI * i / alphaCount);
                        all += Complex.Conjugate(jx[o, i]) * jx[s, i] + Complex.Conjugate(jy[o, i]) * jy[s, i];
                        radial += Complex.Conjugate(cos * jx[o, i] + sin * jy[o, i]) * (cos * jx[s, i] + sin * jy[s, i]);
                    }
                    sum += omega * kp / (16 * Math.PI) * resA[o, s] * all * dAlpha
                           - kp * kp * kp / (16 * Math.PI * omega) * resPhi[o, s] * radial * dAlpha;
                }
            power += sum.Real;
        }
        return power;
    }

    /// <summary>One via's currents for the far field and the surface-wave ledger: its tube, and
    /// each end's attachment fan as horizontal current on that end's level (the lower drawn into
    /// its vertex, the upper out of it).</summary>
    private sealed record ViaLeg(double X, double Y, double[] Nodes, Complex[] Currents,
        AttachmentFan LowerFan, int LowerLevel, AttachmentFan UpperFan, int UpperLevel)
    {
        public Complex LowerCoeff => -Currents[0];
        public Complex UpperCoeff => Currents[^1];
    }

    private static ViaLeg[] Legs(SurfaceStructure surface, LevelsSolution solution, int port) =>
        solution.Vias.Select((via, i) => new ViaLeg(via.Geometry.X, via.Geometry.Y, solution.ViaNodes[i],
            solution.ViaCurrents[port][i],
            new AttachmentFan(surface, solution.LowerVertices[i], via.Geometry.RadiusMeters), via.LowerInterface,
            new AttachmentFan(surface, solution.UpperVertices[i], via.Geometry.RadiusMeters), via.UpperInterface)).ToArray();

    /// <summary>The level's horizontal current transform: its RWG currents and every via fan on it.</summary>
    private static (Complex Jx, Complex Jy) LevelTransform(SurfaceStructure surface, Complex[] levelCurrents,
        int level, ViaLeg[] legs, double kx, double ky)
    {
        var (jx, jy) = LayeredFarField.SpectralCurrent(surface, levelCurrents, kx, ky);
        foreach (var leg in legs)
        {
            if (leg.LowerLevel == level)
            {
                var (dx, dy) = leg.LowerFan.CurrentTransform(surface, kx, ky);
                jx += leg.LowerCoeff * dx; jy += leg.LowerCoeff * dy;
            }
            if (leg.UpperLevel == level)
            {
                var (dx, dy) = leg.UpperFan.CurrentTransform(surface, kx, ky);
                jx += leg.UpperCoeff * dx; jy += leg.UpperCoeff * dy;
            }
        }
        return (jx, jy);
    }

    /// <summary>The far field of one port's excitation of a multi-level solve with vias: every
    /// level's sheet and fan currents through its own amplitude, and each via tube's vertical
    /// current (<see cref="LayeredFarField.VerticalAmplitude(LayeredStackup, double, double, double[], Complex[])"/>)
    /// at its lateral position, all coherently.</summary>
    public static FarFieldPattern Compute(SurfaceStructure surface, LayeredStackup stackup, LevelsSolution solution,
        int port = 0, int thetaCount = 32, int phiCount = 64)
    {
        double frequencyHz = solution.FrequencyHz;
        var (levels, currents) = SplitByLevel(surface, stackup, solution.EdgeCurrents[port]);
        var legs = Legs(surface, solution, port);
        double k0 = 2 * Math.PI * frequencyHz / RfConstants.SpeedOfLight;
        double omega = 2 * Math.PI * frequencyHz;
        double eta = Math.Sqrt(RfConstants.Mu0 / RfConstants.Eps0);
        var (uNodes, uWeights) = GaussLegendre.Rule(thetaCount, 0, 1);
        var theta = uNodes.Select(Math.Acos).ToArray();
        var phi = Enumerable.Range(0, phiCount).Select(i => 2 * Math.PI * i / phiCount).ToArray();
        double phiWeight = 2 * Math.PI / phiCount;
        var intensity = new double[thetaCount, phiCount];
        double total = 0;
        for (int ti = 0; ti < thetaCount; ti++)
        {
            double cosTheta = uNodes[ti], sinTheta = Math.Sin(theta[ti]);
            double kRho = k0 * sinTheta;
            var kz0 = new Complex(k0 * cosTheta, 0);
            var amplitudes = levels.Select(m =>
            {
                var (gA, w) = TransmissionLineGreens.RadiationAmplitude(stackup, k0, kRho, kz0, m);
                return (gA, ThetaFactor: cosTheta + Complex.ImaginaryOne * k0 * sinTheta * sinTheta * w);
            }).ToArray();
            var gHats = legs.Select(l => LayeredFarField.VerticalAmplitude(stackup, k0, theta[ti], l.Nodes, l.Currents)).ToArray();
            double amplitude = omega * k0 * cosTheta / (4 * Math.PI);
            for (int pi = 0; pi < phiCount; pi++)
            {
                var (sinPhi, cosPhi) = Math.SinCos(phi[pi]);
                double kx = kRho * cosPhi, ky = kRho * sinPhi;
                Complex eTheta = Complex.Zero, ePhi = Complex.Zero;
                for (int l = 0; l < levels.Length; l++)
                {
                    var (jx, jy) = LevelTransform(surface, currents[l], levels[l], legs, kx, ky);
                    eTheta += amplitude * amplitudes[l].gA * amplitudes[l].ThetaFactor * (cosPhi * jx + sinPhi * jy);
                    ePhi += amplitude * amplitudes[l].gA * (-sinPhi * jx + cosPhi * jy);
                }
                for (int v = 0; v < legs.Length; v++)
                {
                    var (sinPr, cosPr) = Math.SinCos(kx * legs[v].X + ky * legs[v].Y);
                    eTheta += amplitude * (-sinTheta) * new Complex(cosPr, sinPr) * gHats[v];
                }
                double u = (eTheta.Magnitude * eTheta.Magnitude + ePhi.Magnitude * ePhi.Magnitude) / (2 * eta);
                intensity[ti, pi] = u;
                total += uWeights[ti] * phiWeight * u;
            }
        }
        double maxDirectivity = 0;
        foreach (double u in intensity) maxDirectivity = Math.Max(maxDirectivity, 4 * Math.PI * u / total);
        return new FarFieldPattern(theta, phi, intensity, total, maxDirectivity);
    }

    /// <summary>The surface-wave power of one port's excitation with vias: the levels' bilinear
    /// form of <see cref="SurfaceWavePowerWatts(SurfaceStructure, LayeredStackup, double, IReadOnlyList{Complex}, int)"/>,
    /// the tubes' vertical form (TM modes only), and their coherent cross terms through the
    /// vertical kernels' residues read at each level's height — the single-level mixed ledger
    /// (<see cref="LayeredFarField"/>) with the horizontal side summed over levels. Each fan's
    /// point charge at its vertex comes out of its level's horizontal charge, as the tube's end
    /// charge that cancels it is not carried in the tube's line charge.</summary>
    public static double SurfaceWavePowerWatts(SurfaceStructure surface, LayeredStackup stackup, LevelsSolution solution,
        int port = 0, int alphaCount = 64)
    {
        double frequencyHz = solution.FrequencyHz;
        var (levels, currents) = SplitByLevel(surface, stackup, solution.EdgeCurrents[port]);
        var legs = Legs(surface, solution, port);
        double k0 = 2 * Math.PI * frequencyHz / RfConstants.SpeedOfLight;
        double omega = 2 * Math.PI * frequencyHz;
        var heights = stackup.InterfaceHeights();
        var j = Complex.ImaginaryOne;
        var set = new MultiLayerVerticalKernelSet(stackup, frequencyHz);

        var (gn, gw) = GaussLegendre.Rule(4, 0, 1);
        var z = new List<double>(); var jz = new List<Complex>(); var qv = new List<Complex>(); var w = new List<double>(); var owner = new List<int>();
        for (int v = 0; v < legs.Length; v++)
        {
            var nodes = legs[v].Nodes; var tube = legs[v].Currents;
            for (int e = 0; e + 1 < nodes.Length; e++)
            {
                double h = nodes[e + 1] - nodes[e];
                Complex slope = (tube[e + 1] - tube[e]) / h;
                for (int q = 0; q < gn.Length; q++)
                {
                    z.Add(nodes[e] + h * gn[q]);
                    jz.Add(tube[e] * (1 - gn[q]) + tube[e + 1] * gn[q]);
                    qv.Add(j / omega * slope);
                    w.Add(gw[q] * h);
                    owner.Add(v);
                }
            }
        }
        int n = z.Count, count = levels.Length;
        double power = 0;
        foreach (var pole in SurfaceWavePoles.Find(stackup, k0))
        {
            double kp = pole.KRho.Real;
            var pk = new Complex(kp, 0);
            var resA = new Complex[count, count];
            var resPhi = new Complex[count, count];
            for (int o = 0; o < count; o++)
                for (int s = 0; s < count; s++)
                {
                    var r = TransmissionLineGreens.PoleFieldResidues(stackup, k0, pk, pole.IsTm, levels[s], heights[levels[o]]);
                    resA[o, s] = r.GA;
                    resPhi[o, s] = r.Phi;
                }
            bool vertical = pole.IsTm && n > 0;
            Complex[,] resZz = null!, resPhiVv = null!, resXz = null!, resPhiHv = null!;
            if (vertical)
            {
                resZz = new Complex[n, n]; resPhiVv = new Complex[n, n];
                for (int a = 0; a < n; a++)
                    for (int b = 0; b < n; b++)
                    {
                        var r = set.PoleResidues(pk, pole.IsTm, z[a], z[b]);
                        resZz[a, b] = r.GAzz; resPhiVv[a, b] = r.KPhi;
                    }
                resXz = new Complex[count, n]; resPhiHv = new Complex[count, n];
                for (int l = 0; l < count; l++)
                    for (int b = 0; b < n; b++)
                    {
                        var r = set.PoleResidues(pk, pole.IsTm, heights[levels[l]], z[b]);
                        resXz[l, b] = r.GAxz; resPhiHv[l, b] = r.KPhi;
                    }
            }
            double dAlpha = 2 * Math.PI / alphaCount;
            Complex accum = Complex.Zero;
            var jx = new Complex[count]; var jy = new Complex[count]; var jRho = new Complex[count]; var qh = new Complex[count];
            var phase = new Complex[legs.Length];
            for (int i = 0; i < alphaCount; i++)
            {
                var (sin, cos) = Math.SinCos(2 * Math.PI * i / alphaCount);
                double kx = kp * cos, ky = kp * sin;
                for (int l = 0; l < count; l++)
                {
                    (jx[l], jy[l]) = LevelTransform(surface, currents[l], levels[l], legs, kx, ky);
                    jRho[l] = cos * jx[l] + sin * jy[l];
                    qh[l] = kp / omega * jRho[l];
                    foreach (var leg in legs)
                    {
                        if (leg.LowerLevel == levels[l])
                        {
                            var vp = leg.LowerFan.VertexPosition;
                            qh[l] -= j / omega * leg.LowerCoeff * Complex.Exp(j * (kx * vp.X + ky * vp.Y));
                        }
                        if (leg.UpperLevel == levels[l])
                        {
                            var vp = leg.UpperFan.VertexPosition;
                            qh[l] -= j / omega * leg.UpperCoeff * Complex.Exp(j * (kx * vp.X + ky * vp.Y));
                        }
                    }
                }
                Complex qA = Complex.Zero, qPhi = Complex.Zero;
                for (int o = 0; o < count; o++)
                    for (int s = 0; s < count; s++)
                    {
                        qA += resA[o, s] * (Complex.Conjugate(jx[o]) * jx[s] + Complex.Conjugate(jy[o]) * jy[s]);
                        qPhi += resPhi[o, s] * Complex.Conjugate(qh[o]) * qh[s];
                    }
                if (vertical)
                {
                    for (int v = 0; v < legs.Length; v++) phase[v] = Complex.Exp(j * (kx * legs[v].X + ky * legs[v].Y));
                    for (int a = 0; a < n; a++)
                    {
                        var ja = Complex.Conjugate(jz[a] * phase[owner[a]]);
                        var qa = Complex.Conjugate(qv[a] * phase[owner[a]]);
                        for (int b = 0; b < n; b++)
                        {
                            double ww = w[a] * w[b];
                            var pb = phase[owner[b]];
                            qA += ww * ja * resZz[a, b] * jz[b] * pb;
                            qPhi += ww * qa * resPhiVv[a, b] * qv[b] * pb;
                        }
                    }
                    Complex cross = Complex.Zero, crossPhi = Complex.Zero;
                    for (int l = 0; l < count; l++)
                        for (int b = 0; b < n; b++)
                        {
                            var pb = phase[owner[b]];
                            cross += w[b] * Complex.Conjugate(jRho[l]) * (-j * pk) * resXz[l, b] * jz[b] * pb;
                            crossPhi += w[b] * Complex.Conjugate(qh[l]) * resPhiHv[l, b] * qv[b] * pb;
                        }
                    qA += 2 * cross.Real;
                    qPhi += 2 * crossPhi.Real;
                }
                accum += (qA - qPhi) * dAlpha;
            }
            power += omega * kp / (16 * Math.PI) * accum.Real;
        }
        return power;
    }

    /// <summary>The interfaces the sheet's metal is on, and the edge currents of each level alone
    /// (an edge belongs to its triangles' level).</summary>
    private static (int[] Levels, Complex[][] Currents) SplitByLevel(SurfaceStructure surface, LayeredStackup stackup,
        IReadOnlyList<Complex> edgeCurrents)
    {
        var triangleLevels = SurfaceMomSolver.TriangleLevels(surface, stackup);
        var levels = triangleLevels.Distinct().OrderBy(l => l).ToArray();
        var currents = levels.Select(_ => new Complex[edgeCurrents.Count]).ToArray();
        for (int e = 0; e < edgeCurrents.Count; e++)
        {
            int level = triangleLevels[surface.Edges[e].PlusTriangle];
            currents[Array.IndexOf(levels, level)][e] = edgeCurrents[e];
        }
        return (levels, currents);
    }
}
