"""Gate FU-15 comparison table: MoM (MATLAB RF PCB Toolbox) vs digitised measured and FD-TD.

Usage: python -I final_compare.py <folder>
Reads (in <folder>/results): filter_features.json (analyze_filter.py), final_filter_mesh_convergence.csv
(convergence.py, prefix results/final), patch_features.json (analyze_patch.py), final_patch_mesh_convergence.csv
(convergence.py, prefix results/final).
Writes <folder>/comparison_summary.csv and <folder>/comparison_summary.json.
"""
import csv, json, os, sys

d = sys.argv[1]
R = os.path.join(d, 'results')
FF = json.load(open(os.path.join(R, 'filter_features.json')))['curves']
FC = list(csv.DictReader(open(os.path.join(R, 'final_filter_mesh_convergence.csv'), newline='')))
PF = json.load(open(os.path.join(R, 'patch_features.json')))
PC = list(csv.DictReader(open(os.path.join(R, 'final_patch_mesh_convergence.csv'), newline='')))


def num(x):
    try:
        return float(x)
    except (TypeError, ValueError):
        return None


def pct(a, b):
    return None if a is None or b is None else round(100.0 * (a - b) / b, 2)


rows = []


def add(case, feature, unit, meas, fdtd, mom_by_mesh, sens, note):
    finest = None
    for mel in sorted(mom_by_mesh, key=float):
        if mom_by_mesh[mel] is not None:
            finest = (mel, mom_by_mesh[mel]); break
    vals = [v for v in mom_by_mesh.values() if v is not None]
    rows.append({'case': case, 'feature': feature, 'unit': unit, 'measured': meas, 'fdtd_thesis': fdtd,
                 'mom_finest_mesh_mm': finest[0] if finest else None, 'mom_finest': finest[1] if finest else None,
                 'mom_min_over_meshes': min(vals) if vals else None, 'mom_max_over_meshes': max(vals) if vals else None,
                 'mom_by_mesh': json.dumps(mom_by_mesh), 'mom_sensitivity': json.dumps(sens),
                 'finest_vs_measured_pct': pct(finest[1], meas) if (finest and unit == 'GHz') else None,
                 'finest_vs_fdtd_pct': pct(finest[1], fdtd) if (finest and unit == 'GHz') else None,
                 'finest_minus_measured': round(finest[1] - meas, 3) if (finest and meas is not None and unit == 'dB') else None,
                 'finest_minus_fdtd': round(finest[1] - fdtd, 3) if (finest and fdtd is not None and unit == 'dB') else None,
                 'note': note})


# ---------------- filter (convergence table rows: 1.0 / 0.7 / 0.6 / 0.5 mm, board margin 3.948 mm, ports at 4.232 mm)
conv = {}
for r in FC:
    conv.setdefault(r['mesh_mm'], {})
    for k, v in r.items():
        if v not in ('', None, 'null') and (conv[r['mesh_mm']].get(k) in (None, '', 'null')):
            conv[r['mesh_mm']][k] = v


def cser(key):
    return {m: num(c.get(key)) for m, c in conv.items()}


m, f = FF['measured'], FF['fdtd']
s10, sL = FF.get('m100_gx10', {}), FF.get('m100_L8464', {})
add('filter', '|S21| -3 dB corner (falling)', 'GHz', m['S21_-3dB_falling'], f['S21_-3dB_falling'], cser('S21_-3dB_falling_GHz'),
    {'1.0mm_margin10': s10.get('S21_-3dB_falling'), '1.0mm_ports8.464': sL.get('S21_-3dB_falling')},
    '0.6 mm value extrapolated 9 MHz beyond the last direct point if so flagged in filter_mesh_convergence.csv')
add('filter', '|S21| -10 dB (falling)', 'GHz', m['S21_-10dB_falling'], f['S21_-10dB_falling'], cser('S21_-10dB_falling_GHz'),
    {'1.0mm_margin10': s10.get('S21_-10dB_falling'), '1.0mm_ports8.464': sL.get('S21_-10dB_falling')}, '')
lm = m['S21_stopband_local_minima']
add('filter', '|S21| stop-band minimum 1 (freq)', 'GHz', lm[0][0], None, cser('S21_stopband_min1_~7.1_GHz'),
    {'1.0mm_margin10': s10['S21_stopband_local_minima'][0][0], '1.0mm_ports8.464': sL['S21_stopband_local_minima'][0][0]},
    'FD-TD has no first minimum: shoulder of about -35.7 dB near 7.3 GHz')
add('filter', '|S21| stop-band minimum 1 (depth)', 'dB', lm[0][1], None, cser('S21_stopband_min1_~7.1_dB'),
    {'1.0mm_margin10': s10['S21_stopband_local_minima'][0][1], '1.0mm_ports8.464': sL['S21_stopband_local_minima'][0][1]},
    'measured dash-dot null depths are lower bounds')
