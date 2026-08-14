using System.Text.Json;
using OpenSim.Core.PostProcessing;

namespace OpenSim.Tests.PostProcessing;

/// <summary>
/// The editable colormap (Phase 6). Two things matter here and both are pinned exactly:
/// the built-in presets must reproduce the colors the app hardcoded before colormaps
/// became editable (every existing result view keeps its exact appearance), and every
/// edit must preserve the invariants that let one map color the skin, the section cut,
/// the contours and the legend consistently.
/// </summary>
public class ColormapDefinitionTests
{
    // The literal stop tables from OpenSim.App/Rendering/Colormap.cs. The test project
    // cannot reference the WPF app, so the back-compat pin lives here as data: if either
    // table is ever edited on one side, this fails.
    private static readonly (double P, byte R, byte G, byte B)[] LegacyRainbow =
    {
        (0.00, 0, 0, 255), (0.25, 0, 255, 255), (0.50, 0, 255, 0),
        (0.75, 255, 255, 0), (1.00, 255, 0, 0)
    };

    private static readonly (double P, byte R, byte G, byte B)[] LegacyViridis =
    {
        (0.00, 68, 1, 84), (0.25, 59, 82, 139), (0.50, 33, 145, 140),
        (0.75, 94, 201, 98), (1.00, 253, 231, 37)
    };

