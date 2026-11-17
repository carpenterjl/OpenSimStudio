using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.NonSequential;
using OpenSim.Optics.NonSequential.Detectors;
using OpenSim.Optics.NonSequential.Lightning;
using OpenSim.Optics.NonSequential.RayDatabase;
using OpenSim.Optics.NonSequential.Sources;
using OpenSim.Optics.NonSequential.StrayLight;
using OpenSim.Optics.NonSequential.Volume;
using OpenSim.Optics.Paraxial;

namespace OpenSim.Tests.Optics;

/// <summary>
/// Wave 17 wiring: the joins between OP-81's volume physics, OP-84's stray-light tools and OP-85's
/// LightningTrace. Every launcher that builds its own <see cref="NscLaunchRay"/>s sets Bulk Scatter
/// deliberately (one test each), and Path Analysis treats a volume event as no step of a path
/// ("Rays that have the same sequence of object faces are grouped in the same path"; help, "Path
/// Analysis", plans/sources/optics/op-84-help/) while its path energies still sum to the
/// detector's. Geometry, powers and mean paths are arbitrary test inputs; every expected value is
/// derived here or is the same computation without volume physics.
/// </summary>
public sealed class NscWave17JoinTests
{
    private const double Mm = 1e-3;

    /// <summary>The unit roundoff of doubles, 2⁻⁵³.</summary>
    private static readonly double U = Math.Pow(2, -53);

    /// <summary>Angle scattering with a 0.01 mm mean path: a ray crossing millimetres of the volume
    /// meets an event with probability 1 − e^(−100·L/mm), certain to double precision for L ≥ 1 mm.
    /// The shift "1, 1, 1" maps wavelength 1 onto itself, which makes the tracer check the system
    /// wavelengths without changing any ray.</summary>
    private static NscVolumePhysics Dense(string? shift = null) => new()
    {
        Model = NscVolumeModel.AngleScattering, MeanPath = 0.01 * Mm, AngleDegrees = 30,
        WavelengthShift = shift is null ? null : NscWavelengthShift.Parse(shift),
    };

    private static NscObject Glass(double radius, double z, NscVolumePhysics? physics, double n = 1.5) => new()
    {
        Geometry = new NscSphere(radius), Material = NscMaterial.Of(ParaxialMedium.Constant(n)),
        Placement = new NscPlacement(Z: z), VolumePhysics = physics,
    };

    private static NscObject Detector(double halfWidth, int pixels, double z) => new()
    {
        Geometry = new NscDetectorRectangle(halfWidth, halfWidth, pixels, pixels), Placement = new NscPlacement(Z: z), Material = NscMaterial.Absorb,
    };

    // ================================================================ launchers

    /// <summary>LightningTrace's mesh rays "may only follow the specular ray path ... splitting and
    /// scattering of these rays is not supported" (help, "Lightning Trace"): they are launched with
    /// Bulk Scatter Never, so a dense scattering volume leaves the result exactly as the same glass
    /// without volume physics (to the bit), and a note names the object. The trace gets the system
    /// wavelengths, so a wavelength shift string does not stop it.</summary>
    [Fact]
    public void LightningTrace_LaunchesMeshRaysWithBulkScatterNever_AndSaysSo()
    {
        NscScene Scene(NscVolumePhysics? physics) => new()
        {
            Objects = new[]
            {
                new NscObject { Geometry = new NscSourcePoint(10, new NscSourceCommon { Power = 1, AnalysisRays = 100 }) },
                Glass(5 * Mm, 20 * Mm, physics),
                Detector(20 * Mm, 8, 40 * Mm),
            },
        };
        var options = new NscSourceEmitOptions { SystemWavelengths = new[] { new NscSpectralLine(0.55, 1) } };
        var settings = new NscLightningSettings { RaySampling = 0, EdgeSampling = 1 };
        var trace = new NscTraceSettings { UsePolarization = false };
        var with = NscLightningTrace.Run(Scene(Dense("1, 1, 1")), trace, options, settings);
        var without = NscLightningTrace.Run(Scene(null), trace, options, settings);
        var a = ((NscFlatDetectorData)with.Detectors.Single(d => d.ObjectNumber == 3)).Flux;
        var b = ((NscFlatDetectorData)without.Detectors.Single(d => d.ObjectNumber == 3)).Flux;
        Assert.True(b.Sum() > 0);
        Assert.Equal(b.Select(BitConverter.DoubleToInt64Bits), a.Select(BitConverter.DoubleToInt64Bits));
        Assert.Contains(with.Notes, n => n.Contains("2 (Sphere) has volume physics", StringComparison.Ordinal) && n.Contains("Bulk Scatter Never", StringComparison.Ordinal));
        Assert.DoesNotContain(without.Notes, n => n.Contains("volume physics", StringComparison.Ordinal));
    }

