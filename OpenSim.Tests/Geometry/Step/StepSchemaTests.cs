using OpenSim.Core.Numerics;
using OpenSim.Geometry.Step;
using OpenSim.Geometry.Step.Part21;
using OpenSim.Geometry.Step.Schema;
using Xunit;

namespace OpenSim.Tests.Geometry.Step;

public class StepSchemaTests
{
    private static StepEntityResolver ResolverFor(string text)
    {
        var file = Part21Parser.Parse(text);
        return new StepEntityResolver(file, StepUnits.Resolve(file).MetersPerUnit);
    }

    [Fact]
    public void Box_ResolvesTo6PlanarFaces12Edges8Vertices()
    {
        var resolver = ResolverFor(StepFixtures.Box(1, 2, 3));
        var solid = Assert.Single(resolver.ResolveSolids());

        Assert.Empty(solid.Voids);
        Assert.Equal(6, solid.Outer.Faces.Count);
        Assert.All(solid.Outer.Faces, f =>
        {
            Assert.IsType<StepPlane>(f.Surface);
            Assert.True(f.SameSense);
            var bound = Assert.Single(f.Bounds);
            Assert.True(bound.IsOuter);
            Assert.Equal(4, bound.Loop.Edges.Count);
        });

        // Memoization must make shared topology reference-identical: 12 distinct edges,
        // 8 distinct vertices, each edge used by exactly two faces.
        var edgeUses = solid.Outer.Faces.SelectMany(f => f.Bounds[0].Loop.Edges).ToList();
        var distinctEdges = edgeUses.Select(u => u.Edge).Distinct().ToList();
        Assert.Equal(24, edgeUses.Count);
        Assert.Equal(12, distinctEdges.Count);
        Assert.All(distinctEdges, e => Assert.Equal(2, edgeUses.Count(u => ReferenceEquals(u.Edge, e))));
        Assert.Equal(8, distinctEdges.SelectMany(e => new[] { e.Start, e.End }).Distinct().Count());

        // mm fixture: coordinates arrive scaled to meters.
        var bounds = distinctEdges.SelectMany(e => new[] { e.Start.Point, e.End.Point }).ToList();
        Assert.Equal(1e-3, bounds.Max(p => p.X), 12);
        Assert.Equal(2e-3, bounds.Max(p => p.Y), 12);
        Assert.Equal(3e-3, bounds.Max(p => p.Z), 12);
    }

    [Fact]
    public void Box_LoopsChainHeadToTail_AndCurvesAgreeWithVertices()
    {
        var solid = ResolverFor(StepFixtures.Box(1, 1, 1)).ResolveSolids()[0];
        foreach (var face in solid.Outer.Faces)
        {
            var loop = face.Bounds[0].Loop.Edges;
            for (int i = 0; i < loop.Count; i++)
            {
                var use = loop[i];
                var next = loop[(i + 1) % loop.Count];
                var end = use.Forward ? use.Edge.End : use.Edge.Start;
                var nextStart = next.Forward ? next.Edge.Start : next.Edge.End;
                Assert.True(ReferenceEquals(end, nextStart), "loop must chain head-to-tail");

                // The curve's parameterization must reproduce the vertex geometry exactly.
                var curve = use.Edge.Curve;
                double tStart = curve.ParameterOf(use.Edge.Start.Point);
                Assert.Equal(0.0, Vector3D.Distance(curve.Point(tStart), use.Edge.Start.Point), 12);
            }
        }
    }

    [Fact]
    public void InchBox_ScalesBy0_0254()
    {
        var solid = ResolverFor(StepFixtures.Box(1, 1, 1, StepFixtures.Unit.Inch)).ResolveSolids()[0];
        double maxX = solid.Outer.Faces.SelectMany(f => f.Bounds[0].Loop.Edges)
            .SelectMany(u => new[] { u.Edge.Start.Point.X, u.Edge.End.Point.X }).Max();
        Assert.Equal(0.0254, maxX, 12);
    }

    [Fact]
    public void UnsupportedSurface_FailsNamingEntityAndFace()
    {
        var resolver = ResolverFor(
            "ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION((''),'2;1');\n" +
            "FILE_NAME('m','',(''),(''),'','','');\nFILE_SCHEMA(('AP214'));\nENDSEC;\nDATA;\n" +
            "#1=(GLOBAL_UNIT_ASSIGNED_CONTEXT((#2))REPRESENTATION_CONTEXT('',''));\n" +
            "#2=(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.));\n" +
            "#10=ADVANCED_FACE('',(),#11,.T.);\n" +
            "#11=OFFSET_SURFACE('',#12,1.5,.F.);\n" +
            "ENDSEC;\nEND-ISO-10303-21;\n");

        var ex = Assert.Throws<StepUnsupportedEntityException>(() => resolver.Face(10));
        Assert.Contains("#11 OFFSET_SURFACE", ex.Message);
        Assert.Contains("face #10", ex.Message);
        Assert.Contains("not supported yet", ex.Message);
    }

    [Fact]
    public void NoSolid_And_OpenShell_FailWithActionableMessages()
    {
        var empty = ResolverFor(
            "ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION((''),'2;1');\n" +
            "FILE_NAME('m','',(''),(''),'','','');\nFILE_SCHEMA(('AP214'));\nENDSEC;\nDATA;\n" +
            "#1=(GLOBAL_UNIT_ASSIGNED_CONTEXT((#2))REPRESENTATION_CONTEXT('',''));\n" +
            "#2=(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.));\n" +
            "#3=CARTESIAN_POINT('',(0.,0.,0.));\n" +
            "ENDSEC;\nEND-ISO-10303-21;\n");
        Assert.Contains("no MANIFOLD_SOLID_BREP",
            Assert.Throws<StepImportException>(() => empty.ResolveSolids()).Message);

        var sheet = ResolverFor(
            "ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION((''),'2;1');\n" +
            "FILE_NAME('m','',(''),(''),'','','');\nFILE_SCHEMA(('AP214'));\nENDSEC;\nDATA;\n" +
            "#1=(GLOBAL_UNIT_ASSIGNED_CONTEXT((#2))REPRESENTATION_CONTEXT('',''));\n" +
            "#2=(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.));\n" +
            "#3=OPEN_SHELL('',());\n" +
            "ENDSEC;\nEND-ISO-10303-21;\n");
        Assert.Contains("export a solid body",
            Assert.Throws<StepImportException>(() => sheet.ResolveSolids()).Message);
    }

    [Fact]
    public void ExampleModel_ResolvesAllFacesToSupportedSurfaces()
    {
        string? path = Part21Tests.FindExampleStepFile();
        if (path is null) return; // example not present in this checkout

        var file = Part21Parser.ParseFile(path);
        var resolver = new StepEntityResolver(file, StepUnits.Resolve(file).MetersPerUnit);
        var solid = Assert.Single(resolver.ResolveSolids());

        Assert.Equal(65, solid.Outer.Faces.Count);
        Assert.Equal(53, solid.Outer.Faces.Count(f => f.Surface is StepPlane));
        Assert.Equal(10, solid.Outer.Faces.Count(f => f.Surface is StepCylinder));
        Assert.Equal(2, solid.Outer.Faces.Count(f => f.Surface is StepCone));
    }
}
