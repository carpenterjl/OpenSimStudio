using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace OpenSim.App.Controls;

/// <summary>
/// The label/value form every properties panel uses: children are read in pairs, label then
/// value, one pair per row (a last unpaired child takes the whole row).
/// <para>
/// A row sits side by side, split evenly, when both halves fit. A label too long for its half,
/// or a value that would not show in its half (a long choice, a list of numbers, several boxes
/// too narrow for their digits), drops the row to two lines instead: the label above, the value
/// below at full width. A fixed two-column grid here hid the end of long labels under the input
/// next to them and cut split boxes down to a digit, and the end of a label is usually the part
/// that says what the field is.
/// </para>
/// </summary>
public class FormGrid : Panel
{
    /// <summary>Gap between a side-by-side label and its value.</summary>
    private const double ColumnGap = 4;

    /// <summary>The width one box of a split value needs to show a value like "-0.035": the
    /// theme's 12 px monospace digits plus its padding and border.</summary>
    private const double MinBoxWidth = 64;

    /// <summary>The rows of the last measure pass; arrange reuses the decision made there,
    /// because re-measuring a child during arrange would overwrite its desired size.</summary>
    private readonly List<Row> _rows = new();

    private readonly record struct Row(UIElement First, UIElement? Value, bool Stacked);

    protected override Size MeasureOverride(Size availableSize)
    {
        _rows.Clear();
        bool unlimited = double.IsInfinity(availableSize.Width);
        double width = unlimited ? WidthWithEverythingSideBySide() : availableSize.Width;
        double half = Half(width);
        var line = new Size(width, double.PositiveInfinity);
        var halfLine = new Size(half, double.PositiveInfinity);
        double height = 0;

        foreach (var (first, value) in VisibleRows())
        {
            if (value is null)
            {
                first.Measure(line);
                height += first.DesiredSize.Height;
                _rows.Add(new Row(first, null, Stacked: true));
                continue;
            }
            // The natural widths first: the decision to stack is made on them.
            bool stacked = NaturalWidth(first) > half || NaturalWidth(value) > half || MinSplitWidth(value) > half;
            first.Measure(stacked ? line : halfLine);
            value.Measure(stacked ? line : halfLine);
            height += stacked
                ? first.DesiredSize.Height + value.DesiredSize.Height
                : Math.Max(first.DesiredSize.Height, value.DesiredSize.Height);
            _rows.Add(new Row(first, value, stacked));
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double half = Half(finalSize.Width);
        double y = 0;
        foreach (var row in _rows)
        {
            double firstHeight = row.First.DesiredSize.Height;
            if (row.Value is null)
            {
                row.First.Arrange(new Rect(0, y, finalSize.Width, firstHeight));
                y += firstHeight;
            }
            else if (row.Stacked)
            {
                row.First.Arrange(new Rect(0, y, finalSize.Width, firstHeight));
                row.Value.Arrange(new Rect(0, y + firstHeight, finalSize.Width, row.Value.DesiredSize.Height));
                y += firstHeight + row.Value.DesiredSize.Height;
            }
            else
            {
                double rowHeight = Math.Max(firstHeight, row.Value.DesiredSize.Height);
                row.First.Arrange(new Rect(0, y, half, rowHeight));
                row.Value.Arrange(new Rect(half + ColumnGap, y, half, rowHeight));
                y += rowHeight;
            }
        }
        return finalSize;
    }

    private static double Half(double width) => Math.Max(0, (width - ColumnGap) / 2);

    /// <summary>The width a child asks for with no limit: a wrapped caption's one-line width.</summary>
    private static double NaturalWidth(UIElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return element.DesiredSize.Width;
    }

    /// <summary>The narrowest a split value may be and stay legible: a box per column.</summary>
    private static double MinSplitWidth(UIElement value) =>
        value is UniformGrid { Columns: > 1 } split ? split.Columns * MinBoxWidth : 0;

    private double WidthWithEverythingSideBySide()
    {
        double widest = 0;
        foreach (UIElement child in InternalChildren) widest = Math.Max(widest, NaturalWidth(child));
        return 2 * widest + ColumnGap;
    }

    /// <summary>The rows to lay out: a pair with both children collapsed takes no room, and a
    /// collapsed half leaves the other half a row of its own.</summary>
    private IEnumerable<(UIElement First, UIElement? Value)> VisibleRows()
    {
        for (int i = 0; i < InternalChildren.Count; i += 2)
        {
            var label = InternalChildren[i];
            var value = i + 1 < InternalChildren.Count ? InternalChildren[i + 1] : null;
            bool labelShown = label.Visibility != Visibility.Collapsed;
            bool valueShown = value is not null && value.Visibility != Visibility.Collapsed;
            if (labelShown && valueShown) yield return (label, value);
            else if (labelShown) yield return (label, null);
            else if (valueShown) yield return (value!, null);
        }
    }
}
