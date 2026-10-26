using OpenSim.Geometry.Step;
using OpenSim.Geometry.Step.Part21;
using OpenSim.Geometry.Step.Schema;
using Xunit;

namespace OpenSim.Tests.Geometry.Step;

/// <summary>
/// D21 (evaluation 2026-09-09, worked 2026-10-25): the importer read a cone's semi-angle as
/// radians whatever plane-angle unit the file declared, and refused a ring edge on a closed
/// B-spline ("the curve is not periodic") — a fillet's boundary. Now the plane-angle unit is
/// resolved beside the length unit (SI radian, or a conversion unit such as the degree) and
/// scales the semi-angle, and a closed B-spline curve is periodic in its parameter.
/// </summary>
public class StepAnglesAndClosedCurvesTests
{
    private static string Wrap(string data) =>
        "ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION((''),'2;1');\n" +
        "FILE_NAME('m','',(''),(''),'','','');\nFILE_SCHEMA(('AP214'));\n" +
        "ENDSEC;\nDATA;\n" + data + "\nENDSEC;\nEND-ISO-10303-21;\n";

    private const string Millimetres = "#11=(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.));\n";

    [Fact]
    public void ADegreeUnit_ResolvesToPiOver180_RadiansPerUnit()
    {
        var ctx = StepUnits.Resolve(Part21Parser.Parse(Wrap(
            "#10=(GLOBAL_UNIT_ASSIGNED_CONTEXT((#11,#20))REPRESENTATION_CONTEXT('',''));\n" + Millimetres +
            "#20=(CONVERSION_BASED_UNIT('DEGREE',#21)NAMED_UNIT(#23)PLANE_ANGLE_UNIT());\n" +
            "#21=MEASURE_WITH_UNIT(PLANE_ANGLE_MEASURE(0.0174532925199433),#22);\n" +
            "#22=(NAMED_UNIT(*)PLANE_ANGLE_UNIT()SI_UNIT($,.RADIAN.));\n" +
            "#23=DIMENSIONAL_EXPONENTS(0.,0.,0.,0.,0.,0.,0.);")));
        Assert.True(ctx.PlaneAngleDeclared);
        Assert.Equal(Math.PI / 180, ctx.RadiansPerUnit, 12);
        Assert.Equal(1e-3, ctx.MetersPerUnit, 15);
    }

    [Fact]
    public void ARadianUnit_IsOne_AndNoneIsOneUndeclared()
    {
        var radian = StepUnits.Resolve(Part21Parser.Parse(Wrap(
            "#10=(GLOBAL_UNIT_ASSIGNED_CONTEXT((#11,#20))REPRESENTATION_CONTEXT('',''));\n" + Millimetres +
            "#20=(NAMED_UNIT(*)PLANE_ANGLE_UNIT()SI_UNIT($,.RADIAN.));")));
        Assert.True(radian.PlaneAngleDeclared);
        Assert.Equal(1.0, radian.RadiansPerUnit, 15);

        var none = StepUnits.Resolve(Part21Parser.Parse(Wrap(
            "#10=(GLOBAL_UNIT_ASSIGNED_CONTEXT((#11))REPRESENTATION_CONTEXT('',''));\n" + Millimetres)));
        Assert.False(none.PlaneAngleDeclared);
        Assert.Equal(1.0, none.RadiansPerUnit, 15);
    }

    [Fact]
    public void ADegreeUnit_InTheSimpleMeasureForm_Resolves()
    {
        // The PLANE_ANGLE_MEASURE_WITH_UNIT simple entity (what OCCT writes), its radian base an
        // SI_UNIT without a PLANE_ANGLE_UNIT record.
        var ctx = StepUnits.Resolve(Part21Parser.Parse(Wrap(
            "#10=(GLOBAL_UNIT_ASSIGNED_CONTEXT((#11,#20))REPRESENTATION_CONTEXT('',''));\n" + Millimetres +
            "#20=(CONVERSION_BASED_UNIT('DEGREE',#21)NAMED_UNIT(#23)PLANE_ANGLE_UNIT());\n" +
            "#21=PLANE_ANGLE_MEASURE_WITH_UNIT(PLANE_ANGLE_MEASURE(1.745329251994E-02),#22);\n" +
            "#22=(NAMED_UNIT(*)SI_UNIT($,.RADIAN.));\n" +
            "#23=DIMENSIONAL_EXPONENTS(0.,0.,0.,0.,0.,0.,0.);")));
        Assert.True(ctx.PlaneAngleDeclared);
        Assert.Equal(1.745329251994e-2, ctx.RadiansPerUnit, 15);
    }

