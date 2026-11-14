using System.Numerics;
using OpenSim.Core.Model;
using OpenSim.Optics.Materials;

namespace OpenSim.Optics.Coatings;

/// <summary>Converts a <see cref="CoatingCatalog"/> to and from its saved form,
/// <see cref="OpticsCoatingsSettings"/> (the project, the lens file and the coating catalog file).</summary>
public static class CoatingCatalogSettings
{
    public const string LayersKind = "Layers", IdealKind = "Ideal", Ideal2Kind = "Ideal2", TableKind = "Table";

    public static OpticsCoatingsSettings? ToSettings(CoatingCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.Materials.Count == 0 && catalog.Tapers.Count == 0 && catalog.Coatings.Count == 0) return null;
        return new OpticsCoatingsSettings
        {
            Materials = catalog.Materials.Select(m => new OpticsCoatingMaterialSettings
            {
                Name = m.Name,
                Rows = m.Rows.Select(r => new OpticsIndexRowSettings { Micrometers = r.Micrometers, N = r.N, K = r.K }).ToList()
            }).ToList(),
            Tapers = catalog.Tapers.Select(t => new OpticsCoatingTaperSettings
            {
                Name = t.Name, DecenterX = t.DecenterX, DecenterY = t.DecenterY, RotationDegrees = t.RotationDegrees,
                RadialTerms = Terms(t.RadialTerms), CosineTerms = Terms(t.CosineTerms), PolynomialTerms = Terms(t.PolynomialTerms)
            }).ToList(),
            Coatings = catalog.Coatings.Select(ToSettings).ToList()
        };
    }

    private static List<OpticsTaperTermSettings> Terms(IReadOnlyDictionary<int, double> terms) =>
        terms.OrderBy(t => t.Key).Select(t => new OpticsTaperTermSettings { Term = t.Key, Coefficient = t.Value }).ToList();

    private static OpticsCoatingSettings ToSettings(Coating coating) => coating switch
    {
        LayerCoating l => new OpticsCoatingSettings
        {
            Name = l.Name, Kind = LayersKind,
            Layers = l.Layers.Select(x => new OpticsCoatingLayerSettings
            {
                Material = x.Material, Thickness = x.Thickness, IsAbsolute = x.IsAbsolute, LoopIndex = x.LoopIndex, Taper = x.Taper
            }).ToList()
        },
        IdealCoating i => new OpticsCoatingSettings
        {
            Name = i.Name, Kind = IdealKind, Transmission = i.Transmission, Reflection = i.Reflection,
            TotalInternalReflection = i.TotalInternalReflection, SimpleIdeal = i.IsSimple
        },
        Ideal2Coating i => new OpticsCoatingSettings
        {
            Name = i.Name, Kind = Ideal2Kind,
            SReflectionReal = i.SReflection.Real, SReflectionImaginary = i.SReflection.Imaginary,
            STransmissionReal = i.STransmission.Real, STransmissionImaginary = i.STransmission.Imaginary,
            PReflectionReal = i.PReflection.Real, PReflectionImaginary = i.PReflection.Imaginary,
            PTransmissionReal = i.PTransmission.Real, PTransmissionImaginary = i.PTransmission.Imaginary,
            NoPiFlag = i.NoPiFlag, TotalInternalReflection = i.TotalInternalReflection
        },
        TableCoating t => new OpticsCoatingSettings
        {
            Name = t.Name, Kind = TableKind,
            Table = t.Angles.SelectMany(a => a.Rows.Select(r => new OpticsCoatingTableRowSettings
            {
                AngleDegrees = a.AngleDegrees, Micrometers = r.Micrometers, Rs = r.Rs, Rp = r.Rp, Ts = r.Ts, Tp = r.Tp,
                PhaseRs = r.PhaseRs, PhaseRp = r.PhaseRp, PhaseTs = r.PhaseTs, PhaseTp = r.PhaseTp
            })).ToList()
        },
        _ => throw new NotSupportedException($"Coating '{coating.Name}' is of a kind ({coating.GetType().Name}) that cannot be saved.")
    };

    /// <summary>The catalog a saved form describes; null is the empty catalog. A coating of a
    /// kind this build does not know is left out and named in <paramref name="notes"/>. The
    /// catalog is not validated here: <see cref="CoatingCatalog.Validate"/> lists its problems.</summary>
    public static CoatingCatalog FromSettings(OpticsCoatingsSettings? saved, ICollection<string> notes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        if (saved is null) return CoatingCatalog.Empty;
        var materials = (saved.Materials ?? new()).Select(m => new CoatingMaterial
        {
            Name = m.Name ?? "",
            Rows = (m.Rows ?? new()).Select(r => new TabulatedIndexPoint(r.Micrometers, r.N, r.K)).ToArray()
        }).ToArray();
        var tapers = (saved.Tapers ?? new()).Select(t => new CoatingTaper
        {
            Name = t.Name ?? "", DecenterX = t.DecenterX, DecenterY = t.DecenterY, RotationDegrees = t.RotationDegrees,
            RadialTerms = Terms(t.RadialTerms, t.Name, "radial", notes),
            CosineTerms = Terms(t.CosineTerms, t.Name, "cosine", notes),
            PolynomialTerms = Terms(t.PolynomialTerms, t.Name, "polynomial", notes)
        }).ToArray();
        var coatings = new List<Coating>();
        foreach (var c in saved.Coatings ?? new())
        {
            string name = c.Name ?? "";
            switch (c.Kind ?? LayersKind)
            {
                case LayersKind:
                    coatings.Add(new LayerCoating
                    {
                        Name = name,
                        Layers = (c.Layers ?? new()).Select(x => new CoatingLayer
                        {
                            Material = x.Material ?? "", Thickness = x.Thickness, IsAbsolute = x.IsAbsolute,
                            LoopIndex = x.LoopIndex, Taper = string.IsNullOrEmpty(x.Taper) ? null : x.Taper
                        }).ToArray()
                    });
                    break;
                case IdealKind:
                    coatings.Add(new IdealCoating
                    {
                        Name = name, Transmission = c.Transmission, Reflection = c.Reflection,
                        TotalInternalReflection = c.TotalInternalReflection, IsSimple = c.SimpleIdeal
                    });
                    break;
                case Ideal2Kind:
                    coatings.Add(new Ideal2Coating
                    {
                        Name = name,
                        SReflection = new Complex(c.SReflectionReal, c.SReflectionImaginary),
                        STransmission = new Complex(c.STransmissionReal, c.STransmissionImaginary),
                        PReflection = new Complex(c.PReflectionReal, c.PReflectionImaginary),
                        PTransmission = new Complex(c.PTransmissionReal, c.PTransmissionImaginary),
                        NoPiFlag = c.NoPiFlag, TotalInternalReflection = c.TotalInternalReflection
                    });
                    break;
                case TableKind:
                    coatings.Add(new TableCoating
                    {
                        Name = name,
                        Angles = (c.Table ?? new()).GroupBy(r => r.AngleDegrees)
                            .Select(g => new CoatingTableAngle(g.Key, g.Select(r =>
                                new CoatingTableRow(r.Micrometers, r.Rs, r.Rp, r.Ts, r.Tp, r.PhaseRs, r.PhaseRp, r.PhaseTs, r.PhaseTp)).ToArray()))
                            .ToArray()
                    });
                    break;
                default:
                    notes.Add($"Coating '{name}' is of kind '{c.Kind}', which this version does not know; it was left out.");
                    break;
            }
        }
        return new CoatingCatalog { Materials = materials, Tapers = tapers, Coatings = coatings };
    }

    private static IReadOnlyDictionary<int, double> Terms(List<OpticsTaperTermSettings>? terms, string? taper, string kind, ICollection<string> notes)
    {
        var result = new Dictionary<int, double>();
        foreach (var t in terms ?? new())
        {
            if (result.ContainsKey(t.Term))
                notes.Add($"Taper '{taper}': {kind} term {t.Term} is given twice; the last value is used.");
            result[t.Term] = t.Coefficient;
        }
        return result;
    }
}
