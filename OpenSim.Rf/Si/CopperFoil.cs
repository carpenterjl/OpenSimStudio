using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenSim.Rf.Si;

/// <summary>Which roughness statistic a foil's data sheet publishes for its treated side.</summary>
public enum RoughnessStatistic
{
    /// <summary>Rz by JIS B 0601: the ten-point mean height, the number the Cannonball model
    /// was built on.</summary>
    TenPointMean,

    /// <summary>Rz by ISO 4287: the mean of the maximum peak-to-valley height over five
    /// sampling lengths. Circuit Foil's sheets print it beside the JIS value, about 25 % higher.</summary>
    MaximumHeight,

    /// <summary>Rz with the standard not stated on the sheet (IPC-TM-650 2.2.17 leaves the
    /// choice to the vendor).</summary>
    UnstatedRz,

    /// <summary>An RMS roughness: Rq by profilometer or Sq by interferometry. Rogers publishes
    /// Sq, and says it correlates with conductor loss better than Rz.</summary>
    RootMeanSquare
}

/// <summary>One foil weight's roughness as its data sheet gives it.</summary>
public sealed record FoilGauge
{
    /// <summary>Nominal foil thickness [µm] (9 = ¼ oz, 12 = ⅜ oz, 18 = ½ oz, 35 = 1 oz, 70 = 2 oz…).</summary>
    public required double ThicknessMicrons { get; init; }

    /// <summary>The treated side's roughness [µm]: the data sheet's limit ("≤ 2.5"), the top of
    /// its range, or its typical value when <see cref="IsTypical"/>.</summary>
    public required double RoughnessMicrons { get; init; }

    /// <summary>The bottom of the range when the sheet gives one ("3.3 – 5.8"); null otherwise.</summary>
    public double? RoughnessLowMicrons { get; init; }

    /// <summary>True when the sheet gives a typical value rather than a limit.</summary>
    public bool IsTypical { get; init; }

    public string Describe(RoughnessStatistic statistic)
    {
        string value = RoughnessLowMicrons is { } low ? $"{low:g3}–{RoughnessMicrons:g3} µm"
            : IsTypical ? $"{RoughnessMicrons:g3} µm typical"
            : $"≤ {RoughnessMicrons:g3} µm";
        string what = statistic switch
        {
            RoughnessStatistic.TenPointMean => "Rz (JIS)",
            RoughnessStatistic.MaximumHeight => "Rz (ISO)",
            RoughnessStatistic.RootMeanSquare => "RMS",
            _ => "Rz"
        };
        return $"{ThicknessMicrons:g3} µm: {what} {value}";
    }
}

/// <summary>
/// A copper foil by name, with the treated-side roughness its vendor publishes per foil weight
/// and the document it was read from. <see cref="Roughness"/> turns the sheet's number into
/// Huray parameters by the Cannonball stack (<see cref="SurfaceRoughness.Cannonball"/>) — the
/// sheets publish no Huray parameters themselves, and nothing here is fitted to measurements.
/// A data sheet's "≤" limit is used as it stands: the roughness a buyer is guaranteed not to
/// exceed, so the loss it gives is the upper edge of the foil's band; a typical value is the
/// typical loss.
/// </summary>
public sealed record CopperFoil
{
    public required string Vendor { get; init; }

    public required string Name { get; init; }

    /// <summary>The trade class: "standard", "RTF", "VLP", "HVLP", "rolled"…</summary>
    public string Family { get; init; } = "";

    /// <summary>Which face carries the bonding treatment the roughness is measured on: the matte
    /// (electrolyte) side of a standard foil, the drum (shiny) side of a reverse-treated one.</summary>
    public string TreatedSide { get; init; } = "matte side";

    public RoughnessStatistic Statistic { get; init; } = RoughnessStatistic.TenPointMean;

    /// <summary>Per foil weight, thinnest first.</summary>
    public required IReadOnlyList<FoilGauge> Gauges { get; init; }

