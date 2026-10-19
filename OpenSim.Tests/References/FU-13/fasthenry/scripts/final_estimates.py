"""Best-available FastHenry values for the pour loop, assembled from the runs (tag 'gmres').
usage: python -I final_estimates.py <pour root>   -> prints tables, writes best_estimates.csv/.json

Each quantity Q (L or R) at frequency f:
  Q_base   = plane nhinc 1, trace 7x5 r2, on the finest single-node grid run (k41 if done, else k29)
  + dP3    = Q(nhinc 3) - Q(nhinc 1)                        at k=20 (0.146 mm), trace 7x5
  + dT2    = Q(trace 15x9 r2) - Q(trace 7x5 r2)             at k=20, nhinc 3
  + dP9    = Q(nhinc 9) - Q(nhinc 3)                        at k=9, trace 15x9
  + dT3    = Q(trace 21x11 r1.6) - Q(trace 15x9 r2)         at k=9, nhinc 3
  = Q_comp (the composite at the finest grid)
For L also: L_extrap = Q_comp + (first-order-in-h extrapolation of the nhinc 1 / 7x5 ladder from its two
finest single-node levels) -- the plane-pitch limit. R has no pitch limit with point contacts (it grows as
(Rs/pi) ln(1/h)), so R_comp is quoted AT the finest grid's pitch.
Slotted pour: the single-node ladder's effective slot is off 0.6 x 4 mm by up to one pitch; x10 (0.1 mm,
exact slot, four-node 2h x 2h contacts) is listed with its contact bias removed using the solid pour:
  L_x10_corr = L(x10 slot) + [L(finest single-node solid at ~0.1 mm, k29) - L(x10 solid)].
"""
import json, os, sys

root = sys.argv[1]
latest = {}
for l in open(os.path.join(root, 'results.jsonl')):
    r = json.loads(l)
    if r['R'] is not None and r['tag'] == 'gmres':
        latest[(r['config'], r['f'])] = r
man = {m['name']: m for m in json.load(open(os.path.join(root, 'manifest.json')))}
F = [1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 3e8, 1e9]


def v(cfg, f, q='L'):
    r = latest.get((cfg, f))
    return None if r is None else r[q]


def diff(a, b, f, q):
    x, y = v(a, f, q), v(b, f, q)
    return None if x is None or y is None else y - x


out = []
for slot in (False, True):
    s = 'slot' if slot else 'solid'
    ladder = [c for c in (f'k{k:02d}_p1_T1_{s}' for k in (6, 9, 20, 29, 41)) if all(v(c, f) is not None for f in F)]
    a, b = ladder[-2], ladder[-1]
    ha, hb = man[a]['pitch_mm'], man[b]['pitch_mm']
    for f in F:
        rec = {'pour': s, 'f_Hz': f, 'finest_grid': b, 'finest_pitch_mm': hb}
        for q in ('L', 'R'):
            base = v(b, f, q)
            inc = {'dP3': diff(f'k20_p1_T1_{s}', f'k20_p3_T1_{s}', f, q),
                   'dT2': diff(f'k20_p3_T1_{s}', f'k20_p3_T2_{s}', f, q),
                   'dP9': None, 'dT3': diff(f'k09_p3_T2_{s}', f'k09_p3_T3_{s}', f, q)}
            inc['dP9'] = diff(f'k09_p3_T2_{s}', f'k09_p9_T2_{s}', f, q)
            comp = base + sum(x for x in inc.values() if x is not None)
            rec[f'{q}_base'] = base
            for k_, x in inc.items():
                rec[f'{q}_{k_}'] = x
            rec[f'{q}_comp'] = comp
            rec[f'{q}_missing_increments'] = [k_ for k_, x in inc.items() if x is None]
            if q == 'L':
                La, Lb = v(a, f), base
                C = (La - Lb) / (ha - hb)
                rec['L_pitch_extrap_shift'] = -C * hb
                rec['L_extrap'] = comp - C * hb
        rec['L_os03_p3T1'] = v(f'os03_p3_T1_{s}', f)
        rec['R_os03_p3T1'] = v(f'os03_p3_T1_{s}', f, 'R')
        rec['L_x10_p1T1'] = v(f'x10_p1_T1_{s}', f)
        out.append(rec)
