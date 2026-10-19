"""Mesh-convergence tables for the filter and the patch, with an eps_eff-scaled indication.

Usage: python -I convergence.py <results_dir> <patch_features.json> <filter_convergence.csv> <out_prefix>

Scaled indication (secondary, heuristic): a feature frequency f(mel) is multiplied by
sqrt(eps_eff_line_MoM(mel) / eps_eff_line_closed_form), where eps_eff_line_MoM comes from the MoM
two-length thru-line diagnostic at the same MaxEdgeLength (diag_thru_BLx20.csv for the patch
substrate at 2.28 GHz; diag_line_filter.csv for the filter substrate at 5 GHz) and the closed form
from txlineMicrostrip (results/txline_check.txt).  It assumes the discretisation error of the
substrate acts like a uniform change of eps_eff.
"""
import csv, json, os, re, sys
import numpy as np


def line_eps(path, fsel):
    out = {}
    for r in csv.DictReader(open(path, newline='')):
        if abs(float(r['freq_GHz']) - fsel) < 1e-6:
            out[round(float(r['mel_mm']), 3)] = float(r['eps_eff'])
    return out


def closed_form(path):
    vals = {}
    for line in open(path):
        m = re.match(r'(.+?) @ ([\d.]+) GHz: Z0 = ([\d.]+) ohm, eps_eff = ([\d.]+)', line)
        if m:
            vals[(m.group(1).split(',')[0].strip(), float(m.group(2)))] = float(m.group(4))
    return vals


def main():
    rdir, pjson, fconv, prefix = sys.argv[1:5]
    cf = closed_form(os.path.join(rdir, 'txline_check.txt'))
    ee_patch_cf = cf[('patch feed 4.48 mm', 2.28)]
    ee_filt_cf = cf[('filter line 2.413 mm', 5.0)]
    ep = line_eps(os.path.join(rdir, 'diag_thru_BLx20.csv'), 2.28)
    ef = line_eps(os.path.join(rdir, 'diag_line_filter.csv'), 5.0)
    # ---------------- patch
    P = json.load(open(pjson))
    rows = []
    meta = {  # label: (mesh mm, ground margin mm, feed length mm)
        'm400': (4.0, 16.75, 16.75), 'm300': (3.0, 16.75, 16.75),
        'm400g6': (4.0, 6, 16.75), 'm300g6': (3.0, 6, 16.75), 'm250g6': (2.5, 6, 16.75),
        'm200g6': (2.0, 6, 16.75), 'm160g6': (1.6, 6, 16.75),
        'm200g6L6': (2.0, 6, 6.0), 'm130g6L6': (1.3, 6, 6.0), 'm400g335': (4.0, 33.5, 16.75)}
    for lab, F in P['mom'].items():
        if lab not in meta:
            continue
        mel, m, lf = meta[lab]
        A = F['deembed_A_thesis_eq4.5']
        if F.get('direct_points'):
            fit = A['RLC_fit_direct_points']; s11 = A['model_S11_50ohm_min']; kind = 'direct points + RLC fit'
        else:
            fit = A['RLC_fit_2.15_2.40']; s11 = F['S11_port_min']; kind = 'interp sweep'
        sc = np.sqrt(ep[mel] / ee_patch_cf) if mel in ep else None
        rows.append({'label': lab, 'mesh_mm': mel, 'ground_margin_mm': m, 'feed_len_mm': lf, 'data': kind,
                     'f0_GHz': fit['f0_GHz'], 'R0_ohm': fit['R0_ohm'], 'Q': fit['Q'], 'fit_rms_ohm': fit['rms_resid_ohm'],
                     'S11_min_GHz': s11[0], 'S11_min_dB': s11[1],
                     'line_eps_eff_MoM': ep.get(mel), 'line_eps_eff_closed_form': ee_patch_cf,
                     'f0_eps_scaled_GHz': round(fit['f0_GHz'] * sc, 4) if sc else None})
    rows.sort(key=lambda r: (r['ground_margin_mm'], r['feed_len_mm'], -r['mesh_mm']))
    with open(prefix + '_patch_mesh_convergence.csv', 'w', newline='') as fh:
        w = csv.DictWriter(fh, fieldnames=list(rows[0].keys())); w.writeheader(); w.writerows(rows)
    for r in rows:
        print('PATCH', r)
    # ---------------- filter
    frows = []
    for r in csv.DictReader(open(fconv, newline='')):
        mel = json.loads(r['mel_mm'])
        out = {'label': r['label'], 'mesh_mm': mel, 'source': json.loads(r['source'])}
        sc = np.sqrt(ef[round(mel, 3)] / ee_filt_cf) if round(mel, 3) in ef else None
        out['line_eps_eff_MoM_5GHz'] = ef.get(round(mel, 3)); out['line_eps_eff_closed_form_5GHz'] = ee_filt_cf
        for k in ['S11_null_~4.9', 'S21_-3dB_falling', 'S21_-10dB_falling', 'S21_stopband_min1_~7.1', 'S21_stopband_min2_~8.1', 'S11_null_~11']:
            v = json.loads(r[k])
            fq = v[0] if isinstance(v, list) else v
            out[k + '_GHz'] = fq
            if isinstance(v, list) and len(v) > 1 and isinstance(v[1], (int, float)):
                out[k + '_dB'] = v[1]
            if isinstance(v, list) and isinstance(v[-1], str):
                out[k + '_note'] = v[-1]
            out[k + '_eps_scaled_GHz'] = round(fq * sc, 4) if (sc and fq) else None
        for k in ['S11_dB@2.0', 'S11_dB@3.0', 'S11_dB@4.0']:
            out[k] = json.loads(r[k])
        frows.append(out)
    keys = []
    for o in frows:
        for k in o:
            if k not in keys:
                keys.append(k)
    with open(prefix + '_filter_mesh_convergence.csv', 'w', newline='') as fh:
        w = csv.DictWriter(fh, fieldnames=keys); w.writeheader(); w.writerows(frows)
    for o in frows:
        print('FILTER', o)


if __name__ == '__main__':
    main()