    /// <summary>The document the numbers were read from: title, date or revision, address.</summary>
    public required string Source { get; init; }

    /// <summary>Anything a user must know before trusting the entry.</summary>
    public string? Note { get; init; }

    [JsonIgnore]
    public string FullName => $"{Vendor} {Name}";

    /// <summary>The gauge nearest the copper thickness (in the logarithm of thickness, so 25 µm
    /// is nearer 18 than 35); the only gauge when there is one.</summary>
    public FoilGauge GaugeFor(double thicknessMeters)
    {
        if (Gauges.Count == 0) throw new InvalidOperationException($"{FullName} lists no foil weights.");
        if (!(thicknessMeters > 0)) return Gauges[0];
        double target = Math.Log(thicknessMeters * 1e6);
        FoilGauge best = Gauges[0];
        double bestDistance = double.PositiveInfinity;
        foreach (var gauge in Gauges)
        {
            double distance = Math.Abs(Math.Log(gauge.ThicknessMicrons) - target);
            if (distance < bestDistance - 1e-12)
            {
                best = gauge;
                bestDistance = distance;
            }
        }
        return best;
    }

    /// <summary>Huray parameters by the Cannonball stack from the gauge nearest the copper
    /// thickness: the ten-point Rz route for an Rz sheet, the RMS route for Rq/Sq.</summary>
    public SurfaceRoughness Roughness(double thicknessMeters) => Roughness(GaugeFor(thicknessMeters));

    public SurfaceRoughness Roughness(FoilGauge gauge)
    {
        double meters = gauge.RoughnessMicrons * 1e-6;
        string origin = $"Cannonball from {FullName} {gauge.Describe(Statistic)}";
        return Statistic == RoughnessStatistic.RootMeanSquare
            ? SurfaceRoughness.CannonballFromRms(meters, origin)
            : SurfaceRoughness.Cannonball(meters, origin);
    }

    public IEnumerable<string> Describe()
    {
        yield return $"{FullName}" + (Family.Length > 0 ? $" ({Family})" : "") + $", treated {TreatedSide}";
        foreach (var gauge in Gauges)
        {
            var rough = Roughness(gauge);
            yield return $"  {gauge.Describe(Statistic)} → sphere radius {rough.SphereRadiusMeters * 1e6:g3} µm, "
                       + $"surface ratio {rough.SurfaceRatio:g3}";
        }
        yield return $"  Source: {Source}";
        if (Note is { } note) yield return $"  Note: {note}";
    }
}

