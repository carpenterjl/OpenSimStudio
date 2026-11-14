namespace OpenSim.Optics.Analysis;

/// <summary>
/// One kind of optical analysis — a spot diagram, an MTF, a prescription report — as the
/// analysis windows and the report see it: a saved id, a name, a settings record and a compute.
/// Implementations derive from <see cref="OpticsAnalysis{TSettings}"/>, which supplies the
/// untyped half of this interface.
/// <para>
/// <see cref="Compute"/> runs off the UI thread on an immutable snapshot of the system, and may
/// be abandoned mid-way when the system changes again: it must poll the token at least once per
/// field point or per few milliseconds of work, and must not touch shared mutable state.
/// </para>
/// </summary>
public interface IOpticsAnalysis
{
    /// <summary>The name written to project files. Never changes once released, whatever the
    /// display name becomes.</summary>
    string Id { get; }

    /// <summary>The window title and menu entry.</summary>
    string Name { get; }

    /// <summary>The menu group ("Reports", "Image quality", …).</summary>
    string Category { get; }

    /// <summary>The settings record's type: a class with a public parameterless constructor
    /// whose public settable properties are numbers, booleans, enums or strings, so the window
    /// can edit it and the project file can store it.</summary>
    Type SettingsType { get; }

    /// <summary>A new settings record holding the analysis's defaults.</summary>
    object CreateDefaultSettings();

    /// <summary>Computes the analysis for <paramref name="context"/> with
    /// <paramref name="settings"/> (an instance of <see cref="SettingsType"/>). Throws
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellation"/> fires.</summary>
    OpticsAnalysisResult Compute(OpticsAnalysisContext context, object settings, CancellationToken cancellation);
}
