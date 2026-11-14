namespace OpenSim.Optics.Materials;

/// <summary>Whether a glass was checked, and how it came out.</summary>
public enum GlassCheckOutcome
{
    /// <summary>The computed nd (and Vd, when checked) match the printed values to the printed digits.</summary>
    Passed,
    /// <summary>A computed value differs from the printed one by more than half a printed digit.</summary>
    Failed,
    /// <summary>The glass was left out by one of the rules in <see cref="CatalogSelfCheck"/>; the reason says which.</summary>
    NotChecked
}

/// <summary>
/// One glass's self-check: nd and Vd computed from its coefficients beside the values its NM
/// line prints, and the half-digit tolerance each printed value implies.
/// </summary>
public sealed record GlassCheck
{
    public required OpticalMaterial Material { get; init; }
    public required GlassCheckOutcome Outcome { get; init; }

    /// <summary>Why the glass was not checked, or which value failed; empty when it passed.</summary>
    public string Reason { get; init; } = "";

    public double? ComputedNd { get; init; }
    public double? ComputedVd { get; init; }
    public PrintedNumber? PrintedNd => Material.Nd;
    public PrintedNumber? PrintedVd => Material.Vd;

    /// <summary>Half a unit in the last digit of the printed nd and Vd.</summary>
    public double? NdTolerance { get; init; }
    public double? VdTolerance { get; init; }

    /// <summary>|computed − printed|.</summary>
    public double? NdError => ComputedNd is double c && PrintedNd is { } p ? Math.Abs(c - p.Value) : null;
    public double? VdError => ComputedVd is double c && PrintedVd is { } p ? Math.Abs(c - p.Value) : null;

    public bool NdPassed { get; init; }

    /// <summary>False when the printed Vd is a placeholder (absent, zero or negative).</summary>
    public bool VdChecked { get; init; }
    public bool VdPassed { get; init; }
}

/// <summary>
/// The catalog checks itself (OP-11's gate): for each glass, nd and Vd computed from the CD
/// coefficients against the NM line's printed nd and Vd, to the digits printed.
/// <para>
/// nd is the index at the helium d line and Vd = (nd − 1)/(nF − nC) with the hydrogen F and C
/// lines (the Abbe number, as opticalglass computes it: <c>agf_glass.py</c> line 198,
/// <c>vd = (nd - 1)/(nF - nC)</c>; the F, d, C triple is the one <see cref="SpectralLines"/> cites
/// from SCHOTT TIE-29 equation 2.1-1). The three wavelengths are
/// <see cref="SpectralLines.LowerD"/>, <see cref="SpectralLines.F"/> and <see cref="SpectralLines.C"/>
/// (SCHOTT TIE-29, Table 2.3-1). A value passes when it differs from the printed one by no more
/// than half a unit in the last printed digit (<see cref="PrintedNumber.HalfLastDigit"/>), plus
/// 10⁻¹² of the value for binary rounding of the printed decimal.
/// </para>
/// <para>
/// <b>Not checked</b>, by these rules and no others (never by name): a glass without a dispersion
/// formula (tabulated, or a number outside 1–13 — or 1–9 for a refractiveindex.info page, OP-12,
/// whose formulas are checked like the others when the page's PROPERTIES state nd and Vd, and
/// skipped when they do not); a glass with no LD range; a printed nd that is
/// absent or ≤ 1 (vendors print 1.0 for infrared and birefringent entries whose nd is not
/// meaningful); a glass whose LD range does not contain the F, d and C lines; a glass whose
/// coefficients give exactly n = 1 at d (a placeholder entry). The Vd comparison is skipped,
/// with nd still checked, when the printed Vd is absent, zero or negative.
/// </para>
/// <para>
/// A failure is not necessarily an error in the arithmetic or the file: some vendors print the
/// melt's measured nd and a least-squares fit as the coefficients, and the fit's residual can
/// exceed half a printed digit (the test over real catalogs records by how much).
/// </para>
/// </summary>
public static class CatalogSelfCheck
{
    /// <summary>Every glass of <paramref name="catalog"/>, in file order.</summary>
    public static IReadOnlyList<GlassCheck> Run(OpticalCatalog catalog) => catalog.Materials.Select(Check).ToArray();

