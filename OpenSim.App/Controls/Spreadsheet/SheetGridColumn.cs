using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Shapes;

namespace OpenSim.App.Controls.Spreadsheet;

/// <summary>
/// One grid column of a <see cref="SpreadsheetGrid"/>: column 0 shows the row type, column k the
/// row's cell k − 1. The display element binds by path from the row (the cell's DataContext), so
/// a recycled row container shows its new row without regenerating anything. The editing element
/// is built per edit from the row's type: a list for the type column and choice cells, a text box
/// otherwise. A commit goes through <see cref="SheetDocument.SetText"/>; a value the document
/// rejects keeps the cell in edit with the reason as its tooltip.
/// </summary>
internal sealed class SheetGridColumn : DataGridColumn
{
    private readonly SpreadsheetGrid _owner;
    private Style? _readOnlyTextStyle;

    public SheetGridColumn(SpreadsheetGrid owner, int gridColumn)
    {
        _owner = owner;
        GridColumn = gridColumn;
        Width = new DataGridLength(gridColumn == SheetSchema.TypeColumn ? 128 : 96);
        MinWidth = 40;
    }

    /// <summary>The grid column this shows (0 is the row type).</summary>
    public int GridColumn { get; }

    private string CellPath(string property) => $"{nameof(SheetRow.Cells)}[{GridColumn - 1}].{property}";

    protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem)
    {
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tint = new Rectangle();
        tint.SetResourceReference(FrameworkElement.StyleProperty, "SheetTintStyle");
        Grid.SetColumnSpan(tint, 2);
        root.Children.Add(tint);

        var text = new TextBlock
        {
            Margin = new Thickness(4, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        root.Children.Add(text);

        if (GridColumn == SheetSchema.TypeColumn)
        {
            text.SetBinding(TextBlock.TextProperty, new Binding(nameof(SheetRow.TypeName)));
            text.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
            root.SetBinding(FrameworkElement.ToolTipProperty, new Binding($"{nameof(SheetRow.Type)}.{nameof(SheetRowType.Description)}"));
            return root;
        }

        text.SetBinding(TextBlock.TextProperty, new Binding(CellPath(nameof(SheetCell.Display))));
        text.Style = ReadOnlyTextStyle();

        var badge = new TextBlock
        {
            Margin = new Thickness(2, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.Bold,
            FontSize = 10,
        };
        badge.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        badge.SetBinding(TextBlock.TextProperty, new Binding(CellPath(nameof(SheetCell.Badge))));
        Grid.SetColumn(badge, 1);
        root.Children.Add(badge);

        root.SetBinding(FrameworkElement.ToolTipProperty, new Binding(CellPath(nameof(SheetCell.ToolTip))));
        return root;
    }

    /// <summary>Computed cells show in the faint text colour. One style per column, shared by
    /// its cells (the trigger's path names the column's slot).</summary>
    private Style ReadOnlyTextStyle()
    {
        if (_readOnlyTextStyle is not null) return _readOnlyTextStyle;
        var style = _readOnlyTextStyle = new Style(typeof(TextBlock));
        var computed = new DataTrigger { Binding = new Binding(CellPath(nameof(SheetCell.IsEditable))), Value = false };
        computed.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("TextFaintBrush")));
        style.Triggers.Add(computed);
        style.Seal();
        return style;
    }

    protected override FrameworkElement GenerateEditingElement(DataGridCell cell, object dataItem)
    {
        var row = (SheetRow)dataItem;
        IReadOnlyList<string>? choices = GridColumn == SheetSchema.TypeColumn
            ? row.Document.Schema.RowTypes.Select(t => t.Name).ToArray()
            : row.Cells[GridColumn - 1].Definition is { Kind: CellKind.Choice } d ? d.Choices : null;
        string current = row.Document.GetText(row.Index, GridColumn);

        if (choices is not null)
        {
            return new ComboBox
            {
                ItemsSource = choices,
                SelectedItem = choices.FirstOrDefault(c => string.Equals(c, current, StringComparison.OrdinalIgnoreCase)),
                Margin = new Thickness(0),
                Padding = new Thickness(4, 0, 4, 0),
                MinHeight = 0,
            };
        }
        return new TextBox
        {
            Text = current,
            Margin = new Thickness(0),
            Padding = new Thickness(2, 0, 2, 0),
            BorderThickness = new Thickness(0),
            MinHeight = 0,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
    }

    protected override object PrepareCellForEdit(FrameworkElement editingElement, RoutedEventArgs editingEventArgs)
    {
        switch (editingElement)
        {
            case TextBox box:
                box.Focus();
                string original = box.Text;
                // Typing on a selected cell starts the edit with what was typed, as in a
                // spreadsheet (the text-input event that began the edit is not otherwise seen
                // by an editing element created after it).
                if (editingEventArgs is TextCompositionEventArgs typed)
                {
                    box.Text = typed.Text;
                    box.Select(box.Text.Length, 0);
                }
                else
                {
                    box.SelectAll();
                }
                return original;

            case ComboBox combo:
                combo.Focus();
                if (editingEventArgs is not TextCompositionEventArgs) combo.IsDropDownOpen = true;
                return combo.SelectedItem ?? "";

            default:
                return "";
        }
    }

    protected override bool CommitCellEdit(FrameworkElement editingElement)
    {
        if (editingElement.DataContext is not SheetRow row || !row.IsAttached) return true;
        string? text = editingElement switch
        {
            TextBox box => box.Text,
            ComboBox combo => combo.SelectedItem as string,
            _ => null,
        };
        if (text is null) return true;
        if (text == row.Document.GetText(row.Index, GridColumn)) return true;

        var result = _owner.CommitText(row, GridColumn, text);
        if (result.Succeeded) return true;

        editingElement.ToolTip = result.Message;
        if (editingElement is Control control)
        {
            control.BorderThickness = new Thickness(1);
            control.SetResourceReference(Control.BorderBrushProperty, "DangerBrush");
        }
        return false;
    }

    protected override void CancelCellEdit(FrameworkElement editingElement, object uneditedValue)
    {
        // Nothing to undo: the document changes only on commit.
    }
}
