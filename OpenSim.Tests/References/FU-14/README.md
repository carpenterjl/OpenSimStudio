# FU-14 — Rautio's stripline standard

Reference material behind `OpenSim.Tests/Rf/StriplineStandardTests.cs`.

## Sources (read 2026-10-18, with the owner's approval)

- J. C. Rautio, "An ultra-high precision benchmark for validation of planar electromagnetic
  analyses," *IEEE Trans. Microwave Theory Tech.*, vol. 42, no. 11, pp. 2046–2050, Nov. 1994,
  DOI 10.1109/22.330117. Read from Sonnet's scanned copy,
  https://www.sonnetsoftware.com/support/downloads/publications/TransNov94_StriplineStandard.pdf
  (579,488 bytes, SHA-256 F532DFBCD976C25E1AAF4D0D0138F1B00817C3357C5F91C2FCF2CC29C49997A6).
  The scan is not kept in the repository.
- Sonnet Software, *Benchmarking*, chapters 3 (the standard, the reporting form
  E% = 100·|S11| + |90 + ∠S21|/0.9, Sonnet 2.4's results table) and 7 (zero-length through),
  https://www.sonnetsoftware.com/products/sonnet-suites/benchmarking-ch3.html and `-ch7.html`.

## What the paper defines (p. 2047–2049)

- A zero-thickness, lossless strip centred between ground planes b = 1.0 mm apart, εr = 1.
- Z0·√εr = (η0/4)·K(k′)/K(k), k = tanh(πw/2b), k′ = √(1 − k²), η0 = 376.7303136 Ω as printed. The paper
  notes that the often-quoted 30π for η0/4 is 0.07 % in error.
- Table I: 25 Ω → 3.3260319 mm, 50 Ω → 1.4423896 mm, 100 Ω → 0.50396767 mm.
- The standard: the 50 Ω width, L = 4.99654097 mm (a quarter wave at 15 GHz), analysed at 15 GHz. The exact
  answer is |S11| = 0, ∠S21 = −90°.
- Error metric (Sec. II, in words): the percent impedance error plus the percent velocity error. To first
  order (Sec. V, and Sonnet's form above), 100·|S11| plus the phase error of S21 from −90°, in percent.
- Residual error of a rooftop analysis: e_T ≤ 16/N_W + 2·(16/N_L)² %, N_W > 3, N_L > 15 (N_W cells across
  the width, N_L per wavelength). Sonnet's own table exceeds this bound slightly for N_W ≥ 16, so it is an
  estimate, not a strict bound.
- The paper gives no box, side walls, port or de-embedding geometry. Sonnet's page adds "side walls, if
  present, more than 5 mm away".

## Files

- `stripline_hp.py`: Python `decimal`, 40 and 80 digits (AGM). Computes Z0 at the printed widths, the exact
  widths, the quarter wave, and the error floor of an ideal line with the printed W and L. Run with
  `python -I stripline_hp.py <outdir>`. Its output `stripline_hp_80digits.json` and `table1_check.csv`
  are included.
- `rautio_vpa.m` / `rautio_vpa_results.csv`: an independent MATLAB Symbolic Math check (vpa, 50 digits)
  of both the paper's form and Cohn's sech/tanh form.
- `sonnet_web_fig2_table.csv`: Sonnet 2.4's table from the ch3 page (N = 1…512), with the error recomputed
  from the reporting formula. Two rows are truncated rather than rounded on the page.
- `paper_fig2_digitized_verified.csv`: the paper's Fig. 2 markers (error against analysis time), read off
  the scan and re-read independently (about ±2 %).

## Results of the checks

- The printed widths are the exact ones correctly rounded: 3.6e-8, 9.0e-9 and 4.0e-9 mm off. Z0 at the
  printed 50 Ω width is 49.99999976 Ω. The printed L is 90.00000006°.
- An ideal line with the printed W and L reads E = 5.45e-7 %, the floor any analysis is judged against.
- η0 = µ0·c (4π·10⁻⁷ × 299792458) gives the same rounded widths as the paper's printed η0.
- Side finding: the comment in `ShieldedKernelTests` quoted 60.57 Ω for the exact zero-thickness
  stripline (w 0.5 mm, b 1.6 mm, εr 4.4). That is the 30π value; with η0 = µ0c it is 60.5195 Ω. The test
  helpers that used 30π now share `OpenSim.Tests/Si/ExactLines.cs`.

## OpenSim (shielded layered MoM, FU-30 kernel and calibration), measured 2026-10-18

The strip is meshed with gap ports a quarter wave (5 mm) in from the open ends. Two lines with ports 7.5
and 12.5 mm apart calibrate; a third, the standard plus 2.5 mm each side, is de-embedded. Cells along the
strip are 0.25 mm (N_L = 80). Release-build runs:

| cells across | unknowns | Z0 [Ω] | εeff | E [%] | bound [%] | Sonnet 2.4, uniform cells [%] |
|---|---|---|---|---|---|---|
| 2 graded | 448 | 52.4365 | 1.024999 | 5.9955 | 8.08 | — |
| 4 graded | 986 | 50.9532 | 1.006975 | 2.2373 | 4.08 | — |
| 8 graded | 2062 | 50.2713 | 1.001209 | 0.6023 | 2.08 | — |
| 4 uniform | 986 | 51.4739 | 1.012710 | 3.5384 | 4.08 | 3.53 |
| 8 uniform | 2062 | 50.8347 | 1.005687 | 1.9403 | 2.08 | 1.96 |

"Graded" means cosine-spaced across the strip, which resolves the edge current and converges faster;
Sonnet's rooftops are uniform. The zero-length through (Sonnet ch7) reads |S11| = 5–6e-6,
|S21| = 1.000000 and ∠S21 = 3–5e-4° on every mesh. The air-filled kernel (the TEM pole exactly at k0)
matches the plate-pair mode sum to 1.4e-12 at 0.3·b and 9.2e-11 at 1·b.
