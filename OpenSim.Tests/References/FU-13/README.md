# FU-13 — PEEC extractor against FastHenry, a 2D FEM and measured strip R_ac

This folder holds the reference material behind `OpenSim.Tests/Pcb/PeecReferenceTests.cs`. The owner chose
three references: FastHenry built here from source, a MATLAB FEM, and Haefner's 1937 measurements.

## `fasthenry/` — FastHenry 3.0.1 on OpenSim's own grids

**Source.** FastHenry 3.0.1 (28 May 2012), M.I.T.'s FastHenry 3.0 with FastFieldSolvers' fixes.
- Downloaded with approval from https://codeload.github.com/ediloren/FastHenry2/zip/refs/heads/master
  (1,255,351 bytes, SHA-256 1017FE2F1668A50A97952B87AC6E972ADD4E7A0F7631430A6474052895790A3A).
- FastHenry's terms are M.I.T.'s 1992/1994 notice (`src/fasthenry/mulGlobal.h`, `src/fasthenry/induct.h`). It
  allows use, copying and modification for internal, noncommercial purposes only. Distributing the program or
  any part of it is prohibited without M.I.T.'s written consent. (The `sparse/` solver library built into it
  carries Kenneth Kundert and the University of California's own notice.)
- FastHenry was only built and run locally to produce reference numbers. Neither its source nor the binary is
  kept here; only our own decks, scripts and FastHenry's outputs are.

**Build.** Built with MSVC 19.44 x64 (`scripts/build.bat`, `cl /O2 /fp:precise`). The changes are all
portability; no numerical code was touched:
- `parse_command_line.c`: in `fix_and_print_opts`, `clock` is declared `time_t` instead of `long` (on Win64
  `long` is 32 bits and `time()` writes 64).
- `resusage.h`: a `WINTIMER` branch beside the `FOUR` and `FIVE` timer branches. It defines `NOTOTHER`, its
  `starttimer`/`stoptimer` read the process's user CPU time from `fh_user_cpu_seconds()`, and its `DUMPRSS` is
  empty.
- These two in-place edits are described here, not kept as a diff, because the notice does not allow
  distributing any part of FastHenry's source.
- `patches.diff` adds two new files of our own: `win_compat.c` (`fh_user_cpu_seconds()` from
  `GetProcessTimes`, and an `sbrk()` stub that returns 0; FastHenry uses `sbrk`'s value only in its
  memory-usage messages) and `win_compat.h` (`strncasecmp` and `finite` mapped to MSVC's `_strnicmp` and
  `_finite`, force-included with `/FI`).
- `build.bat` defines `WINTIMER`, which selects that timer branch, and `NO_GETHOSTNAME`, which selects
  FastHenry's existing branch that does not read the host name.

**Validation of the build.**
- The distribution's own `pin-con7.inp` (not kept here), run with FastHenry's default options, reproduces its
  shipped `Zc_pin_con7.mat` in every printed digit (entry `pin-con7_default` in
  `validation/examples_results.json`). With the direct solve (`-s ludecomp -a off`, entry `pin-con7`) it
  differs from the shipped file by up to 0.88 % in R and 0.18 % in Im Z.
- The user guide's printed `onebargp` values (entries `onebargp_guidefreq` and `onebargp_guidefreq_default`)
  differ from this build by up to 9.4 % in R11 (from 1 MHz up), 1.4 % in R22, 0.6 % in L11 and 0.13 % in L22,
  with either option set. The cause is not established.
- A 10 mm × 1 mm × 35 µm bar at 1 Hz gives R = 4.92611 mΩ, equal to l/(σwt) to 6 digits, and L = 6.98638 nH
  (`validation/bar_results.csv`). The independent DC quadrature in `scripts/bar_validation.py` gives
  6.9863822 nH, equal to it to 6 digits and to InductanceTests' Hoer–Love value (6.98638222352 nH). The formula
  R and the quadrature L are in `validation/bar_results.json`.

