using System.Globalization;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Sequential.Birefringent;

/// <summary>
/// One space of a lens filled by a uniaxial crystal: from its Birefringent In surface to the
/// next Birefringent In or Birefringent Out, at the trace's wavelength.
/// </summary>
public sealed record CrystalSpace
{
    /// <summary>The Birefringent In surface that begins the crystal.</summary>
    public required int InSurface { get; init; }

    /// <summary>The mode of that surface (0–3).</summary>
    public required int Mode { get; init; }

    /// <summary>The mode traced: ordinary for modes 0 and 2, extraordinary for 1 and 3.</summary>
    public CrystalMode TracedMode => Mode is 0 or 2 ? CrystalMode.Ordinary : CrystalMode.Extraordinary;

    /// <summary>True for modes 2 and 3 (the other mode's part of the field is kept).</summary>
    public bool KeepsOtherMode => Mode >= 2;

    /// <summary>The crystal with its axis in the global frame (surface 1's frame).</summary>
    public required UniaxialCrystal Global { get; init; }
}

/// <summary>
/// The birefringent crystals of a lens at one wavelength (OP-53, docs/Optics-Conventions.md
/// §62), built by <see cref="SequentialTrace"/> only for a lens that has a Birefringent In or Out
/// surface (otherwise the trace holds null and runs exactly as before). It checks the sequence
/// and gives, for each surface, the crystal on either side in that surface's local frame, and
/// refracts a ray there (<see cref="TryRefract"/>).
/// <para>
/// <b>Rules</b> (the help describes the pair "In" and "Out"; these make it precise): a crystal
/// begins at a Birefringent In and ends at the next Birefringent In (crystal to crystal, as in
/// the help's Wollaston prism) or Birefringent Out; between them only coordinate breaks and
/// ignored surfaces may stand; a crystal must end before the image; a Birefringent Out needs a
/// crystal before it; neither type may be a mirror, ignored, carry composite add-ons, or be the
/// object or image surface. The ordinary index is the In surface's medium, the extraordinary
/// index its <see cref="BirefringentInShape.Extraordinary"/>; the axis (parameters 2–4) is in the
/// In surface's local frame and must not be zero.
/// </para>
/// </summary>
public sealed class BirefringentSpaces
{
    private readonly CrystalSpace?[] _space;
    private readonly UniaxialCrystal?[] _before, _after;
    private readonly bool[] _interface;

    private BirefringentSpaces(CrystalSpace?[] space, UniaxialCrystal?[] before, UniaxialCrystal?[] after, bool[] isInterface)
    {
        _space = space;
        _before = before;
        _after = after;
        _interface = isInterface;
    }

    /// <summary>True when the lens has a Birefringent In or Out surface.</summary>
    public static bool Applies(SequentialLens lens) =>
        lens.Surfaces.Any(s => s?.Shape is BirefringentShape);

    /// <summary>The crystal filling the space after surface <paramref name="j"/>, or null.</summary>
    public CrystalSpace? Space(int j) => j >= 0 && j < _space.Length ? _space[j] : null;

    /// <summary>True at a Birefringent In or Out surface.</summary>
    public bool IsInterface(int j) => _interface[j];

    /// <summary>The crystal before surface <paramref name="j"/>, axis in surface j's local frame.</summary>
    public UniaxialCrystal? Before(int j) => _before[j];

    /// <summary>The crystal after surface <paramref name="j"/>, axis in surface j's local frame.</summary>
    public UniaxialCrystal? After(int j) => _after[j];