    /// <summary>One glass.</summary>
    public static GlassCheck Check(OpticalMaterial glass)
    {
        GlassCheck Skip(string why) => new() { Material = glass, Outcome = GlassCheckOutcome.NotChecked, Reason = why };

        if (glass.Tabulated is not null) return Skip("A tabulated material has no printed nd and Vd to check.");
        if (glass.Numbering == FormulaNumbering.RefractiveIndexInfo)
        {
            if (glass.FormulaNumber is not int rii || !RefractiveIndexInfoFormulas.IsDefined(rii))
                return Skip($"{glass.FormulaName ?? "No formula"}: not evaluated.");
            if (glass.Nd is null) return Skip("The refractiveindex.info page states no nd and Vd (in PROPERTIES) to check against.");
        }
        else if (glass.Formula is null) return Skip($"Dispersion formula {DispersionFormulas.NameOf(glass.FormulaNumber ?? 0)} is not evaluated.");
        if (glass.ValidRange is not { } range) return Skip("No LD line: the valid range is unknown.");
        if (glass.Nd is not { } nd || nd.Value <= 1) return Skip("The printed nd is a placeholder (absent or ≤ 1).");
        double d = SpectralLines.LowerD.Micrometers, f = SpectralLines.F.Micrometers, c = SpectralLines.C.Micrometers;
        if (!glass.Covers(d) || !glass.Covers(f) || !glass.Covers(c))
            return Skip($"The valid range {range.Min}–{range.Max} µm does not contain the F, d and C lines.");

        double nD, nF, nC;
        try
        {
            nD = glass.RelativeIndex(d);
            nF = glass.RelativeIndex(f);
            nC = glass.RelativeIndex(c);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            return new GlassCheck { Material = glass, Outcome = GlassCheckOutcome.Failed, Reason = e.Message };
        }
        if (nD == 1) return Skip("The coefficients give n = 1 at d: a placeholder entry.");

        double ndTol = Tolerance(nd);
        bool ndPassed = Math.Abs(nD - nd.Value) <= ndTol;
        double vd = (nD - 1) / (nF - nC);
        bool vdChecked = glass.Vd is { Value: > 0 };
        double? vdTol = vdChecked ? Tolerance(glass.Vd!) : null;
        bool vdPassed = vdChecked && Math.Abs(vd - glass.Vd!.Value) <= vdTol!.Value;

        var failures = new List<string>();
        if (!ndPassed) failures.Add($"nd {nD:F7} differs from the printed {nd.Text} by {Math.Abs(nD - nd.Value):E2} (tolerance {ndTol:E1}).");
        if (vdChecked && !vdPassed) failures.Add($"Vd {vd:F5} differs from the printed {glass.Vd!.Text} by {Math.Abs(vd - glass.Vd.Value):E2} (tolerance {vdTol:E1}).");
        string reason = string.Join(" ", failures);
        if (!vdChecked) reason = (reason + " The printed Vd is a placeholder; only nd was checked.").Trim();

        return new GlassCheck
        {
            Material = glass,
            Outcome = failures.Count == 0 ? GlassCheckOutcome.Passed : GlassCheckOutcome.Failed,
            Reason = reason,
            ComputedNd = nD,
            ComputedVd = vd,
            NdTolerance = ndTol,
            VdTolerance = vdTol,
            NdPassed = ndPassed,
            VdChecked = vdChecked,
            VdPassed = vdPassed
        };
    }

    /// <summary>One line for a catalog: how many glasses passed, failed and were not checked.</summary>
    public static string Summarize(IReadOnlyList<GlassCheck> checks)
    {
        int passed = checks.Count(c => c.Outcome == GlassCheckOutcome.Passed);
        int failed = checks.Count(c => c.Outcome == GlassCheckOutcome.Failed);
        int skipped = checks.Count(c => c.Outcome == GlassCheckOutcome.NotChecked);
        return $"{passed} of {passed + failed} checked glasses match the printed nd and Vd to the printed digits; " +
               $"{failed} do not; {skipped} not checked.";
    }

    private static double Tolerance(PrintedNumber printed) => printed.HalfLastDigit + 1e-12 * Math.Abs(printed.Value);
}
