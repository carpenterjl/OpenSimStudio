"""Consistency checks on Haefner (1937) Tables I and II and the worked examples.
Usage: python -I checks.py <table1.csv> <table2.csv> <out.json>
All inputs are the transcribed printed values; everything else is computed here.
"""
import sys, csv, json, math
import numpy as np
from scipy.special import jv, ber, bei, berp, beip

t1, t2, out = sys.argv[1], sys.argv[2], sys.argv[3]
FT_CM = 30.48
IN2_CM2 = 6.4516
res = {}

def ratio_round(p):
    """Exact isolated round wire R_ac/R_dc with x = p (Haefner's p = sqrt(8 pi f A / sigma), mu = 1).
    Z/R0 = (ka/2) J0(ka)/J1(ka), ka = (1-j) p/sqrt(2)."""
    ka = (1 - 1j) * p / math.sqrt(2)
    return float(np.real(ka / 2 * jv(0, ka) / jv(1, ka)))

def ratio_round_ber(x):
    """Same via Kelvin functions: (x/2) (ber bei' - bei ber') / (ber'^2 + bei'^2)."""
    return float(x / 2 * (ber(x) * beip(x) - bei(x) * berp(x)) / (berp(x) ** 2 + beip(x) ** 2))

# ---- Table I
rows = list(csv.DictReader(open(t1, encoding="utf-8")))
tI = []
for r in rows:
    d = {"specimen": r["specimen"], "group": r["group"]}
    try:
        a2 = float(r["actual_2a_mils"]); c2 = float(r["actual_2c_mils"])
        d["area_from_dims_sq_in"] = a2 * c2 * 1e-6
        d["ratio_from_dims"] = a2 / c2
    except ValueError:
        if r["actual_2a_mils"] == "diam.":
            dia = float(r["actual_2c_mils"]) * 1e-3
            d["area_from_diam_sq_in"] = math.pi * dia ** 2 / 4
    if r["avg_area_sq_in"] and r["total_Rdc_ohm"] and r["wire_length_ft"]:
        A_cm2 = float(r["avg_area_sq_in"]) * IN2_CM2
        L_cm = float(r["wire_length_ft"]) * FT_CM
        Rcm = float(r["total_Rdc_ohm"]) / L_cm                  # ohm per cm
        d["A_cm2"] = A_cm2
        d["Rdc_ohm_per_cm"] = Rcm
        d["Rdc_abohm_per_cm"] = Rcm * 1e9
        d["implied_resistivity_ohm_cm"] = Rcm * A_cm2
        d["implied_resistivity_abohm_cm"] = Rcm * A_cm2 * 1e9
        # p at the frequencies used, from the measured d.c. resistance per cm
        d["p_at_f"] = {f: math.sqrt(8 * math.pi * f / (Rcm * 1e9)) for f in (300, 500, 1000, 2000, 3000, 4000, 5000, 6000, 7000, 8000, 8500)}
    tI.append(d)
res["tableI_checks"] = tI

# ---- Table II
rows2 = list(csv.DictReader(open(t2, encoding="utf-8")))
R_by = {}
for d in tI:
    if d["specimen"] in ("No. 4, B.S.", "No. 0, B.S."):
        R_by[d["specimen"].replace(", B.S.", "")] = d["Rdc_abohm_per_cm"]
tII = []
for r in rows2:
    f = float(r["freq_cycles"]); p = float(r["p"])
    key = "No. 4" if r["conductor"].startswith("No. 4") else "No. 0"
    Rab = R_by[key]
    p_from_tableI = math.sqrt(8 * math.pi * f / Rab)
    Rab_implied = 8 * math.pi * f / p ** 2
    calc = float(r["ratio_calc"]); obs = float(r["ratio_obs"])
    tII.append({
        "conductor": r["conductor"], "f": f, "p_printed": p,
        "p_from_TableI_Rdc": p_from_tableI, "p_ratio_printed_over_TableI": p / p_from_tableI,
        "Rdc_abohm_per_cm_implied_by_printed_p": Rab_implied,
        "calc_printed": calc, "bessel_at_printed_p": ratio_round(p), "kelvin_at_printed_p": ratio_round_ber(p),
        "calc_minus_bessel": calc - ratio_round(p),
        "obs_printed": obs, "pct_diff_printed": r["pct_diff"],
        "pct_diff_recomputed_(obs-calc)/calc": 100 * (obs - calc) / calc,
        "obs_minus_bessel_pct": 100 * (obs - ratio_round(p)) / ratio_round(p),
    })
