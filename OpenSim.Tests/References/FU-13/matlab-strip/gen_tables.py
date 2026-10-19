"""Markdown tables of the results, read straight from the result CSVs.

usage: python -I gen_tables.py <dir>   (prints to stdout)
"""
import csv
import math
import sys
from collections import defaultdict
from pathlib import Path

d = Path(sys.argv[1])


def rows(name):
    with open(d / name, newline="") as fh:
        return list(csv.DictReader(fh))


def g(x, fmt):
    try:
        v = float(x)
    except (TypeError, ValueError):
        return str(x)
    if math.isnan(v):
        return "-"
    return format(v, fmt)


out = []

# ---- wire validation ----
W = rows("wire_results.csv")
out.append("### Round wire validation (a = 0.5 mm, sigma = 5.8e7 S/m)\n")
out.append("| a/delta | f (Hz) | run | level | hs/delta | nodes | R/Rdc FE (Re E0/I) | R/Rdc power form | exact Re[(ka/2)J0/J1] | rel. error | Lint rel. error | dL rel. error |")
out.append("|---|---|---|---|---|---|---|---|---|---|---|---|")
for r in W:
    out.append("| {} | {} | {} | {} | {} | {} | {} | {} | {} | {} | {} | {} |".format(
        r["a_over_delta"], g(r["f_Hz"], ".6g"), r["case"], r["mesh_level"], g(r["hs_over_delta"], ".3g"),
        r["nodes"], g(r["Rac_over_Rdc_FE"], ".9f"), g(r["Rac_over_Rdc_powerform"], ".9f"),
        g(r["Rac_over_Rdc_exact"], ".9f"), g(r["rel_err"], "+.2e"),
        g(float(r["Lint_FE_Hpm"]) / float(r["Lint_exact_Hpm"]) - 1, "+.2e"),
        g(float(r["dL_FE_Hpm"]) / float(r["dL_exact_Hpm"]) - 1, "+.2e")))
out.append("")

# ---- rectangles: level sequences ----
M = rows("strip_rac_results.csv")
seq = defaultdict(dict)
nodes = defaultdict(dict)
meta = {}
for r in M:
    k = (r["case"], float(r["t_over_delta"]))
    seq[k][int(r["mesh_level"])] = float(r["Rac_over_Rdc"])
    nodes[k][int(r["mesh_level"])] = int(r["nodes"])
    meta[k] = r
S = {(r["case"], float(r["t_over_delta"])): r for r in rows("strip_rac_summary.csv")}
names = {"a": "1 mm x 1 mm", "b": "1 mm x 0.25 mm", "c": "1 mm x 35 um", "d": "0.2 mm x 35 um"}

out.append("### Rectangles: R_ac/R_dc on each mesh level (main runs: quarter model, far circle 20 x diagonal)\n")
out.append("| case | t/delta | f (Hz) | level 1 | level 2 | level 3 | level 4 | level 5 | nodes (finest) |")
out.append("|---|---|---|---|---|---|---|---|---|")
for k in sorted(seq):
    s = seq[k]
    cells = [g(s[L], ".9f") if L in s else "-" for L in range(1, 6)]
    fin = max(s)
    out.append("| {} ({}) | {:g} | {} | {} | {} |".format(
        k[0], names[k[0]], k[1], g(meta[k]["f_Hz"], ".6g"), " | ".join(cells), nodes[k][fin]))
out.append("")

out.append("### Rectangles: convergence, far boundary, independent integral equation\n")
out.append("| case | t/delta | finest FE | change two finest | rel. change | monotone | p | Richardson (FE) | Rfar 40x (level) | Rfar40 - main, same level | IE Richardson (p) | FE finest - IE | mesh scatter | U (abs) |")
out.append("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|")
for k in sorted(S):
    r = S[k]
    out.append("| {} | {:g} | {} | {} | {} | {} | {} | {} | {} ({}) | {} | {} ({}) | {} | {} | {} |".format(
        k[0], k[1], g(r["Rac_over_Rdc_finest"], ".9f"), g(r["change_two_finest"], "+.2e"),
        g(r["rel_change_two_finest"], "+.1e"), "yes" if r["monotone"] == "1" else "no",
        g(r["observed_order"], ".2f"), g(r["Rac_over_Rdc_Richardson"], ".9f"),
        g(r["Rac_over_Rdc_Rfar40"], ".9f"), r["Rfar40_level"], g(r["Rfar40_minus_main_same_level"], "+.1e"),
        g(r["IE_Richardson"], ".9f"), g(r["IE_observed_order"], ".2f"),
        g(r["FE_finest_minus_IE_Richardson"], "+.1e"), (g(r["mesh_scatter_spread"], ".1e") + (" (L{}, n={})".format(r["mesh_scatter_level"], r["mesh_scatter_n"]) if r["mesh_scatter_n"] != "0" else "")),
        g(r["uncertainty_abs"], ".1e")))
