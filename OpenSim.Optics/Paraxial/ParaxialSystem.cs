namespace OpenSim.Optics.Paraxial;

/// <summary>
/// The paraxial (first-order) model of a sequential system at one wavelength: the y-nu
/// marginal and chief ray traces, the ABCD matrices, focal lengths, cardinal points, pupils,
/// magnifications, F-numbers, numerical apertures and the Lagrange invariant. Built by
/// <see cref="Create"/> from a <see cref="ParaxialLens"/> and the system data (aperture,
/// fields, wavelengths, environment); immutable once built.
/// <para>
/// <b>Conventions</b> (docs/Optics-Conventions.md §11). Each space has a signed index: its
/// index times −1 per mirror before it. Slopes u = dy/dz are in the system's frame, whose z
/// does not turn round at a mirror; the reduced slope is ν = n·u with the signed index. Then
/// one pair of equations covers lenses and mirrors: transfer y′ = y + (t/n)·ν, surface
/// ν′ = ν − y·φ with φ = c·(n′ − n). Positions are signed distances along z: object-space ones
/// from surface 1's vertex, image-space ones from the last surface's vertex (the surface before
/// the image), so after an odd number of mirrors an image-space position is negative, as a
/// thickness is.
/// </para>
/// <para>
/// One system data and one lens make one model; a configuration (OP-30) is another lens and
/// system data and so another model. Every result that needs the primary wavelength is computed
/// at the wavelength the model was built for; <see cref="ForEachWavelength"/> builds one model
/// per table row.
/// </para>
/// </summary>
public sealed class ParaxialSystem
{
    private static readonly double Epsilon = Math.BitIncrement(1.0) - 1.0;   // 2^-52

    private readonly double[] _thickness;
    private readonly ParaxialRay _unitMarginal, _unitChief, _unitMarginalSize, _unitChiefSize;
    private readonly double _tolerance;
    private readonly List<string> _notes = new();

    // ---- Inputs ----

    public ParaxialLens Lens { get; }
    public OpticalSystemData System { get; }

    /// <summary>0-based row of the wavelength table the model is built at.</summary>
    public int WavelengthIndex { get; }

    /// <summary>That wavelength in micrometers, in the system's air, as the table gives it.</summary>
    public double WavelengthMicrometers { get; }

    // ---- Media and powers ----

    /// <summary>The index of the space after each surface, relative to the system's air,
    /// always positive. Entry 0 is object space; the image surface's entry repeats the
    /// image-space index.</summary>
    public IReadOnlyList<double> Indices { get; }

    /// <summary>The same indices with the sign of the direction of travel: −1 per mirror
    /// before the space.</summary>
    public IReadOnlyList<double> SignedIndices { get; }

    /// <summary>The power of each surface, φ = c·(n′ − n) with signed indices, 1/m; zero on
    /// the object and image surfaces.</summary>
    public IReadOnlyList<double> Powers { get; }

    // ---- First-order properties ----

    /// <summary>The matrix from just before surface 1 to just after the last surface.</summary>
    public ParaxialMatrix SystemMatrix { get; }

    /// <summary>The system's power, −C of <see cref="SystemMatrix"/>, 1/m.</summary>
    public double Power { get; }

    /// <summary>True when the power is zero within its rounding error: an afocal system, with
    /// no focal length, cardinal points, image distance or image-space F-number.</summary>
    public bool IsAfocal { get; }

    /// <summary>1/Φ: positive for a converging system, lens or mirror. Null when afocal.</summary>
    public double? EffectiveFocalLength { get; }

    /// <summary>n′/Φ with the signed image-space index: the distance from the rear principal
    /// plane to the rear focal point.</summary>
    public double? ImageSpaceFocalLength { get; }

    /// <summary>−n/Φ: the distance from the front principal plane to the front focal point.</summary>
    public double? ObjectSpaceFocalLength { get; }

    /// <summary>Rear focal point, from the last surface's vertex: A·n′/Φ.</summary>
    public double? BackFocalLength { get; }

    /// <summary>Front focal point, from surface 1's vertex: −D·n/Φ.</summary>
    public double? FrontFocalLength { get; }