    [Fact]
    public void DisagreeingPlaneAngleUnits_FailLoudly()
    {
        var ex = Assert.Throws<StepImportException>(() => StepUnits.Resolve(Part21Parser.Parse(Wrap(
            "#10=(GLOBAL_UNIT_ASSIGNED_CONTEXT((#11,#20))REPRESENTATION_CONTEXT('',''));\n" +
            "#30=(GLOBAL_UNIT_ASSIGNED_CONTEXT((#11,#22))REPRESENTATION_CONTEXT('',''));\n" + Millimetres +
            "#20=(CONVERSION_BASED_UNIT('DEGREE',#21)NAMED_UNIT(#23)PLANE_ANGLE_UNIT());\n" +
            "#21=MEASURE_WITH_UNIT(PLANE_ANGLE_MEASURE(0.0174532925199433),#22);\n" +
            "#22=(NAMED_UNIT(*)PLANE_ANGLE_UNIT()SI_UNIT($,.RADIAN.));\n" +
            "#23=DIMENSIONAL_EXPONENTS(0.,0.,0.,0.,0.,0.,0.);"))));
        Assert.Contains("plane-angle", ex.Message);
    }

    [Fact]
    public void ACone_WrittenInDegrees_IsTheConeWrittenInRadians()
    {
        // The same frustum three ways: no angle unit (radians by default, as before), radians
        // declared, degrees declared with the semi-angle in degrees. One mesh.
        var importer = new StepImporter();
        var none = importer.ImportText(StepFixtures.ConeFrustum(6, 3, 10));
        var radian = importer.ImportText(StepFixtures.ConeFrustum(6, 3, 10, StepFixtures.AngleUnit.Radian));
        var degree = importer.ImportText(StepFixtures.ConeFrustum(6, 3, 10, StepFixtures.AngleUnit.Degree));

        double v0 = none.Mesh.ComputeSignedVolume(), v1 = radian.Mesh.ComputeSignedVolume(), v2 = degree.Mesh.ComputeSignedVolume();
        Assert.Equal(1.0, v1 / v0, 12);
        Assert.Equal(1.0, v2 / v0, 9);                                  // the degree→radian product to round-off
        Assert.Equal(none.Mesh.Vertices.Count, degree.Mesh.Vertices.Count);
        // The frustum's volume: πh(r1² + r1r2 + r2²)/3 = π·10·(36 + 18 + 9)/3 mm³, the chords inside it.
        double exact = Math.PI * 10e-3 * (36 + 18 + 9) * 1e-6 / 3;
        Assert.InRange(v2, 0.97 * exact, exact);
        Assert.Contains(degree.Notes, n => n.Contains("plane angles: 0.01745329251994") && n.Contains("rad per unit"));
        Assert.Contains(none.Notes, n => n.Contains("no plane-angle unit declared"));
    }

    [Fact]
    public void ARingEdge_OnAClosedBSpline_Tessellates()
    {
        // The rounded-square prism: its outline is a closed cubic B-spline and both its caps are
        // bounded by one ring edge on it — refused before D21.
        var (text, octagonArea) = StepFixtures.ClosedBSplinePrism(5, 8);
        var report = new StepImporter().ImportText(text);
        var mesh = report.Mesh;
        Assert.True(mesh.IsWatertight());
        Assert.Equal(3, mesh.FaceCount);

        // A B-spline lies inside its control polygon: the outline's area is below the octagon's
        // (control points on the circle of radius 5√2), and a cubic through eight of them is not
        // far below it; the mesh's volume is that area times the height, chords inside.
        double octagon = octagonArea * 1e-6;
        double volume = mesh.ComputeSignedVolume();
        Assert.InRange(volume / 8e-3, 0.8 * octagon, octagon);
    }