fd_null = round(0.5 * (f['S21_-40dB_falling'] + f['S21_-40dB_rising']), 4)
add('filter', '|S21| stop-band minimum 2 / FD-TD null (freq)', 'GHz', lm[1][0], fd_null, cser('S21_stopband_min2_~8.1_GHz'),
    {'1.0mm_margin10': s10['S21_stopband_local_minima'][1][0], '1.0mm_ports8.464': sL['S21_stopband_local_minima'][1][0]},
    'FD-TD null is off the plot (< -48.5 dB); its frequency is the centre of the -40 dB crossings %.3f / %.3f GHz' % (f['S21_-40dB_falling'], f['S21_-40dB_rising']))
add('filter', '|S21| stop-band minimum 2 (depth)', 'dB', lm[1][1], None, cser('S21_stopband_min2_~8.1_dB'),
    {'1.0mm_margin10': s10['S21_stopband_local_minima'][1][1], '1.0mm_ports8.464': sL['S21_stopband_local_minima'][1][1]},
    'FD-TD depth unknown (below -48.5 dB, off the plot)')
add('filter', '|S11| null near 5 GHz (freq)', 'GHz', m['S11_null_4.3_5.6'][0], f['S11_null_4.3_5.6'][0], cser('S11_null_~4.9_GHz'),
    {'1.0mm_margin10': s10['S11_null_4.3_5.6'][0], '1.0mm_ports8.464': sL['S11_null_4.3_5.6'][0]},
    'measured point from Fig. 3.27 (p.110); the measured line is under the FD-TD line in Fig. 3.33 there')
add('filter', '|S11| null near 5 GHz (depth)', 'dB', m['S11_null_4.3_5.6'][1], f['S11_null_4.3_5.6'][1], cser('S11_null_~4.9_dB'),
    {'1.0mm_margin10': s10['S11_null_4.3_5.6'][1], '1.0mm_ports8.464': sL['S11_null_4.3_5.6'][1]}, 'measured depth is a lower bound')
add('filter', '|S11| null near 11 GHz (freq)', 'GHz', m['S11_null_9.8_12'][0], f['S11_null_9.8_12'][0], cser('S11_null_~11_GHz'),
    {'1.0mm_margin10': s10['S11_null_9.8_12'][0], '1.0mm_ports8.464': sL['S11_null_9.8_12'][0]}, '')
add('filter', '|S11| null near 11 GHz (depth)', 'dB', m['S11_null_9.8_12'][1], f['S11_null_9.8_12'][1], cser('S11_null_~11_dB'),
    {'1.0mm_margin10': s10['S11_null_9.8_12'][1], '1.0mm_ports8.464': sL['S11_null_9.8_12'][1]}, '')
for fq in ['2.0', '3.0', '4.0']:
    add('filter', 'passband |S11| at %s GHz (all meshes)' % fq, 'dB', m['S11_dB@' + fq], f['S11_dB@' + fq], cser('S11_dB@%s' % fq),
        {'1.0mm_margin10': s10.get('S11_dB@' + fq), '1.0mm_ports8.464': sL.get('S11_dB@' + fq)}, '')
# passband values from the dense sweeps (all meshes) where the convergence table has none
for fq in ['2.5', '3.5', '4.5']:
    add('filter', 'passband |S11| at %s GHz (dense sweeps)' % fq, 'dB', m['S11_dB@' + fq], f['S11_dB@' + fq],
        {'1.0': FF['m100']['S11_dB@' + fq], '0.7': FF['m070']['S11_dB@' + fq]},
        {'1.0mm_margin10': s10.get('S11_dB@' + fq), '1.0mm_ports8.464': sL.get('S11_dB@' + fq)}, '')
add('filter', 'passband worst |S11| 2-4.5 GHz (level)', 'dB', m['S11_worst_2_4.5'][1], f['S11_worst_2_4.5'][1],
    {'1.0': FF['m100']['S11_worst_2_4.5'][1], '0.7': FF['m070']['S11_worst_2_4.5'][1]}, {}, 'freq: meas %.3f, FD-TD %.3f, MoM 0.7 mm %.3f GHz' % (
        m['S11_worst_2_4.5'][0], f['S11_worst_2_4.5'][0], FF['m070']['S11_worst_2_4.5'][0]))
for fq in ['2.5', '3.0', '3.5', '4.8', '5.0']:
    add('filter', 'passband |S21| at %s GHz (dense sweeps)' % fq, 'dB', m['S21_dB@' + fq], f['S21_dB@' + fq],
        {'1.0': FF['m100']['S21_dB@' + fq], '0.7': FF['m070']['S21_dB@' + fq]}, {}, 'MoM is lossless (radiation only)')

