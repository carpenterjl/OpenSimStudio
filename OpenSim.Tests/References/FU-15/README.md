# FU-15 — Sheen's low-pass filter and line-fed patch

Reference material behind `OpenSim.Tests/Rf/SheenCircuitsTests.cs`.

## Source

- D. M. Sheen, "Numerical modeling of microstrip circuits and antennas", PhD thesis, MIT, 1991. The scan is not
  redistributable and is not kept here. The local copy is `reports/sources/Sheen-1991-thesis.pdf` (gitignored;
  12,870,640 bytes, MD5 3177D38BA02FA7E273F4CC090379406E, `sheen-thesis/verification_summary.json`). Printed page
  numbers equal PDF page indices in this scan (`sheen-thesis/geometry_verified.json`, key `thesis`).
- Only digitised points with figure and page citations, our own scripts and short paraphrases are kept. No page
  renders, crops or extracted text.
- The patch's measurement is Deshpande and Bailey, "Input impedance of microstrip antennas", IEEE Trans. AP-30(4),
  pp. 645–650, July 1982 (thesis ref. [22], p. 233). The filter follows D'Inzeo, Giannini and Sorrentino, Electronics
  Letters 15(9), pp. 258–260, 1979 (ref. [25], p. 108).

## The two circuits (`sheen-thesis/geometry_verified.json`)

Low-pass filter, Sec. 3.4, pp. 108–116:
- A 20.32 × 2.54 mm patch on εr 2.2, h 0.794 mm (Fig. 3.25, p. 108).
- Two 2.413 mm lines on opposite long sides. The near edge of each is 5.65 mm from the nearer patch end, so the line
  centres sit 6.8565 mm from the ends and 6.607 mm apart (Fig. 3.25; the TL model of Fig. 3.26, p. 109, uses 6.857 and
  6.607 mm).
- Not stated: loss tangent, metal, metal thickness, feed-line length, board size.
- FD-TD (p. 112): cells 0.3948 × 0.4232 × 0.265 mm, 70 × 45 × 16 of them; reference planes 10 Δy = 4.232 mm from the
  patch's long sides; zero-thickness perfect conductors and no loss (pp. 76, 136). The stated counts do not add up
  (50 Δx = 19.74 mm, not 20.32; 45 cells in y cannot hold the 50 the text describes). See `INCONSISTENCIES`.
- Curves: |S11| in Fig. 3.33 (p. 115) and |S21| in Fig. 3.34 (p. 116), FD-TD solid and measured dash-dot, 0–20 GHz.
  Figs. 3.27–3.30 (pp. 110–111) plot the same measured data against the TL and planar models.

Line-fed patch, Sec. 4.3, pp. 158–163:
- 40.2 × 40.2 mm on εr 2.55, h 1.59 mm, fed by a 4.48 mm line ("nominally 50 ohm") at the centre of one edge, no inset
  (p. 158, Fig. 4.11 p. 159).
- Not stated: feed length, ground size, loss, metal.
- FD-TD: 1.11667 mm cells in x and y (36 across the patch), 0.53 mm in z, 3:1 coarser cells around the antenna in x and
  y only; S11 reference plane 16.75 mm (15 cells) from the patch; Zin referred to the patch's front edge (pp. 158–160).
- Fig. 4.14 (p. 162): R and X, FD-TD curves and measured markers at 2.10 to 2.40 GHz in 0.05 GHz steps. Fig. 4.15
  (p. 163): the same on a Smith chart.
- The thesis puts the resonance at about 2.275 GHz (p. 158) and the FD-TD's shift at about 0.4 % (p. 160).

## What the measurements include (`geometry_verified.json`, `features_verified.json`)

- Filter: circuits and measurements by M. Abouzahra (p. 5). The analyzer model is not stated. Analyzer and connectors
  are rated to 18 GHz (p. 136).
- The microstrip-to-coax transitions were not de-embedded (p. 136). Calibration and measurement reference planes are
  not stated. The measured traces start near 1.98 GHz.
- The measured board loses power: |S11|² + |S21|² from the measured curves is 0.935–0.976 at 2–4 GHz. The FD-TD's is
  1.044–1.072 at 2–4.5 GHz, above 1 for a lossless model.