# x10 contact-bias correction for the slotted pour (uses the solid pour at ~0.1 mm)
for rec in out:
    if rec['pour'] == 'slot' and rec['L_x10_p1T1'] is not None:
        f = rec['f_Hz']
        bias = v('k29_p1_T1_solid', f) - v('x10_p1_T1_solid', f)
        rec['L_x10_corr_p1T1'] = rec['L_x10_p1T1'] + bias
        incs = sum(rec[f'L_{k}'] for k in ('dP3', 'dT2', 'dP9', 'dT3') if rec[f'L_{k}'] is not None)
        rec['L_x10_corr_comp'] = rec['L_x10_corr_p1T1'] + incs

# Slotted pour, slot-size correction: on the 0.1 mm exact-slot grid the slot was made 0.2 mm longer and
# 0.2 mm wider (configs x10s_long / x10s_wide); the slopes correct each single-node level's effective slot
# (cells whose centres fall inside, manifest 'slot_cells_mm') to 0.6 x 4.0 mm. The corrected slot
# increment dS(h) = L_slot,corr(h) - L_solid(h) is then extrapolated linearly in h from the two finest levels.
solid = {r['f_Hz']: r for r in out if r['pour'] == 'solid'}
for rec in out:
    if rec['pour'] != 'slot':
        continue
    f = rec['f_Hz']
    L0, Ll, Lw = v('x10_p1_T1_slot', f), v('x10s_long_p1_T1_slot', f), v('x10s_wide_p1_T1_slot', f)
    if None in (L0, Ll, Lw):
        continue
    s_len, s_w = (Ll - L0) / 0.2, (Lw - L0) / 0.2          # H per mm
    rec['slot_dL_dlength_H_per_mm'], rec['slot_dL_dwidth_H_per_mm'] = s_len, s_w
    pts = []
    for k in (20, 29, 41):
        c = f'k{k:02d}_p1_T1_slot'
        Ls, Lso = v(c, f), v(f'k{k:02d}_p1_T1_solid', f)
        if Ls is None or Lso is None:
            continue
        x0, x1, y0, y1 = man[c]['slot_cells_mm']
        corr = Ls - s_len * ((y1 - y0) - 4.0) - s_w * ((x1 - x0) - 0.6)
        pts.append((man[c]['pitch_mm'], corr, corr - Lso, k))
        rec[f'L_slotcorr_k{k:02d}'] = corr
        rec[f'dS_k{k:02d}'] = corr - Lso
    (h1, _, d1, _), (h2, _, d2, _) = pts[-2], pts[-1]
    slope = (d1 - d2) / (h1 - h2)
    dS_inf = d2 - slope * h2
    rec['dS_inf'] = dS_inf
    rec['dS_extrap_shift'] = dS_inf - d2
    sol = solid[f]
    p1t1_solid_inf = sol['L_base'] + sol['L_pitch_extrap_shift']
    incs = sum(rec[f'L_{k_}'] for k_ in ('dP3', 'dT2', 'dP9', 'dT3') if rec[f'L_{k_}'] is not None)
    rec['L_best'] = p1t1_solid_inf + dS_inf + incs
    rec['L_best_spread'] = abs(rec['dS_extrap_shift']) + abs(sol['L_pitch_extrap_shift'])
for rec in out:
    if rec['pour'] == 'solid':
        rec['L_best'] = rec['L_extrap']
        rec['L_best_spread'] = abs(rec['L_pitch_extrap_shift'])
for rec in out:
    if rec['pour'] == 'slot' and rec.get('L_best') is not None:
        rec['ratio_slot_over_solid_best'] = rec['L_best'] / solid[rec['f_Hz']]['L_best']