**Decks** (`scripts/pour_decks.py`; geometry from `ExtractionTests.AReturnThroughAPour_GoesRoundASlot`):
- The plane is a uniform G grid whose nodes sit at OpenSim's cell centres, with 1, 3, 5, 7 or 9 strips through
  its thickness, graded by 2. The trace has 7 × 5 strips (T1) or 15 × 9 (T2), graded by 2, or 21 × 11 (T3),
  graded by 1.6.
- The port runs from the trace start to the plane node under it. The far end is joined by `.equiv` (ideal) or
  by a wire and bar (real).
- `.units mm` and `sigma=5.8e4` are used throughout.
- Solved with `-s iterative -m direct -a off -t 1e-7 -c 1000`, which equals FastHenry's LU in all six digits.
- `os03_*` is the 0.3 mm grid. `k09_*` is a 6/19 mm grid with a node under each end; every `kN` grid has pitch
  6/(2N+1) mm and a node under each end. `x10_*` is a 0.1 mm grid whose removed cells are exactly the
  0.6 × 4 mm slot.
- `pN` gives N strips through the plane; `T1`/`T2`/`T3` are the trace levels above; `_re` marks the real end
  joins. p1 was run on every grid and p3 on k06, k09, k20 and os03. p5, p7 and p9 (with T2) were run on k06
  and k09 only, T2 with p3 on k06, k09 and k20, and T3 (with p3) on k09 only. `best_estimates.csv` adds the
  increments dP3, dT2, dP9 and dT3 defined in `scripts/final_estimates.py`.

**Pipeline and what is kept.**
- Run order: `pour_decks.py <root>` writes every deck and `manifest.json`. `run_pour.py` runs them and appends
  to `results.jsonl`. `slot_sensitivity.py` adds the `x10s_*` slot-size variants, built from the
  `x10_p1_T1_slot` decks. `summarize.py` flattens `results.jsonl` into `pour/pour_results.csv`, `analyze.py`
  writes `pour/convergence.json` and `final_estimates.py` writes `pour/best_estimates.csv`.
- `pour_results.csv` holds every run: 505 rows over 50 configs, with solver, options and Zc path.
- `manifest.json`, `results.jsonl` and `best_estimates.json` are not kept. The first two hold absolute local
  paths, and the steps above regenerate all three. `analyze.py` and `final_estimates.py` read the first two, so
  they do not rerun from this folder as it is; the same R and L values are in `pour_results.csv`.
- Decks and Zc files are kept only for the 8 configs the tests and this README cite:
  - the gated `os03_p3_T1_slot`, `os03_p1_T1_solid` and `k09_p3_T1_solid_re`;
  - `os03_p3_T1_solid`, the solid partner of the gated slot case (also an LU check);
  - the real-end `os03_p3_T1_slot_re` and `os03_p3_T1_solid_re`;
  - the raised-cap `k09_p9_T2_solid` and `k09_p9_T2_slot`.
- The other 42 configs are rows of `pour_results.csv` only. `os03_p3_T1_slot` has no LU run at 100 MHz.

**OpenSim against FastHenry (same filaments).**
- `PeecReferenceTests.ThePourLoop_IsFastHenrysOnTheSameGrid` gates three cases at 100 Hz, 1 MHz, 100 MHz and
  1 GHz (tolerance 3e-5). Every value agrees within 1.1e-5 relative, about five of FastHenry's six printed
  digits:
  - `os03_p3_T1_slot`: ideal ends, OpenSim's iterative path;
  - `os03_p1_T1_solid`: ideal ends, the dense path;
  - `k09_p3_T1_solid_re`: real ends (a node under each end). `k09_p3_T1_slot_re` is in `pour_results.csv`
    but is not compared.
- With the caps raised, `k09_p9_T2_solid` (ideal ends; not gated, 30 min) agrees at 1 GHz to 1.8e-5 in R and
  5e-6 in L: OpenSim 108.836 mΩ / 2.13410 nH against FastHenry's 108.838 mΩ / 2.13411 nH
  (`pour/pour_results.csv`).
- With real ends on `os03` the far end is not a grid node, so both codes add a diagonal join bar. FastHenry
  couples it by its formula for filaments meeting at a point, OpenSim by the centre-line formula. R then
  differs by 3.4 % and L by 0.5 % at 100 MHz, so that case is not gated.

