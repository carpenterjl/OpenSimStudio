namespace OpenSim.Optics.Analysis;

/// <summary>
/// Every analysis the program offers, in menu order, found by saved id. The analyses register
/// in <see cref="CreateDefault"/>: a new analysis item adds one line there and nothing else in
/// the windows, the project file or the report.
/// </summary>
public sealed class OpticsAnalysisRegistry
{
    private readonly List<IOpticsAnalysis> _all = new();
    private readonly Dictionary<string, IOpticsAnalysis> _byId = new(StringComparer.Ordinal);

    /// <summary>The registered analyses in registration order.</summary>
    public IReadOnlyList<IOpticsAnalysis> All => _all;

    /// <summary>Adds an analysis. Its id must be non-blank and not taken, and its settings type
    /// must be constructible, so that a mistake fails at start-up rather than when a saved
    /// project names the analysis.</summary>
    public void Register(IOpticsAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (string.IsNullOrWhiteSpace(analysis.Id))
            throw new ArgumentException($"Analysis '{analysis.Name}' has no id.", nameof(analysis));
        if (string.IsNullOrWhiteSpace(analysis.Name))
            throw new ArgumentException($"Analysis '{analysis.Id}' has no name.", nameof(analysis));
        if (_byId.ContainsKey(analysis.Id))
            throw new ArgumentException($"An analysis with id '{analysis.Id}' is already registered.", nameof(analysis));
        var defaults = analysis.CreateDefaultSettings();
        if (defaults is null || defaults.GetType() != analysis.SettingsType)
            throw new ArgumentException(
                $"Analysis '{analysis.Id}' must create default settings of its own settings type {analysis.SettingsType.Name}.",
                nameof(analysis));
        _all.Add(analysis);
        _byId.Add(analysis.Id, analysis);
    }

    public bool TryGet(string? id, out IOpticsAnalysis analysis)
    {
        if (id is not null && _byId.TryGetValue(id, out var found))
        {
            analysis = found;
            return true;
        }
        analysis = null!;
        return false;
    }