    /// <summary>
    /// Builds the crystals of <paramref name="lens"/>, or returns null when it has no
    /// birefringent surface. <paramref name="ordinary"/> is the trace's index of each space (the
    /// ordinary index in a crystal). Throws <see cref="ArgumentException"/> listing every broken
    /// rule, and <see cref="InvalidOperationException"/> when an extraordinary index cannot be
    /// evaluated at the wavelength.
    /// </summary>
    internal static BirefringentSpaces? Create(SequentialLens lens, IReadOnlyList<double> ordinary, double airMicrometers,
        OpticsEnvironment environment, IReadOnlyList<RigidTransform> global)
    {
        if (!Applies(lens)) return null;
        var surfaces = lens.Surfaces;
        int count = surfaces.Count, image = lens.ImageIndex;
        var problems = new List<string>();
        var space = new CrystalSpace?[count];
        var before = new UniaxialCrystal?[count];
        var after = new UniaxialCrystal?[count];
        var isInterface = new bool[count];
        CrystalSpace? current = null;
        string Where(int j) => j == 0 ? "Object surface" : j == image ? "Image surface" : $"Surface {j}";

        for (int j = 0; j < count; j++)
        {
            var s = surfaces[j];
            string where = Where(j);
            if (current is not null) before[j] = Local(current.Global, global[j]);
            if (s.Shape is BirefringentShape shape)
            {
                isInterface[j] = true;
                if (j == 0 || j == image) problems.Add($"{where}: a {shape.TypeName} surface cannot be the object or image surface.");
                if (s.IsMirror) problems.Add($"{where}: a {shape.TypeName} surface cannot be a mirror (reflection in a crystal is not built).");
                if (s.IsIgnored) problems.Add($"{where}: a {shape.TypeName} surface cannot be ignored.");
                if (s.AddOns is { Count: > 0 }) problems.Add($"{where}: a {shape.TypeName} surface cannot have composite add-ons.");
                if (shape is BirefringentOutShape)
                {
                    if (current is null) problems.Add($"{where}: a {BirefringentOutShape.Name} needs a crystal before it (a {BirefringentInShape.Name} earlier, with no Out between).");
                    current = null;
                }
                else if (shape is BirefringentInShape bin && j > 0 && j < image)
                {
                    current = null;
                    double ne = double.NaN;
                    if (bin.Extraordinary is not { } extraordinary)
                        problems.Add($"{where}: the {BirefringentInShape.Name} has no extraordinary index; give the surface a glass whose catalog also holds the glass of the same name with \"-E\" appended (OpticStudio help, \"Birefringent In and Birefringent Out\").");
                    else
                    {
                        try { ne = extraordinary.IndexAt(airMicrometers, environment); }
                        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                        {
                            throw new InvalidOperationException($"Real ray trace: the extraordinary index of surface {j} ({extraordinary.Describe()}) at {airMicrometers.ToString(CultureInfo.InvariantCulture)} µm: {e.Message}", e);
                        }
                        if (!double.IsFinite(ne) || !(ne > 0))
                            throw new InvalidOperationException($"Real ray trace: the extraordinary index of surface {j} ({extraordinary.Describe()}) is {ne} at {airMicrometers.ToString(CultureInfo.InvariantCulture)} µm; it must be positive.");
                    }
                    if (!(bin.Axis.Length > 0) || !double.IsFinite(bin.Axis.Length))
                        problems.Add($"{where}: the {BirefringentInShape.Name}'s crystal axis (parameters 2–4) is {bin.Axis}; give its direction cosines.");
                    else if (double.IsFinite(ne) && ne > 0)
                    {
                        var local = new UniaxialCrystal(ordinary[j], ne, bin.Axis);
                        current = new CrystalSpace
                        {
                            InSurface = j, Mode = bin.Mode,
                            Global = local.WithAxis(global[j].ApplyDirection(local.Axis)),
                        };
                    }
                }
            }
            else if (current is not null && !(s.IsCoordinateBreak || s.IsIgnored))
            {
                if (j == image)
                    problems.Add($"{where}: the crystal that begins at surface {current.InSurface} reaches the image; end it with a {BirefringentOutShape.Name}.");
                else
                    problems.Add($"{where} lies inside the crystal that begins at surface {current.InSurface}; only another {BirefringentInShape.Name}, a coordinate break or an ignored surface may come before the {BirefringentOutShape.Name}.");
                current = null;
            }
            if (current is not null && j < image)
            {
                space[j] = current;
                after[j] = Local(current.Global, global[j]);
            }
        }
        if (problems.Count > 0)
            throw new ArgumentException("Real ray trace refused (birefringent surfaces):" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        return new BirefringentSpaces(space, before, after, isInterface);
    }

    private static UniaxialCrystal Local(UniaxialCrystal globalCrystal, RigidTransform placement) =>
        globalCrystal.WithAxis(placement.InverseApplyDirection(globalCrystal.Axis));

    /// <summary>
    /// Refracts a ray at birefringent surface <paramref name="j"/> (local frame): the wave
    /// vector before is <paramref name="waveBefore"/> in a crystal, n·d̂ in an isotropic space;
    /// the mode after is the crystal's traced mode, or the isotropic wave after an Out. Returns
    /// the unit ray direction after and the wave vector after (null in an isotropic space);
    /// false when that mode is totally internally reflected.
    /// </summary>
    internal bool TryRefract(int j, Vector3D direction, Vector3D? waveBefore, double indexBefore, double indexAfter,
        Vector3D normal, out Vector3D ray, out Vector3D? waveAfter)
    {
        var w = _before[j] is not null
            ? waveBefore ?? throw new InvalidOperationException($"Real ray trace: no wave vector inside the crystal before surface {j}.")
            : direction * indexBefore;
        var afterSide = _after[j] is { } c ? OpticalSide.Of(c) : OpticalSide.Isotropic(indexAfter);
        var mode = _space[j]?.TracedMode ?? CrystalMode.Ordinary;
        if (!UniaxialOptics.TryRefract(w, direction, normal, afterSide, mode, out ray, out var wave))
        {
            waveAfter = waveBefore;
            return false;
        }
        waveAfter = afterSide.IsCrystal ? wave : null;
        return true;
    }
}