    /// <summary>Front principal plane, from surface 1's vertex: (1 − D)·n/Φ.</summary>
    public double? FrontPrincipalPlane { get; }

    /// <summary>Rear principal plane, from the last surface's vertex: (A − 1)·n′/Φ.</summary>
    public double? RearPrincipalPlane { get; }

    /// <summary>Front nodal point, from surface 1's vertex: (n′ − D·n)/Φ.</summary>
    public double? FrontNodalPoint { get; }

    /// <summary>Rear nodal point, from the last surface's vertex: (A·n′ − n)/Φ.</summary>
    public double? RearNodalPoint { get; }

    // ---- Pupils, rays and conjugates ----

    /// <summary>True when the entrance pupil is at infinity: telecentric object space is set,
    /// or the surfaces before the stop image it there.</summary>
    public bool EntrancePupilAtInfinity { get; }

    /// <summary>The entrance pupil, from surface 1's vertex. Null at infinity.</summary>
    public double? EntrancePupilPosition { get; }

    /// <summary>The paraxial entrance-pupil diameter. Null at infinity.</summary>
    public double? EntrancePupilDiameter { get; }

    public bool ExitPupilAtInfinity { get; }

    /// <summary>The exit pupil, from the last surface's vertex. Null at infinity.</summary>
    public double? ExitPupilPosition { get; }

    /// <summary>The paraxial exit-pupil diameter. Null at infinity.</summary>
    public double? ExitPupilDiameter { get; }

    /// <summary>The marginal ray: from the axial object point to the rim of the entrance pupil,
    /// sized by the aperture.</summary>
    public ParaxialRay MarginalRay { get; }

    /// <summary>The chief ray of the full field: the y field (in field units) of the largest
    /// normalised field, Hy = 1. Zero everywhere when every field is on the axis.</summary>
    public ParaxialRay ChiefRay { get; }

    /// <summary>The y field the chief ray belongs to, in the field type's unit (degrees or meters).</summary>
    public double FullField { get; }

    /// <summary>True when the marginal ray leaves parallel to the axis.</summary>
    public bool ImageAtInfinity { get; }

    /// <summary>The paraxial image, from the last surface's vertex. Null at infinity.</summary>
    public double? ParaxialImageDistance { get; }

    /// <summary>The chief ray's height at the paraxial image. Null at infinity.</summary>
    public double? ParaxialImageHeight { get; }

    /// <summary>The chief ray's height at the image surface.</summary>
    public double ImageSurfaceChiefHeight => ChiefRay.Heights[Lens.ImageIndex];

    /// <summary>Image height over object height at the paraxial image, ν/ν′ of the marginal
    /// ray. Zero for an object at infinity; null for an image at infinity.</summary>
    public double? LateralMagnification { get; }

    /// <summary>ū′/ū of the chief ray (image-space slope over object-space slope). Null when
    /// the chief ray is parallel to the axis in object space.</summary>
    public double? AngularMagnification { get; }

    /// <summary>1/(2·n′·|u′|) of the marginal ray at the conjugates in use. Null for an image
    /// at infinity.</summary>
    public double? WorkingFNumber { get; }

    /// <summary>|EFL| over the entrance-pupil diameter (the infinite-conjugate F-number).
    /// Null when afocal or when the entrance pupil is at infinity.</summary>
    public double? ImageSpaceFNumber { get; }

    /// <summary>n·sin(atan |u|) of the marginal ray in object space; zero for an object at infinity.</summary>
    public double ObjectNumericalAperture { get; }

    /// <summary>n′·sin(atan |u′|) of the marginal ray in image space.</summary>
    public double ImageNumericalAperture { get; }

    /// <summary>H = n·(ū·y − u·ȳ) of the marginal and full-field chief rays, signed index,
    /// meters; the same in every space.</summary>
    public double LagrangeInvariant { get; }

    /// <summary>Surface 1's vertex to the image surface's vertex, along z, signed.</summary>
    public double TotalTrack { get; }

    /// <summary>Remarks on the model: settings that disagree with what the surfaces do.</summary>
    public IReadOnlyList<string> Notes => _notes;

