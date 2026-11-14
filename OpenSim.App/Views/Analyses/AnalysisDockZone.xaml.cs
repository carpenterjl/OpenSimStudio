using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace OpenSim.App.Views.Analyses;

/// <summary>One dock zone: its panes side by side (<see cref="Vertical"/> false) or one above
/// the other, each taking an equal share.</summary>
public partial class AnalysisDockZone : UserControl
{
    public AnalysisDockZone()
    {
        InitializeComponent();
        ApplyPanel();
    }

    /// <summary>The zone's panes (a layout's <c>Right</c> or <c>Bottom</c> collection).</summary>
    public IEnumerable? Panes
    {
        get => PaneList.ItemsSource;
        set => PaneList.ItemsSource = value;
    }

    private bool _vertical;

    /// <summary>True to stack the panes top to bottom (the right zone).</summary>
    public bool Vertical
    {
        get => _vertical;
        set
        {
            _vertical = value;
            ApplyPanel();
        }
    }

    private void ApplyPanel()
    {
        var panel = new FrameworkElementFactory(typeof(UniformGrid));
        panel.SetValue(_vertical ? UniformGrid.ColumnsProperty : UniformGrid.RowsProperty, 1);
        PaneList.ItemsPanel = new ItemsPanelTemplate(panel);
    }
}
