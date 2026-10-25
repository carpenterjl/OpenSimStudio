# FU-16 — magnetics cross-check (MATLAB PDE Toolbox and FEMM's published examples)

Reference material behind `OpenSim.Tests/Solvers/MagneticsReferenceTests.cs`. The owner chose MATLAB R2025b and FEMM's
own published results in place of installing FEMM.

## `matlab/`: OpenSim's own models, solved independently

MATLAB 25.2 (R2025b), PDE Toolbox 25.2. `generateMesh` meshes quadratic triangles. The linear cases are solved by
femodel (static) or on toolbox-assembled matrices (time-harmonic). Post-processing is our own integration
(`mm_quad.m`, Duffy rule from `mm_trirule.m`).

- `potcore_model.m` and `er_model.m` rebuild `PlanarWindingSection.Build`'s pot core and turns and
  `ErCore.PlanarLegSection` from the OpenSim source: same box, regions and A = 0 sides.
- Case 0 (`case0_parallel_plates.m`): validates the set-up; L′ equals µ0(s + 2t/3)/W to 6e-14.
- Case 1 (`case1_potcore.m`): the static pot core at 0.1 / 0.2 / 0.4 mm gaps, five meshes (to 2.7 / 2.4 / 2.2M
  nodes), extrapolated. L = 6.3698016 / 3.6864253 / 2.2518179 µH.
- Case 2 (`case2_potcore_ac.m`): the turns as solid copper in series at 1 A peak, 0.2 mm gap, with and without the
  core. It uses the ψ = r·A_φ formulation (c = 1/(µr), a = jωσ/r, f = σV/(2πr)), one unit-voltage solve per turn,
  and the 4×4 admittance with the series current imposed.
  - The copper target size is 16.5 µm/2^(level−1). At 1 MHz (δ/4 = 16.5 µm) the largest copper edge is 22 µm on
    level 1, 11.6 µm on level 2 and 7.8 µm on level 3 (`mesh_stats_case2.csv`), so elements are under δ/4 from
    level 2 on.
  - The tests take the with-core L from the Richardson column of `convergence_summary.csv`, and every R and the
    air-case L from the finest mesh.
  - `case2_checks.m` validates the formulation. Errors are relative, on the finest mesh, and ψ errors are the
    worst node over max|ψ|:
    - solid-ring DC resistance: 1.7e-10;
    - µ-jump: ψ to 1.5e-10;
    - eddy-current cylinder: ψ inside to 6.9e-7, at its surface 4.0e-7;
    - solvepde against the assembled route: Z to 1.1e-7, nodal ψ to 7.7e-6.
- Case 3 (`case3_er_nonlinear.m`, `case3_corner_refinement.m`, `nl_newton_planar.m`): the ER leg section with no
  gap, at 5, 17.5, 50 and 550 A.
  - B–H curve: B = µ0H + (2Js/π)·atan(π(µri − 1)µ0H/(2Js)), Js = 1.6 T, µri = 2000, tabulated at H = 0 and
    10^(5k/80) A/m, k = 0…80 (`bh_table.csv`). MATLAB interpolates with pchip; swapping in OpenSim's
    Fritsch–Carlson interpolant moves λ and W by at most 1.32e-5 relative (`case3_interp_sensitivity.m`).
  - Solver: our own Newton with an exact tangent on the toolbox mesh, because femodel's nonlinear solve did not
    converge on this section. It is checked against femodel in the linear limit and against an exact 1D nonlinear
    case (`case3_checks.m`, `case3_femodel_xcheck.m`). Energy is ∫H·dB.
  - The test's λ and W are the level-4 mesh with the eight window corners' vertex size divided by 16
    (`case3_corner_refinement.m`; rows `probe_er_corner_refine … corners/16`, level 4, in
    `case3_corner_refinement.csv`). Between levels 3 and 4 they change by ≤ 6.2e-6; on the plain meshes
    (`case3_er_nonlinear.csv`) by up to 6.5e-5. `case3_outer_corners.m` probes the four outer corners the same
    way (`case3_outer_corner_probe.csv`).