    private ParaxialSystem(ParaxialLens lens, OpticalSystemData system, int wavelengthIndex)
    {
        Lens = lens;
        System = system;
        WavelengthIndex = wavelengthIndex;
        WavelengthMicrometers = system.Wavelengths.Wavelengths[wavelengthIndex].Micrometers;
        var surfaces = lens.Surfaces;
        int count = surfaces.Count, last = lens.LastSurfaceIndex, image = lens.ImageIndex, stop = lens.StopIndex;
        _thickness = surfaces.Select(s => s.Thickness).ToArray();
        _tolerance = 32.0 * (count + 2) * Epsilon;

        // Indices of each space, then signed by the direction of travel.
        var n = new double[count];
        var signed = new double[count];
        double sign = 1;
        for (int j = 0; j <= last; j++)
        {
            var s = surfaces[j];
            if (j > 0 && s.IsMirror)
            {
                n[j] = n[j - 1];
                sign = -sign;
            }
            else
            {
                string where = j == 0 ? "object space" : $"the space after surface {j}";
                double index = s.Medium.IndexAt(WavelengthMicrometers, system.Environment);
                if (!double.IsFinite(index) || !(index > 0))
                    throw new InvalidOperationException(
                        $"Paraxial trace: the index of {where} is {index} at {WavelengthMicrometers} µm; it must be positive.");
                n[j] = index;
            }
            signed[j] = sign * n[j];
        }
        n[image] = n[last];
        signed[image] = signed[last];
        Indices = n;
        SignedIndices = signed;

        var power = new double[count];
        for (int j = 1; j <= last; j++) power[j] = surfaces[j].Curvature * (signed[j] - signed[j - 1]);
        Powers = power;

        double n0 = signed[0], nImage = signed[last];

        // ---- Focal properties from the system matrix ----
        var (m, mAbs) = Compose(1, last);
        SystemMatrix = m;
        Power = -m.C;
        IsAfocal = NearZero(m.C, mAbs.C);
        if (!IsAfocal)
        {
            double phi = Power;
            EffectiveFocalLength = 1 / phi;
            ImageSpaceFocalLength = nImage / phi;
            ObjectSpaceFocalLength = -n0 / phi;
            BackFocalLength = m.A * nImage / phi;
            FrontFocalLength = -m.D * n0 / phi;
            FrontPrincipalPlane = (1 - m.D) * n0 / phi;
            RearPrincipalPlane = (m.A - 1) * nImage / phi;
            FrontNodalPoint = (nImage - m.D * n0) / phi;
            RearNodalPoint = (m.A * nImage - n0) / phi;
        }

        // ---- Entrance pupil: the stop imaged into object space ----
        // A ray crossing the axis at z_E (from surface 1) with slope u is at height −z_E·u on
        // surface 1's vertex plane, with ν = n·u; at the stop y_s = A_s·(−z_E·u) + B_s·n·u,
        // which is zero for every u when z_E = B_s·n/A_s. A_s = 0 puts the pupil at infinity.
        var (ms, msAbs) = ToVertex(stop);
        bool telecentricFlag = system.Aperture.TelecentricObjectSpace;
        bool stopAtInfinity = NearZero(ms.A, msAbs.A);
        EntrancePupilAtInfinity = telecentricFlag || stopAtInfinity;
        if (stopAtInfinity && !telecentricFlag)
            _notes.Add("The surfaces before the stop image it at infinity, so the system is telecentric in object space; chief rays enter parallel to the axis.");
        double zE = EntrancePupilAtInfinity ? double.NaN : ms.B * n0 / ms.A;
        if (!EntrancePupilAtInfinity) EntrancePupilPosition = zE;

        bool objectAtInfinity = lens.ObjectAtInfinity;
        double t0 = _thickness[0];
        if (EntrancePupilAtInfinity && objectAtInfinity)
            throw new InvalidOperationException(
                "Paraxial trace: the entrance pupil and the object are both at infinity, so no ray is defined; put the object at a finite distance or turn telecentric object space off.");

        // ---- The unit rays: shapes the aperture and the fields then scale ----
        double y1, nu1;
        if (EntrancePupilAtInfinity) { y1 = t0; nu1 = n0; }          // from the axial object point, u = 1
        else if (objectAtInfinity) { y1 = 1; nu1 = 0; }               // parallel, height 1 at the pupil
        else
        {
            double d = zE + t0;                                       // object to entrance pupil
            if (d == 0)
                throw new InvalidOperationException(
                    "Paraxial trace: the object lies in the entrance-pupil plane, so the marginal ray has no direction; move the object or the stop.");
            y1 = t0 / d; nu1 = n0 / d;                                // to height 1 at the pupil
        }
        _unitMarginal = Trace(y1, nu1);
        _unitMarginalSize = TraceSize(y1, nu1);

        if (EntrancePupilAtInfinity) { y1 = 1; nu1 = 0; }             // object height 1, parallel
        else { y1 = -zE; nu1 = n0; }                                  // slope 1 through the pupil centre
        _unitChief = Trace(y1, nu1);
        _unitChiefSize = TraceSize(y1, nu1);

        // ---- Marginal ray, sized by the aperture ----
        MarginalRay = _unitMarginal.Scaled(ApertureScale());

        // ---- Chief ray of the full field ----
        FullField = system.Fields.Scale.Y;
        ChiefRay = _unitChief.Scaled(FullField == 0 ? 0 : ChiefScale(0, FullField).Y);

        // ---- Pupil sizes ----
        if (!EntrancePupilAtInfinity)
            EntrancePupilDiameter = 2 * Math.Abs(MarginalRay.Heights[1] + MarginalRay.Slopes[0] * zE);
        double chiefOut = _unitChief.Slopes[last];
        ExitPupilAtInfinity = NearZero(chiefOut, _unitChiefSize.Slopes[last]);
        if (!ExitPupilAtInfinity)
        {
            double zX = -_unitChief.Heights[last] / chiefOut;
            ExitPupilPosition = zX;
            ExitPupilDiameter = 2 * Math.Abs(MarginalRay.Heights[last] + MarginalRay.Slopes[last] * zX);
        }

        // ---- Image and conjugates ----
        double marginalOut = MarginalRay.Slopes[last];
        ImageAtInfinity = NearZero(_unitMarginal.Slopes[last], _unitMarginalSize.Slopes[last]);
        if (!ImageAtInfinity)
        {
            double zI = -MarginalRay.Heights[last] / marginalOut;
            ParaxialImageDistance = zI;
            ParaxialImageHeight = ChiefRay.Heights[last] + ChiefRay.Slopes[last] * zI;
            WorkingFNumber = 1 / (2 * n[last] * Math.Abs(marginalOut));
            LateralMagnification = objectAtInfinity ? 0 : MarginalRay.ReducedSlopes[0] / MarginalRay.ReducedSlopes[last];
        }
        if (_unitChief.Slopes[0] != 0) AngularMagnification = _unitChief.Slopes[last] / _unitChief.Slopes[0];
        if (!IsAfocal && !EntrancePupilAtInfinity)
            ImageSpaceFNumber = Math.Abs(EffectiveFocalLength!.Value) / EntrancePupilDiameter!.Value;
        ObjectNumericalAperture = objectAtInfinity ? 0 : n[0] * Math.Sin(Math.Atan(Math.Abs(MarginalRay.Slopes[0])));
        ImageNumericalAperture = n[last] * Math.Sin(Math.Atan(Math.Abs(marginalOut)));
        LagrangeInvariant = ChiefRay.ReducedSlopes[1] * MarginalRay.Heights[1] - MarginalRay.ReducedSlopes[1] * ChiefRay.Heights[1];
        double track = 0;
        for (int j = 1; j <= last; j++) track += _thickness[j];
        TotalTrack = track;

        if (system.Aperture.AfocalImageSpace && !IsAfocal)
            _notes.Add($"Afocal image space is set, but the surfaces have power (EFL {EffectiveFocalLength!.Value:g6} m); the image-space data below are those of a focal system.");
        if (IsAfocal && !system.Aperture.AfocalImageSpace)
            _notes.Add("The system has no power (afocal); set afocal image space so image-space results are given as angles.");
    }