    /// <summary>The program's analyses. The analysis items (OP-34 to OP-45) register here.</summary>
    public static OpticsAnalysisRegistry CreateDefault()
    {
        var registry = new OpticsAnalysisRegistry();
        registry.Register(new SystemSummaryAnalysis());
        registry.Register(new ParaxialDataAnalysis());
        // OP-20: the real ray trace, on the project's saved surfaces (OP-31).
        registry.Register(new SingleRayTraceAnalysis());
        // OP-21: global vertices and coordinate breaks (no saved surfaces until OP-31).
        registry.Register(new GlobalVertexAnalysis());
        // OP-22: the vignetting plot.
        registry.Register(new VignettingPlotAnalysis());
        // Materials (OP-14).
        registry.Register(new GlassMapAnalysis());
        registry.Register(new AthermalGlassMapAnalysis());
        registry.Register(new DispersionCurveAnalysis());
        registry.Register(new TransmittanceCurveAnalysis());
        registry.Register(new GlassCompareAnalysis());
        registry.Register(new ModelGlassAnalysis());
        registry.Register(new GlassFitAnalysis());
        // OP-15: the twelve coating plots.
        foreach (var coating in CoatingAnalyses.All()) registry.Register(coating);
        // OP-48: Gaussian beams.
        registry.Register(new GaussianBeamAnalysis());
        // OP-51: polarization (no saved surfaces until OP-31).
        registry.Register(new PolarizationRayTraceAnalysis());
        registry.Register(new PolarizationPupilMapAnalysis());
        registry.Register(new PolarizationTransmissionAnalysis());
        registry.Register(new PolarizationPhaseAberrationAnalysis());
        registry.Register(new PolarizationTransmissionFanAnalysis());
        // OP-37: field, distortion, colour and illumination.
        registry.Register(new FieldCurvatureDistortionAnalysis());
        registry.Register(new GridDistortionAnalysis());
        registry.Register(new LongitudinalAberrationAnalysis());
        registry.Register(new LateralColorAnalysis());
        registry.Register(new ChromaticFocalShiftAnalysis());
        registry.Register(new RelativeIlluminationAnalysis());

        // OP-35: optical path difference and wavefront.
        registry.Register(new OpdFanAnalysis());
        registry.Register(new WavefrontMapAnalysis());
        registry.Register(new InterferogramAnalysis());
        registry.Register(new FoucaultAnalysis());
        registry.Register(new PupilAberrationAnalysis());

        // OP-44: reports.
        registry.Register(new SystemDataAnalysis());
        registry.Register(new PrescriptionDataAnalysis());
        registry.Register(new SurfaceDataAnalysis());
        registry.Register(new CardinalPointsAnalysis());
        registry.Register(new SystemSummaryGraphicAnalysis());
        registry.Register(new ReportGraphicAnalysis(id => registry.TryGet(id, out var found) ? found : null));

        // OP-34: ray fans, spot diagrams and footprints.
        registry.Register(new RayAberrationAnalysis());
        registry.Register(new StandardSpotAnalysis());
        registry.Register(new ThroughFocusSpotAnalysis());
        registry.Register(new FullFieldSpotAnalysis());
        registry.Register(new MatrixSpotAnalysis());
        registry.Register(new FootprintDiagramAnalysis());
        registry.Register(new YYbarAnalysis());
        registry.Register(new IncidentAngleAnalysis());

        // OP-43: surface shape analyses (Surface Phase and Phase Slope not built: no surface carries a phase).
        registry.Register(new SagTableAnalysis());
        registry.Register(new SurfaceSagAnalysis());
        registry.Register(new SurfaceCurvatureAnalysis());
        registry.Register(new SurfaceSlopeAnalysis());
        registry.Register(new SurfaceSagCrossSectionAnalysis());
        registry.Register(new SurfaceCurvatureCrossSectionAnalysis());
        registry.Register(new SurfaceSlopeCrossSectionAnalysis());

        // OP-38: point spread functions and the Strehl ratio.
        registry.Register(new FftPsfAnalysis());
        registry.Register(new FftPsfCrossSectionAnalysis());
        registry.Register(new FftLineEdgeSpreadAnalysis());
        registry.Register(new HuygensPsfAnalysis());
        registry.Register(new HuygensPsfCrossSectionAnalysis());
        registry.Register(new StrehlRatioAnalysis());

        // OP-36: third-order and full-field aberrations (the Contrast Loss Map is registered with OP-39).
        registry.Register(new SeidelCoefficientsAnalysis());
        registry.Register(new SeidelDiagramAnalysis());
        registry.Register(new FullFieldAberrationAnalysis());

        // OP-40: RMS against field, wavelength and focus, and the RMS field map, with the Strehl ratio by OP-38's PSF engine.
        registry.Register(new RmsVsFieldAnalysis());
        registry.Register(new RmsVsWavelengthAnalysis());
        registry.Register(new RmsVsFocusAnalysis());
        registry.Register(new RmsFieldMapAnalysis());

        // OP-41: enclosed energy (diffraction, geometric, geometric line/edge spread, extended source).
        registry.Register(new DiffractionEnclosedEnergyAnalysis());
        registry.Register(new GeometricEnclosedEnergyAnalysis());
        registry.Register(new GeometricLineEdgeSpreadAnalysis());
        registry.Register(new ExtendedSourceEnclosedEnergyAnalysis());

        // OP-39: the MTF family (FFT, Huygens, geometric) and the Contrast Loss Map OP-36 left out.
        registry.Register(new FftMtfAnalysis());
        registry.Register(new FftThroughFocusMtfAnalysis());
        registry.Register(new FftSurfaceMtfAnalysis());
        registry.Register(new FftMtfVsFieldAnalysis());
        registry.Register(new FftMtfMapAnalysis());
        registry.Register(new HuygensMtfAnalysis());
        registry.Register(new HuygensThroughFocusMtfAnalysis());
        registry.Register(new HuygensSurfaceMtfAnalysis());
        registry.Register(new HuygensMtfVsFieldAnalysis());
        registry.Register(new GeometricMtfAnalysis());
        registry.Register(new GeometricThroughFocusMtfAnalysis());
        registry.Register(new GeometricMtfVsFieldAnalysis());
        registry.Register(new GeometricMtfMapAnalysis());
        registry.Register(new ContrastLossMapAnalysis());

        // OP-46: stray-light and application analyses.
        registry.Register(new StrayLight.GhostFocusAnalysis());
        registry.Register(new StrayLight.YniContributionsAnalysis());
        registry.Register(new StrayLight.PowerPupilMapAnalysis());
        registry.Register(new StrayLight.PowerFieldMapAnalysis());

        // OP-47: diffraction efficiency (volume holograms by Kogelnik; gratings by RCWA or scalar theory).
        registry.Register(new DiffractionEfficiencyAnalysis());
        registry.Register(new EfficiencyVsAngleAnalysis());
        registry.Register(new EfficiencyVsWavelengthAnalysis());
        registry.Register(new GratingEfficiencyAnalysis());

        // OP-45: Universal Plot 1D and 2D (any registered operand against one or two swept parameters).
        registry.Register(new UniversalPlot.UniversalPlot1DAnalysis());
        registry.Register(new UniversalPlot.UniversalPlot2DAnalysis());

        // OP-49: physical optics propagation and the beam file viewer.
        registry.Register(new PhysicalOptics.PhysicalOpticsPropagationAnalysis());
        registry.Register(new PhysicalOptics.BeamFileViewerAnalysis());
        return registry;
    }
}
