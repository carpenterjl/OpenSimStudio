"""features_verified.json: key features recomputed from the *_verified CSVs (plus verifier marker/Smith readings)."""
import csv, json, sys
import numpy as np
M = sys.argv[1]


def load(p, q):
    d = {}
    for r in csv.DictReader(open(p)):
        if r['quantity'] == q:
            d.setdefault(r['curve'], []).append((float(r['freq_GHz']), float(r['value'])))
    return {k: np.array(sorted(v)) for k, v in d.items()}


s11 = load(M + '/fig3_33_S11_dB_verified.csv', 'S11_dB')
s21 = load(M + '/fig3_34_S21_dB_verified.csv', 'S21_dB')


def at(A, f, maxgap=0.06):
    i = np.searchsorted(A[:, 0], f)
    if i == 0 or i >= len(A):
        return None
    if A[i, 0] - A[i - 1, 0] > maxgap:
        return dict(value=round(float(np.interp(f, A[:, 0], A[:, 1])), 3), note='interpolated across a dash gap %.2f-%.2f GHz' % (A[i - 1, 0], A[i, 0]))
    return round(float(np.interp(f, A[:, 0], A[:, 1])), 3)


def cross(A, v, f0, f1):
    m = (A[:, 0] >= f0) & (A[:, 0] <= f1); B = A[m]
    for i in range(len(B) - 1):
        if (B[i, 1] - v) * (B[i + 1, 1] - v) <= 0 and B[i, 1] != B[i + 1, 1]:
            f = B[i, 0] + (v - B[i, 1]) * (B[i + 1, 0] - B[i, 0]) / (B[i + 1, 1] - B[i, 1])
            note = 'across dash gap %.3f-%.3f GHz' % (B[i, 0], B[i + 1, 0]) if B[i + 1, 0] - B[i, 0] > 0.06 else ''
            return dict(freq_GHz=round(float(f), 3), note=note) if note else round(float(f), 3)
    return None


def mx(A, f0, f1):
    m = (A[:, 0] >= f0) & (A[:, 0] <= f1); B = A[m]; i = int(np.argmax(B[:, 1]))
    return dict(freq_GHz=round(float(B[i, 0]), 3), value=round(float(B[i, 1]), 3))


me = s21['measured']; fd = s21['fdtd']
feat = {'source': 'computed by features_v.py from fig3_33_S11_dB_verified.csv / fig3_34_S21_dB_verified.csv (verifier)',
        'fig3_34_measured': {
            'value_at_2.0GHz': at(me, 2.0), 'value_at_2.5GHz': at(me, 2.5), 'value_at_3.0GHz': at(me, 3.0),
            'value_at_3.5GHz': at(me, 3.5), 'value_at_4.0GHz': at(me, 4.0), 'value_at_4.8GHz': at(me, 4.8), 'value_at_5.0GHz': at(me, 5.0),
            'passband_max_2_5.2GHz': mx(me, 1.95, 5.2),
            'minus1dB_falling_GHz': cross(me, -1.0, 4.5, 5.6), 'minus3dB_falling_GHz': cross(me, -3.0, 5.3, 5.8),
            'minus10dB_falling_GHz': cross(me, -10.0, 5.8, 6.3), 'minus20dB_falling_GHz': cross(me, -20.0, 6.2, 6.7),
            'note': 'the +0.2 dB "measured peak at 4.83 GHz" in the original features.json belongs to the FD-TD line; '
                    'the measured dash at 4.71-5.13 GHz is at -0.43 dB (Figs. 3.34, 3.28 and 3.30 agree)'},
        'fig3_34_fdtd': {'passband_max_2_5.2GHz': mx(fd, 1.95, 5.2), 'minus1dB_falling_GHz': cross(fd, -1.0, 5.0, 5.6),
                         'minus3dB_falling_GHz': cross(fd, -3.0, 5.3, 5.8)},
        }
# power balance (measured) only where both curves have real ink within 0.03 GHz
pb = []
for f in np.round(np.arange(2.0, 4.01, 0.05), 2):
    a = s11['measured']; b = me
    ia = np.min(np.abs(a[:, 0] - f)); ib = np.min(np.abs(b[:, 0] - f))
    if ia < 0.03 and ib < 0.03:
        v11 = np.interp(f, a[:, 0], a[:, 1]); v21 = np.interp(f, b[:, 0], b[:, 1])
        pb.append((float(f), round(float(10 ** (v11 / 10) + 10 ** (v21 / 10)), 3)))
feat['measured_power_balance_2_4GHz'] = dict(points=pb, min=min(p[1] for p in pb), max=max(p[1] for p in pb))
pbf = []
for f in np.round(np.arange(2.0, 4.51, 0.25), 2):
    v11 = np.interp(f, s11['fdtd'][:, 0], s11['fdtd'][:, 1]); v21 = np.interp(f, fd[:, 0], fd[:, 1])
    pbf.append((float(f), round(float(10 ** (v11 / 10) + 10 ** (v21 / 10)), 3)))
feat['fdtd_power_balance_2_4.5GHz'] = dict(points=pbf, min=min(p[1] for p in pbf), max=max(p[1] for p in pbf))
feat['fig3_33'] = {'fdtd_low_frequency_null': dict(freq_GHz=0.13, value=-33.26, u_value=0.25,
                                                    evidence='ink bottom -33.44..-33.55 dB over 0.09-0.18 GHz (Fig. 3.33 p.115), plus half line width 0.21 dB'),
                   'measured_null_10.8GHz': dict(freq_GHz=10.84, value=-40.71, evidence='deepest ink -40.92 dB + half line width'),
                   'measured_19.9GHz': at(s11['measured'], 19.9), 'measured_19.95GHz': at(s11['measured'], 19.95)}
# Fig 4.14 / 4.15 cross-check with the corrected R(2.20)
R = {2.10: 2.57, 2.15: 8.41, 2.20: 21.92, 2.25: 117.11, 2.30: 118.55, 2.35: 17.72, 2.40: 7.70}
X = {2.10: 41.98, 2.15: 59.79, 2.20: 94.49, 2.25: 192.50, 2.30: -180.43, 2.35: -93.66, 2.40: -44.66}
sm = json.load(open(M + '/work/smith_v.json'))['markers']
xc = []
for (f, r), m in zip(sorted(R.items()), sm):
    z = complex(r, X[f]); g = (z - 50) / (z + 50); G = complex(m['G_re'], m['G_im']); Zs = 50 * (1 + G) / (1 - G)
    xc.append(dict(freq_GHz=f, R_fig414_verifier=r, X_fig414_verifier=X[f], S11_dB_from_fig414=round(20 * np.log10(abs(g)), 3),
                   G_re_smith_verifier=round(m['G_re'], 4), G_im_smith_verifier=round(m['G_im'], 4), R_smith=round(Zs.real, 1),
                   X_smith=round(Zs.imag, 1), S11_dB_smith=round(20 * np.log10(abs(G)), 3)))
feat['fig4_14_vs_fig4_15_crosscheck_verifier'] = xc
feat['fig4_14_measured_X_zero_linear_GHz'] = round(2.25 + 0.05 * 192.50 / (192.50 + 180.43), 4)
json.dump(feat, open(M + '/features_verified.json', 'w'), indent=1)
print(json.dumps(feat, indent=1))