res["tableII_checks"] = tII

# ---- worked examples
ex = {}
f, A, sig = 5e5, 0.0021, 1724.0
ex["p445_example_p"] = math.sqrt(8 * math.pi * f * A / sig)
ex["p445_example_p_as_printed_expression"] = math.sqrt(8 * math.pi * 5e5 * 2.1 / (1724 * 1e3))
ex["p439_ratio_at_p0.5_bessel"] = ratio_round(0.5)
ex["p439_ratio_at_p0.5_series_1+p^4/192"] = 1 + 0.5 ** 4 / 192
ex["p446_cockroft_square_coeff_1.86/(pi*sqrt2)"] = 1.86 / (math.pi * math.sqrt(2))
ex["p446_cockroft_validity_sqrt(32/pi)*sqrt(1)"] = math.sqrt(32 / math.pi)
# relation of p to skin depth (SI): p^2 = 8 pi f A / rho_abohm_cm  vs delta = sqrt(rho/(pi f mu0))
mu0 = 4e-7 * math.pi
rho_ab = 1724.0; rho_si = rho_ab * 1e-9 * 1e-2  # abohm-cm -> ohm-m
f = 1e6
delta_cm = math.sqrt(rho_si / (math.pi * f * mu0)) * 100
A_test = 0.01
p_test = math.sqrt(8 * math.pi * f * A_test / rho_ab)
ex["p_vs_skin_depth_check"] = {"p": p_test, "sqrt(2A/pi)/delta": math.sqrt(2 * A_test / math.pi) / delta_cm,
                               "note": "p = sqrt(2A/pi)/delta; round wire (A = pi a^2): p = sqrt(2) a/delta"}
res["examples"] = ex

json.dump(res, open(out, "w"), indent=1)

# brief console print
for d in tI:
    s = d["specimen"]
    extra = ""
    if "ratio_from_dims" in d: extra += f" ratio(2a/2c)={d['ratio_from_dims']:.3f} area(2a*2c)={d['area_from_dims_sq_in']:.5f}"
    if "area_from_diam_sq_in" in d: extra += f" area(pi d^2/4)={d['area_from_diam_sq_in']:.5f}"
    if "Rdc_abohm_per_cm" in d:
        extra += f" Rdc={d['Rdc_abohm_per_cm']:.1f} abohm/cm rho={d['implied_resistivity_ohm_cm']*1e6:.4f} uohm-cm p(8000)={d['p_at_f'][8000]:.3f} p(8500)={d['p_at_f'][8500]:.3f} p(300)={d['p_at_f'][300]:.3f}"
    print(s, extra)
for d in tII:
    print(f"{d['conductor']:14s} f={d['f']:6.0f} p={d['p_printed']:.4f} p(TabI)={d['p_from_TableI_Rdc']:.4f} ratio={d['p_ratio_printed_over_TableI']:.4f} "
          f"Rimpl={d['Rdc_abohm_per_cm_implied_by_printed_p']:.1f} calc={d['calc_printed']:.3f} bessel={d['bessel_at_printed_p']:.4f} kelvin={d['kelvin_at_printed_p']:.4f} "
          f"obs={d['obs_printed']:.3f} diff%={d['pct_diff_printed']} recomputed={d['pct_diff_recomputed_(obs-calc)/calc']:+.2f}")
print(json.dumps(ex, indent=1))
