using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace OpenSim.App.Controls.Spreadsheet;

/// <summary>
/// The editable spreadsheet the optical editors share (lens data, multi-configuration, merit
/// function, tolerance, non-sequential components, fields): a stock WPF <see cref="DataGrid"/>
/// with typed cells, flags, type-dependent headers, row tints, a properties flyout per row and a
/// flag editor per cell, over a <see cref="SheetDocument"/> that holds the content and the undo
/// history.
/// <para>
/// The control holds no state of its own beyond what is on screen: every command turns the grid's
/// selection into a <see cref="SheetSelection"/> and calls the document, so the rules live in
/// plain classes the tests reach without a window. Messages for the log panel come out through
/// <see cref="Message"/>.
/// </para>
/// </summary>
public partial class SpreadsheetGrid : UserControl
{
    /// <summary>The document the grid shows and edits.</summary>
    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
        nameof(Document), typeof(SheetDocument), typeof(SpreadsheetGrid),
        new PropertyMetadata(null, (d, e) => ((SpreadsheetGrid)d).OnDocumentChanged((SheetDocument?)e.OldValue, (SheetDocument?)e.NewValue)));

    /// <summary>The last block this process copied from a sheet. A paste whose clipboard text is
    /// still that block's text pastes the block itself, flags included; any other text pastes as
    /// plain values.</summary>
    private static SheetBlock? s_lastCopied;

    private FlagDraft? _flagDraft;

    /// <summary>A grid with no document.</summary>
    public SpreadsheetGrid()
    {
        InitializeComponent();
        SheetGrid.BeginningEdit += OnBeginningEdit;
        SheetGrid.CurrentCellChanged += (_, _) => UpdateHeaders();
        SheetGrid.PreviewKeyDown += OnPreviewKeyDown;
        SheetGrid.PreviewMouseRightButtonDown += OnPreviewRightButtonDown;
        SheetGrid.ContextMenu = new ContextMenu();
        SheetGrid.ContextMenuOpening += OnContextMenuOpening;
        RowFlyout.Closed += (_, _) => SheetGrid.Focus();
        FlagFlyout.Closed += (_, _) => SheetGrid.Focus();
        RowFlyout.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { RowFlyout.IsOpen = false; e.Handled = true; } };
        FlagFlyout.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { FlagFlyout.IsOpen = false; e.Handled = true; } };
    }

    /// <summary>Raised with a message for the log panel (a refused edit, a paste note).</summary>
    public event EventHandler<SheetMessageEventArgs>? Message;

    /// <summary>The names of the row tints, in palette order; a row's <see cref="SheetRow.Tint"/>
    /// indexes this list. Each is one of the theme's data-kind colours.</summary>
    public static IReadOnlyList<string> TintNames { get; } = new[] { "Blue", "Violet", "Amber", "Cyan", "Green", "Rose", "Teal" };

    /// <summary>The document shown.</summary>
    public SheetDocument? Document
    {
        get => (SheetDocument?)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    /// <summary>The underlying grid, for an editor that needs a DataGrid setting this control
    /// does not expose.</summary>
    public DataGrid DataGrid => SheetGrid;

    /// <summary>The selected cells; the current cell alone when nothing is selected.</summary>
    public SheetSelection Selection
    {
        get
        {
            var cells = SheetGrid.SelectedCells
                .Where(c => c.Item is SheetRow { IsAttached: true } && c.Column is SheetGridColumn)
                .Select(c => new SheetCellAddress(((SheetRow)c.Item).Index, ((SheetGridColumn)c.Column).GridColumn))
                .ToArray();
            if (cells.Length > 0) return new SheetSelection(cells);
            return CurrentCell is { } current ? SheetSelection.Single(current.Row, current.Column) : SheetSelection.Empty;
        }
    }

    /// <summary>The cell with the keyboard focus rectangle, or null.</summary>
    public SheetCellAddress? CurrentCell =>
        SheetGrid.CurrentCell.Item is SheetRow { IsAttached: true } row && SheetGrid.CurrentCell.Column is SheetGridColumn column
            ? new SheetCellAddress(row.Index, column.GridColumn)
            : null;

    /// <summary>Selects <paramref name="selection"/>, makes its top-left cell current and scrolls
    /// it into view. Cells outside the sheet are ignored.</summary>
    public void Select(SheetSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (Document is not { } doc) return;
        var cells = selection.Cells.Where(a => a.Row >= 0 && a.Row < doc.RowCount && a.Column >= 0 && a.Column < SheetGrid.Columns.Count).ToArray();
        if (cells.Length == 0) { SheetGrid.SelectedCells.Clear(); return; }
        var first = cells[0];
        var firstInfo = new DataGridCellInfo(doc.Rows[first.Row], SheetGrid.Columns[first.Column]);
        SheetGrid.CurrentCell = firstInfo;
        SheetGrid.SelectedCells.Clear();
        foreach (var a in cells) SheetGrid.SelectedCells.Add(new DataGridCellInfo(doc.Rows[a.Row], SheetGrid.Columns[a.Column]));
        SheetGrid.ScrollIntoView(firstInfo.Item, firstInfo.Column);
    }

    /// <summary>True when <paramref name="command"/> has something to act on now.</summary>
    public bool CanExecute(SheetCommand command)
    {
        if (Document is not { } doc) return false;
        return command switch
        {
            SheetCommand.Undo => doc.History.CanUndo,
            SheetCommand.Redo => doc.History.CanRedo,
            SheetCommand.Paste => ClipboardHasText(),
            SheetCommand.InsertRowAbove or SheetCommand.InsertRowBelow => true,
            SheetCommand.EditFlag => CurrentCell is { Column: > 0 } c && doc.IsEditable(c.Row, c.Column),
            SheetCommand.RowProperties => CurrentCell is not null,
            _ => !Selection.IsEmpty,
        };
    }

    /// <summary>Runs <paramref name="command"/> on the selection (finishing any edit in progress
    /// first). Returns false when it did nothing; why goes out through <see cref="Message"/>.</summary>
    public bool Execute(SheetCommand command)
    {
        if (Document is not { } doc) return false;
        if (!EndEdit()) return false;
        var selection = Selection;
        SheetEditResult result;
        switch (command)
        {
            case SheetCommand.Copy:
                return Copy(selection);
            case SheetCommand.Cut:
                if (!Copy(selection)) return false;
                result = doc.ClearCells(selection.Cells, "Cut");
                break;
            case SheetCommand.Paste:
                string? text = ClipboardText();
                if (text is null) return false;
                var block = s_lastCopied is { } last && last.ToText() == text ? last : SheetBlock.Parse(text);
                result = doc.Paste(block, selection);
                break;
            case SheetCommand.Undo:
                return doc.Undo();
            case SheetCommand.Redo:
                return doc.Redo();
            case SheetCommand.InsertRowAbove:
            case SheetCommand.InsertRowBelow:
                int count = Math.Max(1, selection.RowIndices.Count);
                int at = selection.IsEmpty ? doc.RowCount
                    : command == SheetCommand.InsertRowAbove ? selection.Top : selection.Bottom + 1;
                result = doc.InsertRows(at, count);
                if (result.Succeeded)
                {
                    int column = CurrentCell?.Column ?? 0;
                    Select(new SheetSelection(Enumerable.Range(at, count).Select(r => new SheetCellAddress(r, column))));
                    Report(result);
                    return true;
                }
                break;
            case SheetCommand.DeleteRows:
                result = doc.DeleteRows(selection.RowIndices);
                if (result.Succeeded && doc.RowCount > 0)
                {
                    Select(SheetSelection.Single(Math.Min(selection.Top, doc.RowCount - 1), Math.Max(0, selection.Left)));
                    return true;
                }
                break;
            case SheetCommand.ClearCells:
                result = doc.ClearCells(selection.Cells);
                break;
            case SheetCommand.MoveRowsUp:
            case SheetCommand.MoveRowsDown:
                int offset = command == SheetCommand.MoveRowsUp ? -1 : 1;
                result = doc.MoveRows(selection.RowIndices, offset);
                if (result.Succeeded) { Select(selection.Offset(offset)); return true; }
                break;
            case SheetCommand.ToggleVariable:
                result = doc.ToggleVariable(selection.Cells);
                break;
            case SheetCommand.EditFlag:
                return CurrentCell is { } cell && OpenFlagEditor(cell.Row, cell.Column);
            case SheetCommand.RowProperties:
                return CurrentCell is { } current && OpenRowProperties(current.Row);
            default:
                return false;
        }
        Report(result);
        if (result.Succeeded && result.Affected is { } affected) Select(affected);
        return result.Succeeded;
    }

    // ------------------------------------------------------------------ document

    private void OnDocumentChanged(SheetDocument? old, SheetDocument? document)
    {
        if (old is not null)
        {
            old.StructureChanging -= OnStructureChanging;
            old.Changed -= OnDocumentContentChanged;
        }
        RowFlyout.IsOpen = false;
        FlagFlyout.IsOpen = false;
        SheetGrid.ItemsSource = null;
        SheetGrid.Columns.Clear();
        if (document is null) return;
        document.StructureChanging += OnStructureChanging;
        document.Changed += OnDocumentContentChanged;
        for (int c = 0; c < document.ColumnCount; c++) SheetGrid.Columns.Add(new SheetGridColumn(this, c));
        SheetGrid.ItemsSource = document.Rows;
        UpdateHeaders();
    }

    /// <summary>A row insert, delete or move (an undo included) must not happen under an open
    /// cell edit: the grid's collection view refuses changes during an edit transaction.</summary>
    private void OnStructureChanging(object? sender, EventArgs e) => SheetGrid.CancelEdit(DataGridEditingUnit.Row);

    private void OnDocumentContentChanged(object? sender, EventArgs e)
    {
        UpdateHeaders();
        if (RowFlyout.IsOpen && RowFlyout.Tag is SheetRow row)
        {
            if (row.IsAttached) BuildRowForm(row);
            else RowFlyout.IsOpen = false;
        }
    }

    /// <summary>The headers follow the current row's type, as in the editors this serves.</summary>
    private void UpdateHeaders()
    {
        if (Document is not { } doc) return;
        var type = SheetGrid.CurrentCell.Item is SheetRow row ? row.Type : null;
        foreach (var column in SheetGrid.Columns.OfType<SheetGridColumn>())
        {
            string header = doc.Schema.HeaderOf(column.GridColumn, type);
            if (!Equals(column.Header, header)) column.Header = header;
        }
    }

    internal SheetEditResult CommitText(SheetRow row, int column, string text)
    {
        var result = row.Document.SetText(row.Index, column, text);
        Report(result);
        return result;
    }

    private bool EndEdit() =>
        SheetGrid.CommitEdit(DataGridEditingUnit.Cell, true) && SheetGrid.CommitEdit(DataGridEditingUnit.Row, true);

    private void Report(SheetEditResult result)
    {
        if (result.Message is { } text) Message?.Invoke(this, new SheetMessageEventArgs(text, !result.Succeeded));
    }

    private void Report(string text, bool isError) => Message?.Invoke(this, new SheetMessageEventArgs(text, isError));

    // ------------------------------------------------------------------ input

    private void OnBeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Row.Item is not SheetRow row || e.Column is not SheetGridColumn column || !row.IsAttached) { e.Cancel = true; return; }
        if (!row.Document.IsEditable(row.Index, column.GridColumn)) e.Cancel = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBoxBase or ComboBox or ComboBoxItem) return;   // a cell is being edited
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (SheetKeyMap.Resolve(key, Keyboard.Modifiers) is not { } command) return;
        e.Handled = true;
        Execute(command);
    }

    private void OnPreviewRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var cell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cell is null || cell.IsEditing) return;
        if (!cell.IsSelected)
        {
            SheetGrid.SelectedCells.Clear();
            cell.IsSelected = true;
        }
        SheetGrid.CurrentCell = new DataGridCellInfo(cell);
        cell.Focus();
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        return node as T;
    }

    // ------------------------------------------------------------------ context menu

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Document is null) { e.Handled = true; return; }
        var menu = SheetGrid.ContextMenu!;
        menu.Items.Clear();
        menu.Items.Add(CommandItem("Cut", SheetCommand.Cut));
        menu.Items.Add(CommandItem("Copy", SheetCommand.Copy));
        menu.Items.Add(CommandItem("Paste", SheetCommand.Paste));
        menu.Items.Add(new Separator());
        menu.Items.Add(CommandItem("Insert row above", SheetCommand.InsertRowAbove));
        menu.Items.Add(CommandItem("Insert row below", SheetCommand.InsertRowBelow));
        menu.Items.Add(CommandItem("Delete rows", SheetCommand.DeleteRows));
        menu.Items.Add(CommandItem("Move rows up", SheetCommand.MoveRowsUp));
        menu.Items.Add(CommandItem("Move rows down", SheetCommand.MoveRowsDown));
        menu.Items.Add(CommandItem("Clear cells", SheetCommand.ClearCells));
        menu.Items.Add(new Separator());
        menu.Items.Add(FlagMenu());
        menu.Items.Add(RowTypeMenu());
        menu.Items.Add(TintMenu());
        menu.Items.Add(CommandItem("Row properties…", SheetCommand.RowProperties));
        menu.Items.Add(new Separator());
        var doc = Document;
        menu.Items.Add(CommandItem(doc.History.UndoDescription is { } u ? $"Undo {u}" : "Undo", SheetCommand.Undo));
        menu.Items.Add(CommandItem(doc.History.RedoDescription is { } r ? $"Redo {r}" : "Redo", SheetCommand.Redo));
    }

    private MenuItem CommandItem(string header, SheetCommand command)
    {
        var item = new MenuItem
        {
            Header = header,
            InputGestureText = SheetKeyMap.GestureText.TryGetValue(command, out var keys) ? keys : "",
            IsEnabled = CanExecute(command),
        };
        item.Click += (_, _) => Execute(command);
        return item;
    }

    private MenuItem ActionItem(string header, Action action, bool enabled = true, bool isChecked = false)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled, IsChecked = isChecked };
        item.Click += (_, _) => { if (EndEdit()) action(); };
        return item;
    }

    private MenuItem FlagMenu()
    {
        var menu = new MenuItem { Header = "Flag" };
        var doc = Document!;
        var current = CurrentCell is { Column: > 0 } c && doc.IsEditable(c.Row, c.Column) ? doc.Rows[c.Row].Cells[c.Column - 1] : null;
        var definition = current?.Definition;
        menu.IsEnabled = definition is not null && definition.Flags != CellFlagSupport.None;
        if (!menu.IsEnabled) return menu;

        menu.Items.Add(ActionItem("Fixed", () => SetFlagOnSelection(CellFlag.Fixed), isChecked: current!.Flag.Kind == CellFlagKind.Fixed));
        var variable = CommandItem("Variable", SheetCommand.ToggleVariable);
        variable.IsEnabled = definition!.Supports(CellFlagKind.Variable);
        variable.IsChecked = current.Flag.Kind == CellFlagKind.Variable;
        menu.Items.Add(variable);
        if (definition.Supports(CellFlagKind.Solve))
        {
            var solve = new MenuItem { Header = "Solve", IsChecked = current.Flag.Kind == CellFlagKind.Solve };
            foreach (var code in definition.SolveCodes)
            {
                solve.Items.Add(ActionItem(code.Name, () =>
                {
                    if (code.ParameterNames.Count > 0) OpenFlagEditor(current.Row.Index, current.Column, CellFlagKind.Solve, code.Key);
                    else Report(doc.SetFlag(current.Row.Index, current.Column, CellFlag.Solve(code.Key)));
                }, isChecked: current.Flag.SolveCode == code.Key));
            }
            menu.Items.Add(solve);
        }
        if (definition.Supports(CellFlagKind.Pickup))
            menu.Items.Add(ActionItem("Pickup…", () => OpenFlagEditor(current.Row.Index, current.Column, CellFlagKind.Pickup),
                isChecked: current.Flag.Kind == CellFlagKind.Pickup));
        menu.Items.Add(CommandItem("Edit flag…", SheetCommand.EditFlag));
        return menu;
    }

    private void SetFlagOnSelection(CellFlag flag)
    {
        var doc = Document!;
        int changed = 0;
        using (doc.History.BeginGroup("Make fixed"))
        {
            foreach (var a in Selection.Cells)
                if (a.Column > 0 && doc.ValidateFlag(a.Row, a.Column, flag) is null && doc.SetFlag(a.Row, a.Column, flag).CellsChanged > 0)
                    changed++;
        }
        if (changed == 0) Report("None of the selected cells could be made fixed.", isError: false);
    }

    private MenuItem RowTypeMenu()
    {
        var doc = Document!;
        var menu = new MenuItem { Header = "Row type" };
        var rows = Selection.RowIndices;
        menu.IsEnabled = rows.Count > 0;
        var currentType = CurrentCell is { } c ? doc.Rows[c.Row].Type : null;
        foreach (var type in doc.Schema.RowTypes)
        {
            menu.Items.Add(ActionItem(type.Name, () =>
            {
                using (doc.History.BeginGroup("Change type"))
                    foreach (int r in rows) Report(doc.SetRowType(r, type));
            }, isChecked: ReferenceEquals(type, currentType)));
        }
        return menu;
    }

    private MenuItem TintMenu()
    {
        var doc = Document!;
        var menu = new MenuItem { Header = "Row colour" };
        var rows = Selection.RowIndices;
        menu.IsEnabled = rows.Count > 0;
        int? currentTint = CurrentCell is { } c ? doc.Rows[c.Row].Tint : null;
        for (int i = -1; i < TintNames.Count; i++)
        {
            int? tint = i < 0 ? null : i;
            menu.Items.Add(ActionItem(i < 0 ? "None" : TintNames[i], () =>
            {
                using (doc.History.BeginGroup("Row colour"))
                    foreach (int r in rows) doc.SetTint(r, tint);
            }, isChecked: currentTint == tint));
        }
        return menu;
    }

    // ------------------------------------------------------------------ clipboard

    private bool Copy(SheetSelection selection)
    {
        if (selection.IsEmpty) { Report("Select the cells to copy.", isError: true); return false; }
        var block = Document!.Copy(selection);
        string text = block.ToText();
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException)
        {
            Report("The clipboard is in use by another program; nothing was copied. Try again.", isError: true);
            return false;
        }
        s_lastCopied = block;
        return true;
    }

    private string? ClipboardText()
    {
        try
        {
            if (Clipboard.ContainsText()) return Clipboard.GetText();
            Report("The clipboard holds no text to paste.", isError: true);
        }
        catch (ExternalException)
        {
            Report("The clipboard is in use by another program; nothing was pasted. Try again.", isError: true);
        }
        return null;
    }

    private static bool ClipboardHasText()
    {
        try { return Clipboard.ContainsText(); }
        catch (ExternalException) { return false; }
    }

    // ------------------------------------------------------------------ row properties flyout

    /// <summary>Opens the properties flyout of row <paramref name="rowIndex"/>: its type, tint and
    /// the type's property fields, laid out with <see cref="FormGrid"/>.</summary>
    public bool OpenRowProperties(int rowIndex)
    {
        if (Document is not { } doc || rowIndex < 0 || rowIndex >= doc.RowCount) return false;
        if (!EndEdit()) return false;
        FlagFlyout.IsOpen = false;
        var row = doc.Rows[rowIndex];
        BuildRowForm(row);
        RowFlyout.Tag = row;
        RowFlyout.PlacementTarget = RowContainer(row);
        RowFlyout.IsOpen = true;
        FocusFirstInput(RowForm);
        return true;
    }

    private void BuildRowForm(SheetRow row)
    {
        var doc = row.Document;
        RowFlyoutTitle.Text = $"Row {row.Number}: {row.Type.Name}";
        RowForm.Children.Clear();
        ShowError(RowError, null);

        var type = new ComboBox { ItemsSource = doc.Schema.RowTypes, SelectedItem = row.Type, ToolTip = row.Type.Description };
        type.SelectionChanged += (_, _) =>
        {
            if (!row.IsAttached || type.SelectedItem is not SheetRowType chosen || ReferenceEquals(chosen, row.Type)) return;
            var result = doc.SetRowType(row.Index, chosen);
            Report(result);
            if (result.Succeeded) BuildRowForm(row);   // the new type's properties
            else ShowError(RowError, result.Message);
        };
        AddField(RowForm, doc.Schema.TypeHeader, type);

        var tintNames = new[] { "None" }.Concat(TintNames).ToArray();
        var tint = new ComboBox
        {
            ItemsSource = tintNames,
            SelectedIndex = row.Tint is int t && t < TintNames.Count ? t + 1 : 0,
        };
        tint.SelectionChanged += (_, _) =>
        {
            if (!row.IsAttached || tint.SelectedIndex < 0) return;
            doc.SetTint(row.Index, tint.SelectedIndex == 0 ? null : tint.SelectedIndex - 1);
        };
        AddField(RowForm, "Colour", tint);

        foreach (var property in row.Type.Properties)
        {
            var current = row.Properties.TryGetValue(property.Key, out var value) ? value : property.DefaultValue;
            if (property.Kind == CellKind.Choice)
            {
                var combo = new ComboBox { ItemsSource = property.Choices, SelectedItem = current.Text, ToolTip = property.ToolTip };
                combo.SelectionChanged += (_, _) =>
                {
                    if (!row.IsAttached || combo.SelectedItem is not string chosen) return;
                    var result = doc.SetProperty(row.Index, property.Key, chosen);
                    Report(result);
                    ShowError(RowError, result.Succeeded ? null : result.Message);
                };
                AddField(RowForm, property.Header, combo);
                continue;
            }
            var box = new TextBox { Text = CellText.Format(property, current), ToolTip = property.ToolTip };
            void Commit()
            {
                if (!row.IsAttached) return;
                var result = doc.SetProperty(row.Index, property.Key, box.Text);
                Report(result);
                MarkInvalid(box, result.Succeeded ? null : result.Message);
                ShowError(RowError, result.Succeeded ? null : result.Message);
            }
            box.LostKeyboardFocus += (_, _) => Commit();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
            AddField(RowForm, property.Header, box);
        }
    }

    // ------------------------------------------------------------------ flag editor

    /// <summary>Opens the flag editor of a cell: Fixed, Variable, a solve with its parameters, or
    /// a pickup's source row, column, scale and offset. <paramref name="kind"/> and
    /// <paramref name="solveCode"/> preselect a flag (from the context menu).</summary>
    public bool OpenFlagEditor(int rowIndex, int column, CellFlagKind? kind = null, string? solveCode = null)
    {
        if (Document is not { } doc || rowIndex < 0 || rowIndex >= doc.RowCount || column < 1 || column >= doc.ColumnCount) return false;
        if (!EndEdit()) return false;
        var cell = doc.Rows[rowIndex].Cells[column - 1];
        if (!cell.IsEditable)
        {
            Report($"Row {cell.Row.Number}: this cell takes no flag.", isError: true);
            return false;
        }
        RowFlyout.IsOpen = false;
        _flagDraft = FlagDraft.From(cell, kind, solveCode);
        BuildFlagForm();
        FlagFlyout.PlacementTarget = CellContainer(cell.Row, column);
        FlagFlyout.IsOpen = true;
        FocusFirstInput(FlagForm);
        return true;
    }

    private void BuildFlagForm()
    {
        var draft = _flagDraft!;
        var cell = draft.Cell;
        var definition = cell.Definition!;
        var doc = cell.Row.Document;
        FlagFlyoutTitle.Text = $"Row {cell.Row.Number}, {definition.Header}";
        FlagForm.Children.Clear();
        ShowError(FlagError, null);

        var kinds = Enum.GetValues<CellFlagKind>().Where(definition.Supports).ToArray();
        var kind = new ComboBox { ItemsSource = kinds, SelectedItem = draft.Kind };
        kind.SelectionChanged += (_, _) =>
        {
            if (kind.SelectedItem is CellFlagKind k && k != draft.Kind) { draft.Kind = k; BuildFlagForm(); }
        };
        AddField(FlagForm, "Flag", kind);

        if (draft.Kind == CellFlagKind.Solve)
        {
            var codes = new ComboBox { ItemsSource = definition.SolveCodes, DisplayMemberPath = nameof(SheetSolveCode.Name) };
            codes.SelectedItem = definition.FindSolveCode(draft.SolveCode) ?? definition.SolveCodes[0];
            draft.SolveCode = ((SheetSolveCode)codes.SelectedItem).Key;
            codes.SelectionChanged += (_, _) =>
            {
                if (codes.SelectedItem is SheetSolveCode code && code.Key != draft.SolveCode) { draft.SolveCode = code.Key; BuildFlagForm(); }
            };
            AddField(FlagForm, "Solve", codes);
            var names = ((SheetSolveCode)codes.SelectedItem).ParameterNames;
            draft.FitParameters(names.Count);
            for (int i = 0; i < names.Count; i++)
            {
                int index = i;
                var box = new TextBox { Text = draft.Parameters[i] };
                box.TextChanged += (_, _) => draft.Parameters[index] = box.Text;
                AddField(FlagForm, names[i], box);
            }
        }
        else if (draft.Kind == CellFlagKind.Pickup)
        {
            var fromRow = new TextBox { Text = draft.FromRow };
            fromRow.TextChanged += (_, _) => draft.FromRow = fromRow.Text;
            fromRow.LostKeyboardFocus += (_, _) => RefreshPickupColumns();
            AddField(FlagForm, "From row", fromRow);

            var fromColumn = new ComboBox { Name = "PickupColumn" };
            fromColumn.SelectionChanged += (_, _) => { if (fromColumn.SelectedIndex >= 0) draft.FromColumn = fromColumn.SelectedIndex + 1; };
            AddField(FlagForm, "From column", fromColumn);
            FillPickupColumns(fromColumn, draft, doc);

            var scale = new TextBox { Text = draft.Scale };
            scale.TextChanged += (_, _) => draft.Scale = scale.Text;
            AddField(FlagForm, "Scale", scale);
            var offset = new TextBox { Text = draft.Offset };
            offset.TextChanged += (_, _) => draft.Offset = offset.Text;
            AddField(FlagForm, "Offset", offset);
        }
    }

    /// <summary>The pickup's column list names each column as the source row's type does.</summary>
    private static void FillPickupColumns(ComboBox combo, FlagDraft draft, SheetDocument doc)
    {
        var source = draft.SourceRow(doc);
        combo.ItemsSource = Enumerable.Range(1, doc.Schema.SlotCount)
            .Select(c => $"{c}: {doc.Schema.HeaderOf(c, source?.Type)}")
            .ToArray();
        combo.SelectedIndex = Math.Clamp(draft.FromColumn, 1, doc.Schema.SlotCount) - 1;
    }

    private void RefreshPickupColumns()
    {
        if (_flagDraft is not { Kind: CellFlagKind.Pickup } draft) return;
        var combo = FlagForm.Children.OfType<ComboBox>().FirstOrDefault(c => c.Name == "PickupColumn");
        if (combo is not null) FillPickupColumns(combo, draft, draft.Cell.Row.Document);
    }

    private void OnFlagApply(object sender, RoutedEventArgs e)
    {
        if (_flagDraft is not { } draft || !draft.Cell.Row.IsAttached) { FlagFlyout.IsOpen = false; return; }
        var doc = draft.Cell.Row.Document;
        if (!draft.TryBuild(doc, out var flag, out string? error))
        {
            ShowError(FlagError, error);
            return;
        }
        var result = doc.SetFlag(draft.Cell.Row.Index, draft.Cell.Column, flag!);
        Report(result);
        if (!result.Succeeded) { ShowError(FlagError, result.Message); return; }
        FlagFlyout.IsOpen = false;
    }

    private void OnFlagCancel(object sender, RoutedEventArgs e) => FlagFlyout.IsOpen = false;

    // ------------------------------------------------------------------ flyout helpers

    private static void AddField(FormGrid form, string label, FrameworkElement editor)
    {
        form.Children.Add(new Label { Content = label });
        form.Children.Add(editor);
    }

    private static void ShowError(TextBlock target, string? message)
    {
        target.Text = message ?? "";
        target.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void MarkInvalid(TextBox box, string? message)
    {
        box.ToolTip = message;
        if (message is null) box.ClearValue(Control.BorderBrushProperty);
        else box.SetResourceReference(Control.BorderBrushProperty, "DangerBrush");
    }

    private void FocusFirstInput(Panel form)
    {
        var first = form.Children.OfType<Control>().FirstOrDefault(c => c is TextBox or ComboBox);
        if (first is not null) Dispatcher.BeginInvoke(() => first.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private UIElement RowContainer(SheetRow row)
    {
        SheetGrid.ScrollIntoView(row);
        SheetGrid.UpdateLayout();
        return SheetGrid.ItemContainerGenerator.ContainerFromItem(row) as UIElement ?? SheetGrid;
    }

    private UIElement CellContainer(SheetRow row, int column)
    {
        SheetGrid.ScrollIntoView(row, SheetGrid.Columns[column]);
        SheetGrid.UpdateLayout();
        return SheetGrid.Columns[column].GetCellContent(row)?.Parent as UIElement ?? RowContainer(row);
    }

    /// <summary>The flag editor's fields as typed, kept as text until Apply so a half-typed
    /// number is not refused on every keystroke.</summary>
    private sealed class FlagDraft
    {
        private FlagDraft(SheetCell cell) => Cell = cell;

        public SheetCell Cell { get; }

        public CellFlagKind Kind { get; set; }

        public string? SolveCode { get; set; }

        public string[] Parameters { get; private set; } = Array.Empty<string>();

        public string FromRow { get; set; } = "";

        public int FromColumn { get; set; }

        public string Scale { get; set; } = "1";

        public string Offset { get; set; } = "0";

        public static FlagDraft From(SheetCell cell, CellFlagKind? kind, string? solveCode)
        {
            var flag = cell.Flag;
            var draft = new FlagDraft(cell)
            {
                Kind = kind ?? flag.Kind,
                SolveCode = solveCode ?? flag.SolveCode,
                Parameters = flag.SolveParameters.Select(Text).ToArray(),
                FromColumn = cell.Column,
            };
            if (flag.Pickup is { } pickup && cell.Row.Document.FindRow(pickup.RowId) is { } source)
            {
                draft.FromRow = source.Number.ToString(CultureInfo.InvariantCulture);
                draft.FromColumn = pickup.Column;
                draft.Scale = Text(pickup.Scale);
                draft.Offset = Text(pickup.Offset);
            }
            else
            {
                // A new pickup defaults to the same column of the row above (or of this row's
                // neighbour below, on the first row).
                int sourceIndex = cell.Row.Index > 0 ? cell.Row.Index - 1 : Math.Min(1, cell.Row.Document.RowCount - 1);
                draft.FromRow = cell.Row.Document.Rows[sourceIndex].Number.ToString(CultureInfo.InvariantCulture);
            }
            return draft;
        }

        public void FitParameters(int count)
        {
            if (Parameters.Length == count) return;
            var fitted = new string[count];
            for (int i = 0; i < count; i++) fitted[i] = i < Parameters.Length ? Parameters[i] : "0";
            Parameters = fitted;
        }

        public SheetRow? SourceRow(SheetDocument doc)
        {
            if (!int.TryParse(FromRow.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)) return null;
            int index = number - doc.Schema.FirstRowNumber;
            return index >= 0 && index < doc.RowCount ? doc.Rows[index] : null;
        }

        public bool TryBuild(SheetDocument doc, out CellFlag? flag, out string? error)
        {
            flag = null;
            error = null;
            switch (Kind)
            {
                case CellFlagKind.Fixed:
                    flag = CellFlag.Fixed;
                    return true;
                case CellFlagKind.Variable:
                    flag = CellFlag.Variable;
                    return true;
                case CellFlagKind.Solve:
                    var code = Cell.Definition!.FindSolveCode(SolveCode);
                    if (code is null) { error = "Choose a solve."; return false; }
                    var values = new double[Parameters.Length];
                    for (int i = 0; i < values.Length; i++)
                    {
                        if (!CellText.TryParseNumber(Parameters[i], out values[i]) || !double.IsFinite(values[i]))
                        {
                            error = $"{code.ParameterNames[i]}: '{Parameters[i].Trim()}' is not a number.";
                            return false;
                        }
                    }
                    flag = CellFlag.Solve(code.Key, values);
                    return true;
                default:
                    var source = SourceRow(doc);
                    if (source is null) { error = $"From row: '{FromRow.Trim()}' is not a row of the sheet."; return false; }
                    if (!CellText.TryParseNumber(Scale, out double scale) || !double.IsFinite(scale)) { error = $"Scale: '{Scale.Trim()}' is not a number."; return false; }
                    if (!CellText.TryParseNumber(Offset, out double offset) || !double.IsFinite(offset)) { error = $"Offset: '{Offset.Trim()}' is not a number."; return false; }
                    flag = CellFlag.PickupFrom(source, FromColumn, scale, offset);
                    return true;
            }
        }

        private static string Text(double x) => x.ToString("R", CultureInfo.InvariantCulture);
    }
}
