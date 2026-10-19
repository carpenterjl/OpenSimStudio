"""Consistency checks between FEMM's published numbers and the .fem models, plus the
analytic tube solution. Usage: python -I checks.py <out_json>"""
import sys, json, math
import numpy as np
from scipy import special, integrate

sys.stdout.reconfigure(encoding='utf-8')
MU0 = 4e-7 * math.pi
IN = 0.0254
CM = 0.01
G0 = 9.80665  # standard gravity (defined constant)
out = {}

# ---------------- induct1a (InductanceExample / ACForceExample) ----------------
n, i = 66, 1.0
a = 0.5 * IN * IN          # centre-pole area 0.5 in x 1 in depth
g = 0.025 * IN
L_circ = MU0 * n * n * a / (2 * g)
F_circ = MU0 * a * n * n * i * i / (4 * g * g)
W_pub = 0.000865042
slot_area = 0.25 * 0.5 * IN * IN
wire_d = 1.0239652968433499e-3
fill = n * math.pi * wire_d ** 2 / 4 / slot_area
out['induct1a'] = {
    'L_circuit_theory_H': L_circ,
    'F_circuit_theory_N': F_circ,
    'F_circuit_theory_AC_mean_N': F_circ / 2,
    'L_from_published_W_H (2W/I^2, I=1 A)': 2 * W_pub / i ** 2,
    'depth_m': IN,
    'published_L_1.73mH_per_metre_H_per_m': 1.73e-3 / IN,
    'published_W_per_metre_J_per_m': W_pub / IN,
    'published_F_WST_DC_per_metre_N_per_m': 1.11347 / IN,
    'published_F_MST_DC_per_metre_N_per_m': 1.11289 / IN,
    'published_F_WST_AC_mean_per_metre_N_per_m': 0.556734 / IN,
    'slot_area_m2': slot_area,
    'J_slot_A_per_m2 (66 A-turns / slot area)': n * i / slot_area,
    'copper_fill_fraction (66 x pi d^2/4 / slot area, d = WireD 1.02397 mm)': fill,
    'half_of_published_DC_WST_N': 1.11347 / 2,
}

# ---------------- TEAM 28 ----------------
R_abc = 24 * CM
ri_in, ro_in, ri_out, ro_out, h = 2.7 * CM, 5.5 * CM, 8 * CM, 9.5 * CM, 5.2 * CM
A_in = (ro_in - ri_in) * h
A_out = (ro_out - ri_out) * h
N_in, N_out, I = 960, 576, 20.0
d18 = 1.0239652968433499e-3
aw = math.pi * d18 ** 2 / 4
sig_cu = 58e6
R_in = N_in * 2 * math.pi * (ri_in + ro_in) / 2 / (sig_cu * aw)
R_out = N_out * 2 * math.pi * (ri_out + ro_out) / 2 / (sig_cu * aw)
sig_al = 34e6
f = 50.0
out['team28'] = {
    'ABC_c0_expected_1_over_mu0_R (R=0.24 m)': 1 / (MU0 * R_abc),
    'ABC_c0_in_file': 3315727.9810811542,
    'inner_coil_area_m2': A_in, 'outer_coil_area_m2': A_out,
    'J_inner_960x20/A_MA_per_m2': N_in * I / A_in / 1e6,
    'J_outer_576x20/A_MA_per_m2': N_out * I / A_out / 1e6,
    'fill_inner (960 x pi d^2/4 / A)': N_in * aw / A_in,
    'fill_outer (576 x pi d^2/4 / A)': N_out * aw / A_out,
    'R_dc_inner_ohm': R_in, 'R_dc_outer_ohm': R_out,
    'P_coils_half_I2R_peak20A_W': 0.5 * I * I * (R_in + R_out),
    'P_coils_I2R_if_20A_were_rms_W': I * I * (R_in + R_out),
    'plate_gap_above_coils_mm': (3.73 - 2.6) * 10,
    'plate_thickness_mm': (4.03 - 3.73) * 10,
    'plate_radius_m': 6.5 * CM,
    'plate_weight_0.107kg_x_g0_N': 0.107 * G0,
    'skin_depth_aluminium_50Hz_m': 1 / math.sqrt(math.pi * f * MU0 * sig_al),
    'skin_depth_copper_50Hz_m': 1 / math.sqrt(math.pi * f * MU0 * sig_cu),
    'wire_diameter_18AWG_m': d18,
}

# ---------------- AirCore ----------------
cases = [(100, 1, 2, 1, 0.64707e-3, 0.64286e-3), (100, 1, 1.25, 4, 0.23081e-3, 0.22376e-3), (100, 0.25, 2, 0.25, 0.40955e-3, 0.38208e-3)]
ac = []
for (nn, ri, ro, z, Lf, Lw) in cases:
    r = 2 * max(ro, ri, z)
    ac.append({'n': nn, 'ri_in': ri, 'ro_in': ro, 'z_in': z, 'sphere_radius_in': r, 'sphere_radius_m': r * IN,
               'abc_c0': 1 / (r * IN * math.pi * 4e-7), 'coil_mesh_in': r / 20, 'air_mesh_in': r / 100,
               'FEMM_L_H': Lf, 'Wheeler_L_H': Lw, 'FEMM_over_Wheeler_minus_1': Lf / Lw - 1})
