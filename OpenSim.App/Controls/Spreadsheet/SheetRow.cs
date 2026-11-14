using System.ComponentModel;

namespace OpenSim.App.Controls.Spreadsheet;

/// <summary>
/// One row of a sheet: its type, one <see cref="SheetCell"/> per slot, a tint and the values of
/// its properties flyout. Read-only from outside; change it through <see cref="SheetDocument"/>.
/// </summary>
public sealed class SheetRow : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs AllChanged = new(string.Empty);
    private readonly SheetCell[] _cells;
    private Dictionary<string, CellValue> _properties = new(StringComparer.Ordinal);
    private int _index = -1;

    internal SheetRow(SheetDocument document, long id, SheetRowType type)
    {
        Document = document;
        Id = id;
        Type = type;
        _cells = new SheetCell[document.Schema.SlotCount];
        for (int i = 0; i < _cells.Length; i++) _cells[i] = new SheetCell(this, i);
        for (int i = 0; i < _cells.Length; i++)
        {
            var definition = type.ColumnAt(i);
            _cells[i].Set(definition, definition?.DefaultValue ?? CellValue.Empty, definition?.DefaultFlag ?? CellFlag.Fixed);
        }
        foreach (var property in type.Properties) _properties[property.Key] = property.DefaultValue;
        Tint = type.DefaultTint;
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The sheet the row belongs to.</summary>
    public SheetDocument Document { get; }

    /// <summary>A number unique within the document, kept for the row's life (pickups refer to it).</summary>
    public long Id { get; }

    /// <summary>The row's type.</summary>
    public SheetRowType Type { get; private set; }

    /// <summary>The type's name (what the type column shows).</summary>
    public string TypeName => Type.Name;

    /// <summary>The cells, one per slot.</summary>
    public IReadOnlyList<SheetCell> Cells => _cells;

    /// <summary>The tint, an index into the control's palette; null for none.</summary>
    public int? Tint { get; private set; }

    /// <summary>The row's position in the sheet (0-based); −1 once deleted.</summary>
    public int Index
    {
        get => _index;
        internal set
        {
            if (_index == value) return;
            _index = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Index)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Number)));
        }
    }

    /// <summary>The number shown in the row header: the index plus the schema's
    /// <see cref="SheetSchema.FirstRowNumber"/>.</summary>
    public int Number => _index + Document.Schema.FirstRowNumber;

    /// <summary>True while the row is in the sheet.</summary>
    public bool IsAttached => _index >= 0;

    /// <summary>
    /// A locked row (the object or image surface) cannot be deleted, and no move may change its
    /// position. Not part of undo: it is set by the owning editor, not edited by the user.
    /// </summary>
    public bool IsLocked { get; set; }

    /// <summary>The values of the properties flyout, by key.</summary>
    public IReadOnlyDictionary<string, CellValue> Properties => _properties;

    /// <summary>The cell whose column has <paramref name="key"/>, or null.</summary>
    public SheetCell? CellFor(string key) =>
        _cells.FirstOrDefault(c => c.Definition is { } d && string.Equals(d.Key, key, StringComparison.Ordinal));

    /// <summary>The value of the cell or property with <paramref name="key"/>, or null.</summary>
    public CellValue? ValueOf(string key) =>
        CellFor(key)?.Value ?? (_properties.TryGetValue(key, out var value) ? value : null);

    internal RowSnapshot Capture() =>
        new(Type, _cells.Select(c => c.State).ToArray(), Tint, new Dictionary<string, CellValue>(_properties, StringComparer.Ordinal));

    internal void Restore(RowSnapshot snapshot)
    {
        Type = snapshot.Type;
        Tint = snapshot.Tint;
        _properties = new Dictionary<string, CellValue>(snapshot.Properties, StringComparer.Ordinal);
        for (int i = 0; i < _cells.Length; i++)
            _cells[i].Set(Type.ColumnAt(i), snapshot.Cells[i].Value, snapshot.Cells[i].Flag);
        PropertyChanged?.Invoke(this, AllChanged);
    }

    /// <inheritdoc/>
    public override string ToString() => $"Row {Number} ({Type.Name})";
}