    /// <summary>The Critical Ray Tracer uses no scattering ("Polarization, splitting and scattering
    /// are not used"; help, "Critical Ray Tracer"): an axial ray through a dense scattering volume of
    /// index 1 (no refraction) arrives undeviated on target and passes, as without the volume.</summary>
    [Fact]
    public void TheCriticalRayTracer_LaunchesWithBulkScatterNever()
    {
        var scene = new NscScene { Objects = new[] { Glass(5 * Mm, 20 * Mm, Dense("1, 1, 1"), n: 1.0) } };
        var set = new NscCriticalRayset
        {
            Rays = new[] { new NscCriticalRay(Vector3D.Zero, Vector3D.UnitZ, 0.55, 1, 1, NscCriticalRayFlag.Chief, new Vector3D(0, 0, 50 * Mm), Vector3D.UnitZ) },
            UnitsFlag = 4, Date = 20261116, Pattern = NscRaysetPattern.ChiefAndMarginals, TargetRotation = new NscPlacement().ToTransform(),
        };
        var outcome = Assert.Single(NscCriticalRayTracer.Trace(scene, new NscTraceSettings(), set, new[] { 0.55 }, 1e-6 * Mm, 1e-6));
        Assert.True(outcome.Pass, outcome.Reason);
        Assert.Equal(-1, outcome.TerminationObject);
    }

    /// <summary>Reverse radiance refuses volume physics up front, naming the object, instead of
    /// failing only when a backward ray happens to meet a volume event.</summary>
    [Fact]
    public void ReverseRadiance_RefusesVolumePhysics_ByObject()
    {
        var scene = new NscScene
        {
            Objects = new[]
            {
                new NscObject { Geometry = new NscSourceRectangle(5 * Mm, 5 * Mm, cosineExponent: 1, common: new NscSourceCommon { AnalysisRays = 10, Power = 1 }) },
                Glass(3 * Mm, 10 * Mm, Dense()),
                Detector(4 * Mm, 4, 20 * Mm),
            },
        };
        var options = new NscReverseRadianceOptions
        {
            Detector = 3, PixelX = 1, PixelY = 1, Rays = 10,
            Emission = new NscSourceEmitOptions { SystemWavelengths = new[] { new NscSpectralLine(0.55, 1) } },
        };
        var e = Assert.Throws<NotSupportedException>(() => NscReverseRadiance.Estimate(scene, new NscTraceSettings(), options));
        Assert.Contains("object 2 (Sphere) has volume physics", e.Message);
    }

    /// <summary>The NSC Single Ray Trace launches with the source default, Bulk Scatter Many: a ray
    /// through a sphere with a 0.01 mm mean path lists B segments.</summary>
    [Fact]
    public void TheSingleRayTrace_LaunchesWithBulkScatterMany()
    {
        var context = new OpticsAnalysisContext("P", new OpticsSettings
        {
            NonSequential = new OpticsNonSequentialSettings
            {
                Objects = new List<OpticsNscObjectSettings>
                {
                    new()
                    {
                        Type = "Sphere", Parameters = new List<double> { 10, 1 }, Material = "1.5",
                        VolumePhysics = new OpticsNscVolumePhysicsSettings { Model = "Angle Scattering", MeanPath = 0.01, Angle = 10 },
                    },
                    new() { Type = "Rectangle", Parameters = new List<double> { 50, 50 }, Z = 30, DetectingStandIn = true },
                }
            }
        });
        var result = new NscSingleRayTraceAnalysis().Compute(context, new NscSingleRayTraceSettings { Z = -20, UsePolarization = false }, default);
        var segments = Assert.Single(result.Sections, s => s.Heading == "Segments");
        Assert.Contains(segments.TableRows, row => row[6].Contains('B'));
    }

