using System.Globalization;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.NonSequential.Sources;

/// <summary>
/// Source Diode (help, "Source Diode"): one diode or a 1-D or 2-D array of them. Parameters
/// 6 Astigmatism, 7 X divergence αx (degrees), 8 X super-Gaussian factor Gx, 9 Y divergence αy,
/// 10 Gy, 11–12 the number of diodes in X and Y, 13–14 their spacing, 15 Wx, 16 sx, 17 Hx,
/// 18 Wy, 19 sy, 20 Hy.
/// <list type="bullet">
/// <item><b>Angles.</b> I(θx, θy) = I₀·exp(−2[(θx/αx)^(2Gx) + (θy/αy)^(2Gy)]), θx the angle of
/// the ray projected on the XZ plane with the z axis (tan θx = l/n) and θy likewise in YZ
/// (tan θy = m/n); 0.01 ≤ G ≤ 50. The help says values beyond 90° are resampled "until a
/// physically plausible angle is found", which reads as sampling θx and θy independently from
/// these one-dimensional profiles restricted to (−90°, 90°): that is done here, exactly, by
/// inverting each profile's CDF on (−90°, 90°) (<see cref="NscInverseCdf"/>), and the direction is
/// (tan θx, tan θy, 1) normalised. A divergence of 0 gives θ = 0.</item>
/// <item><b>Start points</b> (astigmatism 0): density ∝ exp(−2[(x/sx)^(2Hx) + (y/sy)^(2Hy)]) for
/// |x| &lt; Wx and |y| &lt; Wy, Hx, Hy ≥ 0.01. A zero half width, or a zero width s (the limit of the
/// formula as s → 0), puts every point at 0 in that direction.</item>
/// <item><b>Astigmatism</b> A &gt; 0: "the distance along the local Z axis from which the XZ
/// distribution is measured. At the local XY plane at Z = 0, the resulting ray pattern is a line
/// oriented along the local X axis": each ray starts at (A·tan θx, 0, 0). The spatial terms are
/// then ignored.</item>
/// <item><b>Array.</b> Nx × Ny diodes "symmetrically placed ... about the local coordinate
/// origin", at ((i − (Nx − 1)/2)·Δx, (j − (Ny − 1)/2)·Δy); each ray picks a diode from its fifth
/// number, so the source's power is shared equally among them (a reading: the help gives the
/// power as the source's total, parameter 3).</item>
/// </list>
/// </summary>
public sealed class NscSourceDiode : NscSource
{
    private readonly NscInverseCdf? _thetaX, _thetaY, _x, _y;

    public double Astigmatism { get; }
    public double DivergenceXDegrees { get; }
    public double SuperGaussX { get; }
    public double DivergenceYDegrees { get; }
    public double SuperGaussY { get; }
    public int CountX { get; }
    public int CountY { get; }
    public double SpacingX { get; }
    public double SpacingY { get; }
    public double WidthX { get; }
    public double SigmaX { get; }
    public double SpatialGaussX { get; }
    public double WidthY { get; }
    public double SigmaY { get; }
    public double SpatialGaussY { get; }

    public override string TypeName => "Source Diode";
    public override int Dimensions => 5;
    public override string Distribution => $"{CountX} × {CountY} diode(s), super-Gaussian far field {F(DivergenceXDegrees)}° × {F(DivergenceYDegrees)}°";
    public override IReadOnlyList<string> Notes { get; }

    public override Aabb LocalBounds
    {
        get
        {
            double hx = 0.5 * (CountX - 1) * Math.Abs(SpacingX) + (Astigmatism > 0 ? 0 : WidthX);
            double hy = 0.5 * (CountY - 1) * Math.Abs(SpacingY) + (Astigmatism > 0 ? 0 : WidthY);
            return new Aabb(new Vector3D(-hx, -hy, 0), new Vector3D(hx, hy, 0));
        }
    }

