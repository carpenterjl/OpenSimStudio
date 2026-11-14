using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenSim.App.Controls.Plotting;

/// <summary>One exportable table of a plot: a name for the menu and file, and its CSV text
/// (built on demand: a 1024 × 1024 image is several megabytes of text).</summary>
public sealed record PlotTable(string Name, Func<string> Csv);

/// <summary>The clipboard and file side of plot export. Purely visual plumbing, so it lives with
/// the controls; the CSV itself is built by <see cref="OpenSim.Core.Plotting.PlotCsv"/>.</summary>
public static class PlotExport
{
    public static void CopyText(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            MessageBox.Show($"Could not copy to the clipboard (another program holds it): {ex.Message}", "Copy plot data",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Asks for a file name and writes <paramref name="csv"/> there; failures are shown
    /// with the path and the reason.</summary>
    public static void SaveCsv(string suggestedName, Func<string> csv)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = SafeName(suggestedName) + ".csv",
            Filter = "CSV (comma-separated values)|*.csv|Text|*.txt|All files|*.*",
            DefaultExt = ".csv"
        };
        if (dialog.ShowDialog() != true) return;
        try { File.WriteAllText(dialog.FileName, csv()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Could not write '{dialog.FileName}': {ex.Message}", "Export plot data",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>The element as it is on screen, as a bitmap at 96 dpi.</summary>
    public static BitmapSource Snapshot(FrameworkElement element)
    {
        int width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
        int height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var background = element.TryFindResource("PanelBgBrush") as Brush ?? Brushes.White;
            dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public static void CopyImage(FrameworkElement element)
    {
        try { Clipboard.SetImage(Snapshot(element)); }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            MessageBox.Show($"Could not copy to the clipboard (another program holds it): {ex.Message}", "Copy plot image",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    internal static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "plot" : cleaned;
    }
}
