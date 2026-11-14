using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using OpenSim.App.Analyses;
using OpenSim.App.Controls;

namespace OpenSim.App.Views.Analyses;

/// <summary>
/// An analysis window's settings form: a <see cref="FormGrid"/> with one row per field of the
/// <see cref="AnalysisSettingsEditor"/>. Built in code because the rows come from the settings
/// record and a FormGrid reads its children in label/value pairs, which an ItemsControl cannot
/// produce; the values are bound, so nothing here holds state.
/// </summary>
public sealed class AnalysisSettingsForm : ContentControl
{
    public static readonly DependencyProperty EditorProperty = DependencyProperty.Register(
        nameof(Editor), typeof(AnalysisSettingsEditor), typeof(AnalysisSettingsForm),
        new PropertyMetadata(null, (d, _) => ((AnalysisSettingsForm)d).Rebuild()));

    public AnalysisSettingsEditor? Editor
    {
        get => (AnalysisSettingsEditor?)GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    private void Rebuild()
    {
        var grid = new FormGrid();
        foreach (var field in Editor?.Fields ?? Array.Empty<AnalysisSettingField>())
        {
            grid.Children.Add(new Label { Content = field.Label, ToolTip = field.Description });
            grid.Children.Add(EditorFor(field));
        }
        if (grid.Children.Count == 0)
            grid.Children.Add(new TextBlock { Text = "This analysis has no settings.", Style = TryFindResource("FaintText") as Style });
        Content = grid;
    }

    private static FrameworkElement EditorFor(AnalysisSettingField field)
    {
        FrameworkElement editor = field.Kind switch
        {
            SettingKind.Flag => Bound(new CheckBox { VerticalAlignment = VerticalAlignment.Center },
                ToggleButtonIsChecked, nameof(AnalysisSettingField.Flag)),
            SettingKind.Choice => Bound(new ComboBox { ItemsSource = field.Choices },
                System.Windows.Controls.Primitives.Selector.SelectedItemProperty, nameof(AnalysisSettingField.Choice)),
            _ => Bound(new TextBox(), TextBox.TextProperty, nameof(AnalysisSettingField.Text)),
        };
        editor.DataContext = field;
        editor.ToolTip = field.Description;
        System.Windows.Automation.AutomationProperties.SetAutomationId(editor, "AnalysisSetting_" + field.Name);
        return editor;
    }

    private static DependencyProperty ToggleButtonIsChecked => System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty;

    private static FrameworkElement Bound(FrameworkElement element, DependencyProperty property, string path)
    {
        element.SetBinding(property, new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        return element;
    }
}