/// <summary>
/// The foils the panels and the CLI offer by name: the built-in catalog (every entry read from
/// a vendor document named in its <see cref="CopperFoil.Source"/>) plus whatever a user's
/// foils file adds or overrides. The file is a JSON array of <see cref="CopperFoil"/> objects
/// (camel-case property names, the statistic by name); an entry with the vendor and name of a
/// built-in foil replaces it.
/// </summary>
public sealed class FoilCatalog
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly List<CopperFoil> _foils;

    /// <summary>The built-in catalog alone.</summary>
    public FoilCatalog() : this(BuiltIn) { }

    public FoilCatalog(IEnumerable<CopperFoil> foils)
    {
        _foils = new List<CopperFoil>();
        foreach (var foil in foils) Add(foil);
    }

    public IReadOnlyList<CopperFoil> Foils => _foils;

    /// <summary>Every foil's full name, in catalog order.</summary>
    public IEnumerable<string> Names => _foils.Select(f => f.FullName);

    private void Add(CopperFoil foil)
    {
        int existing = _foils.FindIndex(f => Same(f, foil));
        if (existing >= 0) _foils[existing] = foil;
        else _foils.Add(foil);
    }

    private static bool Same(CopperFoil a, CopperFoil b) =>
        string.Equals(a.Vendor, b.Vendor, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>A catalog with these foils added, those sharing a vendor and name replacing the
    /// existing entries.</summary>
    public FoilCatalog Merge(IEnumerable<CopperFoil> foils) => new(_foils.Concat(foils));

    /// <summary>A catalog with the foils of a user's JSON file merged in.</summary>
    public FoilCatalog WithFile(string path) => Merge(ReadJson(path));

    /// <summary>The foil whose full name ("Circuit Foil BF-TZA") or bare name ("BF-TZA") matches,
    /// case-insensitively; null when none does, or when the bare name is ambiguous.</summary>
    public CopperFoil? Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string wanted = name.Trim();
        var full = _foils.FirstOrDefault(f => string.Equals(f.FullName, wanted, StringComparison.OrdinalIgnoreCase));
        if (full is not null) return full;
        var bare = _foils.Where(f => string.Equals(f.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        return bare.Count == 1 ? bare[0] : null;
    }

    /// <summary>The foil by name, or an explanation of what would have matched.</summary>
    public CopperFoil Require(string name)
    {
        if (Find(name) is { } foil) return foil;
        string wanted = name.Trim();
        var near = _foils.Where(f => f.FullName.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                                     || wanted.Contains(f.Name, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.FullName).ToList();
        throw new KeyNotFoundException(near.Count > 0
            ? $"No foil is named '{name}'. Did you mean: {string.Join(", ", near)}?"
            : $"No foil is named '{name}'. The catalog has: {string.Join(", ", Names)}.");
    }

    public static IReadOnlyList<CopperFoil> ReadJson(string path)
    {
        var foils = JsonSerializer.Deserialize<List<CopperFoil>>(File.ReadAllText(path), JsonOptions)
                    ?? throw new InvalidDataException($"'{path}' is not a foils file (a JSON array of foils).");
        foreach (var foil in foils)
        {
            if (string.IsNullOrWhiteSpace(foil.Vendor) || string.IsNullOrWhiteSpace(foil.Name))
                throw new InvalidDataException($"'{path}': every foil needs a vendor and a name.");
            if (foil.Gauges.Count == 0)
                throw new InvalidDataException($"'{path}': {foil.FullName} lists no gauges.");
            foreach (var gauge in foil.Gauges)
                if (!(gauge.ThicknessMicrons > 0) || !(gauge.RoughnessMicrons > 0))
                    throw new InvalidDataException($"'{path}': {foil.FullName} has a gauge without a positive thickness and roughness.");
        }
        return foils;
    }

    public static void WriteJson(string path, IEnumerable<CopperFoil> foils) =>
        File.WriteAllText(path, JsonSerializer.Serialize(foils.ToList(), JsonOptions));

    // ------------------------------------------------------------------ the built-in catalog

    private const string CircuitFoilCatalogue =
        "Circuit Foil, Product Catalogue, March 2024 (last revision November 2021), " +
        "www.circuitfoil.com/wp-content/uploads/2024/03/CFL-PRODUCT-CATALOGUE-Mar24-Website.pdf";

    private const string Furukawa =
        "Furukawa Electric, copper foil product pages www.furukawaelectric.com/foil/en/product/print/ (read 2026-10-16; undated)";

    private const string Isola =
        "Isola, I-Speed data sheet Rev. F (2021-06-03) and Tachyon 100G product page (read 2026-10-16), isola-group.com";

    private const string Rogers =
        "Rogers Corporation, \"Copper Foils for High Frequency Materials\", publication 92-243, Table 1: " +
        "typical treated-side Sq by white-light interferometry (read 2026-10-16)";

    private static FoilGauge Max(double thicknessUm, double rzUm) =>
        new() { ThicknessMicrons = thicknessUm, RoughnessMicrons = rzUm };

    private static FoilGauge Range(double thicknessUm, double lowUm, double highUm) =>
        new() { ThicknessMicrons = thicknessUm, RoughnessMicrons = highUm, RoughnessLowMicrons = lowUm };

    private static FoilGauge Typical(double thicknessUm, double rzUm) =>
        new() { ThicknessMicrons = thicknessUm, RoughnessMicrons = rzUm, IsTypical = true };

    private static FoilGauge[] AllGauges(double rzUm, params double[] thicknessesUm) =>
        thicknessesUm.Select(t => Max(t, rzUm)).ToArray();

    /// <summary>Every entry read from the document in its Source; JIS Rz where Circuit Foil
    /// prints both standards (the Cannonball model's own input), the vendor's unstated Rz for
    /// Furukawa and Chang Chun, Isola's JIS class limits, Rogers' RMS.</summary>
    public static IReadOnlyList<CopperFoil> BuiltIn { get; } = new[]
    {
        // ---- Circuit Foil (Luxembourg), matte-side treated unless marked RTF ------------------
        new CopperFoil
        {
            Vendor = "Circuit Foil", Name = "BFL-NF", Family = "HVLP ('no profile')",
            Gauges = new[] { Max(12, 0.70), Max(18, 0.65), Max(35, 0.60) },
            Source = CircuitFoilCatalogue + ", BFL-NF-HT / BFL-NF-Z page",
            Note = "ISO 4287 Rz ≤ 1.0 / 0.9 / 0.8 µm on the same sheet; Sdr about 0.1 %."
        },
        new CopperFoil
        {
            Vendor = "Circuit Foil", Name = "BFL-NN", Family = "HVLP ('extremely flat')",
            Gauges = new[] { Max(9, 0.85), Max(12, 0.70), Max(18, 0.65), Max(35, 0.60) },
            Source = CircuitFoilCatalogue + ", BFL-NN / BFL-NN-HT / BFL-NN-Z page",
            Note = "ISO 4287 Rz ≤ 1.1 / 1.0 / 0.9 / 0.8 µm on the same sheet."
        },
        new CopperFoil
        {
            Vendor = "Circuit Foil", Name = "BF-NN", Family = "HVLP ('super flat')",
            Gauges = new[] { Max(9, 1.4), Max(12, 1.2), Max(18, 1.1), Max(35, 1.0) },
            Source = CircuitFoilCatalogue + ", BF-NN / BF-NN-HT page (BF-NN-Z identical)",
            Note = "ISO 4287 Rz ≤ 1.8 / 1.6 / 1.4 / 1.3 µm on the same sheet."
        },
        new CopperFoil
        {
            Vendor = "Circuit Foil", Name = "BF-ANP", Family = "HVLP ('almost no profile')",
            Gauges = AllGauges(1.6, 9, 12, 18, 35, 70),
            Source = CircuitFoilCatalogue + ", BF-ANP page",
            Note = "The sheet prints four JIS limits (≤ 1.6 / 1.4 / 1.3 / 1.2 µm) over five gauges and the " +
                   "columns could not be assigned from its text; the largest is used for every gauge."
        },
        new CopperFoil
        {
            Vendor = "Circuit Foil", Name = "BF-TZA", Family = "HVLP ('ultra flat')",
            Gauges = AllGauges(2.5, 9, 12, 18, 35, 70),
            Source = "Circuit Foil, BF-TZA data sheet, www.circuitfoil.com/wp-content/uploads/2021/06/BF-TZA.pdf (2021), " +
                     "and the same page of the " + CircuitFoilCatalogue,
            Note = "The sheet prints two JIS limits (≤ 2.5 and ≤ 2.0 µm; ISO ≤ 3.1 and ≤ 2.5) over five gauges and " +
                   "the split could not be read from its text; the larger is used for every gauge."
        },
        new CopperFoil
        {
            Vendor = "Circuit Foil", Name = "BF-HFI-LP2", Family = "HVLP ('ultra flat')",
            Gauges = AllGauges(2.5, 12, 18, 35),
            Source = CircuitFoilCatalogue + ", BF-HFI-LP2 page",
            Note = "ISO 4287 Rz ≤ 3.1 µm."
        },
        new CopperFoil
        {
            Vendor = "Circuit Foil", Name = "BF-HFA", Family = "HVLP, for fluoropolymer substrates",
            Gauges = AllGauges(2.5, 12, 18, 35),
            Source = CircuitFoilCatalogue + ", BF-HFA page",
            Note = "ISO 4287 Rz ≤ 3.1 µm."
        },
        new CopperFoil
        {
            Vendor = "Circuit Foil", Name = "TZA-B3", Family = "RTF ('ultra flat reverse treated')", TreatedSide = "drum side",
            Gauges = AllGauges(2.1, 18, 35),
            Source = CircuitFoilCatalogue + ", TZA-B3 page",
            Note = "ISO 4287 Rz ≤ 2.6 µm; the untreated matte side is about 3 µm (½ oz) and 5 µm (1 oz) JIS."
        },
        new CopperFoil
        {
            Vendor = "Circuit Foil", Name = "TZA-B2", Family = "RTF ('flat reverse treated')", TreatedSide = "drum side",
            Gauges = AllGauges(2.5, 18, 35),
            Source = CircuitFoilCatalogue + ", TZA-B2 page",
            Note = "ISO 4287 Rz ≤ 3.1 µm; the untreated matte side is about 3 µm (½ oz) and 5 µm (1 oz) JIS."
        },
        new CopperFoil
        {
            Vendor = "Circuit Foil", Name = "TZA-B", Family = "RTF", TreatedSide = "drum side",
            Gauges = AllGauges(4.2, 12, 18, 35, 70, 105),
            Source = CircuitFoilCatalogue + ", TZA-B page",
            Note = "ISO 4287 Rz ≤ 5.1 µm; the untreated matte side runs ≤ 4.2 (12 µm) to ≤ 10.1 µm (105 µm) JIS."
        },
        new CopperFoil
        {
            Vendor = "Circuit Foil", Name = "TZA", Family = "standard (matte-side treated)",
            Gauges = new[]
            {
                Range(12, 3.3, 5.8), Range(18, 4.1, 6.7), Range(35, 5.0, 8.4), Range(70, 5.8, 11),
                Range(105, 6.7, 13.5), Range(140, 6.7, 14.3), Range(175, 7.5, 15.2), Range(210, 7.5, 16)
            },
            Source = "Circuit Foil, TZA data sheet, www.circuitfoil.com/wp-content/uploads/2022/11/TZA-datasheet.pdf (2022)",
            Note = "A range per gauge; the top of the range is used. ISO 4287: 4–7 µm (12 µm) to 9–19 µm (210 µm)."
        },
        new CopperFoil
        {
            Vendor = "Circuit Foil", Name = "TWS-B-YE", Family = "RTF, high-Tg substrates", TreatedSide = "drum side",
            Gauges = new[] { Range(18, 3, 5), Range(35, 3, 5), Range(70, 3, 5) },
            Source = "Circuit Foil, TWS-B-YE data sheet dated 2019-05-21, www.circuitfoil.com/wp-content/uploads/2019/07/TWS-B-YE.pdf",
            Note = "ISO 4287 Rz 4–6 µm; the untreated matte side ≤ 5.0 / 7.5 / 8.4 µm JIS."
        },
        new CopperFoil
        {
            Vendor = "Circuit Foil", Name = "TW-B-YE / TW-B", Family = "RTF (very low profile treatment)", TreatedSide = "drum side",
            Gauges = AllGauges(4.2, 12, 18, 35, 70, 105, 140, 175, 210),
            Source = "Circuit Foil, TW-B-YE / TW-B data sheet dated 2016-01-04, www.circuitfoil.com/wp-content/uploads/2019/07/TW-B-YE_TW-B.pdf",
            Note = "ISO 4287 Rz ≤ 5.1 µm."
        },
        new CopperFoil
        {
            Vendor = "Circuit Foil", Name = "TWLS-B", Family = "RTF, hydrocarbon substrates", TreatedSide = "drum side",
            Gauges = AllGauges(4.2, 18, 35, 70),
            Source = CircuitFoilCatalogue + ", TWLS-B page",
            Note = "ISO 4287 Rz ≤ 5.1 µm."
        },
        new CopperFoil
        {
            Vendor = "Circuit Foil", Name = "TWLS / TWL-HP", Family = "standard, hydrocarbon substrates",
            Gauges = new[] { Range(12, 3.7, 5.4), Range(18, 5, 7.5), Range(35, 5.8, 10.9), Range(70, 6.7, 11.8) },
            Source = CircuitFoilCatalogue + ", TWLS / TWL-HP page",
            Note = "A range per gauge; the top of the range is used. ISO 4287: 4.5–6.5 to 8–14 µm."
        },
        new CopperFoil
        {
            Vendor = "Circuit Foil", Name = "HFA-B", Family = "RTF, fluoropolymer substrates", TreatedSide = "drum side",
            Gauges = AllGauges(4.2, 18, 35, 70),
            Source = CircuitFoilCatalogue + ", HFA-B page",
            Note = "ISO 4287 Rz ≤ 5.1 µm."
        },
        new CopperFoil
        {
            Vendor = "Circuit Foil", Name = "HFA-LP", Family = "low profile, fluoropolymer substrates",
            Gauges = AllGauges(5, 18, 35),
            Source = CircuitFoilCatalogue + ", HFA-LP page",
            Note = "ISO 4287 Rz ≤ 6 µm."
        },

        // ---- Furukawa Electric (Japan): Rz, standard not stated, typical values -----------------
        new CopperFoil
        {
            Vendor = "Furukawa", Name = "FZ-WS", Family = "H-VLP2 (high-frequency boards)", Statistic = RoughnessStatistic.UnstatedRz,
            Gauges = new[] { Typical(12, 1.2), Typical(18, 1.1), Typical(35, 1.0) },
            Source = Furukawa + " fz-ws.html"
        },
        new CopperFoil
        {
            Vendor = "Furukawa", Name = "F0-WS", Family = "low-roughening treatment", Statistic = RoughnessStatistic.UnstatedRz,
            Gauges = new[] { Typical(12, 1.4), Typical(18, 1.2), Typical(35, 1.2) },
            Source = Furukawa + " f-ws.html"
        },
        new CopperFoil
        {
            Vendor = "Furukawa", Name = "F2-WS", Family = "low-roughening treatment", Statistic = RoughnessStatistic.UnstatedRz,
            Gauges = new[] { Typical(9, 1.8), Typical(12, 1.8), Typical(18, 1.8), Typical(35, 1.3) },
            Source = Furukawa + " f-ws.html"
        },
        new CopperFoil
        {
            Vendor = "Furukawa", Name = "F3-WS", Family = "low-roughening treatment", Statistic = RoughnessStatistic.UnstatedRz,
            Gauges = new[] { Typical(12, 3.1), Typical(70, 2.6) },
            Source = Furukawa + " f-ws.html"
        },
        new CopperFoil
        {
            Vendor = "Furukawa", Name = "FWL-WS", Family = "VLP (LCP and hard substrates)", Statistic = RoughnessStatistic.UnstatedRz,
            Gauges = new[] { Typical(12, 2.3), Typical(18, 2.0) },
            Source = Furukawa + " fwl-ws.html"
        },

        // ---- Chang Chun Petrochemical HTE, as sold by Ventec ----------------------------------
        new CopperFoil
        {
            Vendor = "Chang Chun", Name = "HTE (Ventec)", Family = "standard HTE, IPC-4562 grade 3", Statistic = RoughnessStatistic.UnstatedRz,
            Gauges = new[] { Max(9, 6), Max(12, 7), Max(18, 8), Max(35, 10), Max(70, 14), Max(105, 12), Max(140, 13), Max(175, 14), Max(210, 15) },
            Source = "Ventec International Group, HTE copper foil data sheet (foil by Chang Chun Petrochemical), 2019-08-12, " +
                     "www.ventec-group.com/media/97160/190812vtc_data_copperfoil.pdf",
            Note = "Matte-side Rz by IPC-TM-650 (standard not stated), as printed: the 70 µm limit (< 14) exceeds the " +
                   "105 µm one (< 12). Shiny side Ra < 0.43 µm."
        },

        // ---- Isola's foil classes (laminate vendor; the foil maker is not named) --------------
        new CopperFoil
        {
            Vendor = "Isola", Name = "HVLP (VLP2)", Family = "HVLP class on Isola laminates",
            Gauges = AllGauges(2.5, 18, 35),
            Source = Isola,
            Note = "Class limit, Rz JIS, 1 oz and below; Tachyon 100G's feature text says 2 µm. Renamed from VLP2 in 2021."
        },
        new CopperFoil
        {
            Vendor = "Isola", Name = "HVLP3 (VLP1)", Family = "HVLP3 class on Isola laminates",
            Gauges = AllGauges(1.1, 18, 35),
            Source = Isola,
            Note = "Class limit, Rz JIS, ½ and 1 oz."
        },
        new CopperFoil
        {
            Vendor = "Isola", Name = "Advanced RTF", Family = "RTF class on Isola laminates", TreatedSide = "drum side",
            Gauges = AllGauges(2.5, 18, 35),
            Source = Isola + " (Tachyon 100G page)",
            Note = "Class limit, Rz JIS."
        },

        // ---- Rogers' foils: typical RMS (Sq) of the treated side ------------------------------
        new CopperFoil
        {
            Vendor = "Rogers", Name = "Rolled foil", Family = "rolled annealed", TreatedSide = "dielectric side",
            Statistic = RoughnessStatistic.RootMeanSquare,
            Gauges = new[] { Typical(18, 0.4), Typical(35, 0.4) },
            Source = Rogers,
            Note = "On RO1200, RO3003, RT/duroid 5870/5880/6002, CLTE, CuClad, DiClad, IsoClad; top side 0.3 µm."
        },
        new CopperFoil
        {
            Vendor = "Rogers", Name = "Reverse treated ED foil", Family = "RTF", TreatedSide = "drum side",
            Statistic = RoughnessStatistic.RootMeanSquare,
            Gauges = new[] { Typical(18, 1.0), Typical(35, 1.0), Typical(70, 1.0) },
            Source = Rogers,
            Note = "On DiClad, IsoClad, CuClad, CLTE, AD, RT/duroid 6002/6006/6010, TC350/TC600; the top (matte) side 0.8–2.0 µm."
        },
        new CopperFoil
        {
            Vendor = "Rogers", Name = "LoPro foil", Family = "RTF with bond-enhancement layer", TreatedSide = "drum side",
            Statistic = RoughnessStatistic.RootMeanSquare,
            Gauges = new[] { Typical(18, 0.9), Typical(35, 0.9) },
            Source = Rogers,
            Note = "On RO4003C, RO4350B, RO4533, RO4534, RO4725JXR, RO4730, RO4830, RO4835, CU4000 LoPro."
        },
        new CopperFoil
        {
            Vendor = "Rogers", Name = "ED foil (RO4000 series)", Family = "standard electrodeposited",
            Statistic = RoughnessStatistic.RootMeanSquare,
            Gauges = new[] { Typical(18, 2.8), Typical(35, 3.2), Typical(70, 3.5), Typical(105, 3.2), Typical(140, 3.0) },
            Source = Rogers,
            Note = "The ED foil on Kappa 438, RO4003C, RO4350B, RO4360G2, RO4533, RO4534, RO4730G3, RO4835, 92ML, CU4000."
        },
        new CopperFoil
        {
            Vendor = "Rogers", Name = "ED foil (RO3000 and RT/duroid)", Family = "standard electrodeposited",
            Statistic = RoughnessStatistic.RootMeanSquare,
            Gauges = new[] { Typical(9, 1.3), Typical(18, 2.0), Typical(35, 2.4) },
            Source = Rogers,
            Note = "The ED foil on RO3003, RO3006, RO3010, RO3035, RO3210, RT/duroid 5870, 5880, 6002, 6006, 6010.2LM, 6202, 6035HTC, TMM."
        }
    };
}
