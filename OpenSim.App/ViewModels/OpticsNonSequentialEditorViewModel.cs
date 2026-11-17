using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Controls.Spreadsheet;
using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.NonSequential.Editing;
using S = OpenSim.App.ViewModels.OpticsNonSequentialEditorSchema;

namespace OpenSim.App.ViewModels;

/// <summary>
/// The Non-Sequential Component Editor (plan item OP-71, docs/Optics-Conventions.md §71): the
/// non-sequential objects on OP-10's spreadsheet (<see cref="SheetDocument"/>, schema
/// <see cref="OpticsNonSequentialEditorSchema"/>), saved as <see cref="OpticsNonSequentialSettings"/>
/// with the project, and the object tools of <c>OpenSim.Optics.NonSequential.Editing</c>. Every
/// edit lands on the sheet's undo stack; each tool is one step. After every change the editor
/// checks the objects (<see cref="NscObjectCheck"/>) and publishes the system.
/// <para>
/// Inserting, deleting and moving rows — from the buttons, the grid's keys or its menu — keeps
/// every reference object and Inside Of number pointing at the same object, in the same form
/// (absolute or relative), as part of the same undo step; an edit that would leave one pointing
/// at a deleted object, or at an object after it, is refused naming both
/// (<see cref="NscObjectTable.Remap"/>). Data the sheet has no cell for (the parameters of an
/// unknown type past the last column, faces of other numbers, fields later plan items add to
/// <see cref="OpticsNscObjectSettings"/>) are kept with the row and written back, and a value
/// left as loaded is written back exactly as the file had it (a missing value stays missing).
/// New objects of a type the catalogue does not know are refused, naming the plan item.
/// </para>
/// </summary>
public sealed partial class OpticsNonSequentialEditorViewModel : ObservableObject
{
    private readonly Action _publish;
    private readonly Func<LensUnit> _unit;
    private readonly Func<IReadOnlyCollection<string>?> _coatings;
    private readonly NscObjectCatalogue _catalogue;
    private readonly Dictionary<long, OpticsNscObjectSettings> _base = new();
    private OpticsNonSequentialSettings? _settings;
    private bool _applying;
    private bool _quiet;
    private (SheetRow Row, NscObjectLinks Links)[]? _pending;

    /// <param name="publish">Called after every change, to publish the system.</param>
    /// <param name="unit">The lens unit (for building shapes in the check and the viewer).</param>
    /// <param name="coatings">The coating catalog's names, or null to skip the coating check.</param>
    /// <param name="catalogue">The object types; <see cref="NscObjectCatalogue.Default"/> when null.</param>
    public OpticsNonSequentialEditorViewModel(Action publish, Func<LensUnit> unit,
        Func<IReadOnlyCollection<string>?>? coatings = null, NscObjectCatalogue? catalogue = null)
    {
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _unit = unit ?? throw new ArgumentNullException(nameof(unit));
        _coatings = coatings ?? (() => null);
        _catalogue = catalogue ?? NscObjectCatalogue.Default;
        Document = NewDocument(Array.Empty<string>());
        NewObjectType = _catalogue.Types[0].TypeName;
    }

    /// <summary>The sheet the grid shows; replaced when a file brings a type the schema lacks.</summary>
    [ObservableProperty] private SheetDocument _document = null!;

    /// <summary>The row of the grid's current cell (0-based; object number − 1).</summary>
    [ObservableProperty] private int _currentRow;

    /// <summary>What the last command did, or why it did nothing.</summary>
    [ObservableProperty] private string _status = "";

    /// <summary>What is wrong with the objects, one line each.</summary>
    [ObservableProperty] private IReadOnlyList<string> _problems = Array.Empty<string>();

    /// <summary>The type the insert buttons add.</summary>
    [ObservableProperty] private string _newObjectType = "";