out['aircore'] = ac

# ---------------- Tube (analytic, Stoll-type) ----------------
ri, ro, rw = 0.5 * IN, 1.0 * IN, 0.1 * IN
mu = 1000 * MU0
sig = 10e6
f = 1.0
w = 2 * math.pi * f
Iw = 100.0
delta = math.sqrt(2 / (w * mu * sig))
alpha = (1 + 1j) / delta
# H = c0 I1(alpha r) + c1 K1(alpha r); H(ri) = I/(2 pi ri), H(ro) = I/(2 pi ro)
Mx = np.array([[special.iv(1, alpha * ri), special.kv(1, alpha * ri)], [special.iv(1, alpha * ro), special.kv(1, alpha * ro)]], dtype=complex)
rhs = np.array([Iw / (2 * math.pi * ri), Iw / (2 * math.pi * ro)], dtype=complex)
c0, c1 = np.linalg.solve(Mx, rhs)


def H(r):
    return c0 * special.iv(1, alpha * r) + c1 * special.kv(1, alpha * r)


def J(r):
    # J_z = (1/r) d(r H)/dr = alpha (c0 I0 - c1 K0)
    return alpha * (c0 * special.iv(0, alpha * r) - c1 * special.kv(0, alpha * r))


net = integrate.quad(lambda r: (J(r) * 2 * math.pi * r).real, ri, ro, epsabs=1e-12, limit=200)[0] + 1j * integrate.quad(lambda r: (J(r) * 2 * math.pi * r).imag, ri, ro, epsabs=1e-12, limit=200)[0]
loss = integrate.quad(lambda r: abs(J(r)) ** 2 / sig * math.pi * r, ri, ro, epsabs=1e-14, epsrel=1e-13, limit=400)[0]  # 0.5*|J|^2/sigma*2 pi r
# wire loss (DC-uniform approximation check of skin depth in copper)
delta_cu = math.sqrt(2 / (w * MU0 * 58e6))
# RMS of |J| over candidate sets of 20 test points
cands = {}
for name, rr in {
    'linspace(ri,ro,20) inclusive': np.linspace(ri, ro, 20),
    'midpoints of 20 equal intervals': ri + (np.arange(20) + 0.5) * (ro - ri) / 20,
    'linspace excluding ri, including ro': ri + (np.arange(1, 21)) * (ro - ri) / 20,
    'linspace including ri, excluding ro': ri + (np.arange(0, 20)) * (ro - ri) / 20,
    'linspace(ri,ro,21) all 21 points': np.linspace(ri, ro, 21),
}.items():
    j = J(rr)
    cands[name] = {'rms_abs_J': float(np.sqrt(np.mean(np.abs(j) ** 2))), 'rms_re_J': float(np.sqrt(np.mean(j.real ** 2))), 'rms_im_J': float(np.sqrt(np.mean(j.imag ** 2)))}
samples = []
for r_in in [0.5, 0.55, 0.6, 0.65, 0.7, 0.75, 0.8, 0.85, 0.9, 0.95, 1.0]:
    r = r_in * IN
    hh = H(r)
    jj = J(r)
    samples.append({'r_in': r_in, 'r_m': r, 'H_re': hh.real, 'H_im': hh.imag, 'B_phi_re_T': (mu * hh).real, 'B_phi_im_T': (mu * hh).imag,
                    'J_re': jj.real, 'J_im': jj.imag, 'J_abs': abs(jj)})
air = []
for r_in in [0.2, 0.3, 0.4]:
    r = r_in * IN
    air.append({'r_in': r_in, 'B_phi_T (mu0 I / 2 pi r)': MU0 * Iw / (2 * math.pi * r)})
out['tube'] = {
    'delta_m': delta, 'alpha_per_m': [alpha.real, alpha.imag],
    'c0_A_per_m': [c0.real, c0.imag], 'c1_A_per_m': [c1.real, c1.imag],
    'published': {'c0': '-2.548 + 30.431 (no j printed)', 'c1': '22724.439 + 2858.776j', 'delta': 0.00503292, 'alpha': '198.692+198.692j'},
    'net_tube_current_A': [net.real, net.imag],
    'tube_loss_W_per_m (0.5 int |J|^2/sigma dA)': loss,
    'skin_depth_copper_1Hz_m': delta_cu, 'wire_radius_m': rw,
    'rms_candidates_A_per_m2': cands, 'published_rms_exact_20pts': 170670.0, 'published_rms_error': 669.5,
    'samples_in_tube': samples, 'samples_in_air_gap': air,
    'depth_m (39.370078740157481 in)': 39.370078740157481 * IN,
}

json.dump(out, open(sys.argv[1], 'w', encoding='utf-8'), indent=1, default=float)
print(json.dumps(out, indent=1, default=float))
