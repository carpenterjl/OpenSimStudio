using System.Globalization;
using System.Numerics;
using OpenSim.Optics.Materials;

namespace OpenSim.Optics.Coatings;

/// <summary>
/// A coating material (OpticStudio's MATE section): n and k against wavelength, k ≥ 0 for an
/// absorbing material (index n + ik, docs/Optics-Conventions.md section 12). One row is a constant
/// index used at every wavelength, as OpticStudio's coating file defines it. Two or more rows are
/// a tabulated <see cref="OpticalMaterial"/> (OP-11) and interpolate linearly, as both OP-11 and
/// OpticStudio's MATE section do.
/// <para>
/// <b>Outside the table.</b> OpticStudio holds the first or last row's values beyond the ends of
/// a MATE table (its help, "The MATE Data Section"). Here, as for every OP-11 material, a
/// wavelength outside the table is refused with a message naming the material: nothing is
/// extrapolated, not even as a constant (docs/Optics-Conventions.md section 8).
/// </para>
/// </summary>
public sealed record CoatingMaterial
{
    public required string Name { get; init; }

    /// <summary>The rows, wavelength (µm) strictly increasing; one row is a constant index.</summary>
    public required IReadOnlyList<TabulatedIndexPoint> Rows { get; init; }

    /// <summary>A constant-index material.</summary>
    public static CoatingMaterial Constant(string name, double n, double k = 0) =>
        new() { Name = name, Rows = new[] { new TabulatedIndexPoint(1, n, k) } };

    public bool IsConstant => Rows.Count == 1;

    /// <summary>The table as an OP-11 material, for two or more rows.</summary>
    public OpticalMaterial AsOpticalMaterial(string catalog = "") => new()
    {
        Name = Name, Catalog = catalog, Tabulated = new TabulatedIndex { Points = Rows }
    };

    /// <summary>What is wrong with the material, one line each; empty when it can be used.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Name)) problems.Add("A coating material has no name.");
        if (Rows is null || Rows.Count == 0)
        {
            problems.Add($"Coating material '{Name}' has no index rows.");
            return problems;
        }
        if (Rows.Count == 1)
        {
            var row = Rows[0];
            if (!double.IsFinite(row.N) || row.N <= 0) problems.Add($"Coating material '{Name}': n must be positive (it is {F(row.N)}).");
            if (!double.IsFinite(row.K) || row.K < 0) problems.Add($"Coating material '{Name}': k must be zero or positive (it is {F(row.K)}).");
            return problems;
        }
        foreach (var p in new TabulatedIndex { Points = Rows }.Validate())
            problems.Add($"Coating material '{Name}': {p}");
        return problems;
    }

    /// <summary>The complex index n + ik at <paramref name="micrometers"/>. Throws, naming the
    /// material and the wavelength, when the material cannot be used there.</summary>
    public Complex Index(double micrometers)
    {
        if (Validate() is { Count: > 0 } problems)
            throw new InvalidOperationException(string.Join(" ", problems));
        if (IsConstant) return new Complex(Rows[0].N, Rows[0].K);
        var material = AsOpticalMaterial();
        double lo = Rows[0].Micrometers, hi = Rows[^1].Micrometers;
        if (!double.IsFinite(micrometers) || micrometers < lo || micrometers > hi)
            throw new ArgumentOutOfRangeException(nameof(micrometers), micrometers,
                $"Coating material '{Name}': {F(micrometers)} µm is outside its table, {F(lo)}–{F(hi)} µm. Indices are not extrapolated; add rows that cover the wavelength.");
        var (n, k) = material.Tabulated!.Interpolate(micrometers);
        return new Complex(n, k);
    }

    private static string F(double v) => v.ToString("G9", CultureInfo.InvariantCulture);
}
