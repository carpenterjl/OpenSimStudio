"""Filter features from sparse direct-solve windows (verify_direct.m output) and from dense
sweeps evaluated the same way, for the mesh-convergence table.

Usage: python -I direct_features.py <results_dir> <out_csv> label=mel_mm:file.csv [...]

Minima: least-squares parabola in |S|^2 (linear power, smooth through a null) over the points
of the window; the minimum dB is 10*log10 of the parabola minimum (clipped at 1e-8).
Crossings: linear interpolation in dB between the two bracketing points.
"""
import csv, json, os, sys
import numpy as np

WIN = {  # feature: (quantity, fmin, fmax, kind)
    'S11_null_~4.9': ('S11', 4.80, 5.06, 'min'),
    'S21_-3dB_falling': ('S21', 5.40, 5.65, -3.0),
    'S21_-10dB_falling': ('S21', 5.90, 6.10, -10.0),
    'S21_stopband_min1_~7.1': ('S21', 6.95, 7.30, 'min'),
    'S21_stopband_min2_~8.1': ('S21', 7.95, 8.30, 'min'),
    'S11_null_~11': ('S11', 10.65, 11.40, 'min'),
}


def load(path):
    rows = list(csv.DictReader(open(path, newline='')))
    f = np.array([float(r['freq_GHz']) for r in rows])
    s11 = np.array([complex(float(r['S11_re']), float(r['S11_im'])) for r in rows])
    s21 = np.array([complex(float(r['S21_re']), float(r['S21_im'])) for r in rows])
    o = np.argsort(f)
    return f[o], s11[o], s21[o]


def feat(f, s, fmin, fmax, kind, dense):
    m = (f >= fmin - 1e-9) & (f <= fmax + 1e-9)
    ff, ss = f[m], s[m]
    if len(ff) < 2:
        return None
    if kind == 'min':
        p2 = np.abs(ss) ** 2
        if dense:   # restrict to +-0.04 GHz around the sampled minimum
            k = int(np.argmin(p2)); w = np.abs(ff - ff[k]) <= 0.04
            ff, p2 = ff[w], p2[w]
        else:       # use the 3 lowest points of the window
            k = np.argsort(p2)[:3]; ff, p2 = ff[k], p2[k]
        if len(ff) < 3:
            return None
        a, b, c = np.polyfit(ff, p2, 2)
        if a <= 0:
            k = int(np.argmin(p2)); return [round(float(ff[k]), 4), round(float(10 * np.log10(p2[k])), 2), 'sampled']
        f0 = -b / (2 * a); pmin = max(c - b * b / (4 * a), 1e-8)
        inside = ff.min() - 0.02 <= f0 <= ff.max() + 0.02
        return [round(float(f0), 4), round(float(10 * np.log10(pmin)), 2), 'parabola' if inside else 'extrapolated']
    db = 20 * np.log10(np.abs(ss))
    for i in range(len(ff) - 1):
        if db[i] >= kind > db[i + 1]:
            return round(float(ff[i] + (ff[i + 1] - ff[i]) * (db[i] - kind) / (db[i] - db[i + 1])), 4)
    # not bracketed: linear extrapolation from the two points nearest the level, if within 0.03 GHz
    if len(ff) >= 2 and not dense:
        k = np.argsort(np.abs(db - kind))[:2]
        k.sort()
        x0, x1, y0, y1 = ff[k[0]], ff[k[1]], db[k[0]], db[k[1]]
        if y1 != y0:
            fx = x0 + (kind - y0) * (x1 - x0) / (y1 - y0)
            if min(abs(fx - x0), abs(fx - x1)) <= 0.03:
                return [round(float(fx), 4), 'extrapolated']
    return None


def main():
    rdir, out = sys.argv[1], sys.argv[2]
    table = {}
    for arg in sys.argv[3:]:
        lab, rest = arg.split('=', 1)
        mel, fn = rest.split(':', 1)
        f, s11, s21 = load(os.path.join(rdir, fn))
        dense = len(f) > 200
        F = {'mel_mm': float(mel), 'source': fn, 'dense': dense}
        for name, (q, a, b, kind) in WIN.items():
            F[name] = feat(f, s11 if q == 'S11' else s21, a, b, kind, dense)
        for fq in [2.0, 3.0, 4.0]:
            k = np.where(np.abs(f - fq) < 1e-6)[0]
            F[f'S11_dB@{fq}'] = round(float(20 * np.log10(abs(s11[k[0]]))), 3) if len(k) else None
        table[lab] = F
    keys = ['mel_mm', 'source'] + list(WIN.keys()) + ['S11_dB@2.0', 'S11_dB@3.0', 'S11_dB@4.0']
    with open(out, 'w', newline='') as fh:
        w = csv.writer(fh)
        w.writerow(['label'] + keys)
        for lab, F in table.items():
            w.writerow([lab] + [json.dumps(F.get(k)) for k in keys])
    for lab, F in table.items():
        print(lab, json.dumps({k: F.get(k) for k in keys}))


if __name__ == '__main__':
    main()
