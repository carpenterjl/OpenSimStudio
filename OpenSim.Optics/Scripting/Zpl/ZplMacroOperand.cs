using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Scripting.Zpl;

// OP-99: the ZPLM merit operand (docs/Optics-Conventions.md §56, "ZPLM").

/// <summary>
/// The folder ZPLM and macro solves read macros from. It defaults to
/// %AppData%\OpenSimStudio\Macros; the macro window and the command line (--macro-folder) set it.
/// It is not saved with a project.
/// </summary>
public static class ZplMacroFolder
{
    /// <summary>%AppData%\OpenSimStudio\Macros.</summary>
    public static string Default { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenSimStudio", "Macros");

    private static string _current = Default;

    /// <summary>The folder in use.</summary>
    public static string Current
    {
        get => _current;
        set => _current = string.IsNullOrWhiteSpace(value) ? Default : value;
    }
}

/// <summary>
/// ZPLM, "used for optimizing numerical results computed in ZPL macros" (help, Optimization
/// Operands Alphabetically; Optimizing with ZPL Macros): Int1 is Mac#, the macro ZPLnn.ZPL ("The
/// macro name must always use a two digit representation of the macro number") in the macro
/// folder (<see cref="ZplMacroFolder"/>); Int2 is the data field, 0 to 50, whose OPTRETURN value
/// is the operand's value; Data1 to Data6 are passed to the macro as PVHX, PVHY, PVPX, PVPY,
/// PVEX and PVEY.
/// <para>
/// The macro runs on a copy of the configuration's solved lens in lens units
/// (<see cref="SavedSurfaces.FromLens"/>), as "the merit function is always evaluated using a
/// temporary copy of the lens" (Changes Made to the Lens from within the ZPLM Macro); its changes
/// are discarded. One difference from the help: it says a non-zero data field "is not executed,
/// but any previous value stored from an earlier call to the macro is used"; here every ZPLM row
/// runs its macro (with its own Data1–Data6), because merit rows are evaluated in parallel and a
/// row must not depend on another row having run first. A macro that reads no PV function
/// returns the same values either way; it only costs time.
/// </para>
/// </summary>
public static class ZplMacroOperand
{
    public static MeritOperandDefinition Definition() => new()
    {
        Code = "ZPLM",
        Name = "ZPL macro value",
        Category = "User defined operands",
        Source = "Optimizing with ZPL Macros",
        Description = "The value macro ZPLnn.ZPL (nn = Mac#) returns with OPTRETURN in data field Data (0 to 50); Data1 to Data6 are passed to the macro (PVHX … PVEY).",
        Int1 = "Mac#",
        Int2 = "Data",
        Data = new[] { "PVHX", "PVHY", "PVPX", "PVPY", "PVEX", "PVEY" },
        Compute = Evaluate,
    };

    /// <summary>The macro file of macro number <paramref name="number"/> (0 to 99).</summary>
    public static string MacroFile(int number) =>
        "ZPL" + number.ToString("00", CultureInfo.InvariantCulture) + ZplMacro.Extension.ToUpperInvariant();

    private static double Evaluate(MeritRowContext c)
    {
        int mac = c.Row.Int1, field = c.Row.Int2;
        if (mac < 0 || mac > 99) throw new MeritOperandException($"Mac# must be from 0 to 99 (macro ZPLnn.ZPL), not {mac}.");
        if (field < 0 || field > 50) throw new MeritOperandException($"the data field must be from 0 to 50, not {field}.");
        string folder = ZplMacroFolder.Current;
        string path = Path.Combine(folder, MacroFile(mac));
        if (!File.Exists(path)) throw new MeritOperandException($"the macro {MacroFile(mac)} is not in the macro folder '{folder}'.");
        OpticsSettings copy;
        IReadOnlyList<OpenSim.Optics.Materials.OpticalCatalog> catalogs;
        try { (copy, catalogs) = ZplLensCopy.Of(c.Lens, c.System, c.Catalogs); }
        catch (ArgumentException e) { throw new MeritOperandException($"the lens cannot be handed to the macro: {e.Message}"); }
        ZplRunResult result;
        try
        {
            result = ZplMacro.RunFile(path, new ZplRunOptions
            {
                System = copy,
                Catalogs = catalogs,
                MacroFolder = folder,
                PassValues = c.Row.Data.ToArray(),
            });
        }
        catch (ZplException e) { throw new MeritOperandException($"the macro failed: {e.Message}"); }
        return result.OptReturn[field]
            ?? throw new MeritOperandException($"{MacroFile(mac)} returned nothing in data field {field} (OPTRETURN {field}, value).");
    }
}