- Case 4 (`case4_rings.m`): the coaxial rings in the 0.15 m box, L 6.0271882e-8 H, M 1.1121673e-8 H.
- `convergence_summary.csv` (written by `summarize.py`) has one row per case, parameter and quantity. Each row
  gives the levels run, the finest level's value and node count, the relative change between the finest two
  levels, the ratio of the last two changes and, where that ratio is above 1.2, the Richardson value. The values
  at each level are in the `case*.csv` files. The `meshA:` rows are an earlier pot-core and AC mesh family
  without the window-air refinement box (`meshA_no_window_box/`).

## `femm-examples/`: FEMM's published results (femm.info, read 2026-10-18)

The numbers come from https://www.femm.info/wiki/InductanceExample, `/ACForceExample`, `/Team28`, `/AirCore`,
`/TubeExample` and `/FAQ` (Q2: "everything is Peak, not RMS"). The models come from FEMM's own `.fem` files
(https://www.femm.info/examples/induct1a/induct1a.fem, `team28/team28a.fem`, `team28/team28k.fem`,
`tube/tube.fem`). Those files are FEMM's and are not copied here.

- `reference_values.csv`: every published number with its page and section, the independent value and the OpenSim
  call that matches it.
- `checks.py`: checks of the numbers against the models — circuit-theory L and F, the TEAM 28 coil loss
  ½·20²·R_dc = 2361.589 W, the fills, and the tube's analytic constants. The page prints the tube's c1 without the
  minus sign on its real part; the boundary conditions fix it. It also gives the tube's loss, 1.7599703 W/m, which
  the page does not print.
- `aircore_exact.py`: the exact air-core inductance, from the filament double integral over the coil section.
  Its quadratures agree to 3e-13.
- `matlab/solve_*.m` and `p1_assemble.m`: a linear-triangle solver written for FU-16 on the toolbox's mesher,
  with 1/r at centroids, which is OpenSim's own discretisation family. It finds each example's converged value and
  the box needed to stand in for FEMM's open boundary. `summarize.py` extrapolates these runs and gives the
  virtual-work check of the force. The TEAM 28 box needs to be 2 m: 0.24 m reads the force 12 % high. For the
  inductor example, FEMM's printed L of 1.73 mH and W of 0.000865042 J are 2.25 % below the converged L of
  1.769840 mH, because of FEMM's 0.05 in mesh. The tube's MATLAB values are finest-mesh values: 72-gons give
  1.7604799 W/m, still falling 0.025 % per halving, and 360-gons give 1.7601272.

## OpenSim, measured 2026-10-18 (Release runs)

The tests run the first column. The two AC-core 1 MHz rows also run the refined column.

| case | test mesh | refined | refined more | reference |
|---|---|---|---|---|
| pot core L, gap 0.1 mm | 6.34823 µH (−0.34 %) | 6.35991 (−0.16 %) | 6.36500 (−0.08 %) | 6.36980 (MATLAB) |
| pot core L, gap 0.2 mm | 3.67537 (−0.30 %) | 3.68117 (−0.14 %) | 3.68390 (−0.07 %) | 3.68643 |
| pot core L, gap 0.4 mm | 2.24605 (−0.26 %) | 2.24922 (−0.12 %) | 2.25055 (−0.06 %) | 2.25182 |
| AC core 100 kHz R | 0.104766 Ω (+0.018 %) | 0.104756 | 0.1047515 | 0.1047477 |
| AC core 1 MHz R | 0.409046 (+2.37 %) | 0.403347 (+0.94 %) | 0.401279 (+0.43 %) | 0.399571 |
| AC core 1 MHz L | 3.60580 µH (−0.33 %) | 3.61200 (−0.15 %) | 3.61489 (−0.07 %) | 3.61757 |
| ER λ, 5 A | 2.193186e-3 Wb/m (−0.31 %) | 2.196970 | 2.198746 (−0.06 %) | 2.200083e-3 |
| ER λ, 550 A | 1.014461e-2 (−0.06 %) | 1.014849 | 1.015013 (−0.01 %) | 1.015116e-2 |
| rings L | 5.99750e-8 H (−0.49 %) | 6.01290 (−0.24 %) | — | 6.02719e-8 |
| induct1a L | 1.763964 mH/in (−0.33 %) | 1.767619 | 1.768990 (−0.05 %) | 1.769840 (FEMM 1.73) |
| induct1a force | 1.112334 N/in | 1.114766 | 1.115887 | 1.116476 (FEMM 1.11347) |
| TEAM 28 force | 1.003429 N | 1.006930 | — | FEMM 1.00693, MATLAB 1.008390 |
| TEAM 28 plate loss | 13.17988 W | 13.18439 | — | FEMM 13.1981, MATLAB 13.2038 |
| air core 1 L | 0.644221 mH (−0.50 %) | 0.646617 (−0.13 %) | — | 0.6474631 (exact) |
| tube loss | 1.7607909 W/m, 72-gons (+0.047 %) | 144-gons +0.013 % | 360-gons +0.005 % | 1.7599703 (exact) |