- Patch: instrument, connectors and calibration are not stated in the thesis. The measured values are plotted at the
  patch edge with the FD-TD's. Metal loss and ground size are unknown.

## `sheen-thesis/`: the digitisation

Two passes on 5088-px page renders made with `render_winrt.ps1` (Windows.Data.Pdf). The renders are not kept: render
the pages from the local PDF into `pages/` to re-run any script.

- First pass. `digi.py` calibrates the axes from the tick marks (`work/fig*_calib.json`) and traces the curves. The
  configs are `cfg_fig*.json`; `mkcfg.py` wrote those for Figs. 3.27–3.30.
  - `markers.py` reads the Fig. 4.14 markers (`mk_fig4_14.json` → `work/fig4_14_markers.json`).
  - `smith.py` reads Fig. 4.15 (`mk_fig4_15.json` → `work/fig4_15_smith.json`).
  - `export.py` writes the CSVs, `make_geometry.py` the geometry and `power_check.py` the power balance.
- Independent re-read, `verify/`. Its scripts take the data folder as an argument and expect the CSVs next to `work/`,
  as here. The re-read worked in a folder named `sheen-thesis-verify`, the name `geometry_verified.json`,
  `verification_summary.json` and `verify/geometry_v.py` still carry; its outputs are now the `_verified` files here
  and its scripts are in `verify/`.
  - Own axis calibration: `calib.py`, `work/cfgNNN.json` → `work/calNNN.json`.
  - Own checkpoints: `checkpoints.py` → `my_checkpoints_fig3_33_34.csv`. For Figs. 4.14 and 4.15, `mk.py`, `mk2.py`,
    `smith_v.py` → `work/smith_v.json`, then `fig4_checks.py` → `my_checkpoints_fig4_14_4_15.csv`.
  - Re-digitised stretches: `redigit.py` → `work/redig333_hi.csv`, `work/redig334_pb.csv`.
  - `make_verified.py` merges them into the `_verified` CSVs (log in `work/make_verified_log.json`). `features_v.py`
    writes `features_verified.json` and `geometry_v.py` writes `geometry_verified.json`.
  - Re-running `features_v.py` on the files here reproduces `features_verified.json` exactly.

Axis-fit residuals (`verification_summary.json`):
- Fig. 3.33: 0.0107 GHz rms (38 ticks), 0.033 dB rms (9 ticks).
- Fig. 3.34: 0.0141 GHz, 0.054 dB.
- Fig. 4.14: 0.0002 GHz, 0.38 Ω rms (max 1.04 Ω).
- Fig. 4.15: outer circle 1085.40 px, rms 3.21 px; the r-circles cross the real axis at Γ = −0.499, +0.001 and +0.504.

Corrections the re-read made (`work/make_verified_log.json`, `features_verified.json`):
- Fig. 3.34, measured, 1.95–5.52 GHz. The first pass had followed the FD-TD line there. Its 107 rows were replaced by
  104 re-read ones, with the dash gaps left empty. The first pass's "+0.2 dB measured peak at 4.83 GHz" belongs to the
  FD-TD line. The measured dash at 4.71–5.13 GHz is at −0.43 dB.
- Fig. 3.33: measured rows at 18.88–20 GHz were replaced (31 by 24). FD-TD rows below 0.19 GHz were replaced by the
  null tip, 0.130 GHz and −33.26 dB.
- Fig. 4.14: measured R at 2.20 GHz went from 19.284 to 21.92 Ω.
- Elsewhere the two readings agree within 0.147 dB (measured |S11| at 15.85 GHz), 0.114 dB at null tips and 0.0203 GHz
  at level crossings (`my_checkpoints_fig3_33_34.csv`). The Fig. 4.14 markers agree within 0.24 Ω, apart from the
  corrected one, and the Fig. 4.15 Γ within 0.0006 (`my_checkpoints_fig4_14_4_15.csv`). In both files the "theirs"
  column is the first pass, which is not kept.