    /// <summary>
    /// The paraxial model of <paramref name="lens"/> with <paramref name="system"/> at one row
    /// of the wavelength table (the primary when null). Throws <see cref="ArgumentException"/>
    /// listing every problem of the lens or the system data, and
    /// <see cref="InvalidOperationException"/> when the aperture, fields and conjugates cannot
    /// define the rays (each message says what to change).
    /// </summary>
    public static ParaxialSystem Create(ParaxialLens lens, OpticalSystemData system, int? wavelengthIndex = null)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        var problems = lens.Validate().Concat(system.Validate()).ToList();
        if (problems.Count > 0)
            throw new ArgumentException("Paraxial trace refused:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        int index = wavelengthIndex ?? system.Wavelengths.PrimaryIndex;
        if (index < 0 || index >= system.Wavelengths.Wavelengths.Count)
            throw new ArgumentOutOfRangeException(nameof(wavelengthIndex), index,
                $"There are {system.Wavelengths.Wavelengths.Count} wavelengths; the row must be one of them (0-based).");
        return new ParaxialSystem(lens, system, index);
    }

    /// <summary>One model per wavelength-table row, in table order.</summary>
    public static IReadOnlyList<ParaxialSystem> ForEachWavelength(ParaxialLens lens, OpticalSystemData system)
    {
        var first = Create(lens, system, 0);
        var all = new List<ParaxialSystem> { first };
        for (int i = 1; i < system.Wavelengths.Wavelengths.Count; i++) all.Add(new ParaxialSystem(lens, system, i));
        return all;
    }

