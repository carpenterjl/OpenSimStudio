using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Geometry;
using OpenSim.Meshing;
using Xunit;

namespace OpenSim.Tests.Core;

/// <summary>
/// The assembly cache: what it reuses between solves, and — more importantly — what it does
/// not.
///
/// The merge is a pure function of the bodies' meshes, so reusing it is safe. The boundary
/// conditions are not: they change while the user works, with no mesh change to invalidate
/// anything, and a cached scope reaching a solver is exactly the class of silent-wrong-answer
/// bug this codebase exists to refuse. Both halves of that are gated below.
/// </summary>
public class AssemblyCacheTests
{
    private static readonly Material Steel = new()
    {
        Name = "Structural steel",
        YoungsModulus = 200e9,
        PoissonRatio = 0.30,
        Density = 7850,
        ThermalConductivity = 45,
        SpecificHeat = 490,
        Emissivity = 0.8
    };

    private static readonly Material Aluminium = new()
    {
        Name = "Aluminium",
        YoungsModulus = 69e9,
        PoissonRatio = 0.33,
        Density = 2700,
        ThermalConductivity = 237,
        SpecificHeat = 900,
        Emissivity = 0.09
    };

    private static Body MakeBody(string name, double x0, double edge = 0.010)
    {
        var geometry = PrimitiveFactory.CreateBox(0.02, 0.02, 0.02);
        var moved = new TriangleMesh(
            geometry.Vertices.Select(v => new Vector3D(v.X + x0, v.Y, v.Z)).ToList(),
            geometry.Triangles, geometry.TriangleFaceIds);

        var body = new Body { Name = name, Material = Steel, Geometry = moved };
        body.Mesh = new DelaunayMeshGenerator().Generate(moved,
            new MeshSettings { TargetEdgeLength = edge });
        return body;
    }

    private static List<Body> TwoBodies() =>
        new() { MakeBody("left", 0.0), MakeBody("right", 0.02) };

    [Fact]
    public void ASecondSolveWithTheSameMeshes_ReusesTheMerge()
    {
        var bodies = TwoBodies();
        var cache = new AssemblyCache();

        var first = cache.Merge(bodies);
        Assert.Equal(0, cache.MergeHits);

        var second = cache.Merge(bodies);
        Assert.Equal(1, cache.MergeHits);
        Assert.Same(first, second);
        Assert.Same(first.Mesh, second.Mesh);
    }

    /// <summary>Re-meshing one body must MISS: the merge describes node indices that no
    /// longer mean the same thing.</summary>
    [Fact]
    public void RemeshingOneBody_MissesTheCache()
    {
        var bodies = TwoBodies();
        var cache = new AssemblyCache();

        var first = cache.Merge(bodies);
        bodies[1].Mesh = new DelaunayMeshGenerator().Generate(bodies[1].Geometry!,
            new MeshSettings { TargetEdgeLength = 0.007 });

        var second = cache.Merge(bodies);
        Assert.Equal(0, cache.MergeHits);
        Assert.NotSame(first.Mesh, second.Mesh);
        Assert.NotEqual(first.Mesh.NodeCount, second.Mesh.NodeCount);
    }

    [Fact]
    public void AddingOrRemovingABody_MissesTheCache()
    {
        var bodies = TwoBodies();
        var cache = new AssemblyCache();
        cache.Merge(bodies);

        bodies.Add(MakeBody("third", 0.04));
        cache.Merge(bodies);
        Assert.Equal(0, cache.MergeHits);
    }

    [Fact]
    public void ClearingTheCache_ForcesAFreshMerge()
    {
        var bodies = TwoBodies();
        var cache = new AssemblyCache();

        var first = cache.Merge(bodies);
        cache.Clear();
        var second = cache.Merge(bodies);

        Assert.Equal(0, cache.MergeHits);
        Assert.NotSame(first, second);
    }

    /// <summary>
    /// THE gate that makes the cache safe: a boundary-condition edit is reflected in the very
    /// next assembly, even though the merge is reused. Conditions are resolved fresh every
    /// time; only the geometry is cached.
    /// </summary>
    [Fact]
    public void AConditionEdit_IsSeenImmediately_ThoughTheMergeIsReused()
    {
        var bodies = TwoBodies();
        var cache = new AssemblyCache();

        bodies[0].BoundaryConditions.Add(new FixedSupport
        {
            Name = "root", FaceIds = new List<int> { 0 }
        });

        var before = cache.Assemble(bodies);
        Assert.Single(before.BoundaryConditions);

        bodies[1].BoundaryConditions.Add(new FixedTemperature
        {
            Name = "hot", FaceIds = new List<int> { 1 }, Kelvin = 400
        });

        var after = cache.Assemble(bodies);
        Assert.Equal(2, after.BoundaryConditions.Count);
        Assert.Contains(after.BoundaryConditions, c => c.Name.Contains("hot"));

        // The merge itself was NOT rebuilt to notice that.
        Assert.Equal(1, cache.MergeHits);
        Assert.Same(before.Mesh, after.Mesh);
    }