json.dump(out, open(os.path.join(root, 'best_estimates.json'), 'w'), indent=1)
keys = sorted({k for r in out for k in r}, key=lambda k: list(out[-1].keys()).index(k) if k in out[-1] else 99)
with open(os.path.join(root, 'best_estimates.csv'), 'w') as fh:
    fh.write(','.join(keys) + '\n')
    for r in out:
        fh.write(','.join('' if r.get(k) is None else (' '.join(r[k]) if isinstance(r[k], list) else (f'{r[k]:.6g}' if isinstance(r[k], float) else str(r[k]))) for k in keys) + '\n')


def n(x, s=1e9, fmt='{:.4f}'):
    return '--' if x is None else fmt.format(x * s)


print('L [nH]')
print('| pour | f | base (finest p1 7x5) | dP3 | dT2 | dP9 | dT3 | composite | + pitch extrap | L_extrap | x10 exact slot corr. comp | OpenSim grid p3 7x5 |')
print('|---|---|---|---|---|---|---|---|---|---|---|---|')
for r in out:
    print(f"| {r['pour']} | {r['f_Hz']:.0e} | {n(r['L_base'])} | {n(r['L_dP3'])} | {n(r['L_dT2'])} | {n(r['L_dP9'])} | {n(r['L_dT3'])} | "
          f"{n(r['L_comp'])} | {n(r['L_pitch_extrap_shift'])} | **{n(r['L_extrap'])}** | {n(r.get('L_x10_corr_comp'))} | {n(r['L_os03_p3T1'])} |")
print('\nslot correction [nH, nH/mm]')
print('| f | dL/dlength | dL/dwidth | L slot corr k20 | k29 | k41 | dS k20 | dS k29 | dS k41 | dS extrap | x10 corr (contact) |')
print('|---|---|---|---|---|---|---|---|---|---|---|')
for r in out:
    if r['pour'] == 'slot' and 'dS_inf' in r:
        print(f"| {r['f_Hz']:.0e} | {n(r['slot_dL_dlength_H_per_mm'])} | {n(r['slot_dL_dwidth_H_per_mm'])} | {n(r.get('L_slotcorr_k20'))} | "
              f"{n(r.get('L_slotcorr_k29'))} | {n(r.get('L_slotcorr_k41'))} | {n(r.get('dS_k20'))} | {n(r.get('dS_k29'))} | "
              f"{n(r.get('dS_k41'))} | {n(r['dS_inf'])} | {n(r.get('L_x10_corr_p1T1'))} |")
print('\nbest L [nH] (solid: composite + pitch extrapolation; slot: solid p1/7x5 limit + extrapolated corrected slot increment + increments)')
print('| f | L solid | +- | L slot | +- | slot/solid |')
print('|---|---|---|---|---|---|')
for f in F:
    a = solid[f]
    b = next(r for r in out if r['pour'] == 'slot' and r['f_Hz'] == f)
    print(f"| {f:.0e} | {n(a.get('L_best'))} | {n(a.get('L_best_spread'))} | {n(b.get('L_best'))} | {n(b.get('L_best_spread'))} | "
          f"{'--' if b.get('ratio_slot_over_solid_best') is None else format(b['ratio_slot_over_solid_best'], '.4f')} |")
print('\nR [mOhm] (at the finest grid pitch; not pitch-converged, see text)')
print('| pour | f | base (finest p1 7x5) | dP3 | dT2 | dP9 | dT3 | composite | OpenSim grid p3 7x5 |')
print('|---|---|---|---|---|---|---|---|---|')
for r in out:
    print(f"| {r['pour']} | {r['f_Hz']:.0e} | {n(r['R_base'], 1e3)} | {n(r['R_dP3'], 1e3)} | {n(r['R_dT2'], 1e3)} | {n(r['R_dP9'], 1e3)} | "
          f"{n(r['R_dT3'], 1e3)} | **{n(r['R_comp'], 1e3)}** | {n(r['R_os03_p3T1'], 1e3)} |")