Data files:
- `fig3_33_S11_dB_verified.csv`, `fig3_34_S21_dB_verified.csv`, `fig4_14_Zin_verified.csv`. The columns are frequency,
  value, quantity, curve (measured / fdtd), figure, pages and the reading uncertainties u_freq and u_value.
  - Where the measured line is hidden under the FD-TD line, 20 measured points come from Figs. 3.27–3.29 and are
    labelled with that figure.
  - Measured null depths are lower bounds (dash-dot ink).
  - `fig4_14_Zin_verified.csv` also carries the FD-TD's 50 Ω |S11| derived from its R and X.
  - `features_verified.json` puts the measured |S11| null near 10.8 GHz at 10.84 GHz, −40.71 dB. That is the
    verifier's reading of the ink tip, written into `features_v.py`. The CSV's deepest point is 10.80216 GHz,
    −40.7148 dB, and the parabola the test uses gives 10.802 GHz (`opensim/analysis.txt`).
- `fig4_15_smith_measured.csv`: Γ and the 50 Ω R and X of the seven measured points. This is the first pass, which the
  re-read confirmed.

## `matlab-rfpcb/`: solver-to-solver cross-check

- RF PCB Toolbox MoM (`pcbComponent`, SolverType 'MoM'), MATLAB R2025b (the release FU-16 used). The
  structures are built by `build_filter.m`, `build_patch.m` and `build_thru.m` from `geometry_verified.json`, with PEC
  of zero thickness, a lossless substrate and a finite board.
  - Filter: edge feeds at the board edge, placed at the FD-TD reference planes (4.232 mm). The board extends 3.948 mm
    beyond the patch ends (`gx` 3.948 in the run tags).
  - Patch: an edge feed 16.75 mm from the patch, and 6 mm of ground beyond the patch on the other three sides (`g6`).
    The 16.75 and 33.5 mm margins are sensitivity runs.
- Run order: `queue.ps1` lists the MATLAB calls, except the first 1.0 mm filter sweeps (`F_m100_gx3948`,
  `F_m100_gx10_b1/b2`) and the stand-alone scripts run directly: `diag_line.m`, `diag_thru.m`, `txline_check.m`
  (→ `results/txline_check.txt`), `t_meshstats.m`, `write_touchstone_direct.m` and the plot scripts. Its wrapper
  `run_ml.ps1` holds a local install path and is not kept. `make_all.ps1` runs the Python post-processing.
  `run_manifest.csv` gives each run's mesh, triangles, frequencies and solve time. The 1.3 mm patch runs
  (`D_P_m130_*`) solved no frequency.
- The full sweeps (`results/F_*`, `P_*` and `T_*` as .csv/.s2p/.s1p/.mat) are not kept. `analyze_filter.py` and
  `analyze_patch.py` computed `results/filter_features.json` and `patch_features.json` from them. Kept: the
  de-embedded patch impedances (`results/patch_m*_Zedge.csv`) and the direct solves (`results/D_F_*.csv`,
  `D_P_*.csv`).
- `analyze_filter.py` also computes the thesis-curve features the test uses (`results/filter_features.json`, curves
  measured and fdtd).

MATLAB is not mesh-converged (`results/final_filter_mesh_convergence.csv`, `comparison_console.txt`):
- Filter, edges 1.0 / 0.7 / 0.6 / 0.5 mm:
  - −3 dB corner: 5.4949 / 5.5215 / 5.5493 / 5.5707 GHz (the 0.6 mm value extrapolated 9 MHz past the last direct
    solve, at 5.54 GHz).
  - |S11| null near 5 GHz: 4.8893 → 4.9416 GHz.
  - Second |S21| minimum: 8.0613 → 8.1659 GHz.
  - |S11| null near 11 GHz: 10.8629 → 11.1148 GHz.
- The feed line's εeff at 5 GHz goes from 1.96577 to 1.91248 between 1.0 and 0.4 mm edges (`results/diag_line_filter.csv`).
  The closed form gives 1.8833 (`results/txline_check.txt`).
- Patch, 16.75 mm feed and 6 mm margin, edges 4.0 / 3.0 / 2.5 / 2.0 / 1.6 mm
  (`results/final_patch_mesh_convergence.csv`):
  - f0 by RLC fit: 2.24999 / 2.2188 / 2.19005 / 2.19542 / 2.20631 GHz.
  - R0 244.8–289.2 Ω and Q 50.7–52.2.
  - The feed's εeff is 2.14658–2.32033 and not monotonic (`results/diag_thru_BLx20.csv`), against 2.1303 closed form.