    // ---- Matrices ----

    /// <summary>The matrix from just before surface <paramref name="first"/> to just after
    /// surface <paramref name="last"/> (1 ≤ first ≤ last ≤ the last surface before the image),
    /// the transfers between them included.</summary>
    public ParaxialMatrix Matrix(int first, int last)
    {
        if (first < 1 || last < first || last > Lens.LastSurfaceIndex)
            throw new ArgumentOutOfRangeException(nameof(first),
                $"The surfaces must run from 1 to {Lens.LastSurfaceIndex} in order (they are {first} to {last}).");
        return Compose(first, last).M;
    }

    private (ParaxialMatrix M, ParaxialMatrix Abs) Compose(int first, int last)
    {
        var m = ParaxialMatrix.Refraction(Powers[first]);
        var a = m.Abs();
        for (int j = first + 1; j <= last; j++)
        {
            var step = ParaxialMatrix.Refraction(Powers[j]) * ParaxialMatrix.Transfer(_thickness[j - 1] / SignedIndices[j - 1]);
            m = step * m;
            a = step.Abs() * a;
        }
        return (m, a);
    }

    /// <summary>From just before surface 1 to surface <paramref name="j"/>'s vertex plane.</summary>
    private (ParaxialMatrix M, ParaxialMatrix Abs) ToVertex(int j)
    {
        if (j == 1) return (ParaxialMatrix.Identity, ParaxialMatrix.Identity);
        var (m, a) = Compose(1, j - 1);
        var t = ParaxialMatrix.Transfer(_thickness[j - 1] / SignedIndices[j - 1]);
        return (t * m, t.Abs() * a);
    }

    /// <summary>True when <paramref name="value"/> is zero within the rounding error of a sum
    /// whose terms are bounded by <paramref name="size"/>.</summary>
    private bool NearZero(double value, double size) => Math.Abs(value) <= _tolerance * size;

    // ---- Tracing ----

    /// <summary>
    /// Traces a paraxial ray given by its height <paramref name="height"/> on surface 1's
    /// vertex plane and its reduced slope <paramref name="reducedSlope"/> (n·u) in object
    /// space. Linear in both; <see cref="MarginalRay"/> and <see cref="ChiefRay"/> are such rays.
    /// </summary>
    public ParaxialRay Trace(double height, double reducedSlope)
    {
        int count = Lens.Surfaces.Count;
        var h = new double[count];
        var v = new double[count];
        var u = new double[count];
        v[0] = reducedSlope;
        u[0] = reducedSlope / SignedIndices[0];
        h[1] = height;
        for (int j = 1; j < count; j++)
        {
            if (j > 1) h[j] = h[j - 1] + _thickness[j - 1] / SignedIndices[j - 1] * v[j - 1];
            v[j] = v[j - 1] - h[j] * Powers[j];
            u[j] = v[j] / SignedIndices[j];
        }
        bool atInfinity = Lens.ObjectAtInfinity;
        h[0] = atInfinity ? double.NaN : h[1] - _thickness[0] * u[0];
        return new ParaxialRay { Heights = h, Slopes = u, ReducedSlopes = v, ObjectAtInfinity = atInfinity };
    }

