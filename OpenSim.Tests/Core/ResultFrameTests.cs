using OpenSim.Core.Interfaces;
using OpenSim.Core.Results;
using Xunit;

namespace OpenSim.Tests.Core;

public class ResultFrameTests
{
    private static NodalScalarField Field(string name, params double[] values) =>
        new(name, "K", values);

    [Fact]
    public void SolveOutput_WithoutFrames_KeepsSingleResultShape()
    {
        var output = new SolveOutput
        {
            Fields = new IResultField[] { Field("Temperature", 1, 2, 3) },
            Log = Array.Empty<string>()
        };

        Assert.Null(output.Frames);
        Assert.Null(output.FrameAxis);
    }

    [Fact]
    public void SolveOutput_FieldsIsTheDefaultFramesFieldList()
    {
        // The convention every multi-frame solver must follow: SolveOutput.Fields is
        // (reference-equal to) the default frame's field list, so single-frame consumers
        // and the UI's default-frame selection agree without any extra bookkeeping.
        var frames = new[]
        {
            new ResultFrame("t = 0 s", 0.0, new IResultField[] { Field("Temperature", 1, 1) }) { Unit = "s" },
            new ResultFrame("t = 1 s", 1.0, new IResultField[] { Field("Temperature", 2, 2) }) { Unit = "s" },
            new ResultFrame("t = 2 s", 2.0, new IResultField[] { Field("Temperature", 3, 3) }) { Unit = "s" }
        };
        var output = new SolveOutput
        {
            Fields = frames[^1].Fields,
            Log = Array.Empty<string>(),
            Frames = frames,
            FrameAxis = "Time"
        };

        Assert.Same(frames[^1].Fields, output.Fields);
        Assert.Equal("Time", output.FrameAxis);
        // Frames are ordered by Value, and all frames carry the same field names/counts.
        for (int i = 1; i < frames.Length; i++)
        {
            Assert.True(frames[i].Value > frames[i - 1].Value);
            Assert.Equal(frames[0].Fields.Count, frames[i].Fields.Count);
            for (int f = 0; f < frames[0].Fields.Count; f++)
            {
                Assert.Equal(frames[0].Fields[f].Name, frames[i].Fields[f].Name);
                Assert.Equal(frames[0].Fields[f].Count, frames[i].Fields[f].Count);
            }
        }
    }

    [Fact]
    public void ResultFrame_CarriesOptionalUnitAndSummary()
    {
        var frame = new ResultFrame("f = 1 MHz", 1e6, new IResultField[] { Field("Potential magnitude", 1) })
        {
            Unit = "Hz",
            Summary = new Dictionary<string, double> { ["|Z| (Ω)"] = 50.0, ["Phase (°)"] = -45.0 }
        };

        Assert.Equal("Hz", frame.Unit);
        Assert.Equal(50.0, frame.Summary!["|Z| (Ω)"]);

        var plain = new ResultFrame("Mode 1 — 100 Hz", 100.0, frame.Fields);
        Assert.Null(plain.Unit);
        Assert.Null(plain.Summary);
    }
}