    [Fact]
    public void Presets_AreByteIdenticalToTheLegacyHardcodedMaps()
    {
        AssertStops(LegacyRainbow, ColormapDefinition.Rainbow);
        AssertStops(LegacyViridis, ColormapDefinition.Viridis);

        static void AssertStops((double P, byte R, byte G, byte B)[] expected, ColormapDefinition map)
        {
            Assert.Equal(expected.Length, map.Stops.Count);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i].P, map.Stops[i].Position);     // exact — these are literals
                Assert.Equal(expected[i].R, map.Stops[i].R);
                Assert.Equal(expected[i].G, map.Stops[i].G);
                Assert.Equal(expected[i].B, map.Stops[i].B);
            }
        }
    }

    [Fact]
    public void AllPresets_SpanTheAxis_AndAreDistinct()
    {
        Assert.All(ColormapDefinition.Presets, m =>
        {
            Assert.Equal(0.0, m.Stops[0].Position);
            Assert.Equal(1.0, m.Stops[^1].Position);
            Assert.False(m.Discrete);
        });
        Assert.Equal(ColormapDefinition.Presets.Count,
            ColormapDefinition.Presets.Select(m => m.Name).Distinct().Count());
    }

    [Fact]
    public void Constructor_RejectsMapsThatCannotColorTheWholeAxis()
    {
        var one = new[] { new ColormapStop(0, 0, 0, 0) };
        Assert.Throws<ArgumentException>(() => new ColormapDefinition("x", one));

        var unsorted = new[] { new ColormapStop(0, 0, 0, 0), new ColormapStop(0.7, 1, 1, 1), new ColormapStop(0.3, 2, 2, 2), new ColormapStop(1, 3, 3, 3) };
        Assert.Throws<ArgumentException>(() => new ColormapDefinition("x", unsorted));

        var duplicate = new[] { new ColormapStop(0, 0, 0, 0), new ColormapStop(0.5, 1, 1, 1), new ColormapStop(0.5, 2, 2, 2), new ColormapStop(1, 3, 3, 3) };
        Assert.Throws<ArgumentException>(() => new ColormapDefinition("x", duplicate));

        var short0 = new[] { new ColormapStop(0.1, 0, 0, 0), new ColormapStop(1, 1, 1, 1) };
        Assert.Throws<ArgumentException>(() => new ColormapDefinition("x", short0));

        var short1 = new[] { new ColormapStop(0, 0, 0, 0), new ColormapStop(0.9, 1, 1, 1) };
        Assert.Throws<ArgumentException>(() => new ColormapDefinition("x", short1));
    }

    [Fact]
    public void Sample_HitsStopsExactly_AndInterpolatesLinearlyBetween()
    {
        var map = ColormapDefinition.Rainbow;
        foreach (var s in map.Stops)
            Assert.Equal((s.R, s.G, s.B), map.Sample(s.Position));

        // Half way from blue (0,0,255) to cyan (0,255,255).
        Assert.Equal((byte)0, map.Sample(0.125).R);
        Assert.Equal((byte)128, map.Sample(0.125).G);      // round(127.5)
        Assert.Equal((byte)255, map.Sample(0.125).B);

        // A quarter of the way along the green→yellow band (0.50 … 0.75).
        var q = map.Sample(0.5625);
        Assert.Equal((byte)64, q.R);                       // round(0.25 * 255) = 64
        Assert.Equal((byte)255, q.G);
        Assert.Equal((byte)0, q.B);
    }

    [Fact]
    public void Sample_ClampsOutsideTheAxis_AndNeverThrows()
    {
        var map = ColormapDefinition.Viridis;
        Assert.Equal(map.Sample(0.0), map.Sample(-3.0));
        Assert.Equal(map.Sample(1.0), map.Sample(4.0));
        Assert.Equal(map.Sample(0.0), map.Sample(double.NaN));
    }

    [Fact]
    public void Discrete_QuantizesEachBandToOneColor_TakenFromTheBandMidpoint()
    {
        var continuous = ColormapDefinition.Rainbow;
        var banded = continuous.WithDiscrete(true);

        // Everything strictly inside a band gets that band's single color …
        foreach (double u in new[] { 0.26, 0.40, 0.49 })
            Assert.Equal(continuous.Sample(0.375), banded.Sample(u));

        // … and neighbouring bands are genuinely different colors, top band included.
        var bandColors = Enumerable.Range(0, continuous.Stops.Count - 1)
            .Select(i => banded.Sample(0.5 * (continuous.Stops[i].Position + continuous.Stops[i + 1].Position)))
            .ToList();
        Assert.Equal(bandColors.Count, bandColors.Distinct().Count());
        Assert.Equal(bandColors[^1], banded.Sample(1.0));
    }

    [Fact]
    public void WithStopAdded_DoesNotChangeHowTheMapLooks()
    {
        var map = ColormapDefinition.Viridis;
        var denser = map.WithStopAdded(0.375);

        Assert.Equal(map.Stops.Count + 1, denser.Stops.Count);
        Assert.Equal(0.375, denser.Stops[2].Position);
        for (int i = 0; i <= 200; i++)
        {
            double u = i / 200.0;
            // The inserted stop takes the color the map already had, so resampling the
            // denser map reproduces the original — within one LSB of the byte rounding
            // the inserted anchor itself went through.
            var a = map.Sample(u);
            var b = denser.Sample(u);
            Assert.True(Math.Abs(a.R - b.R) <= 1 && Math.Abs(a.G - b.G) <= 1 && Math.Abs(a.B - b.B) <= 1,
                $"u = {u}: {a} vs {b}");
        }
    }

    [Fact]
    public void WithStopAdded_RejectsEndpointsAndDuplicates()
    {
        var map = ColormapDefinition.Rainbow;
        Assert.Throws<ArgumentOutOfRangeException>(() => map.WithStopAdded(0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.WithStopAdded(1.0));
        Assert.Throws<ArgumentException>(() => map.WithStopAdded(0.25));
    }

    [Fact]
    public void WithStopMoved_StaysStrictlyBetweenNeighbours_AndPinsTheEndpoints()
    {
        var map = ColormapDefinition.Rainbow;

        var moved = map.WithStopMoved(2, 0.6);
        Assert.Equal(0.6, moved.Stops[2].Position, 12);
        Assert.Equal(map.Stops[2].R, moved.Stops[2].R);      // moving is not recoloring

        // An overshooting drag lands just inside the neighbour, never past it.
        var over = map.WithStopMoved(2, 5.0);
        Assert.True(over.Stops[2].Position < over.Stops[3].Position);
        Assert.True(over.Stops[2].Position > over.Stops[1].Position);
        var under = map.WithStopMoved(2, -5.0);
        Assert.True(under.Stops[2].Position > under.Stops[1].Position);

        Assert.Throws<ArgumentOutOfRangeException>(() => map.WithStopMoved(0, 0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.WithStopMoved(map.Stops.Count - 1, 0.9));
    }

    [Fact]
    public void WithStopRemoved_DropsInteriorStopsOnly()
    {
        var map = ColormapDefinition.Rainbow;
        var fewer = map.WithStopRemoved(1);
        Assert.Equal(map.Stops.Count - 1, fewer.Stops.Count);
        Assert.Equal(0.0, fewer.Stops[0].Position);
        Assert.Equal(1.0, fewer.Stops[^1].Position);

        Assert.Throws<ArgumentOutOfRangeException>(() => map.WithStopRemoved(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.WithStopRemoved(map.Stops.Count - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ColormapDefinition.Grayscale.WithStopRemoved(0));
    }

    [Fact]
    public void WithStopColor_RecolorsOneStop_IncludingEndpoints()
    {
        var map = ColormapDefinition.Rainbow.WithStopColor(0, 10, 20, 30);
        Assert.Equal(((byte)10, (byte)20, (byte)30), map.Sample(0.0));
        Assert.Equal(0.0, map.Stops[0].Position);
        Assert.Equal(ColormapDefinition.Rainbow.Sample(1.0), map.Sample(1.0));
    }

    [Fact]
    public void Edits_LeaveTheOriginalUntouched()
    {
        var map = ColormapDefinition.Rainbow;
        var before = map.Stops.ToArray();
        _ = map.WithStopAdded(0.4);
        _ = map.WithStopMoved(1, 0.3);
        _ = map.WithStopColor(1, 9, 9, 9);
        _ = map.WithStopRemoved(1);
        Assert.Equal(before, map.Stops.ToArray());
    }

    [Fact]
    public void Equality_IsByValue_AndJsonRoundTrips()
    {
        var edited = ColormapDefinition.Viridis
            .WithStopAdded(0.4)
            .WithStopColor(2, 12, 34, 56)
            .WithDiscrete(true)
            .WithName("My map");

        Assert.Equal(edited, new ColormapDefinition(edited.Name, edited.Stops.ToArray(), edited.Discrete));
        Assert.NotEqual(edited, ColormapDefinition.Viridis);

        string json = JsonSerializer.Serialize(edited);
        var back = JsonSerializer.Deserialize<ColormapDefinition>(json);
        Assert.Equal(edited, back);
        Assert.Equal(edited.GetHashCode(), back!.GetHashCode());
    }
}
