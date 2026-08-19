using OpenSim.Core.Results;
using Xunit;

namespace OpenSim.Tests.Core;

/// <summary>
/// Gates for the tensor invariants a commercial FE report shows: equivalent elastic strain,
/// principal values, named components and max shear.
/// </summary>
public class TensorInvariantTests
{
    /// <summary>Hooke law for an isotropic material, in TENSOR shear components.</summary>
    private static SymmetricTensor Stress(SymmetricTensor strain, double e, double nu)
    {
        double lambda = e * nu / ((1 + nu) * (1 - 2 * nu));
        double mu = e / (2 * (1 + nu));
        double trace = strain.Trace();
        return new SymmetricTensor(
            lambda * trace + 2 * mu * strain.XX,
            lambda * trace + 2 * mu * strain.YY,
            lambda * trace + 2 * mu * strain.ZZ,
            2 * mu * strain.XY,
            2 * mu * strain.YZ,
            2 * mu * strain.ZX);
    }

    /// <summary>
    /// THE headline identity, and the one measured in the Ansys reference data: for isotropic
    /// linear elasticity the equivalent elastic strain times Young modulus IS the von Mises
    /// stress, exactly. (Steel 0.13522 x 2e11 = 2.7044e10 = the reported peak stress, and the
    /// same holds for ABS, alumina and E-glass.) It is an algebraic identity, not a
    /// tolerance, because the deviatoric strain is s/2G with G = E/2(1+v).
    /// </summary>
    [Theory]
    [InlineData(2.0e11, 0.3)]       // structural steel
    [InlineData(1.628e9, 0.4089)]   // ABS
    [InlineData(2.459e11, 0.2392)]  // alumina 88%
    [InlineData(7.3e10, 0.22)]      // E-glass
    [InlineData(1.0, 0.0)]          // the degenerate-but-legal ends of the range
    [InlineData(1.0, 0.49)]
    public void EquivalentStrainTimesYoungsModulus_IsVonMisesStress(double e, double nu)
    {
        var random = new Random(20260818);
        for (int trial = 0; trial < 200; trial++)
        {
            double N() => random.NextDouble() * 2 - 1;
            var strain = new SymmetricTensor(N(), N(), N(), N(), N(), N());
            var stress = Stress(strain, e, nu);

            double predicted = strain.EquivalentStrain(nu) * e;
            // Asserted as a RATIO so the tolerance is genuinely relative 1e-12 whatever the
            // scale of E: an absolute comparison at E = 2.46e11 would be meaningless.
            Assert.Equal(1.0, predicted / stress.VonMises(), 12);
        }
    }

    [Fact]
    public void UniaxialStrainState_EquivalentStrainIsTheAxialStrain()
    {
        const double eps = 1e-3, nu = 0.3;
        var strain = new SymmetricTensor(eps, -nu * eps, -nu * eps, 0, 0, 0);

        Assert.Equal(eps, strain.EquivalentStrain(nu), 15);
    }

    [Fact]
    public void PoissonRatioAtOrBelowMinusOne_IsATypedFailure()
    {
        var strain = new SymmetricTensor(1, 0, 0, 0, 0, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => strain.EquivalentStrain(-1));
    }

    [Fact]
    public void Principals_AreDescending_AndReproduceTheInvariants()
    {
        var random = new Random(7);
        for (int trial = 0; trial < 100; trial++)
        {
            double N() => random.NextDouble() * 20 - 10;
            var t = new SymmetricTensor(N(), N(), N(), N(), N(), N());
            var (s1, s2, s3) = t.Principals();

            Assert.True(s1 >= s2 && s2 >= s3, $"Principals out of order: {s1}, {s2}, {s3}");
            // First invariant and von Mises are basis-independent, so the principals must
            // reproduce both — an eigenvector mix-up would break one or the other.
            Assert.Equal(t.Trace(), s1 + s2 + s3, 10);
            double vmFromPrincipals = Math.Sqrt(0.5 *
                ((s1 - s2) * (s1 - s2) + (s2 - s3) * (s2 - s3) + (s3 - s1) * (s3 - s1)));
            Assert.Equal(t.VonMises(), vmFromPrincipals, 10);
        }
    }

    [Fact]
    public void DiagonalTensor_PrincipalsAreItsDiagonal()
    {
        var (s1, s2, s3) = new SymmetricTensor(3, -1, 7, 0, 0, 0).Principals();
        Assert.Equal(7, s1, 12);
        Assert.Equal(3, s2, 12);
        Assert.Equal(-1, s3, 12);
    }

    [Fact]
    public void PureShear_HasOppositePrincipals_AndMaxShearEqualToIt()
    {
        const double tau = 5;
        var t = new SymmetricTensor(0, 0, 0, tau, 0, 0);
        var (s1, s2, s3) = t.Principals();

        Assert.Equal(tau, s1, 12);
        Assert.Equal(0, s2, 12);
        Assert.Equal(-tau, s3, 12);
        Assert.Equal(tau, t.MaxShear(), 12);
        Assert.Equal(tau * Math.Sqrt(3), t.VonMises(), 12);
    }

    [Fact]
    public void Component_ReturnsTheStoredTensorComponents()
    {
        var t = new SymmetricTensor(1, 2, 3, 4, 5, 6);

        Assert.Equal(1, t.Component(TensorComponent.XX));
        Assert.Equal(2, t.Component(TensorComponent.YY));
        Assert.Equal(3, t.Component(TensorComponent.ZZ));
        Assert.Equal(4, t.Component(TensorComponent.XY));
        Assert.Equal(5, t.Component(TensorComponent.YZ));
        Assert.Equal(6, t.Component(TensorComponent.ZX));
        Assert.Equal(t.VonMises(), t.Component(TensorComponent.VonMises));
        Assert.Equal(t.MaxShear(), t.Component(TensorComponent.MaxShear));
        Assert.Equal(t.Principals().S1, t.Component(TensorComponent.MaxPrincipal));
        Assert.Equal(t.Principals().S3, t.Component(TensorComponent.MinPrincipal));
    }

    /// <summary>
    /// The default view must keep the source field NAME, because the results panel picks a
    /// default field by matching on it — renaming would silently change what a static solve
    /// opens on.
    /// </summary>
    [Fact]
    public void TensorComponentField_MirrorsItsSource_AndNamesOnlyNonDefaultViews()
    {
        var tensors = new[]
        {
            new SymmetricTensor(1, 2, 3, 4, 5, 6),
            new SymmetricTensor(-2, 0, 1, 0, 0, 0)
        };
        var source = new ElementTensorField("Stress tensor", "Pa", tensors);

        var vonMises = source.View(TensorComponent.VonMises);
        Assert.Equal("Stress tensor", vonMises.Name);
        Assert.Equal("Pa", vonMises.Unit);
        Assert.Equal(FieldLocation.Element, vonMises.Location);
        Assert.Equal(source.Count, vonMises.Count);
        for (int i = 0; i < source.Count; i++)
            Assert.Equal(source.GetScalar(i), vonMises.GetScalar(i));

        var shear = source.View(TensorComponent.XY);
        Assert.Contains("XY", shear.Name);
        Assert.Equal(4, shear.GetScalar(0));
        Assert.Equal(0, shear.GetScalar(1));
    }
}