**Physical (converged) values** (`pour/best_estimates.csv`; k-ladder of pitches to 0.072 mm, plane and trace
strips raised, extrapolated). Ideal ends at 100 MHz:

| | L |
|---|---|
| solid | 2.1816 ± 0.0025 nH |
| slot (exact 0.6 × 4 mm slot) | 2.7640 ± 0.021 nH |
| ratio | 1.267 |

- On the 0.3 mm grid the same loop gives 1.318 with ideal ends and 1.306 with the real ends that
  ExtractionTests uses (`os03_p3_T1_slot`/`_solid` and `os03_p3_T1_slot_re`/`_solid_re`, 100 MHz).
- Part of the excess over 1.267 comes from OpenSim's cell-centre rule, which makes the slot 4.2 mm long: a
  0.6 × 4.2 mm slot on the exact 0.1 mm grid gives 1.292 (`x10s_long`), against 1.276 for 0.6 × 4 mm (`x10`).
  The rest comes from the coarse grid.
- ExtractionTests' `> 1.3` holds there with about 0.5 % to spare.
- R has no grid-converged value for this loop. The port and the short are point contacts, so each halving of
  the pitch adds (R_s/π)·ln 2.

**Strip caps (finding).** LoopExtraction's defaults are 7 × 5 trace strips and at most 3 through the plane.
Against 15 × 9 and 9 strips on the same grid:

| frequency | R reads low by |
|---|---|
| 100 MHz | 13 % |
| 300 MHz | 25 % |
| 1 GHz | 45 % |

L moves by under 1 %: 0.4–1.0 % on the solid pour and 0.3–0.8 % on the slotted one (k06 and k09 grids,
100 MHz to 1 GHz). The owner chose to keep the defaults and have the result say so: see `ResolutionNote` in
`LoopExtraction`.

## `matlab-strip/` — the isolated rectangle in MATLAB R2025b's PDE Toolbox

`stripRac.m` solves the 2D voltage-driven eddy-current problem in an isolated conductor:
- −∇·(∇A/µ0) + jωσA = σE0;
- A = 0 on a far circle;
- R_ac = Re(E0/I).

It uses quadratic triangles, a quarter model, and buffer rings around thin strips.

Validation:
- On a round wire against the exact Bessel ratio it is within 3e-6 at δ/5 elements and 1.5e-9 at the finest
  level (`run_wire.m`, `wire_results.csv`).
- An independent volume integral-equation solve (`ieRac.m`) agrees to 6.2e-7.
- Mesh-to-mesh scatter: case c at level 3 with RfarFactor, CornerFactor and Hgrad perturbed (9/5/3 runs at
  t/δ = 2/4/8; `run_scatter.m`, `strip_rac_scatter.csv`). It feeds the `mesh_scatter_*` columns of
  `strip_rac_summary.csv`.

Results:
- Four rectangles (1×1 mm, 1×0.25 mm, 1 mm×35 µm, 0.2 mm×35 µm) at t/δ = 0.5–8 have converged to 2.3e-6
  (`strip_rac_summary.csv`). The tests use its `Rac_over_Rdc_finest` column.
- `run_haefner.m` gives the ideal rectangle at Haefner's points (`haefner_fem.csv`).

## `haefner/` — measured R_ac of rectangular copper bars

S. J. Haefner, "Alternating-current resistance of rectangular conductors," *Proc. IRE* 25(4), pp. 434–447,
April 1937.
- Read from the free scan of the issue, https://www.worldradiohistory.com/Archive-IRE/30s/IRE-1937-04.pdf
  (7,353,971 bytes, SHA-256 E87587CD2FAB9B1CCC9C6C036F888F3DD3D5BD0C3499735CB73847042A3E0BDE; not kept here).
- Printed pp. 434–447 are PDF pp. 76–89.

What the paper gives:
- **Measured quantity:** R_ac/R_dc of an isolated conductor, skin and edge effect only.
- **Similitude variable:** p = √(2µ0 f / R′_dc), with R′_dc per metre. The same shape at the same p gives the
  same ratio.
- **Specimens:** soft-drawn copper with slightly rounded edges.
- **Tables:** Table I (specimens) and Table II (round wires) are transcribed. The rectangular results exist
  only as curves.