- Use it as a rough check only. At 0.5 mm the filter's corner is +0.36 % from measured and −0.53 % from the FD-TD. At
  1.6 mm the patch reads R0 289.2 Ω and Q 51.6, against the measured fit's 411.7 Ω and 70.8 (`comparison_console.txt`).

## `opensim/`: OpenSim's layered MoM, measured 2026-10-18

- The runs are temporary exploration theories, `Explore_Filter`, `Explore_Patch` and `Explore_Coarse`, which are not in
  the committed test file. Release build. `Explore_Coarse`'s lines were appended to `mesh_study.txt` by hand (section
  "coarse"); `extract_logs.py` covers the others.
- `extract_logs.py` turned their console output into `filter_sweep.csv`, `patch_variants.csv` and `mesh_study.txt`.
  The raw logs are mostly build output with local paths and are not kept.
- `fu15_analyze.py` writes `analysis.txt`, the source of the 0.6 mm sweep, mesh-study and patch numbers below. The
  0.8 mm numbers are from `mesh_study.txt`, section "coarse". The gate values are from the Debug test runs' console
  output, which is not kept; the 0.6 mm value at 4.95–5.05 GHz, 4.9996 GHz, is from the first of them.

### Filter, the 0.6 mm sweep

The sweep (`filter_sweep.csv`, 34 frequencies, 2.5–11.3 GHz) that located the features. The test itself runs on 0.8 mm
cells (next section), which give the same features to 0.06 %.
- 0.6 mm cells graded from 0.15 mm, growth 1.4.
- Ports 2 mm beyond the reference planes, with 2.5 mm stubs.
- Calibration lines of 6 and 12 mm.
- De-embedded to the FD-TD reference planes in the line's own Z0.

| feature (rule) | measured | FD-TD | OpenSim | vs measured | vs FD-TD |
|---|---|---|---|---|---|
| −3 dB corner of \|S21\| (5.55–5.60 GHz) | 5.5509 GHz | 5.6003 | 5.5854 | +0.62 % | −0.27 % |
| \|S11\| null near 5 GHz (quadratic, 4.9/5.0/5.1) | 4.9769 (−28.40 dB) | 5.0044 (−29.84) | 5.0010 (−31.5) | +0.48 % | −0.07 % |
| \|S21\| null near 7 GHz (chord 7.0–7.1) | 6.9393 (−38.73) | — | 7.0445 (−34.5) | +1.52 % | — |
| \|S21\| null near 8 GHz (chord 8.2–8.3) | 7.9510 (−35.37) | 8.0117 | 8.2481 (−38.3) | +3.74 % | +2.95 % |
| \|S11\| null near 11 GHz (chord 10.9–11.0) | 10.8020 (−40.71) | 10.9928 (−19.03) | 10.9219 (−22.6) | +1.11 % | −0.64 % |

- Thesis depths are the deepest digitised point near each null (`sheen-thesis/*_verified.csv`). The three-point
  parabola that `analyze_filter.py` fits (and `matlab-rfpcb/comparison_console.txt` reports) runs through interleaved
  points of different strokes and reads 0.08 to 0.63 dB deeper than the ink: −28.48, −30.47 and −40.97 dB.

- How the thesis features were taken (`matlab-rfpcb/comparison_summary.csv`, notes):
  - The measured |S11| null near 5 GHz is a Fig. 3.27 point. In Fig. 3.33 the measured line runs under the FD-TD line
    there.
  - The FD-TD has no |S21| minimum near 7 GHz, only a shoulder near 7.3 GHz.
  - The FD-TD's |S21| null near 8 GHz is off the plot. Its frequency is the centre of the −40 dB crossings, 7.7680 and
    8.2554 GHz.
- In the line's own Z0, the chord (the test's rule) and the quadratic agree within 6.4 MHz on every null. The test's
  pair for the 5 GHz null (4.95/5.05 GHz) is not in the sweep. The 50 Ω values are listed too: corner 5.6010 GHz.