    /// <summary>A material swap is the same story: no mesh change, so the merge stands, but
    /// the material map must be current.</summary>
    [Fact]
    public void AMaterialSwap_IsSeenImmediately_ThoughTheMergeIsReused()
    {
        var bodies = TwoBodies();
        var cache = new AssemblyCache();

        var before = cache.Assemble(bodies);
        Assert.Equal(Steel.Name, before.RegionMaterials[1].Name);

        bodies[1].Material = Aluminium;
        var after = cache.Assemble(bodies);

        Assert.Equal(Aluminium.Name, after.RegionMaterials[1].Name);
        Assert.Equal(1, cache.MergeHits);
        Assert.Same(before.Mesh, after.Mesh);
    }

    /// <summary>The cached assembly is the one the uncached path produces — same merged mesh
    /// shape, same bases, same conditions.</summary>
    [Fact]
    public void TheCachedAssembly_MatchesTheDirectOne()
    {
        var bodies = TwoBodies();
        bodies[0].BoundaryConditions.Add(new FixedSupport
        {
            Name = "root", FaceIds = new List<int> { 0 }
        });

        var direct = FeMeshAssembler.Assemble(bodies);
        var cached = new AssemblyCache().Assemble(bodies);

        Assert.Equal(direct.Mesh.NodeCount, cached.Mesh.NodeCount);
        Assert.Equal(direct.Mesh.ElementCount, cached.Mesh.ElementCount);
        Assert.Equal(direct.NodeBases, cached.NodeBases);
        Assert.Equal(direct.ElementBases, cached.ElementBases);
        Assert.Equal(direct.FaceIdBases, cached.FaceIdBases);
        Assert.Equal(direct.BoundaryConditions.Count, cached.BoundaryConditions.Count);
        for (int i = 0; i < direct.Mesh.NodeCount; i++)
        {
            Assert.Equal(direct.Mesh.Nodes[i].X, cached.Mesh.Nodes[i].X);
            Assert.Equal(direct.Mesh.Nodes[i].Y, cached.Mesh.Nodes[i].Y);
            Assert.Equal(direct.Mesh.Nodes[i].Z, cached.Mesh.Nodes[i].Z);
        }
    }

    // ------------------------------------------------------------------ contacts

    [Fact]
    public void ContactsAreDetectedOncePerSettings()
    {
        var bodies = TwoBodies();
        var cache = new AssemblyCache();
        cache.Merge(bodies);

        int detections = 0;
        IReadOnlyList<ContactInterface> Detect()
        {
            detections++;
            return Array.Empty<ContactInterface>();
        }

        var settings = new AssemblySettings { ContactConductance = 5e3 };
        cache.Contacts(settings, Detect);
        cache.Contacts(settings, Detect);
        Assert.Equal(1, detections);

        // Different settings must miss — a changed conductance is a different answer.
        cache.Contacts(new AssemblySettings { ContactConductance = 1e4 }, Detect);
        Assert.Equal(2, detections);
    }

    /// <summary>A new merge invalidates the contacts detected on the old one, without anyone
    /// having to remember to clear them.</summary>
    [Fact]
    public void ANewMerge_InvalidatesTheContactsDetectedOnTheOldOne()
    {
        var bodies = TwoBodies();
        var cache = new AssemblyCache();
        cache.Merge(bodies);

        int detections = 0;
        IReadOnlyList<ContactInterface> Detect()
        {
            detections++;
            return Array.Empty<ContactInterface>();
        }

        var settings = new AssemblySettings { ContactConductance = 5e3 };
        cache.Contacts(settings, Detect);
        Assert.Equal(1, detections);

        bodies[0].Mesh = new DelaunayMeshGenerator().Generate(bodies[0].Geometry!,
            new MeshSettings { TargetEdgeLength = 0.007 });
        cache.Merge(bodies);

        cache.Contacts(settings, Detect);
        Assert.Equal(2, detections);
    }
}
