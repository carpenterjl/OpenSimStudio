using System.Windows;

namespace OpenSim.App.Views.Analyses;

/// <summary>The window a floating analysis lives in. Created and closed by
/// <see cref="AnalysisDockSite"/> as the layout's floating list changes.</summary>
public partial class AnalysisFloatWindow : Window
{
    public AnalysisFloatWindow() => InitializeComponent();
}
