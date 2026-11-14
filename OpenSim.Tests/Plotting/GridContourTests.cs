using OpenSim.Core.Geometry2D;
using OpenSim.Core.Plotting;

namespace OpenSim.Tests.Plotting;

/// <summary>
/// Contours on a regular grid (OP-7). The field is linear on each triangle of the split cells,
/// so a linear field is contoured exactly and the band polygons tile the grid exactly: those
/// identities are pinned at rounding level. A Gaussian's contour radii are checked against the
/// closed form within the interpolation bound of the triangle model.
/// </summary>
public class GridContourTests
{
    [Fact]
    public void InteriorLevels_NeverIncludeTheExtremes()
    {
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, GridContour.InteriorLevels(0, 4, 3));
        Assert.Empty(GridContour.InteriorLevels(1, 1, 3));
    }

    [Fact]
    public void NiceLevels_AreRoundValuesStrictlyInside()
    {
        var levels = GridContour.NiceLevels(0, 1, 4);
        Assert.Equal(new[] { 0.2, 0.4, 0.6, 0.8 }, levels.Select(l => Math.Round(l, 12)));
        Assert.All(GridContour.NiceLevels(-0.37, 2.91, 8), l => Assert.InRange(l, -0.37 + 1e-15, 2.91 - 1e-15));
    }

    [Fact]
    public void BandEdges_And_BandIndex()
    {
        var edges = GridContour.BandEdges(0, 1, new[] { 0.75, 0.25, 0.5, 1.0, -0.1 });
        Assert.Equal(new[] { 0, 0.25, 0.5, 0.75, 1 }, edges);
        Assert.Equal(0, GridContour.BandIndex(0, edges));
        Assert.Equal(1, GridContour.BandIndex(0.25, edges));
        Assert.Equal(3, GridContour.BandIndex(1, edges));    // the top edge belongs to the last band
        Assert.Equal(-1, GridContour.BandIndex(1.01, edges));
        Assert.Equal(-1, GridContour.BandIndex(double.NaN, edges));
    }

    [Fact]
    public void LinearField_ContoursAreExactlyTheIsoLines()
    {
        // f = 2x + y: every crossing of level L lies on 2x + y = L to rounding.
        var field = GridField.FromFunction((x, y) => 2 * x + y, 11, 13, -1, 2, 0, 1.5);
        var levels = new[] { -0.7, 0.3, 1.9, 3.2 };
        var lines = GridContour.Lines(field, levels);
        Assert.Equal(levels.Length, lines.Count);
        foreach (var line in lines)
        {
            Assert.NotEmpty(line.Segments);
            foreach (var (a, b) in line.Segments)
            {
                Assert.Equal(line.Level, 2 * a.X + a.Y, 12);
                Assert.Equal(line.Level, 2 * b.X + b.Y, 12);
            }
        }
    }

    [Fact]
    public void ALevelThroughGridNodes_StillYieldsWholeSegments()
    {
        // f = x on columns at 0, 0.25, …: level 0.5 passes exactly through a column of nodes.
        var field = GridField.FromFunction((x, _) => x, 5, 5, 0, 1, 0, 1);
        var line = GridContour.Lines(field, new[] { 0.5 })[0];
        Assert.NotEmpty(line.Segments);
        Assert.All(line.Segments, s => { Assert.Equal(0.5, s.A.X, 9); Assert.Equal(0.5, s.B.X, 9); });
        // The segments cover the full height of the grid.
        Assert.Equal(1.0, line.Segments.Sum(s => Math.Abs(s.B.Y - s.A.Y)), 9);
    }

    [Fact]
    public void Gaussian_ContourRadiiMatchTheClosedForm()
    {
        // f = exp(-r²/2): the level-L contour is the circle r = sqrt(-2 ln L).
        const double h = 0.05;
        var field = GridField.FromFunction((x, y) => Math.Exp(-(x * x + y * y) / 2), 161, 161, -4, 4, -4, 4);
        Assert.Equal(h, field.DeltaX, 14);
        foreach (double level in new[] { 0.1, 0.5, 0.9 })
        {
            double r0 = Math.Sqrt(-2 * Math.Log(level));
            // Linear interpolation along an edge of length ℓ errs in value by at most ℓ²/8·max|f''|;
            // the longest edge is the diagonal ℓ = h√2, and |f''| ≤ 1 for this Gaussian in any
            // direction. Dividing by the radial slope |f'(r0)| = r0·L gives the position bound.
            double bound = (2 * h * h / 8) / (r0 * level);
            var line = GridContour.Lines(field, new[] { level })[0];
            Assert.True(line.Segments.Count > 50);
            double perimeter = 0;
            foreach (var (a, b) in line.Segments)
            {
                Assert.InRange(a.Length, r0 - bound, r0 + bound);
                Assert.InRange(b.Length, r0 - bound, r0 + bound);
                perimeter += (b - a).Length;
            }
            // A closed curve all the way round: the perimeter is 2πr0 to the same order.
            Assert.InRange(perimeter, 2 * Math.PI * (r0 - bound) * 0.995, 2 * Math.PI * (r0 + bound));
        }
    }

    [Fact]
    public void Bands_TileTheWholeGrid()
    {
        var field = GridField.FromFunction((x, y) => Math.Sin(3 * x) * Math.Cos(2 * y) + 0.3 * x * y, 23, 31, -1.5, 1.5, -1, 1);
        var (min, max) = field.FiniteRange();
        var edges = GridContour.BandEdges(min, max, GridContour.NiceLevels(min, max, 9));
        var bands = GridContour.Bands(field, edges);
        double area = bands.Sum(b => b.Polygons.Sum(GridContour.Area));
        Assert.Equal(3.0 * 2.0, area, 10);
    }

    [Fact]
    public void Bands_OfALinearField_HaveTheExactStripAreas()
    {
        // f = x on [0, 4] × [0, 1]: the band [a, b] is the strip a ≤ x ≤ b, area (b − a)·1.
        var field = GridField.FromFunction((x, _) => x, 7, 9, 0, 4, 0, 1);
        var bands = GridContour.Bands(field, new[] { 0, 0.3, 1.7, 2.0, 4.0 });
        double[] expected = { 0.3, 1.4, 0.3, 2.0 };
        for (int k = 0; k < bands.Count; k++)
        {
            Assert.Equal(expected[k], bands[k].Polygons.Sum(GridContour.Area), 12);
            Assert.All(bands[k].Polygons, p => Assert.All(p, v => Assert.InRange(v.X, bands[k].Lower - 1e-12, bands[k].Upper + 1e-12)));
        }
    }

    [Fact]
    public void CellsTouchingMissingData_AreSkipped()
    {
        var field = GridField.FromFunction((x, y) => x + y, 3, 3, 0, 2, 0, 2);
        field.Values[1, 1] = double.NaN;   // the centre node
        // Of the eight triangles (cells split along the rising diagonal) only two avoid the
        // centre: (0,1)(0,2)(1,2) and (1,0)(2,1)(2,0), half a unit cell each.
        var bands = GridContour.Bands(field, new[] { 0.0, 4.0 });
        Assert.Equal(2, bands[0].Polygons.Count);
        Assert.Equal(1.0, bands[0].Polygons.Sum(GridContour.Area), 15);
        // Level 3.5 crosses only triangles that touch the centre or lie beyond the data.
        Assert.Empty(GridContour.Lines(field, new[] { 3.5 })[0].Segments);
    }

    [Fact]
    public void Bands_RejectUnsortedEdges() =>
        Assert.Throws<ArgumentException>(() =>
            GridContour.Bands(GridField.FromFunction((x, _) => x, 2, 2, 0, 1, 0, 1), new[] { 0.0, 0.5, 0.5 }));

    [Fact]
    public void CrossSection_SamplesTheBilinearSurface()
    {
        // Bilinear interpolation is exact for f = a + bx + cy + dxy.
        static double F(double x, double y) => 1 + 2 * x - y + 0.5 * x * y;
        var field = GridField.FromFunction(F, 6, 8, 0, 7, 0, 5);
        var samples = CrossSection.Sample(field, new Point2(0.5, 4.5), new Point2(6.5, 0.25), 25);
        Assert.Equal(25, samples.Count);
        Assert.Equal(0, samples[0].Distance);
        Assert.Equal(Math.Sqrt(36 + 4.25 * 4.25), samples[^1].Distance, 12);
        foreach (var s in samples) Assert.Equal(F(s.X, s.Y), s.Value, 12);
        // Off the grid there is no value.
        Assert.True(double.IsNaN(CrossSection.Sample(field, new Point2(-1, 1), new Point2(1, 1), 3)[0].Value));
    }

    [Fact]
    public void GridField_NearestSampleAndRange()
    {
        var field = GridField.FromFunction((x, y) => x - y, 3, 5, 0, 4, 0, 2);
        Assert.Equal((1, 3), field.NearestSample(3.2, 0.9));
        Assert.Equal((0, 0), field.NearestSample(-0.49, -0.49));
        Assert.Null(field.NearestSample(-0.51, 0));
        Assert.Equal((-2.0, 4.0), field.FiniteRange());
    }
}
