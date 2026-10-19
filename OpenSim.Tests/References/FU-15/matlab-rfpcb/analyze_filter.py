"""Feature extraction and comparison for the Sheen low-pass filter.

Usage:
  python -I analyze_filter.py <verify_dir> <results_dir> <out_prefix> label=csv [label=csv ...]

<verify_dir>  holds fig3_33_S11_dB_verified.csv and fig3_34_S21_dB_verified.csv (digitised
              thesis Figs. 3.33 / 3.34, p.115 / p.116; measured = dash-dot, FD-TD = solid).
label=csv     MoM result CSVs written by run_sweep.m (columns freq_GHz, S11_dB, S21_dB, ...).
Writes <out_prefix>_features.json and <out_prefix>_table.csv.

All features are computed with the same code for the digitised curves and for MoM.
Crossings use linear interpolation between neighbouring samples; minima use a 3-point
parabola on the dB values.  A digitised value is reported only when samples exist within
0.06 GHz on both sides (dash gaps in the measured curves are not bridged).
"""
import csv, json, os, sys
import numpy as np

HIDDEN = {  # ranges where the measured line is under the FD-TD line (thesis transcription caveat 1)
    'S11': [(4.68, 8.25), (10.97, 11.3), (17.75, 19.1)],
    'S21': [(8.68, 10.55)],
}


def load_dig(path, quantity):
    out = {}
    with open(path, newline='') as fh:
        for r in csv.DictReader(fh):
            if r['quantity'] != quantity:
                continue
            out.setdefault(r['curve'], []).append((float(r['freq_GHz']), float(r['value']), r['figure']))
    res = {}
    for k, rows in out.items():
        rows.sort()
        res[k] = (np.array([a for a, _, _ in rows]), np.array([b for _, b, _ in rows]), [c for _, _, c in rows])
    return res


def load_mom(path):
    f, s11, s21, p = [], [], [], []
    with open(path, newline='') as fh:
        for r in csv.DictReader(fh):
            f.append(float(r['freq_GHz']))
            s11.append(float(r['S11_dB']))
            s21.append(float(r['S21_dB']))
            a = complex(float(r['S11_re']), float(r['S11_im']))
            b = complex(float(r['S21_re']), float(r['S21_im']))
            p.append(abs(a) ** 2 + abs(b) ** 2)
    f = np.array(f); i = np.argsort(f)
    return f[i], np.array(s11)[i], np.array(s21)[i], np.array(p)[i]


def value_at(f, y, f0, maxgap=0.06):
    if f0 < f[0] or f0 > f[-1]:
        return None
    j = np.searchsorted(f, f0)
    if j < len(f) and abs(f[j] - f0) < 1e-9:
        return float(y[j])
    lo, hi = f[j - 1], f[j]
    if f0 - lo > maxgap or hi - f0 > maxgap:
        return None
    return float(y[j - 1] + (y[j] - y[j - 1]) * (f0 - lo) / (hi - lo))


def crossings(f, y, level, fmin, fmax, direction, maxgap=0.08):
    m = (f >= fmin) & (f <= fmax)
    ff, yy = f[m], y[m]
    out = []
    for k in range(len(ff) - 1):
        a, b = yy[k] - level, yy[k + 1] - level
        if ff[k + 1] - ff[k] > maxgap:
            continue
        if direction == 'falling' and a >= 0 > b or direction == 'rising' and a < 0 <= b:
            out.append(float(ff[k] + (ff[k + 1] - ff[k]) * a / (a - b)))
    return out


def crossing(f, y, level, fmin, fmax, direction, maxgap=0.08, last=False):
    c = crossings(f, y, level, fmin, fmax, direction, maxgap)
    if not c:
        return None
    return c[-1] if last else c[0]