- The calibrated line reads εeff 1.8694 at 3 GHz against the 2D solve's 1.8847 (−0.81 %).

Mesh study at 5.55 and 10.8 GHz, calibration lines 6 and 12 mm (last row: 6.4 and 12.8 mm):

| cells (largest/smallest/growth) | circuit unknowns | circuit solve, 5.55 GHz | \|S21\| 5.55 GHz, own Z0 / 50 Ω | \|S11\| 10.8 GHz, own Z0 / 50 Ω | cal εeff 5.55 GHz (2D 1.8840) |
|---|---|---|---|---|---|
| 0.8 / 0.2 / 1.5 | 1252 | 10.5 s | −2.69 / −2.93 dB | −17.94 / −17.68 dB | 1.9643 (+4.26 %) |
| 0.6 / 0.15 / 1.4 | 2223 | 28.6 s | −2.61 / −2.46 | −18.40 / −18.10 | 1.8977 (+0.73 %) |
| 0.4 / 0.1 / 1.3 | 4373 | 113.2 s | −2.59 / −2.46 | −18.74 / −18.39 | 1.8995 (+0.82 %) |
| 0.8 / 0.2 / 1.4, test lengths | — | — | −2.60 / — | −18.39 / — | 1.8961 (+0.64 %) |

- The last row is from `Explore_Coarse` (`mesh_study.txt`, section "coarse"): ports 2.4 mm beyond the reference planes,
  2.4 mm stubs, calibration lines 6.4 and 12.8 mm. Its unknown counts and times were not logged. At 3 GHz its
  calibration reads εeff 1.8678 against the 2D solve's 1.8847 (−0.90 %).
- Calibration finding: some length choices make the calibration drift, for a cause not isolated.
  - The 0.8 / 0.2 / 1.5 row used the 0.6 mm configuration's lengths (2 mm shift, 2.5 mm stubs, 6 and 12 mm lines), and
    its calibration read εeff 4.26 % high at 5.55 GHz. With the test's lengths on the same cells it reads 1.8985
    (+0.77 %).
  - On the 0.4 mm mesh, an 11 mm calibration line (explore1) gave εeff 1.7388 at 3 GHz against the 2D solve's 1.8847
    (−7.74 %). At 5.55 GHz it gave 1.8584, where the 12 mm line gives 1.8995 (explore2). The circuit's own solve did
    not change (raw −0.25 / −13.32 dB at 5.55 GHz in both runs); only the calibration did.
  - It is not a whole-number-of-cells effect. `SheetMetalAssembly` grades every interval from the smallest edge at
    both ends and stretches the steps to fit, so no length is a whole number of cells; on 0.8 / 0.2 / 1.4 cells the 6
    and 12 mm lines even get identical cells next to their ports (stretch 0.9857), and the 6.4 and 12.8 mm lines
    slightly different ones (0.9736 and 0.9866).
  - The test uses 6.4 and 12.8 mm lines, 2.4 mm stubs and a 2.4 mm shift on 0.8 / 0.2 / 1.4 cells.

### Patch

The two-point rule is the test's (B = 0 between 2.25 and 2.30 GHz, R0 = 1/G there, Q = (f0/2)·R0·dB/df).

The thesis, by the same rule:
- Measured markers (`fig4_14_Zin_verified.csv`): f0 2.27472 GHz, R0 412.6 Ω, Q 71.9.
  - A five-parameter fit to all seven markers gives 2.27489 / 411.7 / 70.8.
  - The verifier's re-read gives the same 412.6 Ω and 71.9.
  - The Fig. 4.15 Smith points give 2.27502 / 406.2 / 69.1 by the fit.
- FD-TD curve: f0 2.28558 GHz, R0 397.0 Ω, Q 77.6.

