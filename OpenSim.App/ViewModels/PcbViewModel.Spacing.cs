using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Rules;

namespace OpenSim.App.ViewModels;

/// <summary>A net with the working voltage the spacing check reads it at.</summary>
public partial class NetVoltageRow : ObservableObject
{
    public NetVoltageRow(CopperNet net)
    {
        Net = net;
        Name = net.Name ?? $"Net {net.Id}";
    }

    public CopperNet Net { get; }
    public string Name { get; }
    public string Label => Net.Label;

    /// <summary>Working voltage [V] as the table defines it (IPC-2221: peak). Blank leaves the net out.</summary>
    [ObservableProperty] private string _volts = "";

    /// <summary>The voltage as a number, or null when blank or not a number.</summary>
    public double? VoltsValue =>
        double.TryParse(Volts, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : null;
}

/// <summary>
/// Clearance and creepage of the imported board against a spacing table
/// (<see cref="SpacingChecker"/>): a working voltage per net, the table column for the
/// outer layers, and the pairs that fall short.
/// </summary>
public partial class PcbViewModel
{
    public ObservableCollection<NetVoltageRow> SpacingNets { get; } = new();
    public ObservableCollection<string> SpacingLines { get; } = new();

    [ObservableProperty] private string _spacingResult = "";
    [ObservableProperty] private string _spacingAssumptions = "";

    /// <summary>The outer layers carry a permanent coating (IPC-2221 column B4 instead of B2).</summary>
    [ObservableProperty] private bool _spacingOuterCoated;

    /// <summary>Pairs farther apart than this many times their requirement are not listed.</summary>
    [ObservableProperty] private double _spacingReportWithin = 2.0;

    [ObservableProperty] private string _spacingTableName = Ipc2221Spacing.Table6_1.Name;

    private SpacingTable _spacingTable = Ipc2221Spacing.Table6_1;

    private void ClearSpacing()
    {
        SpacingNets.Clear();
        SpacingLines.Clear();
        SpacingResult = "";
        SpacingAssumptions = "";
    }

    /// <summary>Lists the board's nets for their working voltages.</summary>
    [RelayCommand]
    private void ListSpacingNets()
    {
        if (_board is null)
        {
            SpacingResult = "Import a board first.";
            return;
        }
        ClearSpacing();
        foreach (var net in _board.Nets.OrderByDescending(n => n.Area)) SpacingNets.Add(new NetVoltageRow(net));
        SpacingResult = $"{SpacingNets.Count} nets. Enter the working voltage of each net that carries one; the rest are left out.";
    }

    /// <summary>Loads a spacing table from CSV (the form <see cref="SpacingTable.ToCsv"/> writes).</summary>
    [RelayCommand]
    private void LoadSpacingTable()
    {
        var dialog = new OpenFileDialog { Filter = "Spacing table (CSV)|*.csv|All files|*.*", Title = "Load a spacing table" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var name = Path.GetFileNameWithoutExtension(dialog.FileName);
            _spacingTable = SpacingTable.ParseCsv(File.ReadAllText(dialog.FileName), name, $"loaded from {dialog.FileName}");
            SpacingTableName = name;
            foreach (var line in _spacingTable.Describe()) _log.Append(line);
            SpacingResult = $"Table '{name}' loaded: columns {string.Join(", ", _spacingTable.Columns)}. " +
                            "The first column is used on the outer layers unless it has a B2/B4 pair, the second on inner layers.";
        }
        catch (Exception ex)
        {
            SpacingResult = $"The table could not be read: {ex.Message}";
        }
    }

    /// <summary>Writes the IPC-2221 table shipped with the program as CSV, to edit and reload.</summary>
    [RelayCommand]
    private void SaveSpacingTable()
    {
        var dialog = new SaveFileDialog { FileName = "ipc2221-table6-1.csv", Filter = "Spacing table (CSV)|*.csv", Title = "Save the spacing table" };
        if (dialog.ShowDialog() != true) return;
        try { File.WriteAllText(dialog.FileName, _spacingTable.ToCsv()); }
        catch (Exception ex) { _session.ReportError(ex); }
    }

    [RelayCommand]
    private void CheckSpacing()
    {
        if (_board is null)
        {
            SpacingResult = "Import a board first.";
            return;
        }
        var volts = new Dictionary<string, double>();
        foreach (var row in SpacingNets)
            if (row.VoltsValue is { } v) volts[row.Name] = v;
        if (volts.Count < 2)
        {
            SpacingResult = "Give at least two nets a working voltage.";
            return;
        }

        var table = _spacingTable;
        bool ipc = ReferenceEquals(table, Ipc2221Spacing.Table6_1);
        string outer = ipc ? (SpacingOuterCoated ? "B4" : "B2") : table.Columns[0];
        string inner = ipc ? "B1" : table.Columns[Math.Min(1, table.Columns.Count - 1)];
        var options = new SpacingCheckOptions
        {
            Table = table, OuterColumn = outer, InnerColumn = inner,
            Stackup = BuildBoardStackup(),
            ReportWithinFactor = Math.Max(1, SpacingReportWithin)
        };
        try
        {
            var report = SpacingChecker.Check(_board, volts, options);
            SpacingLines.Clear();
            foreach (var line in report.Describe().Skip(1).Take(report.Findings.Count)) SpacingLines.Add(line.Trim());
            SpacingResult = report.Violations.Count == 0
                ? $"{report.Findings.Count} pair(s) within {options.ReportWithinFactor:g2}× their requirement; none below it ({table.Name})."
                : $"{report.Violations.Count} of {report.Findings.Count} listed pair(s) are BELOW the required spacing ({table.Name}).";
            SpacingAssumptions = string.Join(Environment.NewLine, report.Notes);
            foreach (var line in report.Describe()) _log.Append(line);
        }
        catch (Exception ex)
        {
            SpacingResult = ex.Message;
            _session.ReportError(ex);
        }
    }

    /// <summary>The stackup as the panel has it (gap thicknesses), or the board's own.</summary>
    private BoardStackup BuildBoardStackup()
    {
        if (_board is null) return new BoardStackup();
        var fromBoard = BoardStackup.FromBoard(_board);
        if (DielectricGaps.Count == 0) return fromBoard;
        return fromBoard with
        {
            GapThickness = DielectricGaps.ToDictionary(g => g.UpperLayerOrder, g => g.ThicknessMeters),
            GapPermittivity = DielectricGaps.ToDictionary(g => g.UpperLayerOrder, g => g.RelativePermittivity),
            LayerThickness = LayerFilters.ToDictionary(f => f.LayerOrder, f => f.ThicknessMeters),
            Source = "from the stackup panel"
        };
    }
}