def minimum(f, y, fmin, fmax):
    m = np.where((f >= fmin) & (f <= fmax))[0]
    if len(m) == 0:
        return None
    k = m[np.argmin(y[m])]
    fk, yk = float(f[k]), float(y[k])
    if 0 < k < len(f) - 1 and f[k + 1] - f[k - 1] < 0.1:
        x0, x1, x2 = f[k - 1], f[k], f[k + 1]
        y0, y1, y2 = y[k - 1], y[k], y[k + 1]
        den = (x0 - x1) * (x0 - x2) * (x1 - x2)
        A = (x2 * (y1 - y0) + x1 * (y0 - y2) + x0 * (y2 - y1)) / den
        B = (x2 * x2 * (y0 - y1) + x1 * x1 * (y2 - y0) + x0 * x0 * (y1 - y2)) / den
        if A > 0:
            xv = -B / (2 * A)
            if x0 <= xv <= x2:
                C = y1 - A * x1 * x1 - B * x1
                fk, yk = float(xv), float(min(yk, A * xv * xv + B * xv + C))
    return [round(fk, 4), round(yk, 3)]


def maximum(f, y, fmin, fmax):
    m = np.where((f >= fmin) & (f <= fmax))[0]
    if len(m) == 0:
        return None
    k = m[np.argmax(y[m])]
    return [round(float(f[k]), 4), round(float(y[k]), 3)]


def local_minima(f, y, fmin, fmax, below):
    m = np.where((f >= fmin) & (f <= fmax))[0]
    out = []
    for k in m[1:-1]:
        if y[k] < y[k - 1] and y[k] <= y[k + 1] and y[k] < below:
            # keep only minima that are the lowest within +-0.15 GHz
            w = (f >= f[k] - 0.15) & (f <= f[k] + 0.15)
            if y[k] <= y[w].min():
                out.append([round(float(f[k]), 4), round(float(y[k]), 3)])
    return out


def r(v, n=4):
    return None if v is None else round(v, n)


def features(f11, y11, f21, y21):
    F = {}
    for fq in [2.0, 2.5, 3.0, 3.5, 4.0, 4.5]:
        F[f'S11_dB@{fq}'] = r(value_at(f11, y11, fq), 3)
    F['S11_worst_2_4.5'] = maximum(f11, y11, 2.0, 4.5)
    F['S11_null_4.3_5.6'] = minimum(f11, y11, 4.3, 5.6)
    F['S11_null_9.8_12'] = minimum(f11, y11, 9.8, 12.0)
    F['S11_null_14.8_17'] = minimum(f11, y11, 14.8, 17.0)
    for fq in [2.0, 2.5, 3.0, 3.5, 4.0, 4.8, 5.0]:
        F[f'S21_dB@{fq}'] = r(value_at(f21, y21, fq), 3)
    F['S21_max_2_5.2'] = maximum(f21, y21, 2.0, 5.2)
    for lev in [-1, -3, -10, -20]:
        F[f'S21_{lev}dB_falling'] = r(crossing(f21, y21, lev, 4.5, 7.0, 'falling'))
    F['S21_-40dB_falling'] = r(crossing(f21, y21, -40, 6.0, 9.0, 'falling'))
    F['S21_-40dB_rising'] = r(crossing(f21, y21, -40, 6.0, 9.0, 'rising', last=True))
    F['S21_stopband_min_6.3_9'] = minimum(f21, y21, 6.3, 9.0)
    lm = local_minima(f21, y21, 6.3, 9.0, -25.0)
    F['S21_stopband_local_minima'] = lm
    F['S21_stopband_hump_between_first_two_minima'] = maximum(f21, y21, lm[0][0], lm[1][0]) if len(lm) >= 2 else None
    for fq in [6.5, 7.0, 7.3, 7.5, 8.0, 8.5, 9.0]:
        F[f'S21_dB@{fq}'] = r(value_at(f21, y21, fq), 3)
    F['S21_-20dB_rising'] = r(crossing(f21, y21, -20, 8.3, 9.6, 'rising'))
    F['S21_-3dB_rising'] = r(crossing(f21, y21, -3, 9.5, 11.5, 'rising'))
    F['S21_range_10.7_15'] = [r(float(np.min(y21[(f21 >= 10.7) & (f21 <= 15)])), 3),
                             r(float(np.max(y21[(f21 >= 10.7) & (f21 <= 15)])), 3)] if np.any((f21 >= 10.7) & (f21 <= 15)) else None
    F['S21_min_18_20'] = minimum(f21, y21, 18.0, 20.0)
    return F


