using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using Xunit;

namespace OpenSim.Tests.Core;

/// <summary>
/// Persistence gates for edge and vertex scoping. The whole design rests on the new ids
/// being NULLABLE and non-required: a required member would make System.Text.Json throw on
/// every project file written before this feature existed.
/// </summary>
public class BoundaryScopePersistenceTests
{
    private static SimProject WithCondition(BoundaryCondition condition)
    {
        var body = new Body { Name = "Beam" };
        body.BoundaryConditions.Add(condition);
        var project = new SimProject { Name = "P" };
        project.Bodies.Add(body);
        return project;
    }

    private static T RoundTrip<T>(SimProject project) where T : BoundaryCondition
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ossproj");
        try
        {
            var serializer = new ProjectSerializer();
            serializer.Save(project, path);
            return Assert.IsType<T>(Assert.Single(serializer.Load(path).Bodies).BoundaryConditions[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EdgeAndVertexScopes_RoundTrip()
    {
        var loaded = RoundTrip<FixedSupport>(WithCondition(new FixedSupport
        {
            Name = "Supports",
            FaceIds = Array.Empty<int>(),
            EdgeIds = new[] { 3, 7 },
            VertexIds = new[] { 1 }
        }));

        Assert.Equal(new[] { 3, 7 }, loaded.EdgeIds);
        Assert.Equal(new[] { 1 }, loaded.VertexIds);
        Assert.Empty(loaded.FaceIds);
    }

    /// <summary>
    /// A face-scoped condition must serialize with NO edge or vertex members at all — the
    /// serializer omits nulls, so an unchanged project file stays byte-identical and an
    /// older build could still read it.
    /// </summary>
    [Fact]
    public void FaceOnlyCondition_WritesNoScopeMembers()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ossproj");
        try
        {
            new ProjectSerializer().Save(
                WithCondition(new FixedSupport { Name = "Base", FaceIds = new[] { 4 } }), path);
            string json = File.ReadAllText(path);

            Assert.DoesNotContain("edgeIds", json);
            Assert.DoesNotContain("vertexIds", json);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The back-compatibility pin: a project written before this feature loads with nulls.</summary>
    [Fact]
    public void ProjectFileWithoutScopeMembers_LoadsWithNullScopes()
    {
        const string legacy = """
            {
              "name": "Legacy",
              "bodies": [
                {
                  "name": "Beam",
                  "boundaryConditions": [
                    { "$type": "fixedSupport", "name": "Base", "faceIds": [4] }
                  ]
                }
              ]
            }
            """;
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ossproj");
        try
        {
            File.WriteAllText(path, legacy);
            var support = Assert.IsType<FixedSupport>(
                Assert.Single(new ProjectSerializer().Load(path).Bodies).BoundaryConditions[0]);

            Assert.Equal(new[] { 4 }, support.FaceIds);
            Assert.Null(support.EdgeIds);
            Assert.Null(support.VertexIds);
            Assert.False(support.HasZeroAreaScope);
            Assert.False(support.IsEmptyScope);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScopeSummary_NamesEveryScopeKindPresent()
    {
        Assert.Equal("2 face(s)",
            new FixedSupport { Name = "a", FaceIds = new[] { 1, 2 } }.ScopeSummary);
        Assert.Equal("1 edge(s)",
            new FixedSupport { Name = "b", FaceIds = Array.Empty<int>(), EdgeIds = new[] { 0 } }.ScopeSummary);
        Assert.Equal("1 face(s) + 1 edge(s) + 1 vertex/vertices",
            new FixedSupport
            {
                Name = "c", FaceIds = new[] { 0 }, EdgeIds = new[] { 0 }, VertexIds = new[] { 0 }
            }.ScopeSummary);
        Assert.Equal("nothing",
            new FixedSupport { Name = "d", FaceIds = Array.Empty<int>() }.ScopeSummary);
    }
}
