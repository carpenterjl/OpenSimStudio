using System.ComponentModel;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Paraxial;

namespace OpenSim.Optics.Analysis;

/// <summary>Settings of the <see cref="ParaxialDataAnalysis"/>.</summary>
public sealed record ParaxialDataSettings
{
    /// <summary>The wavelength row (1 = the first); 0 is the primary wavelength.</summary>
    [DisplayName("Wavelength")]
    [Description("Row of the wavelength table, 1 for the first; 0 for the primary.")]
    public int Wavelength { get; init; }

    [DisplayName("Cardinal points")]
    public bool ShowCardinalPoints { get; init; } = true;

    [DisplayName("Paraxial ray data")]
    public bool ShowRayData { get; init; } = true;
}

/// <summary>
/// System data, cardinal points and paraxial ray data of a sequential system (OpticStudio's
/// System Data, Cardinal Points and Paraxial Ray Data reports), from the paraxial engine
/// (<see cref="ParaxialSystem"/>, OP-19).
/// <para>
/// The project has no sequential surfaces until the lens data editor (OP-20, OP-31) adds them,
/// so the lens comes from a source given to the constructor; the default source has none, and
/// the result then says so instead of listing numbers.
/// </para>
/// </summary>
public sealed class ParaxialDataAnalysis : OpticsAnalysis<ParaxialDataSettings>
{
    /// <summary>The saved id; see <see cref="IOpticsAnalysis.Id"/>.</summary>
    public const string SavedId = "ParaxialData";

    private readonly Func<OpticsAnalysisContext, ParaxialLens?> _lensSource;

    /// <param name="lensSource">Where the surfaces come from; null for none (until OP-31).</param>
    public ParaxialDataAnalysis(Func<OpticsAnalysisContext, ParaxialLens?>? lensSource = null)
    {
        _lensSource = lensSource ?? (_ => null);
    }

    /// <summary>The analysis fed from a real-trace lens source (OP-20): the paraxial engine
    /// takes the same lens through <see cref="Sequential.SequentialLens.ToParaxialLens"/>.</summary>
    public static ParaxialDataAnalysis FromSequential(Func<OpticsAnalysisContext, Sequential.SequentialLens?> lensSource)
    {
        ArgumentNullException.ThrowIfNull(lensSource);
        return new ParaxialDataAnalysis(context => lensSource(context)?.ToParaxialLens());
    }

    public override string Id => SavedId;
    public override string Name => "System data and paraxial rays";
    public override string Category => "Reports";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, ParaxialDataSettings settings,
        CancellationToken cancellation)
    {
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        var lens = _lensSource(context);
        if (lens is null)
        {
            return OpticsAnalysisResult.FromSections(Name, new[]
            {
                new ReportSection("No surfaces")
                {
                    Lines = new[] { "The system has no sequential surfaces yet: the surface editor is a later plan item (OP-20), so there is nothing to trace." }
                }
            }, new[] { "Nothing is traced." });
        }
        int count = system.Wavelengths.Wavelengths.Count;
        if (settings.Wavelength < 0 || settings.Wavelength > count)
            throw new ArgumentException($"Paraxial data: the wavelength row must be 0 (primary) or 1 to {count} (it is {settings.Wavelength}).");
        int? row = settings.Wavelength == 0 ? null : settings.Wavelength - 1;
        var model = ParaxialSystem.Create(lens, system, row);
        cancellation.ThrowIfCancellationRequested();
        var all = count > 1 ? ParaxialSystem.ForEachWavelength(lens, system) : null;
        cancellation.ThrowIfCancellationRequested();
        var sections = ParaxialReport.Sections(model, all, settings.ShowCardinalPoints, settings.ShowRayData);
        var assumptions = ParaxialReport.Assumptions(model).Concat(notes).ToArray();
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions);
    }
}