    // Replicate Object (help, "Replicate Object").
    [ObservableProperty] private int _replicateNumberX = 3;
    [ObservableProperty] private int _replicateNumberY = 1;
    [ObservableProperty] private int _replicateNumberZ = 1;
    [ObservableProperty] private double _replicateDeltaX = 1;
    [ObservableProperty] private double _replicateDeltaY;
    [ObservableProperty] private double _replicateDeltaZ;
    [ObservableProperty] private bool _replicateRelativeReferences;
    [ObservableProperty] private bool _replicateAddPickupSolves;

    // Modify Reference Object (help, "Modify Reference Object").
    [ObservableProperty] private int _modifyFirstObject = 1;
    [ObservableProperty] private int _modifyLastObject = 1;
    [ObservableProperty] private int _modifyReferTo;

    /// <summary>The type names the insert buttons offer (the catalogue's).</summary>
    public IReadOnlyList<string> ObjectTypes => _catalogue.Types.Select(t => t.TypeName).ToArray();

    /// <summary>The number of objects.</summary>
    public int ObjectCount => Document.RowCount;

    public bool HasProblems => Problems.Count > 0;

    /// <summary>The check for the window and the panel.</summary>
    public string ProblemsText => ObjectCount == 0 ? "No non-sequential objects."
        : Problems.Count == 0 ? $"{ObjectCount} object{(ObjectCount == 1 ? "" : "s")}; no problems found (glass names are checked when traced)."
        : string.Join(Environment.NewLine, Problems);

    /// <summary>The Object Properties sections this build does not have, by name.</summary>
    public string NotBuiltPropertiesText => NscNotBuiltTools.PropertySectionsText;