# ---------------- patch
D = PF['digitised']
meas_f0 = D['measured_Fig4.14_RLC_fit']['f0_GHz']
fd = D['fdtd_Fig4.14']
pc6 = {r['mesh_mm']: r for r in PC if r['ground_margin_mm'] in ('6', '6.0') and r['feed_len_mm'] in ('16.75',)}
pc16 = {r['mesh_mm']: r for r in PC if r['ground_margin_mm'] == '16.75'}
pcL6 = {r['mesh_mm']: r for r in PC if r['feed_len_mm'] in ('6', '6.0')}
pc33 = {r['mesh_mm']: r for r in PC if r['ground_margin_mm'] == '33.5'}
sens = {'margin16.75_' + k + 'mm': num(v['f0_GHz']) for k, v in pc16.items()}
sens.update({'margin33.5_' + k + 'mm': num(v['f0_GHz']) for k, v in pc33.items()})
sens.update({'feed6mm_' + k + 'mm': num(v['f0_GHz']) for k, v in pcL6.items()})
sens.update({'eps_scaled_' + k + 'mm': num(v['f0_eps_scaled_GHz']) for k, v in pc6.items()})
add('patch', 'resonance f0 (parallel-RLC fit of Zin at the patch edge)', 'GHz', meas_f0, fd['RLC_fit_2.15_2.40']['f0_GHz'],
    {k: num(v['f0_GHz']) for k, v in pc6.items()}, sens,
    'measured: RLC fit to the 7 markers of Fig. 4.14 (Fig. 4.15 Smith points give %.4f); thesis text p.158: approximately 2.275 GHz' % D['measured_Fig4.15_RLC_fit']['f0_GHz'])
sens = {'margin16.75_' + k + 'mm': num(v['S11_min_dB']) for k, v in pc16.items()}
sens.update({'margin33.5_' + k + 'mm': num(v['S11_min_dB']) for k, v in pc33.items()})
sens.update({'feed6mm_' + k + 'mm': num(v['S11_min_dB']) for k, v in pcL6.items()})
best_meas = min(D['measured_S11_50ohm_from_Fig4.14'], key=lambda p: p[1])
add('patch', '|S11| minimum (50 ohm)', 'dB', best_meas[1], fd['S11_50ohm_edge_min'][1],
    {k: num(v['S11_min_dB']) for k, v in pc6.items()}, sens,
    'measured: lowest of the 7 sampled points (%.2f GHz, from Fig. 4.14 R,X); FD-TD: from digitised R,X (%.3f GHz). Not plotted in the thesis.' % (best_meas[0], fd['S11_50ohm_edge_min'][0]))
add('patch', '|S11| minimum frequency (50 ohm)', 'GHz', None, fd['S11_50ohm_edge_min'][0],
    {k: num(v['S11_min_GHz']) for k, v in pc6.items()}, {'margin16.75_' + k + 'mm': num(v['S11_min_GHz']) for k, v in pc16.items()}, '')
add('patch', 'R0 (RLC fit, ohm)', 'ohm', D['measured_Fig4.14_RLC_fit']['R0_ohm'], fd['RLC_fit_2.15_2.40']['R0_ohm'],
    {k: num(v['R0_ohm']) for k, v in pc6.items()}, {'margin16.75_' + k + 'mm': num(v['R0_ohm']) for k, v in pc16.items()}, '')
add('patch', 'Q (RLC fit)', '-', D['measured_Fig4.14_RLC_fit']['Q'], fd['RLC_fit_2.15_2.40']['Q'],
    {k: num(v['Q']) for k, v in pc6.items()}, {'margin16.75_' + k + 'mm': num(v['Q']) for k, v in pc16.items()}, '')

with open(os.path.join(d, 'comparison_summary.csv'), 'w', newline='') as fh:
    w = csv.DictWriter(fh, fieldnames=list(rows[0].keys())); w.writeheader(); w.writerows(rows)
json.dump(rows, open(os.path.join(d, 'comparison_summary.json'), 'w'), indent=1)
for r in rows:
    print('%-7s %-52s meas %-9s fdtd %-9s MoM(%s mm) %-9s range [%s, %s] dMeas%% %s dFDTD%% %s dMeas %s' % (
        r['case'], r['feature'], r['measured'], r['fdtd_thesis'], r['mom_finest_mesh_mm'], r['mom_finest'],
        r['mom_min_over_meshes'], r['mom_max_over_meshes'], r['finest_vs_measured_pct'], r['finest_vs_fdtd_pct'], r['finest_minus_measured']))
    print('        sens', r['mom_sensitivity'], '| by mesh', r['mom_by_mesh'])
