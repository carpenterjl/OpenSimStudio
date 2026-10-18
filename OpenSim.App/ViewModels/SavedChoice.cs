namespace OpenSim.App.ViewModels;

/// <summary>
/// A panel choice read back from a project file (D23): the saved label when the panel still
/// offers it; otherwise the panel keeps what it has and a note names the label — a renamed
/// option or a hand-edited file must not silently become another choice.
/// </summary>
internal static class SavedChoice
{
    /// <param name="what">The choice, for the note ("Antenna source").</param>
    /// <param name="hint">What to do about a missing label, appended to the note.</param>
    public static string? Pick(string? saved, IEnumerable<string> choices, string? current, string what,
        ICollection<string> notes, string? hint = null)
    {
        if (saved is null) return current;
        if (choices.Contains(saved)) return saved;
        notes.Add($"{what} '{saved}' is not one of the panel's choices; "
                  + (string.IsNullOrEmpty(current) ? "left unset." : $"kept '{current}'.")
                  + (hint is null ? "" : " " + hint));
        return current;
    }
}
