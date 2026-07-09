using OpenSim.Geometry.Step;
using OpenSim.Geometry.Step.Part21;
using OpenSim.Geometry.Step.Schema;
using Xunit;

namespace OpenSim.Tests.Geometry.Step;

public class StepUnitsTests
{
    private static string Wrap(string data) =>
        "ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION((''),'2;1');\n" +
        "FILE_NAME('m','',(''),(''),'','','');\nFILE_SCHEMA(('AP214'));\n" +
        "ENDSEC;\nDATA;\n" + data + "\nENDSEC;\nEND-ISO-10303-21;\n";

    private const string MillimetreContext =
        "#10=(GEOMETRIC_REPRESENTATION_CONTEXT(3)" +
        "GLOBAL_UNCERTAINTY_ASSIGNED_CONTEXT((#12))" +
        "GLOBAL_UNIT_ASSIGNED_CONTEXT((#11,#20,#21))" +
        "REPRESENTATION_CONTEXT('',''));\n" +
        "#11=(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.));\n" +
        "#12=UNCERTAINTY_MEASURE_WITH_UNIT(LENGTH_MEASURE(0.01),#11," +
        "'DISTANCE_ACCURACY_VALUE','');\n" +
        "#20=(NAMED_UNIT(*)PLANE_ANGLE_UNIT()SI_UNIT($,.RADIAN.));\n" +
        "#21=(NAMED_UNIT(*)SI_UNIT($,.STERADIAN.)SOLID_ANGLE_UNIT());";

    [Fact]
    public void Millimetres_ResolveToScale1e3_WithUncertainty()
    {
        var ctx = StepUnits.Resolve(Part21Parser.Parse(Wrap(MillimetreContext)));
        Assert.Equal(1e-3, ctx.MetersPerUnit, 15);
        Assert.NotNull(ctx.UncertaintyMeters);
        Assert.Equal(1e-5, ctx.UncertaintyMeters!.Value, 12); // 0.01 mm
    }

    [Fact]
    public void PlainMetres_ResolveToScale1()
    {
        var ctx = StepUnits.Resolve(Part21Parser.Parse(Wrap(
            "#10=(GLOBAL_UNIT_ASSIGNED_CONTEXT((#11))REPRESENTATION_CONTEXT('',''));\n" +
            "#11=(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT($,.METRE.));")));
        Assert.Equal(1.0, ctx.MetersPerUnit, 15);
        Assert.Null(ctx.UncertaintyMeters);
    }

    [Fact]
    public void InchConversionBasedUnit_Resolves25_4mm()
    {
        var ctx = StepUnits.Resolve(Part21Parser.Parse(Wrap(
            "#10=(GLOBAL_UNIT_ASSIGNED_CONTEXT((#11))REPRESENTATION_CONTEXT('',''));\n" +
            "#11=(CONVERSION_BASED_UNIT('INCH',#13)LENGTH_UNIT()NAMED_UNIT(#14));\n" +
            "#13=MEASURE_WITH_UNIT(LENGTH_MEASURE(25.4),#15);\n" +
            "#14=DIMENSIONAL_EXPONENTS(1.,0.,0.,0.,0.,0.,0.);\n" +
            "#15=(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.));")));
        Assert.Equal(0.0254, ctx.MetersPerUnit, 15);
    }

    [Fact]
    public void MissingLengthUnit_FailsLoudly()
    {
        var ex = Assert.Throws<StepImportException>(() =>
            StepUnits.Resolve(Part21Parser.Parse(Wrap("#1=CARTESIAN_POINT('',(0.,0.,0.));"))));
        Assert.Contains("refusing to guess", ex.Message);
    }

    [Fact]
    public void DisagreeingContexts_FailLoudly()
    {
        var ex = Assert.Throws<StepImportException>(() =>
            StepUnits.Resolve(Part21Parser.Parse(Wrap(
                "#10=(GLOBAL_UNIT_ASSIGNED_CONTEXT((#11))REPRESENTATION_CONTEXT('',''));\n" +
                "#11=(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.));\n" +
                "#20=(GLOBAL_UNIT_ASSIGNED_CONTEXT((#21))REPRESENTATION_CONTEXT('',''));\n" +
                "#21=(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT($,.METRE.));"))));
        Assert.Contains("ambiguous length units", ex.Message);
    }

    [Fact]
    public void ExampleModel_ResolvesMillimetresAnd0_01mmUncertainty()
    {
        string? path = Part21Tests.FindExampleStepFile();
        if (path is null) return; // example not present in this checkout

        var ctx = StepUnits.Resolve(Part21Parser.ParseFile(path));
        Assert.Equal(1e-3, ctx.MetersPerUnit, 15);
        Assert.Equal(1e-5, ctx.UncertaintyMeters!.Value, 12);
    }
}