def hidden_flag(name, F):
    """Return True if a measured feature lies in a range where the measured line is hidden."""
    q = 'S11' if name.startswith('S11') else 'S21'
    v = F.get(name)
    fr = None
    if isinstance(v, list) and v and not isinstance(v[0], list):
        fr = v[0]
    elif isinstance(v, (int, float)) and ('falling' in name or 'rising' in name):
        fr = v
    elif '@' in name:
        fr = float(name.split('@')[1])
    if fr is None:
        return False
    return any(a <= fr <= b for a, b in HIDDEN[q])


def main():
    vdir, rdir, prefix = sys.argv[1], sys.argv[2], sys.argv[3]
    d11 = load_dig(os.path.join(vdir, 'fig3_33_S11_dB_verified.csv'), 'S11_dB')
    d21 = load_dig(os.path.join(vdir, 'fig3_34_S21_dB_verified.csv'), 'S21_dB')
    out = {'note': __doc__.strip().splitlines()[0], 'sources': {
        'S11': 'Fig. 3.33, thesis p.115 = PDF 115 (plus Figs. 3.27/3.29 p.110/111 measured points)',
        'S21': 'Fig. 3.34, thesis p.116 = PDF 116 (plus Fig. 3.28 p.110 measured points)'}, 'curves': {}}
    out['curves']['measured'] = features(d11['measured'][0], d11['measured'][1], d21['measured'][0], d21['measured'][1])
    out['curves']['fdtd'] = features(d11['fdtd'][0], d11['fdtd'][1], d21['fdtd'][0], d21['fdtd'][1])
    out['measured_hidden_under_fdtd'] = [k for k in out['curves']['measured'] if hidden_flag(k, out['curves']['measured'])]
    labels = []
    for arg in sys.argv[4:]:
        lab, path = arg.split('=', 1)
        f, s11, s21, p = load_mom(os.path.join(rdir, path))
        F = features(f, s11, f, s21)
        m = (f >= 2) & (f <= 4)
        F['power_|S11|^2+|S21|^2_2_4'] = [r(float(p[m].min()), 4), r(float(p[m].max()), 4)]
        F['power_|S11|^2+|S21|^2_min_1_20'] = [r(float(f[np.argmin(p)]), 3), r(float(p.min()), 4)]
        F['f_range_GHz'] = [r(float(f[0]), 3), r(float(f[-1]), 3), r(float(np.median(np.diff(f))), 4)]
        out['curves'][lab] = F
        labels.append(lab)
    with open(prefix + '_features.json', 'w') as fh:
        json.dump(out, fh, indent=1)
    keys = list(out['curves']['measured'].keys())
    with open(prefix + '_table.csv', 'w', newline='') as fh:
        w = csv.writer(fh)
        w.writerow(['feature', 'measured', 'measured_hidden_under_fdtd', 'fdtd'] + labels)
        for k in keys:
            row = [k, json.dumps(out['curves']['measured'].get(k)), k in out['measured_hidden_under_fdtd'],
                   json.dumps(out['curves']['fdtd'].get(k))]
            row += [json.dumps(out['curves'][lab].get(k)) for lab in labels]
            w.writerow(row)
        for k in ['power_|S11|^2+|S21|^2_2_4', 'power_|S11|^2+|S21|^2_min_1_20', 'f_range_GHz']:
            w.writerow([k, '', '', ''] + [json.dumps(out['curves'][lab].get(k)) for lab in labels])
    # console summary
    print('%-28s %-22s %-22s ' % ('feature', 'measured', 'fdtd') + ' '.join('%-22s' % l for l in labels))
    for k in keys + ['power_|S11|^2+|S21|^2_2_4']:
        vals = [out['curves']['measured'].get(k), out['curves']['fdtd'].get(k)] + [out['curves'][l].get(k) for l in labels]
        flag = '*' if k in out['measured_hidden_under_fdtd'] else ' '
        print('%-27s%s ' % (k, flag) + ' '.join('%-22s' % json.dumps(v) for v in vals))


if __name__ == '__main__':
    main()