    public NscSourceDiode(double astigmatism, double divergenceXDegrees, double superGaussX, double divergenceYDegrees, double superGaussY,
        int countX = 1, int countY = 1, double spacingX = 0, double spacingY = 0,
        double widthX = 0, double sigmaX = 0, double spatialGaussX = 1, double widthY = 0, double sigmaY = 0, double spatialGaussY = 1,
        NscSourceCommon? common = null, NscSourceProperties? properties = null)
        : base(common, properties)
    {
        Astigmatism = Need(astigmatism, "the astigmatism (meters)", TypeName, 0);
        DivergenceXDegrees = Need(divergenceXDegrees, "the X divergence (degrees)", TypeName, 0);
        SuperGaussX = Need(superGaussX, "the X super-Gaussian factor", TypeName, 0.01, 50);
        DivergenceYDegrees = Need(divergenceYDegrees, "the Y divergence (degrees)", TypeName, 0);
        SuperGaussY = Need(superGaussY, "the Y super-Gaussian factor", TypeName, 0.01, 50);
        if (countX < 1 || countY < 1) throw new ArgumentException($"Source Diode: the number of diodes in X and Y must be at least 1; they are {countX} and {countY}.");
        CountX = countX;
        CountY = countY;
        SpacingX = Need(spacingX, "the X spacing (meters)", TypeName);
        SpacingY = Need(spacingY, "the Y spacing (meters)", TypeName);
        WidthX = Need(widthX, "the X half width Wx (meters)", TypeName, 0);
        SigmaX = Need(sigmaX, "the X Gaussian width sx (meters)", TypeName, 0);
        SpatialGaussX = Need(spatialGaussX, "the X spatial super-Gaussian factor Hx", TypeName, 0.01);
        WidthY = Need(widthY, "the Y half width Wy (meters)", TypeName, 0);
        SigmaY = Need(sigmaY, "the Y Gaussian width sy (meters)", TypeName, 0);
        SpatialGaussY = Need(spatialGaussY, "the Y spatial super-Gaussian factor Hy", TypeName, 0.01);

        _thetaX = Profile(DivergenceXDegrees, SuperGaussX, 90, "Source Diode, θx (degrees)");
        _thetaY = Profile(DivergenceYDegrees, SuperGaussY, 90, "Source Diode, θy (degrees)");
        if (Astigmatism == 0)
        {
            _x = WidthX > 0 && SigmaX > 0 ? Profile(SigmaX, SpatialGaussX, WidthX, "Source Diode, x (meters)") : null;
            _y = WidthY > 0 && SigmaY > 0 ? Profile(SigmaY, SpatialGaussY, WidthY, "Source Diode, y (meters)") : null;
        }
        Notes = new[] { _thetaX, _thetaY, _x, _y }.Where(c => c is not null).SelectMany(c => c!.Notes).ToArray();
    }

    /// <summary>exp(−2|t/width|^(2g)) on (−limit, limit); null for a zero width (all at 0).</summary>
    private static NscInverseCdf? Profile(double width, double g, double limit, string where) =>
        width > 0 ? new NscInverseCdf(t => Math.Exp(-2 * Math.Pow(Math.Abs(t / width), 2 * g)), -limit, limit, where, 0) : null;

    public override void Sample(ReadOnlySpan<double> u, SplitMix64 extra, out Vector3D position, out Vector3D direction)
    {
        double tx = (_thetaX?.Sample(u[0]) ?? 0) * Math.PI / 180;
        double ty = (_thetaY?.Sample(u[1]) ?? 0) * Math.PI / 180;
        double p = Math.Tan(tx), q = Math.Tan(ty);
        direction = new Vector3D(p, q, 1);
        double x, y;
        if (Astigmatism > 0) { x = Astigmatism * p; y = 0; }
        else { x = _x?.Sample(u[2]) ?? 0; y = _y?.Sample(u[3]) ?? 0; }
        int n = CountX * CountY;
        int k = Math.Min(n - 1, (int)(u[4] * n));
        int i = k % CountX, j = k / CountX;
        x += (i - 0.5 * (CountX - 1)) * SpacingX;
        y += (j - 0.5 * (CountY - 1)) * SpacingY;
        position = new Vector3D(x, y, 0);
    }

    public override string Describe(Func<double, string> length) =>
        $"divergence {F(DivergenceXDegrees)}° × {F(DivergenceYDegrees)}°, super-Gaussian {F(SuperGaussX)} × {F(SuperGaussY)}, {CountX} × {CountY} diode(s)"
        + (Astigmatism > 0 ? $", astigmatism {length(Astigmatism)}" : $", region {length(WidthX)} × {length(WidthY)}");

    private static string F(double v) => v.ToString("g6", CultureInfo.InvariantCulture);
}