    /// <summary>The same trace on absolute values: each entry bounds the terms summed into the
    /// corresponding entry of <see cref="Trace"/>, the scale of its rounding error.</summary>
    private ParaxialRay TraceSize(double height, double reducedSlope)
    {
        int count = Lens.Surfaces.Count;
        var h = new double[count];
        var v = new double[count];
        var u = new double[count];
        v[0] = Math.Abs(reducedSlope);
        u[0] = v[0] / Math.Abs(SignedIndices[0]);
        h[1] = Math.Abs(height);
        for (int j = 1; j < count; j++)
        {
            if (j > 1) h[j] = h[j - 1] + Math.Abs(_thickness[j - 1] / SignedIndices[j - 1]) * v[j - 1];
            v[j] = v[j - 1] + h[j] * Math.Abs(Powers[j]);
            u[j] = v[j] / Math.Abs(SignedIndices[j]);
        }
        return new ParaxialRay { Heights = h, Slopes = u, ReducedSlopes = v };
    }

    /// <summary>H = ν̄·y − ν·ȳ of the marginal and chief rays at each surface (after it), from
    /// surface 1 to the image; the object plane is entry 0, NaN when the object is at infinity.
    /// Every entry equals <see cref="LagrangeInvariant"/> up to rounding.</summary>
    public IReadOnlyList<double> LagrangeInvariants()
    {
        var m = MarginalRay;
        var c = ChiefRay;
        var h = new double[Lens.Surfaces.Count];
        for (int j = 0; j < h.Length; j++)
            h[j] = c.ReducedSlopes[j] * m.Heights[j] - m.ReducedSlopes[j] * c.Heights[j];
        return h;
    }

    // ---- Aperture ----

    /// <summary>The factor that turns the unit marginal ray into the marginal ray the aperture
    /// asks for. Each type's definition is on <see cref="ApertureType"/>.</summary>
    private double ApertureScale()
    {
        var aperture = System.Aperture;
        string label = ApertureTypes.Names.Label(aperture.Type);
        int last = Lens.LastSurfaceIndex;
        double n0 = Indices[0];
        switch (aperture.Type)
        {
            case ApertureType.EntrancePupilDiameter:
                RequireFinitePupil(label);
                return aperture.Value / 2;                       // the unit ray reaches height 1 in the pupil
            case ApertureType.ImageSpaceFNumber:
                RequireFinitePupil(label);
                if (IsAfocal)
                    throw new InvalidOperationException($"Paraxial trace: the {label} needs a focal length, and this system is afocal; use the entrance pupil diameter.");
                return Math.Abs(EffectiveFocalLength!.Value) / (2 * aperture.Value);
            case ApertureType.ObjectSpaceNumericalAperture:
            case ApertureType.ObjectConeAngle:
            {
                if (Lens.ObjectAtInfinity)
                    throw new InvalidOperationException($"Paraxial trace: the {label} needs an object at a finite distance; with the object at infinity use the entrance pupil diameter or the image space F/#.");
                double slope;
                if (aperture.Type == ApertureType.ObjectConeAngle)
                    slope = Math.Tan(aperture.Value * (Math.PI / 180));
                else
                {
                    if (!(aperture.Value < n0))
                        throw new InvalidOperationException($"Paraxial trace: the object space NA ({aperture.Value}) must be below the object-space index ({n0:g6}).");
                    slope = Math.Tan(Math.Asin(aperture.Value / n0));
                }
                return slope / Math.Abs(_unitMarginal.Slopes[0]);
            }
            case ApertureType.FloatByStopSize:
            {
                int stop = Lens.StopIndex;
                if (Lens.Surfaces[stop].SemiDiameter is not double a || !(a > 0))
                    throw new InvalidOperationException($"Paraxial trace: float by stop size needs a positive semi-diameter on the stop (surface {stop}).");
                double ys = _unitMarginal.Heights[stop];
                if (NearZero(ys, _unitMarginalSize.Heights[stop]))
                    throw new InvalidOperationException($"Paraxial trace: the stop (surface {stop}) is at an image of the object, so its size cannot set the beam; use another aperture type.");
                return a / Math.Abs(ys);
            }
            case ApertureType.ParaxialWorkingFNumber:
            {
                double u = _unitMarginal.Slopes[last];
                if (NearZero(u, _unitMarginalSize.Slopes[last]))
                    throw new InvalidOperationException($"Paraxial trace: the {label} needs an image at a finite distance, and the marginal ray leaves parallel to the axis; use the entrance pupil diameter.");
                return 1 / (2 * Indices[last] * aperture.Value * Math.Abs(u));
            }
            default:
                throw new InvalidOperationException($"Paraxial trace: the aperture type {aperture.Type} is not known to the paraxial engine.");
        }
    }

