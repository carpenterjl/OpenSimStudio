using OpenSim.Pcb.Import;
using OpenSim.Pcb.Ipc2581;

namespace OpenSim.Tests.Pcb;

/// <summary>
/// The importer runs its per-layer (Gerber) / per-net (IPC-2581) work in parallel; the
/// binding contract is that the assembled board is BITWISE identical for any thread
/// count or schedule. These tests pin the parallel result against a forced
/// single-threaded run on real boards — every island vertex, pad, via, net membership
/// and warning line (timing lines excepted, they legitimately vary).
/// </summary>
public class ImportDeterminismTests
{
    [Fact]
    public void GerberBoard_ParallelImport_IsBitwiseIdenticalToSequential()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Pcb", "Fixtures", "example_board.zip");
        var sequential = new PcbBoardReader { MaxDegreeOfParallelism = 1 }.Read(path);
        var parallel = new PcbBoardReader().Read(path);
        AssertBoardsIdentical(sequential, parallel);
    }

    [Fact]
    public void Ipc2581Board_ParallelBuild_IsBitwiseIdenticalToSequential()
    {
        var file = Ipc2581IntegrationTests.FindKiCadExampleFile();
        if (file is null) return;   // soft-skip: real export not present in this checkout
        var design = new Ipc2581Parser().Parse(file);
        var sequential = new Ipc2581BoardBuilder { MaxDegreeOfParallelism = 1 }.Build(design);
        var parallel = new Ipc2581BoardBuilder().Build(design);
        AssertBoardsIdentical(sequential, parallel);
    }

    private static void AssertBoardsIdentical(PcbBoard a, PcbBoard b)
    {
        Assert.Equal(a.Islands.Count, b.Islands.Count);
        for (int i = 0; i < a.Islands.Count; i++)
        {
            var ia = a.Islands[i];
            var ib = b.Islands[i];
            Assert.Equal(ia.Index, ib.Index);
            Assert.Equal(ia.LayerOrder, ib.LayerOrder);
            Assert.Equal(ia.LayerName, ib.LayerName);
            AssertRingsIdentical(ia.Shape.Outer, ib.Shape.Outer);
            Assert.Equal(ia.Shape.Holes.Count, ib.Shape.Holes.Count);
            for (int h = 0; h < ia.Shape.Holes.Count; h++)
                AssertRingsIdentical(ia.Shape.Holes[h], ib.Shape.Holes[h]);
        }

        Assert.Equal(a.Pads.Count, b.Pads.Count);
        for (int i = 0; i < a.Pads.Count; i++)
        {
            Assert.Equal(a.Pads[i].LayerOrder, b.Pads[i].LayerOrder);
            Assert.Equal(a.Pads[i].Center.X, b.Pads[i].Center.X);
            Assert.Equal(a.Pads[i].Center.Y, b.Pads[i].Center.Y);
        }

        Assert.Equal(a.Vias.Count, b.Vias.Count);
        for (int i = 0; i < a.Vias.Count; i++)
            Assert.Equal(a.Vias[i], b.Vias[i]);

        Assert.Equal(a.Nets.Count, b.Nets.Count);
        for (int i = 0; i < a.Nets.Count; i++)
        {
            Assert.Equal(a.Nets[i].Id, b.Nets[i].Id);
            Assert.Equal(a.Nets[i].Name, b.Nets[i].Name);
            Assert.Equal(
                a.Nets[i].Islands.Select(isl => isl.Index),
                b.Nets[i].Islands.Select(isl => isl.Index));
        }

        Assert.Equal(a.TraceCenterlines.Count, b.TraceCenterlines.Count);
        for (int i = 0; i < a.TraceCenterlines.Count; i++)
            Assert.Equal(a.TraceCenterlines[i], b.TraceCenterlines[i]);

        // Warning ORDER and content must match — only wall-clock digits may differ.
        Assert.Equal(a.Warnings.Count, b.Warnings.Count);
        for (int i = 0; i < a.Warnings.Count; i++)
            if (!a.Warnings[i].Contains(" ms"))
                Assert.Equal(a.Warnings[i], b.Warnings[i]);
    }

    private static void AssertRingsIdentical(
        IReadOnlyList<OpenSim.Core.Geometry2D.Point2> a,
        IReadOnlyList<OpenSim.Core.Geometry2D.Point2> b)
    {
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].X, b[i].X);   // exact — no tolerance
            Assert.Equal(a[i].Y, b[i].Y);
        }
    }
}
