"""Collect the FE cross-checks, extrapolations and the gate table.
Usage: python -I summarize.py <femm-info dir>"""
import sys, os, json, math, csv
import numpy as np
from scipy import special, integrate

sys.stdout.reconfigure(encoding='utf-8')
D = sys.argv[1]
M = os.path.join(D, 'matlab')
ld = lambda p: json.load(open(p, encoding='utf-8'))
out = {}
IN = 0.0254
MU0 = 4e-7 * math.pi

# ---- induct1a ----
ser = ld(os.path.join(M, 'induct1a_fe.json')) + [r for r in ld(os.path.join(M, 'induct1a_fe_vw.json')) if r['dg_in'] == 0]
ser.sort(key=lambda r: -r['scale'])
W = [r['W_J_per_1in'] for r in ser]
F = [r['Fy_WST_band0p02in_N_per_1in'] for r in ser]
F2 = [r['Fy_WST_band0p0125in_N_per_1in'] for r in ser]


def aitken(v):
    a, b, c = v[-3:]
    d1, d2 = b - a, c - b
    q = d1 / d2
    return c + d2 / (q - 1), q


W_ext, qW = aitken(W)
F_ext, qF = aitken(F)
vw = {r['scale']: {} for r in ld(os.path.join(M, 'induct1a_fe_vw.json'))}
for r in ld(os.path.join(M, 'induct1a_fe_vw.json')) + ser:
    if r['scale'] in vw:
        vw[r['scale']][r.get('dg_in', 0.0)] = r
vwres = []
for s, d in vw.items():
    if all(k in d for k in (-0.001, 0.0, 0.001)):
        dg = 0.001 * IN
        Dc = -(d[0.001]['W_J_per_1in'] - d[-0.001]['W_J_per_1in']) / (2 * dg)
        curv = d[0.001]['Fy_WST_band0p02in_N_per_1in'] - 2 * d[0.0]['Fy_WST_band0p02in_N_per_1in'] + d[-0.001]['Fy_WST_band0p02in_N_per_1in']
        vwres.append({'scale': s, 'F_virtual_work_central_difference_N': Dc, 'O(dg^2)_correction_N': -curv / 6,
                      'F_virtual_work_corrected_N': Dc - curv / 6, 'F_WST_same_mesh_N': d[0.0]['Fy_WST_band0p02in_N_per_1in']})
out['induct1a'] = {
    'series': [{k: r[k] for k in ('scale', 'nodes', 'L_H_per_1in', 'W_J_per_1in', 'Fy_WST_band0p0125in_N_per_1in', 'Fy_WST_band0p02in_N_per_1in', 'Fx_WST_band0p02in_N_per_1in')} for r in ser],
    'W_extrapolated_J_per_1in': W_ext, 'W_ratio_last_differences': qW,
    'L_extrapolated_H_per_1in': 2 * W_ext, 'F_extrapolated_N_per_1in': F_ext, 'F_ratio_last_differences': qF,
    'virtual_work': vwres,
    'FEMM_L_1.73mH_rel_to_extrapolated': 1.73e-3 / (2 * W_ext) - 1,
    'FEMM_W_rel_to_extrapolated': 0.000865042 / W_ext - 1,
    'FEMM_F_WST_rel_to_extrapolated': 1.11347 / F_ext - 1,
    'FEMM_F_MST_rel_to_extrapolated': 1.11289 / F_ext - 1,
    'FEMM_AC_mean_WST_rel_to_half_extrapolated': 0.556734 / (F_ext / 2) - 1,
    'per_metre': {'L_H_per_m': 2 * W_ext / IN, 'W_J_per_m': W_ext / IN, 'F_N_per_m': F_ext / IN, 'F_AC_mean_N_per_m': F_ext / 2 / IN},
}

# ---- TEAM 28 ----
box = ld(os.path.join(M, 'team28_box.json'))
mesh = ld(os.path.join(M, 'team28_mesh.json'))
ms = sorted([r for r in box if r['R_box_m'] == 2] + mesh, key=lambda r: -r['scale'])
FL = [r['Fz_lorentz_N'] for r in ms]
FW = [r['Fz_wst_band2mm_N'] for r in ms]
PP = [r['P_plate_W'] for r in ms]
PX = [r['P_coil_proximity_W'] for r in ms]
FL_ext, qFL = aitken(FL)
FW_ext, qFW = aitken(FW)
PP_ext, qPP = aitken(PP)
PX_ext, qPX = aitken(PX)
ref05 = {r['R_box_m']: r for r in box}
out['team28'] = {
    'box_series_scale0.5': [{k: r[k] for k in ('R_box_m', 'nodes', 'Fz_lorentz_N', 'Fz_wst_band2mm_N', 'P_plate_W', 'P_coil_proximity_W', 'flux_linkage_Wb_re', 'flux_linkage_Wb_im')} for r in box],
    'box_effect_rel_to_R2': {str(R): {'F_lorentz': ref05[R]['Fz_lorentz_N'] / ref05[2]['Fz_lorentz_N'] - 1, 'P_plate': ref05[R]['P_plate_W'] / ref05[2]['P_plate_W'] - 1} for R in ref05},
    'mesh_series_R2': [{k: r[k] for k in ('scale', 'nodes', 'Fz_lorentz_N', 'Fz_wst_band2mm_N', 'Fz_wst_band5mm_N', 'P_plate_W', 'P_coil_proximity_W', 'flux_linkage_Wb_re', 'flux_linkage_Wb_im', 'Z_ohm_re_without_Rdc', 'Z_ohm_im', 'I_plate_ring_A_re', 'I_plate_ring_A_im')} for r in ms],
    'F_lorentz_extrapolated_N': FL_ext, 'F_lorentz_ratio': qFL,
    'F_wst_extrapolated_N': FW_ext, 'F_wst_ratio': qFW,
    'P_plate_extrapolated_W': PP_ext, 'P_plate_ratio': qPP,
    'P_proximity_extrapolated_W': PX_ext,
    'FEMM_lorentz_team28a_finest_rel': 1.00693 / FL_ext - 1,
    'FEMM_lorentz_team28k_finest_rel': 1.00676 / FL_ext - 1,
    'FEMM_wst_team28a_finest_rel': 1.00641 / FL_ext - 1,
    'FEMM_wst_team28k_finest_rel': 1.00623 / FL_ext - 1,
    'FEMM_plate_loss_rel': 13.1981 / PP_ext - 1,
    'FEMM_proximity_rel_to_homogenised_formula': 0.914029 / PX_ext - 1,
}