| cells (mm) | stub | port from patch | unknowns | f0 [GHz] | R0 [Ω] | Q | vs measured f0 / R0 / Q | vs FD-TD f0 / R0 / Q |
|---|---|---|---|---|---|---|---|---|
| 2.5 / 0.6 / 1.4 | 23 mm | 16.75 mm (test) | 1909 | 2.26568 | 385.4 | 73.6 | −0.40 / −6.6 / +2.3 % | −0.87 / −2.9 / −5.1 % |
| 2.5 / 0.6 / 1.4 | 23 | 10 | 1867 | 2.26533 | 364.7 | 69.4 | −0.41 / −11.6 / −3.6 | −0.89 / −8.1 / −10.6 |
| 2.5 / 0.6 / 1.4 | 12 | 10 | 1811 | 2.26483 | 351.0 | 66.9 | −0.43 / −14.9 / −7.0 | −0.91 / −11.6 / −13.7 |
| 2.5 / 0.6 / 1.4 | 5 | 10 | 1755 | 2.26544 | 347.1 | 65.5 | −0.41 / −15.9 / −8.9 | −0.88 / −12.6 / −15.5 |
| 2.0 / 0.5 / 1.35 | 23 | 10 | 2673 | 2.26432 | 366.1 | 69.6 | −0.46 / −11.3 / −3.3 | −0.93 / −7.8 / −10.3 |
| 2.0 / 0.5 / 1.35 | 5 | 10 | 2503 | 2.26163 | 360.1 | 66.3 | −0.58 / −12.7 / −7.8 | −1.05 / −9.3 / −14.5 |
| 1.5 / 0.4 / 1.3 | 5 | 10 | 3917 | 2.26435 | 337.5 | 65.7 | −0.46 / −18.2 / −8.6 | −0.93 / −15.0 / −15.2 |

- f0 hardly moves:
  - 2.2648–2.2657 GHz over the four 2.5 mm runs (ports 10 and 16.75 mm, stubs 5, 12 and 23 mm).
  - 2.2616–2.2654 GHz over 2.5, 2.0 and 1.5 mm cells with the 5 mm stub.
- R0 and Q depend on the port and stub: 347.1–385.4 Ω and Q 65.5–73.6 over the 2.5 mm runs. With the 23 mm stub, 2.0
  against 2.5 mm cells moves them only 0.38 % and 0.30 %, and f0 by −0.045 %.
- With the 5 mm stub the calibration depends on the mesh (`patch_variants.csv`). At 2.25 GHz the 2.0 mm mesh reads
  εeff 2.0438 and Z0 53.82 Ω against the 2D solve's 2.1271 (`mesh_study.txt`) and 49.81 Ω. With the 23 mm stub it reads
  2.1287 and 50.57 Ω.
- Five-parameter fits to the seven measured frequencies (stub 5, port 10) give nearly the same f0 and R0 as the
  two-point rule, and a lower Q:
  - 2.5 mm: 2.26569 GHz / 346.7 Ω / 63.8, rms 0.81 Ω.
  - 2.0 mm: 2.26178 / 359.6 / 63.8.

### Filter, the test's 0.8 mm configuration

`Explore_Coarse` ran the test's five features on 0.8 mm cells at growth 1.4 and 1.5 (`mesh_study.txt`, "coarse"):

| feature (two-point rule) | 0.6 mm sweep, chord | 0.8 mm, growth 1.4 (test) | 0.8 mm, growth 1.5 |
|---|---|---|---|
| −3 dB corner (5.55–5.60 GHz) | 5.5854 | 5.5867 (+0.02 %) | 5.5924 (+0.13 %) |
| \|S11\| null (4.95–5.05) | 4.9996 (gate run) | 4.9999 (+0.01 %) | 4.9998 |
| \|S21\| null (7.0–7.1) | 7.0445 | 7.0447 (+0.00 %) | 7.0468 (+0.03 %) |
| \|S21\| null (8.2–8.3) | 8.2481 | 8.2434 (−0.06 %) | 8.2415 (−0.08 %) |
| \|S11\| null (10.9–11.0) | 10.9219 | 10.9213 (−0.01 %) | 10.9057 (−0.15 %) |

The 4.95–5.05 GHz pair is not in the 0.6 mm sweep; its 0.6 mm value is from the first Debug gate run (below).

## Gates (measured 2026-10-18/19, Debug)

`SheenCircuitsTests`, 6/6 passed in 19.5 min (filter features about 3 min each, the patch 4 min), and again in 18.8 min
with the depth bounds and the 1.3 % FD-TD f0 band added after review (the same values). The first gate run, on
0.6 mm filter cells with 2 mm shift, 2.5 mm stubs and 6/12 mm lines, also passed 6/6 (36 min, sharing the CPU with
another run) and gave 5.5854 / 4.9996 / 7.0445 / 8.2481 / 10.9219 GHz.