out.append("")

out.append("### Rectangles: reference values and inductance\n")
out.append("| case | w x t | t/delta | f (Hz) | R_ac/R_dc | +/- (abs) | R_dc (ohm/m) | R_ac (ohm/m) | Lint, energy in conductor (nH/m) | dL = L(f) - L(0) FE (nH/m) | dL IE finest (nH/m) |")
out.append("|---|---|---|---|---|---|---|---|---|---|---|")
for k in sorted(S):
    r = S[k]
    out.append("| {} | {} | {:g} | {} | {} | {} | {} | {} | {} | {} | {} |".format(
        k[0], names[k[0]], k[1], g(r["f_Hz"], ".6g"), g(r["Rac_over_Rdc_finest"], ".6f"),
        g(r["uncertainty_abs"], ".1e"), g(r["Rdc_ohm_per_m"], ".6g"), g(r["Rac_ohm_per_m"], ".6g"),
        g(float(r["Lint_Hpm"]) * 1e9, ".5f"), g(float(r["dL_FE_Hpm"]) * 1e9, ".5f"),
        g(float(r["dL_IE_finest_Hpm"]) * 1e9, ".5f")))
out.append("")

# ---- IE levels ----
IE = rows("ie_results.csv")
ie = defaultdict(dict)
for r in IE:
    ie[(r["case"], float(r["t_over_delta"]))][int(r["ie_level"])] = (float(r["Rac_over_Rdc_IE"]), int(r["cells_quarter"]))
out.append("### Integral-equation cross-check: R_ac/R_dc per level (cells in the quarter)\n")
out.append("| case | t/delta | IE level 1 | IE level 2 | IE level 3 | IE level 4 |")
out.append("|---|---|---|---|---|---|")
for k in sorted(ie):
    s = ie[k]
    out.append("| {} | {:g} | {} |".format(k[0], k[1], " | ".join(
        "{} ({})".format(g(s[L][0], ".9f"), s[L][1]) if L in s else "-" for L in range(1, 5))))
out.append("")

# ---- checks ----
C = rows("strip_rac_checks.csv")
out.append("### Far boundary doubled and full (unsymmetric) section\n")
out.append("| case | t/delta | level | run | Rfar factor | quarter | nodes | R_ac/R_dc | main, same level | difference |")
out.append("|---|---|---|---|---|---|---|---|---|---|")
for r in C:
    out.append("| {} | {} | {} | {} | {} | {} | {} | {} | {} | {} |".format(
        r["case"], r["t_over_delta"], r["mesh_level"], r["run"], r["RfarFactor"], r["quarter"], r["nodes"],
        g(r["Rac_over_Rdc"], ".9f"), g(r["Rac_over_Rdc_main_same_level"], ".9f"), g(r["difference"], "+.1e")))
out.append("")

# ---- scatter ----
SC = rows("strip_rac_scatter.csv")
out.append("### Mesh-to-mesh scatter with the buffer rings, case c (parameters that do not change the physics)\n")
out.append("| t/delta | level | Rfar factor | CornerFactor | Hgrad (level-1 value) | nodes | R_ac/R_dc |")
out.append("|---|---|---|---|---|---|---|")
for r in SC:
    out.append("| {} | {} | {} | {} | {} | {} | {} |".format(
        r["t_over_delta"], r["mesh_level"], r["RfarFactor"], r["CornerFactor"], r["Hgrad"], r["nodes"],
        g(r["Rac_over_Rdc"], ".9f")))
out.append("")

sys.stdout.reconfigure(encoding="utf-8")
print("\n".join(out))