# ---- AirCore ----
exact = {i + 1: c['L_exact_H'] for i, c in enumerate(ld(os.path.join(D, 'aircore_exact.json'))['cases'])}
femm = {1: 0.64707e-3, 2: 0.23081e-3, 3: 0.40955e-3}
rows = []
for fn in ('aircore_fe.json', 'aircore_fe2.json', 'aircore_grad.json', 'aircore_box_c1.json'):
    p = os.path.join(M, fn)
    if not os.path.exists(p):
        continue
    for r in ld(p):
        ci = r['case_index']
        rows.append({'file': fn, 'case': ci, 'mbox': r['mbox'], 'R_box_m': r['R_box_m'], 'scale': r['scale'],
                     'hmaxdiv': r.get('hmaxdiv', 15), 'hgrad': r.get('hgrad', 1.3), 'nearmult': r.get('nearmult', 1.5),
                     'nodes': r['nodes'], 'L_H': r['L_H'], 'rel_to_exact': r['L_H'] / exact[ci] - 1})
out['aircore'] = {'exact_H': exact, 'FEMM_H': femm, 'FEMM_rel_to_exact': {k: femm[k] / exact[k] - 1 for k in femm}, 'fe_runs': rows}
with open(os.path.join(D, 'aircore_fe_runs.csv'), 'w', newline='', encoding='utf-8') as f:
    w = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
    w.writeheader()
    w.writerows(rows)

# ---- Tube extras ----
ri, ro, rw = 0.5 * IN, 1.0 * IN, 0.1 * IN
mu, sig, om, Iw = 1000 * MU0, 10e6, 2 * math.pi, 100.0


def tube_loss(ri, ro):
    delta = math.sqrt(2 / (om * mu * sig))
    al = (1 + 1j) / delta
    A = np.array([[special.iv(1, al * ri), special.kv(1, al * ri)], [special.iv(1, al * ro), special.kv(1, al * ro)]], dtype=complex)
    c0, c1 = np.linalg.solve(A, np.array([Iw / (2 * math.pi * ri), Iw / (2 * math.pi * ro)], dtype=complex))
    J = lambda r: al * (c0 * special.iv(0, al * r) - c1 * special.kv(0, al * r))
    return integrate.quad(lambda r: abs(J(r)) ** 2 / sig * math.pi * r, ri, ro, epsabs=1e-14, epsrel=1e-13, limit=400)[0]


polys = {}
for N in (36, 72, 144, 360):
    ratio = N / (2 * math.pi) * math.sin(2 * math.pi / N)
    polys[N] = {'inscribed_area_ratio': ratio, 'equal_area_radius_ratio': math.sqrt(ratio),
                'tube_loss_W_per_m_at_equal_area_radii': tube_loss(ri * math.sqrt(ratio), ro * math.sqrt(ratio))}
out['tube'] = {'tube_loss_exact_W_per_m': tube_loss(ri, ro), 'polygons': polys,
               'wire_loss_uniform_W_per_m': 0.5 * Iw ** 2 / (58e6 * math.pi * rw ** 2)}
tfe = ld(os.path.join(M, 'tube_fe.json'))
chk = ld(os.path.join(D, 'checks.json'))['tube']['samples_in_tube']
an = {round(s['r_in'], 2): complex(s['B_phi_re_T'], s['B_phi_im_T']) for s in chk}
runs = []
for r in tfe:
    e = {k: r[k] for k in ('Lbox_in', 'Npoly', 'scale', 'nodes', 'P_tube_W_per_m', 'P_wire_W_per_m', 'I_tube_net_A_abs')}
    e['P_tube_rel_to_exact_circle'] = r['P_tube_W_per_m'] / out['tube']['tube_loss_exact_W_per_m'] - 1
    e['P_tube_rel_to_equal_area_polygon_analytic'] = r['P_tube_W_per_m'] / polys[r['Npoly']]['tube_loss_W_per_m_at_equal_area_radii'] - 1 if r['Npoly'] in polys else None
    e['B_samples'] = [{'r_in': ri_, 'fe': [br, bi], 'analytic': [an[round(ri_, 2)].real, an[round(ri_, 2)].imag],
                       'abs_diff_T': abs(complex(br, bi) - an[round(ri_, 2)])} for ri_, br, bi in zip(r['r_in'], r['Bphi_re_T'], r['Bphi_im_T'])]
    runs.append(e)
out['tube']['fe_runs'] = runs

json.dump(out, open(os.path.join(D, 'fe_crosscheck.json'), 'w', encoding='utf-8'), indent=1)
print(json.dumps(out, indent=1))