`TheLowPassFilter_HasTheMeasuredFeatures` (0.8 mm cells; bands are on the relative deviation; depth bound = the gate
run's depth less 5 dB):

| feature (bracket) | OpenSim | depth bound | measured | vs measured (band) | FD-TD | vs FD-TD (band) |
|---|---|---|---|---|---|---|
| −3 dB corner of \|S21\| (5.55–5.60) | 5.5867 GHz | — | 5.551 | +0.64 % (±1 %) | 5.600 | −0.24 % (±0.5 %) |
| \|S11\| null near 5 GHz (4.95–5.05) | 4.9999 (−31.5 dB) | −26.5 dB | 4.977 | +0.46 % (±1 %) | 5.004 | −0.08 % (±0.5 %) |
| \|S21\| null near 7 GHz (7.0–7.1) | 7.0447 (−34.6 dB) | −29.6 dB | 6.939 | +1.52 % (±2.5 %) | — | — |
| \|S21\| null near 8 GHz (8.2–8.3) | 8.2434 (−38.7 dB) | −33.7 dB | 7.951 | +3.68 % (±5 %) | 8.012 | +2.89 % (±4 %) |
| \|S11\| null near 11 GHz (10.9–11.0) | 10.9213 (−22.4 dB) | −17.4 dB | 10.802 | +1.10 % (±2 %) | 10.993 | −0.65 % (±1 %) |

- What binds: the feature must lie inside its bracket (the corner's two |S21| values must straddle −3 dB; a null's
  closest approach must fall between the two frequencies), and a null must be deeper than its bound. The brackets came
  from OpenSim's own sweep and are narrower than most thesis bands, so a thesis band binds only for the 5 GHz null (both)
  and for the corner against the FD-TD. The other thesis bands state the agreement.
- Every filter solve also checks reciprocity (< 1e-9) and passivity (largest singular value ≤ 1 + 1e-6).

`TheLineFedPatch_ResonatesWhereItWasMeasured` (2.5 mm cells, 23 mm stub, port 16.75 mm):

| | OpenSim | measured (markers) | vs measured (band) | FD-TD (curve) | vs FD-TD (band) |
|---|---|---|---|---|---|
| f0 | 2.26568 GHz | 2.27472 | −0.40 % (±1 %) | 2.28558 | −0.87 % (±1.3 %) |
| R0 | 385.4 Ω | 412.6 | −6.6 % (±12 %) | 397.0 | −2.9 % (±12 %) |
| Q | 73.6 | 71.9 | +2.3 % (±12 %) | 77.6 | −5.1 % (±12 %) |

The test also checks the two reference values its rule gives (2.2747 and 2.2856 GHz, to 1e-4).

## Not kept

- The thesis PDF, page renders, crops, overlays and extracted text.
- The first pass's per-figure CSVs, `features.json` and `geometry.json`, which the `_verified` files supersede.
- The multi-MB curve-tracking JSONs (`fig*_track.json`).
- `all_digitised.csv` and `all_digitised_verified.csv`. Each is an exact row-for-row union of its pass's per-figure
  CSVs plus `fig4_15_smith_measured.csv` (checked).
- MATLAB's .mat files, logs, full sweeps and Touchstone files, its plots, and `run_ml.ps1`.
- `make_all.ps1`'s console files `filter_features_console.txt`, `filter_mesh_points_console.txt` and
  `patch_features_console.txt` (only `convergence_console.txt` is kept), and `digi.py`'s `fig*_shortcomps.json`.

### TC-1 (2026-10-25): cause isolated

Data: `opensim/calibration_drift_2026-10-25.txt` (every pair of nine line lengths on three meshes at
3 and 5.55 GHz, each line's raw S, and the cells the mesher laid next to each port).

The two lines are not the same line twice. Two mechanisms, both in the mesh, both amplified by the
calibration:

1. **The port neighbourhoods differ.** `SheetMetalAssembly` grades each grid interval by marching
   from its start with cells of min(hMax, hMin + (growth - 1) x distance to the nearer end) and then
   stretches every step to land on the far end - so the cell next to the far end of an interval is
   whatever remained, not hMin. On 0.8/0.2/1.4 cells the line-side cell at the far port is 212-215 um
   for 6.4, 8, 12, 12.8 and 16 mm lines, 271-274 um for 6, 10 and 18 mm, 314 um for 11 mm (the near
   port's is 195-199 um for all). A gap port's error box depends on its adjacent cells, and the
   method assumes both lines carry the same boxes. Pairs within one cluster calibrate together
   (within 0.6-0.8 % of the 2D solve at 5.55 GHz); pairs across clusters drift by up to +-36 %, the
   more the shorter dl (as 1/(beta dl)) and the more reflective the ports (|S11| 0.95 at 5.55 GHz,
   0.99 at 3 GHz, where the amplification is worst). On 0.4/0.1/1.3 cells every length but 11 mm
   lands 107-108 um at the far port; 11 mm lands 129 um - hence the 11 mm anomaly (-7.7 % at 3 GHz).
2. **The interior cells differ.** The ramps meet at a length-dependent point and the stretch factor
   differs, so the discrete line's own gamma differs slightly between the two lengths; the extraction
   gamma = (gamma_L L - gamma_l l)/dl magnifies that by L/dl. On the coarse 0.8/0.2/1.5 cells, 12 and
   12.8 mm have identical port cells (291.2 / 291.3 um) yet read +17.9 % (L/dl = 16); 12 and 16 mm
   +4.1 %; 12 and 18 mm -0.3 %. On the 0.4 mesh this term is under 0.1 % for L/dl <= 2.

Why the gate's choice works: on 0.8/0.2/1.4 cells 6.4 and 12.8 mm are in the same far-port cluster
(212 / 215 um) with L/dl = 2 - +0.64 % at 5.55 GHz; the earlier 6 + 12 mm choice crossed clusters
(271 / 214 um) - +3.4 %. Not a whole-number-of-cells effect, as noted before; a far-end-cell effect.

Remedies (owner's decision pending, see the follow-up plan): make the interval grading symmetric
(hMin cells at both ends, the remainder absorbed in the middle) so every length carries the same
port neighbourhood; and/or a usage rule with a guard in the calibration - the two lines' port cells
equal, long/short <= 3, beta dl >= 0.5 rad at the lowest frequency, cells along the line <= lambda/40.

### TC-1 remedy (2026-10-25, owner's choice: symmetric grading plus a calibration rule)

`SheetMetalAssembly` now grades every interval the same at both ends: the first three cells at
each end are exactly the ramp's (hMin, then hMin + (growth - 1) x distance), the middle is filled
with cells of about the ramp's next size (never wider than the maximum), and only the cells between
the exact ends are stretched to land on the far end - so two lines of different length carry the
same cells beside their ports (`LineCalibrationMeshTests` checks nine lengths on three meshes).
`LineFromTwoLengths.Calibrate` reports in `LineCalibration.Warnings` a length ratio over 3 or a
beta x dl under 0.5 rad (measured beta); it never refuses (a corner extraction chooses pi/8).

On the gate's cells at 5.55 GHz, pairs of 6 / 10 / 14 / 18 mm lines now read eps_eff +0.70 to
+0.99 % of the 2D solve (spread 0.3 %); before, pairs of 6 / 10 / 12 / 18 mm read -1 to +8.7 %.

The gate re-run in Release with the new grading (all six pass): corner 5.5831 GHz (+0.58 %
measured, -0.30 % FD-TD; was 5.5867), |S11| nulls 4.9971 / 10.9254 GHz (+0.40 / +1.14 %; were
4.9999 / 10.9213), |S21| nulls 7.0463 / 8.2447 GHz (+1.55 / +3.69 %; were 7.0447 / 8.2434); patch
f0 2.26534 GHz, R0 385.5 ohm, Q 73.7 (were 2.26568 / 385.4 / 73.6). The strip now has 7 / 7 / 12
cells across on the 0.8/0.2/1.4, 0.8/0.2/1.5 and 0.4/0.1/1.3 meshes (were 7 / 6 / 11).