    private void RequireFinitePupil(string label)
    {
        if (EntrancePupilAtInfinity)
            throw new InvalidOperationException(
                $"Paraxial trace: the entrance pupil is at infinity (telecentric object space), so the {label} is undefined; use the object space NA, the object cone angle, the paraxial working F/# or float by stop size.");
    }

    // ---- Fields ----

    /// <summary>
    /// The paraxial chief ray of a field point, in the xz and yz planes (a rotationally
    /// symmetric system traces the two independently), for the system's field type:
    /// <list type="bullet">
    /// <item>Angle: object-space slopes tan θx, tan θy, through the entrance-pupil centre.</item>
    /// <item>Theodolite angle (azimuth x, elevation y): the direction (cos el·sin az, sin el,
    /// cos el·cos az), so slopes tan az and tan el / cos az.</item>
    /// <item>Object height: from the object point through the entrance-pupil centre (parallel
    /// to the axis when the pupil is at infinity).</item>
    /// <item>Paraxial image height: the chief ray that meets the image surface at that height.</item>
    /// <item>Real image height: needs the real ray trace and ray aiming (OP-20, OP-22); refused.</item>
    /// </list>
    /// </summary>
    public (ParaxialRay X, ParaxialRay Y) TraceChiefRay(FieldPoint field)
    {
        ArgumentNullException.ThrowIfNull(field);
        var (kx, ky) = ChiefScale(field.X, field.Y);
        return (_unitChief.Scaled(kx), _unitChief.Scaled(ky));
    }

    private (double X, double Y) ChiefScale(double x, double y)
    {
        var type = System.Fields.Type;
        string label = FieldTypes.Names.Label(type);
        switch (type)
        {
            case FieldType.Angle:
            case FieldType.TheodoliteAngle:
            {
                if (EntrancePupilAtInfinity)
                    throw new InvalidOperationException($"Paraxial trace: with the entrance pupil at infinity every chief ray is parallel to the axis in object space, so a field type of {label} has no meaning; use object height or paraxial image height.");
                var (sx, cx) = OpticsConventions.SinCosDegrees(x);
                var (sy, cy) = OpticsConventions.SinCosDegrees(y);
                return type == FieldType.Angle ? (sx / cx, sy / cy) : (sx / cx, sy / cy / cx);
            }
            case FieldType.ObjectHeight:
            {
                if (Lens.ObjectAtInfinity)
                    throw new InvalidOperationException("Paraxial trace: an object-height field needs an object at a finite distance; with the object at infinity use angle fields.");
                if (EntrancePupilAtInfinity) return (x, y);
                double d = EntrancePupilPosition!.Value + _thickness[0];
                return (-x / d, -y / d);
            }
            case FieldType.ParaxialImageHeight:
            {
                int image = Lens.ImageIndex;
                double h = _unitChief.Heights[image];
                if (NearZero(h, _unitChiefSize.Heights[image]))
                    throw new InvalidOperationException("Paraxial trace: every chief ray crosses the axis at the image surface (it is in the exit pupil), so a paraxial image height cannot choose one; move the image surface or use another field type.");
                return (x / h, y / h);
            }
            case FieldType.RealImageHeight:
                throw new NotSupportedException("Paraxial trace: a real-image-height field needs the real ray trace and ray aiming (OP-20, OP-22), which are not built; use paraxial image height for first-order data.");
            default:
                throw new InvalidOperationException($"Paraxial trace: the field type {type} is not known to the paraxial engine.");
        }
    }
}
