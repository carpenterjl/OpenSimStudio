# Optics gates

Where each finished optics item's gate (the "Gate" line of its entry in the optics toolbox
plan) is pinned by a test, and how far. Written for OP-101 on 2026-11-14 against commit
`498c28b`; items finished later add their own rows. The house rules for numerical tests are in
CONTRIBUTING.md, "Writing numerical tests": closed forms or cited values, tolerances sharp against
the method's error, one-sided bands where a method has a known bias, and bitwise determinism for
parallel paths.

Test paths are relative to the repository root. `OpenSim.Tests/Optics/Gates/` holds the tests
OP-101 added; everything else was written with its item.

Status words: **pinned** means every clause of the gate has a test that would fail if the clause
broke; **partial** means some clause has no test yet, and the row says which and why; **not
testable** means the gate is a review, not a computation.

## Gate table

| Item | Gate (plan text, shortened) | Tests | Status |
|---|---|---|---|
| OP-1 Optics project and workspace shell | Empty Optics workspace opens; optics settings save and reload; composition test resolves the optics view model; every WorkspaceKind has an arm in ForWorkspace and WorkspaceOf | `OpenSim.App.Tests/ViewModels/OpticsWorkspaceTests` (`TheEmptyOpticsWorkspace_Opens_AndHasNothingToSolve`, `AnOpticsProject_SavesAndReopens_InItsWorkspaceAndMode`, `EveryWorkspace_HasItsAnalyses_ANavRow_AndAName`, `EveryAnalysis_IsListed_AndBelongsToTheWorkspaceThatOffersIt`); `OpenSim.Tests/Optics/OpticsProjectTests.OpticsSettings_RoundTrip_AndOlderProjectsLoadWithoutThem` | pinned |
| OP-2 Conventions and core types | Every conversion: tilt order both ways, decenter-then-tilt against tilt-then-decenter, mirror sign flip, local to global and back to 1e-14 | `OpenSim.Tests/Optics/OpticsConventionsTests` (`DecenterThenTilt_IsTheWrittenOutMatrix`, `EachTilt_IsAboutTheAxisTheEarlierTiltsLeft`, `TheTwoOrders_PlaceTheSameTiltDifferently`, `TheReversedBreak_UndoesIt`, `TiltsReadBackFromATransform_AreTheOnesPutIn`, `AMirror_FlipsTheRayAndTheThicknessSign`, `ARay_GoesToTheGlobalFrameAndBack` at 1e-14) | pinned |
| OP-3 Numerics promoted to Core | RF, PDN and sweep results bitwise unchanged after the move; J1 against series and asymptotic forms to 1e-13; Sobol reproduces the published first points | `OpenSim.Tests/Core/PromotedNumericsTests` (`J1_MatchesBesselsIntegralTo1e13_AcrossAllThreeRanges`, `J1_SeriesRecurrenceAndAsymptoticFormsAgreeTo1e13`, `Sobol_ReproducesThePublishedFirstTenPointsInThreeDimensions`, `ParallelSlots_WithRandomStreams_AreBitwiseTheSameAtAnyThreadCount`) | partial: "bitwise unchanged after the move" was a one-time comparison of old and new code in commit `e45fa7c` (4.18 million values); the old code is gone, so no test can repeat it. The RF, PDN and sweep regression tests that existed before the move still pass, which pins the results but not to the bit. |
| OP-4 SVD, Cholesky, 2-D FFT, zoom transform | 2-D FFT against direct DFT at 1e-12 and Parseval; SVD reconstructs random and rank-deficient matrices to 1e-12 and agrees with MathNet | `OpenSim.Tests/Core/DenseTransformsTests` (`Fft2D_MatchesTheDirectSum`, `Fft2D_SatisfiesParsevalAndInvertsItself`, `Svd_RandomMatrix_ReconstructsAndMatchesMathNet`, `Svd_RankDeficientMatrix_ReconstructsAndReportsItsRank`, plus `Fft2D_IsBitwiseIdenticalAtAnyThreadCount`, `Transform_IsBitwiseIdenticalAtAnyThreadCount`) | pinned |
| OP-5 Optimizer kernel | Rosenbrock, Powell singular and the MGH least-squares set reach their documented minima; a throwing residual is penalised; identical at any thread count | `OpenSim.Tests/Core/OptimizerTests` (`DampedLeastSquares_ReachesTheDocumentedMinimum` over MINPACK's 18 LMDER problems with minima from ANL-80-74, `DerivativeFree_Rosenbrock_ReachesItsMinimum`, `DerivativeFree_PowellSingular_ReachesItsMinimum`, `DampedLeastSquares_ResidualThatThrowsMidRun_IsPenalisedNotFatal`, `DampedLeastSquares_IsBitwiseIdenticalAtAnyThreadCount`) | pinned |
| OP-6 Spreadsheet control | Edit, flags, type-dependent columns, insert/delete with undo, block paste, 500 rows without re-layout of unseen rows | `OpenSim.App.Tests/Controls/Spreadsheet/SheetDocumentTests`, `SheetModelTests`, `TabularTextTests`, `SpreadsheetGridTests.FiveHundredRows_BuildContainersOnlyForTheRowsInView_BeforeAndAfterScrolling` | pinned |
| OP-7 Plot controls | Renderer tests on known data (a Gaussian's contour radii, a polar cosine lobe); text export round-trips the plotted numbers | `OpenSim.Tests/Plotting/GridContourTests.Gaussian_ContourRadiiMatchTheClosedForm`, `PlotMappingTests.CosineLobe_IsTheCircleThroughTheOriginOfDiameterOne`, `PlotCsvTests.Points_RoundTripBitForBit_WithAwkwardNamesAndValues`; `OpenSim.App.Tests/Controls/Plotting/PlotControlTests` (`Contour_LinesExportTheGaussianRadii`, `Polar_CosineLobeReadsBackItsAngleAndRadius`, `Scatter_CsvRoundTripsThePlottedPoints`) | pinned |
| OP-8 Optical layout rendering | A doublet renders with both elements visible from either side; the cross-section of a known prescription matches the sag at the drawn edge to a pixel | `OpenSim.App.Tests/Rendering/OpticalLayoutSceneTests.Doublet_BothBodiesVisible_FromEitherSide`; `OpenSim.Tests/Optics/CrossSectionTests.Singlet_DrawnEdge_MatchesTheSag_ToAPixel`; sag formula `LayoutMeshTests.ConicSag_MatchesSphereAndParaboloidClosedForms` | pinned |
| OP-9 Analysis window framework | Two analyses side by side update after one edit; a long analysis is cancelled by the next edit; settings persist | `OpenSim.App.Tests/Analyses/OpticsAnalysesViewModelTests` (`TwoAnalysesSideBySide_BothUpdate_AfterOneEdit`, `ALongAnalysis_IsCancelledByTheNextEdit`, `TheAnalysisWindows_SaveAndReopen_WithTheProject`), `DebouncedRunnerTests` | pinned |
| OP-10 Optics settings in the project file | Reflection round-trip of every optics field; a version-3 project still loads; a saved lens reloads to bitwise-identical ray-trace output | `OpenSim.Tests/Optics/OpticsSettingsFileTests` (`OpticsSettings_RoundTripEveryField_ThroughTheProjectFile`, `..._ThroughTheLensFile`, `OlderProjects_LoadWithoutTheSystemData`), `OpticsAnalysesProjectTests`; **`OpenSim.Tests/Optics/Gates/LensFileTraceGateTests.TheSystemData_SavedAndReloaded_TracesToTheSameBits_ThroughTheLensFileAndTheProject`** | partial: surfaces are not saved until OP-31, so the bitwise-trace clause is pinned for the system data only (the lens is built in the test). Extend that test to save the lens when OP-31 lands. |
| OP-11 Material model and AGF reader | nd and Vd computed from the coefficients equal the catalog's printed nd and Vd for every glass in the file | `OpenSim.Tests/Optics/CatalogSelfCheckTests` (`TheFixture_ChecksItself_GlassByGlass`, `RealCatalogs_WhenPresent_CheckThemselves`), `AgfReaderTests`, `DispersionFormulaTests` | pinned, as restated in the plan (5e-5 for every catalog; the real catalogs are under the gitignored `plans/sources/optics`, so on a clone without them `RealCatalogs_WhenPresent_CheckThemselves` checks only the fixture) |
| OP-12 Shipped material data | Each shipped material carries its source; every shipped entry gives n finite and above 1 across its range | `OpenSim.Tests/Optics/RefractiveIndexInfoSeedTests` (`EveryShippedMaterial_CarriesItsSourceReference`, `EveryShippedMaterial_GivesFiniteNAboveOne_AndKAtLeastZero_AcrossItsRange`) | pinned |
| OP-13 Thermal and pressure index | Catalog index exactly at reference conditions; dn/dT by difference of the integrated form equals the differential form to 1e-9; a vacuum returns the absolute index | `OpenSim.Tests/Optics/ThermalIndexTests` (`AtTheReferenceConditions_TheCatalogIndexComesBackExactly`, `DnDT_ByDifferenceOfTheIntegratedForm_EqualsTheDifferentialForm`, `InAVacuum_TheResultIsTheAbsoluteIndex`), `AirIndexTests.AVacuum_HasIndexOneExactly_InEveryModel` | pinned |
| OP-14 Glass tools | Fitting synthetic data from a known Sellmeier set recovers it; the glass map places every shipped glass at its own nd, Vd | `OpenSim.Tests/Optics/GlassToolsTests` (`FittingSyntheticSellmeierData_RecoversTheCoefficients`, `FittingSyntheticSchottData_RecoversTheCoefficients`, `TheGlassMap_PlacesEveryGlassAtItsOwnComputedNdVd`, `TheGlassMap_OfEveryRealCatalog_WhenPresent`) | pinned |
| OP-15 Thin-film coatings | Bare-surface Fresnel at normal incidence; Brewster gives zero Rp; single quarter-wave layer; quarter-wave stack centre; R + T + A = 1; tmm cross-check values | `OpenSim.Tests/Optics/ThinFilmTests` (`BareSurface_AtNormalIncidence_GivesTheFresnelReflectance`, `BrewsterAngle_GivesZeroPReflectance`, `QuarterWaveLayer_GivesTheClosedFormReflectance`, `QuarterWaveStack_CentreReflectance_MatchesTheClosedForm`, `EnergyBalances_InEveryCase`, `AbsorbingMultilayer_AtObliqueIncidence_MatchesTmm`); **`OpenSim.Tests/Optics/Gates/ThinFilmObliqueGateTests`** (Fresnel's s and p pair at every angle, a quarter-wave layer at its tilted thickness, a half-wave layer absent at its tilted thickness and not at the normal-incidence one) | pinned; OP-101 adds closed forms at oblique incidence, which before were checked only against tmm's numbers |
| OP-18 System data | Each aperture type gives the same entrance pupil where equivalent; each field type launches the same chief ray where equivalent | `OpenSim.Tests/Optics/ParaxialTests` (`EveryApertureType_GivesTheSameEntrancePupil_WhereEquivalent_FiniteObject`, `..._ObjectAtInfinity`, `TelecentricObjectSpace_ApertureTypesAgree_AndChiefRaysEnterParallel`, `EveryParaxialFieldType_LaunchesTheSameChiefRay_WhereEquivalent`); **`OpenSim.Tests/Optics/Gates/RealTraceGateTests`** (`EquivalentFieldTypes_LaunchTheSameRealChiefRay_ThroughTheStopCentre`, `EquivalentApertureTypes_LaunchTheSameRealMarginalRays`) | partial: the real image height field type needs ray aiming (OP-22) and is refused today (asserted in ParaxialTests). OP-101 adds the real-trace half that aiming does not need. |
| OP-19 Paraxial engine | Thin and thick lens closed forms, two-lens combination, Galilean and Keplerian angular magnification, Lagrange invariant constant to 1e-13 | `OpenSim.Tests/Optics/ParaxialTests` (`ThickLens_FollowsTheLensmakersEquation_AndThePrincipalPlanePositions`, `ThinLens_HasPrincipalPlanesAtItsVertex_AndPowerFromTheLensmaker`, `TwoThinLenses_FollowTheCombinationFormula`, `Telescope_IsAfocal_WithAngularMagnificationMinusF1OverF2` for both signs, `LagrangeInvariant_IsTheSameAtEverySurface` at 1e-13) | pinned |
| OP-20 Real ray trace core | Parabolic mirror stigmatic to machine precision; ellipsoid and hyperboloid foci image point to point; Snell's law to 1e-14; hyperbolic plano-convex lens with k = −n² focuses perfectly; paraxial limit equals the paraxial trace | `OpenSim.Tests/Optics/RealRayTraceTests` (`ParabolicMirror_IsStigmaticOnAxis_ToMachinePrecision`, `EllipsoidalMirror_ImagesItsNearFocusOntoItsFarFocus`, `HyperbolicMirror_ImagesOneFocusAtTheOther`, `HyperbolicPlanoConvexLens_WithConicMinusNSquared_FocusesACollimatedBeamPerfectly`, `SnellsLaw_HoldsAtEveryRefraction_AndEveryHitIsOnItsSurface`, `ARealRay_InTheParaxialLimit_EqualsTheParaxialTrace_ThroughALensAndAMirror`, `TheBatchTrace_InParallel_IsBitwiseTheSequentialOne`); **`OpenSim.Tests/Optics/Gates/RealTraceGateTests`** (`ARefractingSphere_ImagesItsAplanaticPoint_Perfectly`, `AConcaveSphere_HasItsExactSphericalAberration_AlwaysUndercorrected`); **`OpenSim.Tests/Optics/Gates/OpticsDeterminismTests`** | pinned; OP-101 adds two closed forms on spheres (every listed gate was a conic or a plane) and the determinism tests below |
| OP-24 Toroidal, biconic and irregular surfaces | A biconic with equal x and y terms equals the Standard surface; a cylinder lens focuses to a line at the paraxial focal distance; an Irregular surface with zero terms equals Standard | `OpenSim.Tests/Optics/AnamorphicSurfaceTests` (`FormSetToTheStandardSurface_TracesAsIt` for Biconic, Biconic Zernike, two Irregular cases and a spherical toroid, 1e-12 m ray for ray; `CylinderLens_FocusesToALineAtTheParaxialFocalDistance`, focus Richardson-extrapolated to zero height at the derived BFD Rc/(n − 1) − t/n to 1e-11 m, x rays undeviated, paraxial BFL the same; `CylinderTurnedAboutY_FocusesXRays_AndTheParaxialEngineSeesNoPower`; `Normal_EqualsTheFiniteDifferenceSagGradient`, `Intersection_LiesOnTheSurfaceAndOnTheRay`, `EachType_RoundTripsThroughTheSavedSurfaces_AndTracesBitForBit`); `OpenSim.App.Tests/ViewModels/OpticsLensEditorAnamorphicTests` | pinned |
| OP-33 Zernike polynomials and fitting | Each term's RMS over the disk equals its normalisation by quadrature; fitting a synthetic wavefront returns its coefficients to 1e-12; Fringe and Standard index maps bijective over shared terms | `OpenSim.Tests/Optics/ZernikeTests` (`Terms_AreOrthogonalWithTheirNormalisation`, Gauss–Legendre × uniform rule, 1e-13; `Noll_MapIsBijective`, `FringeAndNoll_AreBijectiveOverTheSharedTerms`); `ZernikeFitTests.Fit_RecoversKnownCoefficientsOnTheFullPupil`, `Fit_RecoversKnownCoefficientsOnAVignettedWeightedPupil` | pinned |
| OP-43 Surface shape analyses | Sag table of a sphere against the closed form; curvature map of a sphere is constant; best-fit sphere of a sphere is itself | `OpenSim.Tests/Optics/SurfaceShapeAnalysisTests` (`TheSagTableOfASphere_IsTheClosedForm_AndItsBestFitSphereIsItself` for R = ±50 mm and all three criteria, sag to 1e-15 m, slope to 1e-14, BFS radius to 1e-8 relative; `TheCurvatureMapOfASphere_IsConstant` for every data choice, analytic to 1e-13 and differenced to 1e-9 relative; `TheBestFitSphereOfASphere_IsItself_AlsoThroughTheDifferencedShapePath_AndAnAnnulus`; added: `TheSagTableOfAnEvenAsphere_IsTheDerivedSag`, `TheCrossSectionsOfAnEvenAsphere_AreTheDerivedSagAndCurvatures`, `TheBestFitSphereOfAnAsphere_IsAMinimumOfItsCriterion`) | pinned |
| OP-34 Ray fans, spot diagrams and footprints | A perfect system's spot radius is zero; a singlet's ray fan is cubic with the third-order coefficient from the Seidel item; the on-axis footprint is the paraxial beam diameter in the paraxial limit | `OpenSim.Tests/Optics/RayFanSpotTests` (`AParaboloidOnAxis_HasZeroSpotRadius_AboutChiefAndCentroid`, `AnIdealParaxialLens_HasZeroSpotRadius_AtEveryField`, `ASphericalMirrorsRayFan_IsItsExactAberration_AndCubicWithTheDerivedCoefficient`, `TheOnAxisFootprint_IsTheParaxialMarginalBeam_InTheParaxialLimit`; also `SpotRadii_FollowTheHelpsDefinitions_OnAHandBuiltSet`, `RaysStoppedByAnAperture_AreCountedAndNamed`, `TheSampler_ReadsTheSameRaysThroughEitherPath_AndIsBitwiseTheSameAtAnyParallelism`) | partial: the Seidel item (OP-36) is not built, so the third-order coefficient is derived in the test from a concave spherical mirror's exact transverse aberration (a₃ = −(D/2)³/(2ρ²), to 10⁻⁶ of a₃) instead of taken from Seidel sums. Swap in OP-36's coefficient when it lands. Spot radii zero to 64–256 ulps of f; footprint to 10⁻⁶ at D = 20 µm |
| OP-44 Reports | Element volume of a biconvex lens against the spherical-cap closed form; the prescription report read back reproduces the lens | `OpenSim.Tests/Optics/ReportTests` (`ElementVolume_OfABiconvexLens_IsTheCylinderPlusTwoSphericalCaps` at 1e-13, with the cylinder-plus-caps form derived in the test; `ElementVolume_SquaresUpTheSmallerFace`, `ElementVolume_OfOtherShapes_IsNumericalAndAgreesWithClosedForms`, `ElementVolume_OfANonRotationalShape_UsesTheTwoDimensionalRule`, `Prescription_ListsTheElementVolumeAndMass`; `Prescription_ReadBack_ReproducesTheLens`: a lens with a tilted element, a coordinate break, an Even Asphere, an aperture, a coating and a comment holding the separator reads back to exactly its saved surfaces and to the same lens and placement; `Reports_AreTheSameByteForByte_OnRepeat`) | pinned |
| OP-48 Gaussian beam | Free-space w(z) and R(z); thin-lens waist transformation; a mode-matched telescope returns the input waist scaled by its magnification | `OpenSim.Tests/Optics/GaussianBeamTests` (`FreeSpace_FollowsTheClosedFormsOfWAndR`, `ThinLens_FollowsTheWaistTransformationFormula`, `ModeMatchedTelescope_ReturnsTheInputWaistScaledByItsMagnification`) | pinned |
| OP-54 Merit function engine and editor | The merit value equals the hand-computed weighted RMS on a small table; reordering independent rows does not change it; a failed ray in one row penalises only that row | `OpenSim.Tests/Optics/MeritFunctionTests` (`TheMeritValue_IsTheHandComputedWeightedRms`, `ReorderingIndependentRows_DoesNotChangeTheMeritValue` bitwise, `AFailedRayInOneRow_PenalisesOnlyThatRow`, plus the operand, control, configuration, determinism and saved-form tests); `OpenSim.App.Tests/ViewModels/OpticsMeritEditorTests` | pinned |
| OP-104 Documentation | Docs match the code at release | none | not testable (a review at release). The plan notes the README row and user guide predate OP-6..OP-33. |

## Determinism

Every parallel path in OpenSim.Optics, found by searching it for `ParallelSlots` and `Parallel.`
on 2026-11-14:

| Path | Test |
|---|---|
| `Sequential/BatchTracer.Trace` (one slot per field and wavelength) | `RealRayTraceTests.TheBatchTrace_InParallel_IsBitwiseTheSequentialOne`; `Gates/OpticsDeterminismTests.TheBatchTrace_WithAutomaticSemiDiameters_IsBitwiseTheSame_AtEveryDop_AndOnRepeat` (degrees of parallelism 1, 2, 3, 7 and unbounded, two runs each, with floating apertures sized inside the trace and vignetted rays), `TheBatchTrace_OfASubsetOfFieldsAndWavelengths_IsTheSameRaysAsTheFullTrace` |
| `Merit/MeritEvaluator.Evaluate` (one slot per distinct ray) | `MeritFunctionTests.TheEvaluation_IsBitwiseTheSame_AtAnyDegreeOfParallelism` (degrees 1, 2, 3, 7 and unbounded) |
| `Sequential/SemiDiameterSizing.Compute` (one slot per field and wavelength) | `Gates/OpticsDeterminismTests.SemiDiameterSizing_IsBitwiseTheSame_AtEveryDop_AndOnRepeat` (clear and mechanical semi-diameters and the failure notes, 4 and 16 rim points) |
| `Analysis/Rays/RaySampler.Trace` (BatchTracer at the image; one slot per field and wavelength elsewhere) | `RayFanSpotTests.TheSampler_ReadsTheSameRaysThroughEitherPath_AndIsBitwiseTheSameAtAnyParallelism` (degree 1 against unbounded, at an intermediate surface; the image path against per-ray traces) |

The Core paths the optics items use have their own tests: `Fft2D` and `MatrixFourierTransform`
(`Core/DenseTransformsTests`), `DampedLeastSquares` (`Core/OptimizerTests`), `ParallelSlots`
with random streams (`Core/PromotedNumericsTests`).

## Rule-4 audit of the optics tests

Plan rule 4 (and CLAUDE.md, "Accuracy and sources"): no expected value typed from memory; each
is derived in the test or copied from a cited source. Every file in `OpenSim.Tests/Optics/` at
`498c28b` was read in full and each numeric literal used as an expected value (in an assertion,
a constant, or an expected-output column of `InlineData`) classified as derived, cited, or an
input (a prescription, wavelength, seed, grid size or tolerance, which needs no source). Nothing
was changed; the findings are listed for the owner.

**Neither derived nor cited (2):**

- `AirIndexTests.cs:154` — the band −2e-8 to 0 for the TIE-19 air index minus Ciddor's (dry air,
  0.4–1.7 µm). The comment at lines 141–146 calls it a pinned measurement, "not a published
  number"; it is a regression record of the code's own output, not derived from either formula.
- `AirIndexTests.cs:157` — the band 3e-7 to 5e-7 for the Ciddor index change from 50 % humidity
  at 20 °C, likewise a measured record ("about 4·10⁻⁷", line 145) with no source or derivation.

Both bound the difference between two implemented models and would catch a regression, but
neither bound comes from a source. A source (for example a published humidity term) or a
derivation would make them gates; otherwise they could be labelled regression records, as
`GlassToolsTests.cs:388` already does for its 2e-4.

**Borderline (cited vaguely, or hand-evaluated literals):**

- `CoatingCatalogTests.cs:137` — `PolynomialPowers(20)` expected `(0, 5)`: the help lists terms
  E0–E6 only; the comment calls this an "inferred continuation", an extrapolation past the source.
- `CoatingCatalogTests.cs:79, 184, 203–204, 235, 294–295` and `OpticalMaterialTests.cs:72–75` —
  sums, squares and interpolation midpoints evaluated by hand and typed (for example 1.48 = 1.38 +
  0.1, 0.15 the midpoint of 0.1 and 0.2). Correct, but not computed in the code.
- `OpticalMaterialTests.cs:99–105` — the "half the last printed digit" column, evident from the
  text column but neither computed nor commented.
- `RefractiveIndexInfoReaderTests.cs:40, 89` — interpolation midpoints (0.3, 2e-6) typed without
  working; the neighbouring line 39 shows its working.
- `DispersionFormulaTests.cs:21–41, 52, 63` and `RefractiveIndexInfoFormulaTests.cs:37–60, 74` —
  hand-worked results typed as literals, with the full working in comments; re-checked and
  correct, and exact in binary, so counted as derived. (`RefractiveIndexInfoFormulaTests.cs:57`
  states n − 1 = 0.375 beside the row's n = 1.375; they agree.)
- `GlassToolsTests.cs:311` — 0.6438 and 0.001682 in the ΔP_g,F normal line, cited as
  "zemaxglass_manual.txt, 'dPgF'" with no line or page.
- `GlassToolsTests.cs:502–506` — density 2.51 and the count 3 of IT points are fixture values
  without the fixture line cited (they are correct: fixture lines 11 and 16–18).
- `OpticsConventionsTests.cs:238` — field points cited as "the manual's own example" without
  naming the manual page; the expected √109 is derived.
- `OpticsConventionsTests.cs:212` — 0.0254 m per inch, "exact by definition", source not named.
- `SystemDataTests.cs:181` — the TIE-19 constant 3.4785e-3 quoted without its equation number;
  `SystemDataTests.cs:180, 199` — Ciddor's limits cited to "NIST EMToolbox documentation,
  Section IV" without title or page; `SystemDataTests.cs:220–230` — spectral lines cited to
  TIE-29 Table 2.3-1 without a page (the full citation is in `SpectralLines.cs:21, 38`).
- `ThermalIndexTests.cs:211–218` — 101325 Pa cited as "TIE-19's P0" without equation or page.
- `ZernikeTests.cs:24, 355, 373` — Noll and Fringe index tables cited to the Wikipedia article
  "Zernike polynomials" with no revision or date; `ZernikeTests.cs:21–23` — Mahajan 1994 Table 1
  read from the publisher's abstract page, no page number.

**Thresholds without a stated derivation** (tolerances, so outside the rule, listed because
nothing justifies them): `ZernikeFitTests.cs:71` (condition number in 1–10), `:104`
(condition > 10), `:143` (worst > 0.05), `:193` (residual RMS > 1e-4);
`CatalogSelfCheckTests.cs:152` (more than 2000 glasses checked). The tolerances
`CatalogSelfCheckTests.cs:150, 166` and `GlassToolsTests.cs:388, 473` are explained in their
comments by measured worst cases.

Every other expected literal in the 27 files is derived in its test or cited to a source with
its table, equation, page, line or file. The new tests in `OpenSim.Tests/Optics/Gates/` derive
every expected value in their comments from the test's own inputs.

## OP-52 Jones and Mueller matrix surfaces

| Item | Gate (plan text, shortened) | Tests | Status |
|---|---|---|---|
| OP-52 Jones and Mueller matrix elements | Malus's law through two polarizers; a quarter-wave plate turns linear into circular; a Mueller depolarizer reduces the degree of polarization by its stated factor (brief: an identity matrix surface leaves the trace unchanged bit for bit) | `OpenSim.Tests/Optics/JonesMuellerSurfaceTests` (`MalusLaw_ThroughTwoPolarizers_IsCosSquared`, cos²θ polarized and ½cos²θ unpolarized at 1e-14; `QuarterWavePlateAt45_TurnsLinearIntoCircular`, \|V\|/I = 1 at 1e-15; `MuellerDepolarizer_ReducesTheDegreeOfPolarization_ByItsFactor`, degree d for linear and circular light at 1e-15; `AnIdentityJonesSurface_LeavesTheTraceUnchanged_BitForBit`; also `AUnitaryJonesMatrix_KeepsThePower_AtObliqueIncidence_AndTheFieldTransverse`, `TheMuellerMatrixOfAJonesMatrix_MatchesTheTable_AndTheTwoSurfacesAgree`) | pinned for the sequential surfaces; the non-sequential Jones and Mueller objects are not built (OP-70). Expected values are derived in the test comments, except two Mueller matrices copied from Wikipedia, "Mueller calculus". Nothing is compared with OpticStudio's output (Conventions §10 item 26). |
