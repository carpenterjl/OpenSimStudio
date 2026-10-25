# FU-19 — FCC Part 15 subpart B limit lines (the FCC half of FU-19)

The CISPR 32 half stays blocked: that standard is not on this machine (owner's decision
2026-10-25: no purchases, no sign-ups). The `Fcc…` lines in `OpenSim.Rf/Emi/EmiLimits.cs`
are gated here against the regulation's own text; the CISPR 32 lines keep their
"typed as commonly reproduced" label.

## Source

47 CFR Part 15 (Radio Frequency Devices), the eCFR point-in-time XML of Title 47 as of
2026-10-19 (Title 47 "up to date as of 2026-10-22" per the eCFR titles index on 2026-10-25),
downloaded with the owner's approval on 2026-10-25 from

    https://www.ecfr.gov/api/versioner/v1/full/2026-10-19/title-47.xml?part=15

(571,831 bytes, SHA-256 `74ea049a987626a0c8509b2c24279f9fea4689bff7248c6a3e1856a9dae641be`),
kept in the gitignored `reports/sources/FU-19-fcc/`. US federal regulation, public domain;
only the cells used are transcribed here (`fcc-15.107-15.109-tables.csv`).

Sections read: § 15.107 (conducted limits), § 15.109 (radiated emission limits),
§ 15.35 (measurement detector functions and bandwidths).

## What the text says, and how the lines use it

- **§ 15.107(a)** (Class B conducted, 50 µH/50 Ω LISN, 150 kHz–30 MHz): quasi-peak 66 to 56*,
  56, 60 dBµV and average 56 to 46*, 46, 50 dBµV over 0.15–0.5, 0.5–5, 5–30 MHz;
  "*Decreases with the logarithm of the frequency"; "The lower limit applies at the band
  edges." → `FccConductedClassBQuasiPeak`, `FccConductedClassBAverage` (a sloping segment is
  linear in log f; `EmiLimitLine.At` takes the minimum where segments meet).
- **§ 15.107(b)** (Class A conducted): quasi-peak 79, 73 dBµV and average 66, 60 dBµV over
  0.15–0.5 and 0.5–30 MHz; "The lower limit applies at the boundary between the frequency
  ranges." → `FccConductedClassAQuasiPeak`, `FccConductedClassAAverage`.
- **§ 15.109(a)** (Class B, 3 m): 100, 150, 200, 500 µV/m over 30–88, 88–216, 216–960,
  above 960 MHz → `FccClassB3m`, as 20·log₁₀ of the µV/m value (40.000, 43.522, 46.021,
  53.979 dBµV/m — the earlier typed line had 43.5 / 46 / 54).
- **§ 15.109(b)** (Class A, 10 m): 90, 150, 210, 300 µV/m over the same bands →
  `FccClassA10m` (39.085, 43.522, 46.444, 49.542 dBµV/m).
- **§ 15.109(c)**: "the tighter limit applies at the band edges"; §§ 15.33 and 15.35 govern
  the range and detectors.
- **§ 15.35(a), (b)**: at or below 1000 MHz the limits are quasi-peak (CISPR detector,
  ANSI C63.4-2014); above 1000 MHz the radiated limits are average, with a peak limit 20 dB
  above the average limit. The radiated lines here therefore end at 1000 MHz; the band above
  1 GHz is not drawn — `At` returning null there means "not drawn", not "no limit": the
  500 / 300 µV/m row keeps applying on the average detector (§ 15.33(b)(1) sets how far up
  measurements go, to 5 or 40 GHz by the highest frequency generated).
- **§ 15.109(g)**: digital devices may alternatively meet CISPR 22 (3rd ed.) radiated limits;
  that route is not drawn — the `Fcc…` radiated lines are the (a)/(b) tables.

## Finding

The FCC conducted tables carry, cell for cell, the numbers of the CISPR 32 Class A and B
conducted lines typed earlier from common reproductions.
`EmiLimitsTests.TheFccConductedTables_CarryTheSameNumbers_AsTheTypedCisprConductedLines`
records this; it supports but does not verify the CISPR 32 lines, which keep their label.
The regulation text itself gives no reason for the agreement.

Also changed: the conducted study's fixed assumption line used to say every limit line was
"typed as commonly reproduced"; it now names the chosen line's source
(`ConductedEmission.AssumptionsFor(input, limit)`), and the EMI panel's caveat says which
lines are which.

## Tests

`OpenSim.Tests/Rf/EmiLimitsTests.cs` (11): the four radiated table rows per class, the
quasi-peak end at 1000 MHz, the tighter limit at 88/216/960 MHz for both classes, the
conducted cells and the lower limit at 0.5 and 5 MHz, the logarithmic slope (61 dBµV at the
geometric mean of 0.15 and 0.5 MHz), the sources, the names the command line resolves, the
study's assumption line, and a CSV round trip.