    [Fact]
    public void AClosedBSplineCurve_IsPeriodic_AndWrapsItsParameter()
    {
        var ones9 = Enumerable.Repeat(1.0, 9).ToList();
        // A closed cubic through eight points on a circle, the first repeated last, clamped knots.
        var ctrl = Enumerable.Range(0, 9).Select(i => i * Math.PI / 4)
            .Select(phi => new OpenSim.Core.Numerics.Vector3D(Math.Cos(phi), Math.Sin(phi), 0)).ToList();
        var knots = new double[] { 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 6, 6, 6 };
        var flagged = new StepBSplineCurve(1, 3, ctrl, knots, ones9, ClosedFlag: true);
        var unknown = new StepBSplineCurve(2, 3, ctrl, knots, ones9);
        var deniedByFlag = new StepBSplineCurve(4, 3, ctrl, knots, ones9, ClosedFlag: false);
        var open = new StepBSplineCurve(3, 3, ctrl.Take(8).ToList(), new double[] { 0, 0, 0, 0, 1, 2, 3, 4, 5, 5, 5, 5 }, Enumerable.Repeat(1.0, 8).ToList());

        Assert.Equal(6.0, flagged.Period!.Value, 12);
        Assert.True(unknown.IsClosed);                                   // ends meet: closed without the flag
        Assert.Equal(6.0, unknown.Period!.Value, 12);
        Assert.True(deniedByFlag.IsClosed);                              // the flag is informational: ends that meet win
        Assert.Null(open.Period);                                        // ends apart: not periodic

        // Wrapping: a parameter a period on is the same point, and the ends are one point.
        Assert.Equal(flagged.Point(0.0).X, flagged.Point(6.0).X, 12);
        Assert.Equal(flagged.Point(1.7).X, flagged.Point(7.7).X, 12);
        Assert.Equal(flagged.Point(1.7).Y, flagged.Point(-4.3).Y, 12);
    }

    [Fact]
    public void AnUnclampedCurve_IsClosedByItsEnds_NotItsControlPoints()
    {
        // The periodic form exporters write: eight circle points with the first three repeated,
        // uniform knots 0…14 — ends meet at t = 3 and t = 11 though the first and last control
        // points differ; period 8; C² at the joint.
        var eight = Enumerable.Range(0, 8).Select(i => i * Math.PI / 4)
            .Select(phi => new OpenSim.Core.Numerics.Vector3D(Math.Cos(phi), Math.Sin(phi), 0)).ToList();
        var wrapped = eight.Concat(eight.Take(3)).ToList();
        var uniform15 = Enumerable.Range(0, 15).Select(k => (double)k).ToArray();
        var periodic = new StepBSplineCurve(5, 3, wrapped, uniform15, Enumerable.Repeat(1.0, 11).ToList());
        Assert.True(periodic.IsClosed);
        Assert.Equal(8.0, periodic.Period!.Value, 12);
        Assert.Equal(periodic.Point(4.3).X, periodic.Point(12.3).X, 12);
        Assert.Equal(periodic.Point(4.3).Y, periodic.Point(12.3).Y, 12);
        Assert.Equal(periodic.Derivative(3.0).X, periodic.Derivative(11.0).X, 9);   // smooth across the joint

        // An unclamped OPEN curve whose first and last control points coincide: its ends do not.
        var sixCtrl = new[] { eight[0], eight[1], eight[2], eight[3], eight[4], eight[0] };
        var uniform10 = Enumerable.Range(0, 10).Select(k => (double)k).ToArray();
        var openUnclamped = new StepBSplineCurve(6, 3, sixCtrl, uniform10, Enumerable.Repeat(1.0, 6).ToList());
        Assert.False(openUnclamped.IsClosed);
        Assert.Null(openUnclamped.Period);
    }
}
