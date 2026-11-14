using System.Text.Json;

namespace OpenSim.Core.Model;

/// <summary>
/// The Optics workspace's analysis windows as the project saves them (OP-9): which analyses
/// were open, where each one was docked, the settings each was computed with, and the last
/// settings used for each kind of analysis. Null on <see cref="OpticsSettings.Analyses"/> — every
/// file written before the framework existed — opens no windows. Results are not saved; an
/// open window recomputes on load.
/// </summary>
public sealed record OpticsAnalysesSettings
{
    /// <summary>The open windows in display order: docked panes first (right zone, then the
    /// bottom zone, pane by pane, tab by tab), then the floating windows.</summary>
    public List<OpticsAnalysisWindowSettings>? Windows { get; init; }

    /// <summary>The settings each kind of analysis was last used with, by the analysis's saved
    /// id, so a window opened again starts where the last one left off. Kept as JSON because
    /// each analysis defines its own settings record, which the project file does not know.</summary>
    public Dictionary<string, JsonElement>? LastSettings { get; init; }

    /// <summary>Width of the right dock zone in device-independent pixels; null means the default.</summary>
    public double? RightDockWidth { get; init; }

    /// <summary>Height of the bottom dock zone in device-independent pixels; null means the default.</summary>
    public double? BottomDockHeight { get; init; }
}

/// <summary>One open analysis window: what it shows, where it is, and its settings.</summary>
public sealed record OpticsAnalysisWindowSettings
{
    /// <summary>The analysis's saved id (OpenSim.Optics' registry). A string, so a file from a
    /// later build with an analysis this one lacks still loads; that window is kept and named.</summary>
    public string? Analysis { get; init; }

    /// <summary>"Right", "Bottom" or "Float". Null or unknown reads as the bottom zone.</summary>
    public string? Dock { get; init; }

    /// <summary>The pane (tab group) within the dock zone, counted from 0. Ignored for a floating window.</summary>
    public int? Pane { get; init; }

    /// <summary>True for the tab shown in its pane.</summary>
    public bool? Selected { get; init; }

    /// <summary>False when the window waits for its Update button instead of recomputing on
    /// every change. Null means automatic.</summary>
    public bool? AutoUpdate { get; init; }

    /// <summary>The window's settings as its analysis defines them.</summary>
    public JsonElement? Settings { get; init; }

    /// <summary>Screen bounds of a floating window, in device-independent pixels.</summary>
    public double? FloatLeft { get; init; }
    public double? FloatTop { get; init; }
    public double? FloatWidth { get; init; }
    public double? FloatHeight { get; init; }
}