    /// <summary>The source stand-ins launch with the source default, Bulk Scatter Many.</summary>
    [Fact]
    public void TheSourceStandIns_LaunchWithBulkScatterMany()
    {
        Assert.All(NscSourceStandIn.PointCone(Vector3D.Zero, Vector3D.UnitZ, 10, 8, 1), r => Assert.Equal(NscSourceBulkScatter.Many, r.BulkScatter));
        Assert.All(NscSourceStandIn.CollimatedDisk(Vector3D.Zero, Vector3D.UnitZ, 1 * Mm, 8, 1), r => Assert.Equal(NscSourceBulkScatter.Many, r.BulkScatter));
    }

    // ================================================================ Path Analysis and volume events

    /// <summary>
    /// A cone into a glass sphere with angle scattering (mean path 2 mm) that shifts wavelength 1
    /// to 2, then an absorbing detector. Volume events are no step of a path (a path is the
    /// sequence of object faces), so a branch's path has one step per face hit after the source;
    /// branches through volume events reach the detector; and Path Analysis by detector still
    /// sums to an independent compensated sum of the same hits (both compensated, bound
    /// 4u·total) and to the detector's total (a plain sum of n positive terms, bound 2n·u·total;
    /// derived in NscStrayLightTests' gate).
    /// </summary>
    [Fact]
    public void PathAnalysis_IgnoresVolumeEvents_AndItsPathEnergiesStillSumToTheDetector()
    {
        var scene = new NscScene
        {
            Objects = new[]
            {
                Glass(5 * Mm, 20 * Mm, new NscVolumePhysics { Model = NscVolumeModel.AngleScattering, MeanPath = 2 * Mm, AngleDegrees = 30, WavelengthShift = NscWavelengthShift.Parse("1, 2, 1") }),
                Detector(30 * Mm, 12, 40 * Mm),
            },
        };
        const int rays = 400;
        var launch = NscSourceStandIn.PointCone(Vector3D.Zero, Vector3D.UnitZ, 10, rays, 1, NscSampling.Random, 5);
        var rec = new NscRayRecorder();
        rec.PrepareRun(Enumerable.Repeat(9, rays).ToArray(), 1);   // every ray from "source object 9"
        var settings = new NscTraceSettings { UsePolarization = false, SystemWavelengths = new[] { 0.55, 0.65 } };
        var result = new NscTracer(scene, 0.55, settings).Run(launch, rec);
        var db = rec.Database;

        var hits = db.Rays.SelectMany(r => r.Segments).SelectMany(s => s.DetectorHits ?? Array.Empty<NscDetectorHit>())
            .Where(h => h.ObjectNumber == 2).ToList();
        Assert.Contains(hits, h => h.AirMicrometers == 0.65);   // some light reached the detector shifted

        bool volumeBranchOnDetector = false;
        foreach (var ray in db.Rays)
            foreach (var b in NscRayFilter.Branches(ray.Segments))
            {
                var steps = NscPathSequence.StepsOf(ray, b);
                int faceHits = b.Skip(1).Count(k => ray.Segments[k].Hit > 0);
                Assert.Equal(1 + faceHits, steps.Count);
                if (b.Any(k => ray.Segments[k].Events.HasFlag(NscEvents.BulkScattered)) && ray.Segments[b[^1]].Hit == 2) volumeBranchOnDetector = true;
            }
        Assert.True(volumeBranchOnDetector, "no branch through a volume event reached the detector");

        double independent = 0, c = 0;
        foreach (double x in hits.Select(h => h.Power))
        {
            double t = independent + x;
            c += Math.Abs(independent) >= Math.Abs(x) ? (independent - t) + x : (x - t) + independent;
            independent = t;
        }
        independent += c;
        double detectorTotal = ((NscFlatDetectorData)result.Detectors!.Single(d => d.ObjectNumber == 2)).Flux.Sum();
        var r = NscPathAnalysis.Analyse(db, new NscPathAnalysisOptions { Detector = 2 });
        double sum = 0, cs = 0;
        foreach (double x in r.Paths.Select(p => p.Flux).Append(r.Uncategorized))
        {
            double t = sum + x;
            cs += Math.Abs(sum) >= Math.Abs(x) ? (sum - t) + x : (x - t) + sum;
            sum = t;
        }
        sum += cs;
        Assert.Equal(hits.Count, r.Branches);
        Assert.True(Math.Abs(sum - independent) <= 4 * U * independent, $"paths {sum} vs hits {independent}");
        Assert.True(Math.Abs(sum - detectorTotal) <= 2 * hits.Count * U * detectorTotal, $"paths {sum} vs detector {detectorTotal}");
    }
}