    partial void OnProblemsChanged(IReadOnlyList<string> value)
    {
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(ProblemsText));
    }

    // ------------------------------------------------------------------ files

    /// <summary>The non-sequential system as saved: the loaded record with its object list
    /// replaced by the table (null when nothing was loaded and the table is empty).</summary>
    public OpticsNonSequentialSettings? ToSettings()
    {
        var rows = Rows();
        if (_settings is null && rows.Count == 0) return null;
        return (_settings ?? new OpticsNonSequentialSettings()) with
        {
            Objects = rows.Count > 0 || _settings?.Objects is not null ? rows : null,
        };
    }

    /// <summary>The objects as saved rows, in order.</summary>
    public List<OpticsNscObjectSettings> Rows() => Document.Rows.Select(ReadRow).ToList();

    /// <summary>Loads a file's non-sequential system (null clears the table). Clears the undo
    /// history; does not publish (the workspace does).</summary>
    public void LoadSettings(OpticsNonSequentialSettings? saved, ICollection<string> notes)
    {
        _settings = saved;
        Load(saved?.Objects ?? new List<OpticsNscObjectSettings>(), notes);
        LoadScatterFields();   // OP-80
    }

    /// <summary>A new project: no objects.</summary>
    public void Reset() => LoadSettings(null, new List<string>());

    private void Load(IReadOnlyList<OpticsNscObjectSettings> rows, ICollection<string> notes)
    {
        var names = rows.Select(r => TypeNameOf(r)).ToArray();
        var known = Document.Schema.RowTypes.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Any(n => !known.Contains(n))) Document = NewDocument(names.Concat(known).ToArray());
        _applying = true;
        try
        {
            var doc = Document;
            if (doc.RowCount > 0) doc.DeleteRows(Enumerable.Range(0, doc.RowCount));
            _base.Clear();
            if (rows.Count > 0) doc.InsertRows(0, rows.Count);
            for (int i = 0; i < rows.Count; i++) WriteRow(i, rows[i] ?? new OpticsNscObjectSettings(), notes);
            doc.History.Clear();
        }
        finally { _applying = false; }
        CurrentRow = 0;
        AfterChange(publish: false);
    }

    private static string TypeNameOf(OpticsNscObjectSettings? r) => string.IsNullOrWhiteSpace(r?.Type) ? S.NoType : r!.Type!.Trim();

    private SheetDocument NewDocument(IReadOnlyList<string> otherTypes)
    {
        if (Document is { } old)
        {
            old.Changed -= OnSheetChanged;
            old.StructureGuard = null;
            old.StructureEdited = null;
        }
        var doc = new SheetDocument(S.Build(_catalogue, otherTypes.Where(n => _catalogue.Find(n) is null)));
        doc.Changed += OnSheetChanged;
        doc.StructureGuard = OnStructureGuard;
        doc.StructureEdited = OnStructureEdited;
        return doc;
    }

    // ------------------------------------------------------------------ renumbering

    private string? OnStructureGuard(IReadOnlyList<int> map)
    {
        _pending = null;
        if (_applying) return null;
        var doc = Document;
        var links = doc.Rows.Select(LinksOf).ToArray();
        NscObjectTable.Remap(links, map, out string? refusal);
        if (refusal is not null) return refusal;
        _pending = doc.Rows.Select((r, i) => (r, links[i])).ToArray();
        return null;
    }

    private void OnStructureEdited(IReadOnlyList<int> map)
    {
        if (_applying || _pending is not { } pending) return;
        _pending = null;
        var remapped = NscObjectTable.Remap(pending.Select(p => p.Links).ToArray(), map, out _);
        bool wasQuiet = _quiet;
        _quiet = true;
        try
        {
            for (int i = 0; i < pending.Length; i++)
            {
                if (remapped[i] is not { } l || !pending[i].Row.IsAttached) continue;
                int index = pending[i].Row.Index;
                if (l.ReferenceObject != pending[i].Links.ReferenceObject) SetInt(index, S.ReferenceSlot, l.ReferenceObject);
                if (l.InsideOf != pending[i].Links.InsideOf) SetInt(index, S.InsideOfSlot, l.InsideOf);
            }
        }
        finally { _quiet = wasQuiet; }
    }

    private static NscObjectLinks LinksOf(SheetRow row) => new(IntOf(row, S.ReferenceSlot), IntOf(row, S.InsideOfSlot));

    private static int IntOf(SheetRow row, int slot) => row.Cells[slot].Value is { IsNumber: true } v ? (int)v.Number : 0;

    private void SetInt(int row, int slot, int value) => Check(Document.SetValue(row, slot + 1, CellValue.Of(value)));

    private static void Check(SheetEditResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException(result.Message);
    }

    // ------------------------------------------------------------------ changes

    private void OnSheetChanged(object? sender, EventArgs e)
    {
        if (_applying || _quiet) return;
        // A row given a type the catalogue does not know (a generic row type exists only for
        // saved objects of that type) is not added: the edit is undone and the reason given.
        if (!Document.History.IsInGroup && Document.Rows.FirstOrDefault(r => _catalogue.Find(r.Type.Name) is null && !IsSavedAs(r)) is { } added)
        {
            string message = $"Object {added.Number}: {_catalogue.NotBuiltMessage(added.Type.Name == S.NoType ? "" : added.Type.Name)}";
            _applying = true;
            try { Document.Undo(); }
            finally { _applying = false; }
            Status = message;
        }
        AfterChange(publish: true);
    }

    private bool IsSavedAs(SheetRow row) =>
        _base.TryGetValue(row.Id, out var b) && string.Equals(TypeNameOf(b), row.Type.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>After any change: check the objects and publish.</summary>
    private void AfterChange(bool publish)
    {
        var problems = new List<string>();
        try { problems.AddRange(NscObjectCheck.Problems(Rows(), _catalogue, _unit(), _coatings())); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException) { problems.Add(ex.Message); }
        Problems = problems;
        OnPropertyChanged(nameof(ObjectCount));
        if (publish) _publish();
    }

    // ------------------------------------------------------------------ rows

    private OpticsNscObjectSettings ReadRow(SheetRow row)
    {
        var known = _base.TryGetValue(row.Id, out var k) ? k : null;
        var b = known ?? new OpticsNscObjectSettings();
        bool sameType = known is not null && string.Equals(TypeNameOf(known), row.Type.Name, StringComparison.OrdinalIgnoreCase);
        var d = _catalogue.Find(row.Type.Name);
        double Num(int slot) => row.Cells[slot].Value is { IsNumber: true } v ? v.Number : 0;
        string Text(int slot) => row.Cells[slot].Value.Text;
        string Prop(string key) => row.Properties.TryGetValue(key, out var v) ? v.Text : "";

        var s = b with
        {
            Type = sameType ? b.Type : row.Type.Name == S.NoType ? null : row.Type.Name,
            Comment = Keep(b.Comment, Text(S.CommentSlot)),
            ReferenceObject = Keep(b.ReferenceObject, IntOf(row, S.ReferenceSlot)),
            InsideOf = Keep(b.InsideOf, IntOf(row, S.InsideOfSlot)),
            X = Keep(b.X, Num(S.XSlot)),
            Y = Keep(b.Y, Num(S.YSlot)),
            Z = Keep(b.Z, Num(S.ZSlot)),
            TiltX = Keep(b.TiltX, Num(S.TiltXSlot)),
            TiltY = Keep(b.TiltY, Num(S.TiltYSlot)),
            TiltZ = Keep(b.TiltZ, Num(S.TiltZSlot)),
            Material = Keep(b.Material, Text(S.MaterialSlot)),
            GlobalXyzRotationOrder = Keep(b.GlobalXyzRotationOrder, Prop(S.GlobalOrderProperty) == S.Yes),
            DetectingStandIn = Keep(b.DetectingStandIn, Prop(S.StandInProperty) == S.Yes),
        };

        // Parameters: the cells shown, then (same type) the saved ones past the last column.
        var saved = sameType ? b.Parameters : null;
        int shown = ShownParameters(d);
        bool unchanged = sameType;
        for (int i = 0; i < shown && unchanged; i++)
            unchanged = Num(S.FirstParameterSlot + i).Equals(ShownParameter(d, saved, i));
        if (!unchanged)
        {
            var list = new List<double>();
            for (int i = 0; i < shown; i++) list.Add(Num(S.FirstParameterSlot + i));
            if (saved is not null) for (int i = shown; i < saved.Count; i++) list.Add(saved[i]);
            if (d is null)
            {
                int keep = Math.Max(saved?.Count ?? 0, list.FindLastIndex(v => v != 0) + 1);
                list.RemoveRange(keep, list.Count - keep);
            }
            s = s with { Parameters = list };
        }

        // Faces: those of other numbers, and every face of a type the catalogue does not know, as saved.
        var faces = sameType ? b.Faces : b.Faces?.Where(f => d is not null && d.Faces.ContainsKey(f.Face ?? 0)).ToList();
        if (!sameType && faces is { Count: 0 } && b.Faces is not null) faces = null;
        if (d is not null)
        {
            List<OpticsNscFaceSettings>? edited = null;
            foreach (int face in d.Faces.Keys.OrderBy(f => f))
            {
                var entry = faces?.LastOrDefault(f => (f.Face ?? 0) == face);
                string faceIs = Prop(S.FaceIsProperty(face)), coating = Prop(S.CoatingProperty(face));
                if (faceIs == DisplayFaceIs(entry) && coating == (entry?.Coating ?? "")) continue;
                edited ??= faces?.ToList() ?? new List<OpticsNscFaceSettings>();
                var replaced = (entry ?? new OpticsNscFaceSettings { Face = face }) with
                {
                    FaceIs = faceIs == DisplayFaceIs(entry) ? entry?.FaceIs : faceIs,
                    Coating = Keep(entry?.Coating, coating),
                };
                int at = entry is null ? -1 : edited.LastIndexOf(entry);
                if (at >= 0) edited[at] = replaced;
                else edited.Add(replaced);
            }
            if (edited is not null) faces = edited;
        }
        return s with { Faces = faces, Flags = ReadFlags(row, b.Flags) };   // OP-88: Variable cells
    }

    private static int ShownParameters(NscObjectTypeDescriptor? d) => d is null ? S.ParameterSlots : Math.Min(d.Parameters.Count, S.ParameterSlots);

    private static double ShownParameter(NscObjectTypeDescriptor? d, List<double>? saved, int i) =>
        saved is not null && i < saved.Count ? saved[i] : d is not null && i < d.Parameters.Count ? d.Parameters[i].Default : 0;

    /// <summary>What the flyout shows for a face's "Face Is": the choice the saved text names
    /// (ignoring case), "Object Default" when blank or missing.</summary>
    private static string DisplayFaceIs(OpticsNscFaceSettings? entry)
    {
        string t = (entry?.FaceIs ?? "").Trim();
        return S.FaceIsChoices.FirstOrDefault(c => string.Equals(c, t, StringComparison.OrdinalIgnoreCase)) ?? S.FaceIsChoices[0];
    }

    private static int? Keep(int? saved, int value) => (saved ?? 0) == value ? saved : value;
    private static double? Keep(double? saved, double value) => (saved ?? 0).Equals(value) ? saved : value;
    private static bool? Keep(bool? saved, bool value) => (saved ?? false) == value ? saved : value;
    private static string? Keep(string? saved, string value) => (saved ?? "") == value ? saved : value.Length == 0 ? null : value;

    /// <summary>Writes a saved row into sheet row <paramref name="index"/> and remembers it.</summary>
    private void WriteRow(int index, OpticsNscObjectSettings s, ICollection<string> notes)
    {
        var doc = Document;
        string where = $"Object {index + 1}";
        var type = doc.Schema.FindRowType(TypeNameOf(s)) ?? throw new InvalidOperationException($"{where}: no row type for '{TypeNameOf(s)}'.");
        if (!ReferenceEquals(doc.Rows[index].Type, type)) Note(doc.SetRowType(index, type));
        var row = doc.Rows[index];
        var d = _catalogue.Find(type.Name);
        Note(doc.SetValue(index, S.CommentSlot + 1, CellValue.Of(s.Comment ?? "")));
        Note(doc.SetValue(index, S.ReferenceSlot + 1, CellValue.Of(s.ReferenceObject ?? 0)));
        Note(doc.SetValue(index, S.InsideOfSlot + 1, CellValue.Of(s.InsideOf ?? 0)));
        Note(doc.SetValue(index, S.XSlot + 1, CellValue.Of(s.X ?? 0)));
        Note(doc.SetValue(index, S.YSlot + 1, CellValue.Of(s.Y ?? 0)));
        Note(doc.SetValue(index, S.ZSlot + 1, CellValue.Of(s.Z ?? 0)));
        Note(doc.SetValue(index, S.TiltXSlot + 1, CellValue.Of(s.TiltX ?? 0)));
        Note(doc.SetValue(index, S.TiltYSlot + 1, CellValue.Of(s.TiltY ?? 0)));
        Note(doc.SetValue(index, S.TiltZSlot + 1, CellValue.Of(s.TiltZ ?? 0)));
        Note(doc.SetValue(index, S.MaterialSlot + 1, CellValue.Of(s.Material ?? "")));
        for (int i = 0; i < ShownParameters(d); i++)
            Note(doc.SetValue(index, S.FirstParameterSlot + 1 + i, CellValue.Of(ShownParameter(d, s.Parameters, i))));
        Note(doc.SetProperty(index, S.GlobalOrderProperty, s.GlobalXyzRotationOrder == true ? S.Yes : S.No));
        Note(doc.SetProperty(index, S.StandInProperty, s.DetectingStandIn == true ? S.Yes : S.No));
        if (d is not null)
            foreach (int face in d.Faces.Keys)
            {
                var entry = s.Faces?.LastOrDefault(f => (f.Face ?? 0) == face);
                if (entry?.FaceIs is { } text && !string.IsNullOrWhiteSpace(text) && !S.FaceIsChoices.Contains(text.Trim(), StringComparer.OrdinalIgnoreCase))
                    notes.Add($"{where}, face {face}: \"Face Is\" '{text}' is not one this build knows; shown as Object Default and kept as saved until changed.");
                Note(doc.SetProperty(index, S.FaceIsProperty(face), DisplayFaceIs(entry)));
                Note(doc.SetProperty(index, S.CoatingProperty(face), entry?.Coating ?? ""));
            }
        WriteFlags(index, s, where, notes);   // OP-88: Variable cells
        _base[row.Id] = s;

        void Note(SheetEditResult result)
        {
            if (!result.Succeeded) notes.Add($"{where}: {result.Message}");
        }
    }

    // ------------------------------------------------------------------ commands

    private SheetRowType? NewRowType()
    {
        var d = _catalogue.Find(NewObjectType);
        if (d is null) { Status = _catalogue.NotBuiltMessage(NewObjectType); return null; }
        return Document.Schema.FindRowType(d.TypeName);
    }

    private int Current => Math.Clamp(CurrentRow, 0, Math.Max(0, Document.RowCount - 1));

    /// <summary>Inserts an object of <see cref="NewObjectType"/> before the current one.</summary>
    [RelayCommand]
    private void InsertObject()
    {
        if (NewRowType() is not { } type) return;
        int at = Document.RowCount == 0 ? 0 : Current;
        if (Report(Document.InsertRows(at, 1, type), $"Object {at + 1} ({type.Name}) inserted.")) CurrentRow = at;
    }

    /// <summary>Inserts an object of <see cref="NewObjectType"/> after the current one.</summary>
    [RelayCommand]
    private void InsertObjectAfter()
    {
        if (NewRowType() is not { } type) return;
        int at = Document.RowCount == 0 ? 0 : Current + 1;
        if (Report(Document.InsertRows(at, 1, type), $"Object {at + 1} ({type.Name}) inserted.")) CurrentRow = at;
    }

    /// <summary>Deletes the current object (refused while another object refers to it).</summary>
    [RelayCommand]
    private void DeleteObject()
    {
        if (Document.RowCount == 0) { Status = "There is no object to delete."; return; }
        int at = Current;
        if (Report(Document.DeleteRows(new[] { at }), $"Object {at + 1} deleted.")) CurrentRow = Math.Min(at, Math.Max(0, Document.RowCount - 1));
    }

    [RelayCommand]
    private void MoveObjectUp() => Move(-1);

    [RelayCommand]
    private void MoveObjectDown() => Move(+1);

    private void Move(int offset)
    {
        if (Document.RowCount == 0) return;
        int at = Current;
        if (Report(Document.MoveRows(new[] { at }, offset), $"Object {at + 1} moved to {at + 1 + offset}.")) CurrentRow = at + offset;
    }

    [RelayCommand]
    private void Undo() => Status = Document.Undo() ? "Undone." : "Nothing to undo.";

    [RelayCommand]
    private void Redo() => Status = Document.Redo() ? "Redone." : "Nothing to redo.";

    /// <summary>Replicate Object on the current object (help, "Replicate Object"): one undo step.</summary>
    [RelayCommand]
    private void ReplicateObject()
    {
        var settings = new NscReplicateSettings
        {
            Object = Current + 1,
            NumberX = ReplicateNumberX, NumberY = ReplicateNumberY, NumberZ = ReplicateNumberZ,
            DeltaX = ReplicateDeltaX, DeltaY = ReplicateDeltaY, DeltaZ = ReplicateDeltaZ,
            RelativeReferences = ReplicateRelativeReferences,
            AddPickupSolves = ReplicateAddPickupSolves,
        };
        Status = Replicate(settings) ?? NscReplicate.Describe(settings);
    }

    /// <summary>Replicates as <paramref name="settings"/> say, as one undo step; returns why not, or null.</summary>
    public string? Replicate(NscReplicateSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (NscReplicate.Check(settings, Document.RowCount) is { } problem) return problem;
        var original = ReadRow(Document.Rows[settings.Object - 1]);
        if (_catalogue.Find(original.Type) is null)
            return $"Replicate Object: object {settings.Object} is of a type this build does not build, and copies would add more. {_catalogue.NotBuiltMessage(original.Type ?? "")}";
        var copies = NscReplicate.Copies(original, settings.Object, settings);
        var notes = new List<string>();
        var doc = Document;
        _quiet = true;
        try
        {
            using var group = doc.History.BeginGroup("Replicate object");
            var inserted = doc.InsertRows(settings.Object, copies.Count, doc.Rows[settings.Object - 1].Type);
            if (!inserted.Succeeded) { group.Rollback(); return inserted.Message; }
            // The copies are written without the renumbering hooks (their links are final).
            for (int k = 0; k < copies.Count; k++) WriteRow(settings.Object + k, copies[k], notes);
        }
        finally { _quiet = false; }
        AfterChange(publish: true);
        return notes.Count > 0 ? string.Join(" ", notes) : null;
    }

    /// <summary>Modify Reference Object (help, "Modify Reference Object") on objects First to
    /// Last: one undo step.</summary>
    [RelayCommand]
    private void ModifyReferenceObject() =>
        Status = ModifyReference(ModifyFirstObject, ModifyLastObject, ModifyReferTo)
                 ?? $"Objects {ModifyFirstObject} to {ModifyLastObject} now refer to object {ModifyReferTo}, in the same global place.";

    /// <summary>Re-references objects <paramref name="first"/> to <paramref name="last"/> to
    /// <paramref name="referTo"/> keeping their global placement; returns why not, or null.</summary>
    public string? ModifyReference(int first, int last, int referTo)
    {
        List<OpticsNscObjectSettings> result;
        try { result = NscObjectPlacement.ModifyReference(Rows(), first, last, referTo); }
        catch (ArgumentException ex) { return ex.Message; }
        var doc = Document;
        _quiet = true;
        try
        {
            using var group = doc.History.BeginGroup("Modify reference object");
            for (int number = first; number <= last; number++)
            {
                var r = result[number - 1];
                int i = number - 1;
                SetInt(i, S.ReferenceSlot, r.ReferenceObject ?? 0);
                SetNumber(i, S.XSlot, r.X ?? 0);
                SetNumber(i, S.YSlot, r.Y ?? 0);
                SetNumber(i, S.ZSlot, r.Z ?? 0);
                SetNumber(i, S.TiltXSlot, r.TiltX ?? 0);
                SetNumber(i, S.TiltYSlot, r.TiltY ?? 0);
                SetNumber(i, S.TiltZSlot, r.TiltZ ?? 0);
            }
        }
        finally { _quiet = false; }
        AfterChange(publish: true);
        return null;
    }

    private void SetNumber(int row, int slot, double value) => Check(Document.SetValue(row, slot + 1, CellValue.Of(value)));

    [RelayCommand]
    private void CombineObjects() => Status = NscNotBuiltTools.CombineObjects;

    [RelayCommand]
    private void CreatePolygonObject() => Status = NscNotBuiltTools.CreatePolygonObject;

    [RelayCommand]
    private void FreeformZTools() => Status = NscNotBuiltTools.FreeformZTools;

    [RelayCommand]
    private void ObjectEditor() => Status = NscNotBuiltTools.ObjectEditor;

    /// <summary>The current object for View Current Object (help, "View Current Object"): its
    /// mesh in its own frame, in lens units, or null with the reason in <paramref name="refusal"/>.</summary>
    public OpticsNscObjectViewModel? ViewCurrentObject(out string? refusal)
    {
        refusal = null;
        if (Document.RowCount == 0) { refusal = "There is no object to view."; return null; }
        int number = Current + 1;
        var row = ReadRow(Document.Rows[number - 1]);
        var d = _catalogue.Find(row.Type);
        if (d is null) { refusal = $"Object {number}: {_catalogue.NotBuiltMessage(row.Type ?? "")}"; return null; }
        try
        {
            // Lens units throughout: the shape is built as if the lens unit were the meter.
            var geometry = d.Build(row.Parameters, LensUnit.Meter);
            return new OpticsNscObjectViewModel(number, d, geometry, row.Comment);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            refusal = $"Object {number} ({d.TypeName}): {ex.Message}";
            return null;
        }
    }

    private bool Report(SheetEditResult result, string success)
    {
        Status = result.Succeeded ? success : result.Message ?? "Refused.";
        return result.Succeeded;
    }
}