Digitisation:
- Figs. 6–8 were calibrated on the graph-paper rulings with a two-band skew fit, and the curves were tracked
  (`digitize.py`, `export.py`, `cfg_fig*.json`). Fig. 5 as exported (`cfg_fig5_border.json`) calibrated its
  p axis in one band without skew. Fig. 9 used one band without skew on both axes (`cfg_fig9.json`), which
  left its f up to 0.036 high. The page renders, made with `render_winrt.ps1`, are not kept.
- An independent re-read (`verify_summary.json`) confirmed the tables and most rows, and corrected Fig. 9 and a
  few rows of Figs. 7–8. Use the `_verified` files.
- `fig9_digitized_verified.csv` is the re-read of the 3496 px render; `cfg_fig9.json`'s output survives only in
  its `f_reported` column. No kept script writes the `_verified` CSVs, and `export.py` writes Figs. 5–8 only.
  The tests use Fig. 8 alone.
- Working material is not kept: the `pages/` renders and the Table I/II crops cited in `cfg_fig*.json` and the
  table CSVs; `digitize.py`'s `fig*_digitized.json`; the unverified Fig. 7–9 CSVs; and the re-read's `img/` and
  `work/` images, `vchecks.json`, `mesh_fig9.json`, `theircal_check.py`, `fig9_cockroft_vs_fig8.json` and
  findings text, which `verify_summary.json` cites. Its corrected Cockroft slopes are in
  `fig9_digitized_verified.csv` (`slope_f_over_pi_sqrt2`).
- `digitized_checks.json` comes from the unverified traces. `verify_summary.json` supersedes its Dwight check at
  p = 2 (1.813 % → 1.70 %), and its Fig. 8 uncertainty summary (300 rows) predates the verified file (298 rows).
- Uncertainty (`u_total`) in R_ac/R_dc is 0.0041–0.0045 on Fig. 7, 0.0053–0.0067 on Figs. 5 and 6, and mostly
  0.0055–0.0074 on Fig. 8. On Fig. 8 three rows reach 0.0081–0.0094, and the double-stroked shape-2 curve at
  p 3.4–4.0 reaches 0.014 (`verify_status` `uncertain_double_stroke`). The six gated points are 0.0057–0.0063.
- The FEM of the ideal rectangle sits −0.04 / +1.04 / +1.13 / +1.51 / −0.55 / −1.59 % off the measurements at
  the gated points.

## OpenSim's PEEC, measured 2026-10-18

**Isolated bar** (500 widths long, filaments 21 × 9 or 21 × 21), against the FEM:

| bar | t/δ | deviation |
|---|---|---|
| 1 mm strip | 1 / 4 | +0.34 / −0.00 % |
| 0.2 mm strip | 2 / 8 | +0.17 / +1.03 % |
| 0.25 mm bar | 2 / 8 | +0.08 / +0.97 % |
| square | 2 / 4 / 8 | −0.18 / −0.40 / +0.98 % |

The remaining ≈ +1 % at t/δ = 8 comes from PeecModel's fixed grading ratio of 2. More filaments move it by under
0.03 %. Bars 20 widths long read 1.4 % low from their finite length.

**Haefner's points** (Fig. 8 test curves, shapes 1, 2, 6, 12). Shapes 1 and 2 are specimens 1 and 2 (a/c 1.00
and 2.07, 65 ft). No Table I specimen has a/c 6 or 12, so those two curves were not measured on a bar of that
shape; they are presumably drawn from the measured shapes (4.95 and 10.0, and Kennelly, Laws and Pierce's 8,
16 and 24):

| against | deviation |
|---|---|
| the measurements | +0.28 / +2.02 / +1.89 / +1.55 / −0.58 / −1.53 % |
| the FEM | +0.32 / +0.97 / +0.76 / +0.04 / −0.02 / +0.06 % |

The test's band against the FEM is graded by t/δ: 0.6 % below t/δ 4 (shapes 6 and 12, within 0.06 %) and
1.2 % above (+0.32 / +0.97 / +0.76 %). Against the measurements it is 2.5 %.