Refined meshes:
- pot core and AC windings: 1 mm / 8.75 µm / growth 1.25, then 0.5 mm / 4.4 µm / 1.18;
- ER section: 1 mm / 50 µm / 1.2, then 0.5 mm / 25 µm / 1.15, from the test's 0.1 mm cells at the region edges;
- rings: 1.5 mm / 6.25 µm / 1.15;
- inductor example: 0.5 mm / 0.04 mm / 1.2, then 0.25 mm / 0.02 mm / 1.15;
- TEAM 28: 25 mm / 0.125 mm / 1.2;
- air core: every size halved;
- tube: 0.3 mm / 0.08 mm (the test's mesh is 0.6 / 0.15 mm), with the circles as 144-gons, then 360-gons.

Three-level Richardson values of the pot-core inductances land within 0.014 % of MATLAB (−0.014 / −0.003 / −0.012 %).
The inductor example's land −0.0015 % (L) and +0.033 % (force) off the converged values.

Finding: Build's default mesh (2 mm far cells, growth 1.35) reads the 1 MHz solid-turn resistance 2.4 % high. At
δ = 66 µm the gap's fringing field needs a finer mesh than the default.

## 2026-10-25 follow-up: the skin-depth default mesh

The finding above (Build's default mesh reads the 1 MHz solid-turn R 2.4 % high) is acted on
(owner's decision 2026-10-25): `PlanarWindingSection.Build` given the frequency puts copper cells
of δ/8 at the turn edges with grid lines at δ and 2δ inside each turn wider or thicker than 4δ,
once δ/8 is under the default half-thickness (1 MHz: δ = 66 µm, δ/8 = 8.3 µm < 17.5 µm; 100 kHz:
26 µm, untouched). Release measurements on the pot core with 0.2 mm gap, 1 MHz, 4 solid rings
(MATLAB R 0.399571209 Ω; the Richardson value is +0.13 % of it):

| mesh (max / min / growth, lines) | R vs MATLAB | vs Richardson | L vs MATLAB | nodes | s (Release) |
|---|---|---|---|---|---|
| default 2 mm / 17.5 µm / 1.35 | +2.371 % | +2.24 % | −0.325 % | 29 760 | 3.6 |
| 1 mm / 17.5 µm / 1.35 (far cells only) | +2.371 % | +2.24 % | −0.325 % | 37 698 | 17.5 |
| 2 mm / 8.75 µm / 1.35 | +1.581 % | +1.45 % | −0.243 % | 43 188 | 7.8 |
| 2 mm / 17.5 µm / 1.25 | +1.697 % | +1.57 % | −0.246 % | 42 273 | 7.5 |
| default + δ, 2δ lines | +1.687 % | +1.56 % | −0.317 % | 35 960 | 4.4 |
| 2 mm / 8.25 µm / 1.35 + δ, 2δ lines (**the new default with the frequency**) | +0.963 % | +0.83 % | −0.239 % | 56 547 | 16.0 |
| 2 mm / 8.25 µm / 1.25 | +0.931 % | +0.80 % | −0.149 % | 66 965 | 25.8 |
| 1 mm / 8.75 µm / 1.25 (FU-16's "refined") | +0.945 % | +0.82 % | −0.154 % | 75 335 | 27.2 |
| 2 mm / 8.25 µm / 1.25 + δ, 2δ lines | +0.597 % | +0.47 % | −0.145 % | 86 033 | 53.8 |

The far cells change nothing; the copper cells do. In air at 1 MHz the same rule reads R +0.17 %
and L −0.67 % (was +0.49 / −0.75 %) at 25 048 nodes. `MagneticsReferenceTests.SolidTurns…` has
three more rows (Build told the frequency) with the bands 0 ≤ dr ≤ 1.13 % and dl ≥ −0.3 % (core),
dr ≤ 0.4 % (air), and the 100 kHz grid asserted identical to the frequency-less one.
