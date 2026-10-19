"""Features of OpenSim's Sheen filter and patch (filter_sweep.csv, patch_variants.csv and mesh_study.txt in this folder)
against the digitised thesis curves (../sheen-thesis/*_verified.csv and the verifier's marker readings).

Usage: python -I fu15_analyze.py > analysis.txt

Feature definitions
  filter, OpenSim: the -3 dB corner of |S21| by linear interpolation in dB between sweep points; a null as the closest
    approach to zero of a quadratic through the three samples around the smallest |S| (real and imaginary parts fitted
    separately); and the test's rule, the closest approach to zero on the chord between two samples astride the null.
  filter, thesis curves: the same definitions as ../matlab-rfpcb/analyze_filter.py (crossings by linear interpolation,
    minima by a 3-point parabola on the dB values, the stop-band minima as local minima, the FD-TD |S21| null near
    8 GHz as the centre of its -40 dB crossings, because the null is off the plot).
  patch: the two-point rule of the test (B = 0 between 2.25 and 2.30 GHz, R0 = 1/G there, Q = (f0/2) R0 dB/df) and a
    five-parameter fit Z = R0/(1 + jQ(f/f0 - f0/f)) + j(X0 + X1 (f - 2.275)) where all seven measured frequencies exist.
"""
import csv, os, re
import numpy as np
from scipy.optimize import least_squares

HERE = os.path.dirname(os.path.abspath(__file__))
THESIS = os.path.join(HERE, '..', 'sheen-thesis')


def rows(path):
    with open(path, newline='', encoding='utf-8-sig') as fh:
        return list(csv.DictReader(fh))


def curve(name, quantity, which):
    pts = sorted((float(r['freq_GHz']), float(r['value'])) for r in rows(os.path.join(THESIS, name))
                 if r['curve'] == which and r['quantity'] == quantity)
    return np.array(pts)


def db(z):
    return 20 * np.log10(abs(z))


def pct(a, b):
    return (a / b - 1) * 100


# ------------------------------------------------------------------ thesis curves (analyze_filter.py definitions)
def t_crossing(c, level, fmin, fmax, maxgap=0.08):
    f, y = c[:, 0], c[:, 1]
    m = (f >= fmin) & (f <= fmax); ff, yy = f[m], y[m]
    for k in range(len(ff) - 1):
        a, b = yy[k] - level, yy[k + 1] - level
        if ff[k + 1] - ff[k] <= maxgap and a >= 0 > b:
            return float(ff[k] + (ff[k + 1] - ff[k]) * a / (a - b))
    return None


def t_crossing_rising_last(c, level, fmin, fmax, maxgap=0.08):
    f, y = c[:, 0], c[:, 1]
    m = (f >= fmin) & (f <= fmax); ff, yy = f[m], y[m]
    out = None
    for k in range(len(ff) - 1):
        a, b = yy[k] - level, yy[k + 1] - level
        if ff[k + 1] - ff[k] <= maxgap and a < 0 <= b:
            out = float(ff[k] + (ff[k + 1] - ff[k]) * a / (a - b))
    return out


def t_minimum(c, fmin, fmax):
    f, y = c[:, 0], c[:, 1]
    m = np.where((f >= fmin) & (f <= fmax))[0]
    k = m[np.argmin(y[m])]
    fk, yk = float(f[k]), float(y[k])
    if 0 < k < len(f) - 1 and f[k + 1] - f[k - 1] < 0.1:
        x0, x1, x2 = f[k - 1], f[k], f[k + 1]; y0, y1, y2 = y[k - 1], y[k], y[k + 1]
        den = (x0 - x1) * (x0 - x2) * (x1 - x2)
        A = (x2 * (y1 - y0) + x1 * (y0 - y2) + x0 * (y2 - y1)) / den
        B = (x2 * x2 * (y0 - y1) + x1 * x1 * (y2 - y0) + x0 * x0 * (y1 - y2)) / den
        if A > 0:
            xv = -B / (2 * A)
            if x0 <= xv <= x2:
                C = y1 - A * x1 * x1 - B * x1
                fk, yk = float(xv), float(min(yk, A * xv * xv + B * xv + C))
    return fk, yk


def t_local_minima(c, fmin, fmax, below):
    f, y = c[:, 0], c[:, 1]
    out = []
    for k in np.where((f >= fmin) & (f <= fmax))[0][1:-1]:
        if y[k] < y[k - 1] and y[k] <= y[k + 1] and y[k] < below:
            w = (f >= f[k] - 0.15) & (f <= f[k] + 0.15)
            if y[k] <= y[w].min():
                out.append((float(f[k]), float(y[k])))
    return out


def at(c, f):
    return float(np.interp(f, c[:, 0], c[:, 1]))


s21m, s21f = curve('fig3_34_S21_dB_verified.csv', 'S21_dB', 'measured'), curve('fig3_34_S21_dB_verified.csv', 'S21_dB', 'fdtd')
s11m, s11f = curve('fig3_33_S11_dB_verified.csv', 'S11_dB', 'measured'), curve('fig3_33_S11_dB_verified.csv', 'S11_dB', 'fdtd')
lm = t_local_minima(s21m, 6.3, 9.0, -25.0)
f40 = (t_crossing(s21f, -40, 6.0, 9.0), t_crossing_rising_last(s21f, -40, 6.0, 9.0))
REF = {  # feature: (measured GHz, measured dB, FD-TD GHz, FD-TD dB)
    'corner': (t_crossing(s21m, -3, 4.5, 7.0), -3.0, t_crossing(s21f, -3, 4.5, 7.0), -3.0),
    's11_5': t_minimum(s11m, 4.3, 5.6) + t_minimum(s11f, 4.3, 5.6),
    's21_7': lm[0] + (None, None),
    's21_8': lm[1] + ((f40[0] + f40[1]) / 2, None),
    's11_11': t_minimum(s11m, 9.8, 12.0) + t_minimum(s11f, 9.8, 12.0),
}
print('== thesis features from the verified digitisation (Figs. 3.33, 3.34)')
for k, (fm, vm, ff, vf) in REF.items():
    print(f'  {k:7s} measured {fm:.4f} GHz ({vm:.2f} dB)   FD-TD ' + (f'{ff:.4f} GHz' if ff else '-') +
          (f' ({vf:.2f} dB)' if vf else ''))
print(f'  FD-TD |S21| -40 dB crossings {f40[0]:.4f} and {f40[1]:.4f} GHz (its null near 8 GHz is off the plot)')

# ------------------------------------------------------------------ OpenSim filter
filt = {}
for r in rows(os.path.join(HERE, 'filter_sweep.csv')):
    v = {k: float(x) for k, x in r.items()}
    filt[v['f_GHz']] = dict(s11=complex(v['s11_50_re'], v['s11_50_im']), s21=complex(v['s21_50_re'], v['s21_50_im']),
                            o11=complex(v['s11_line_re'], v['s11_line_im']), o21=complex(v['s21_line_re'], v['s21_line_im']),
                            z0=v['cal_z0_re'], eeff=v['eps_eff'], eeff2d=v['eps_eff_2d'])
fs = sorted(filt)
mesh = rows(os.path.join(HERE, 'filter_sweep.csv'))[0]
print(f"\n== OpenSim filter, mesh {mesh['max_edge_mm']}/{mesh['min_edge_mm']}/{mesh['growth']} mm, {len(fs)} frequencies")
print('   f GHz   50-ohm |S11| |S21|   own-Z0 |S11| |S21|   measured |S11| |S21|   FD-TD |S11| |S21|   cal Z0  eps_eff (2D)')
for f in fs:
    r = filt[f]
    print(f"  {f:6.2f}   {db(r['s11']):7.2f} {db(r['s21']):7.2f}      {db(r['o11']):7.2f} {db(r['o21']):7.2f}       "
          f"{at(s11m, f):7.2f} {at(s21m, f):7.2f}      {at(s11f, f):7.2f} {at(s21f, f):7.2f}   {r['z0']:.2f}  {r['eeff']:.4f} ({r['eeff2d']:.4f})")
print('  (measured/FD-TD columns: np.interp of the digitised points, across dash gaps and hidden stretches; for orientation only)')


def crossing(xs, vals, level):
    for i in range(len(xs) - 1):
        if (vals[i] - level) * (vals[i + 1] - level) <= 0 and vals[i] != vals[i + 1]:
            return xs[i] + (level - vals[i]) * (xs[i + 1] - xs[i]) / (vals[i + 1] - vals[i])
    return None


def null_quadratic(xs, zs, lo, hi):
    idx = [i for i, f in enumerate(xs) if lo <= f <= hi]
    k = min(idx, key=lambda i: abs(zs[i]))
    k = min(max(k, idx[0] + 1), idx[-1] - 1)
    ff, zz = np.array(xs[k - 1:k + 2]), np.array(zs[k - 1:k + 2])
    cr = np.polyfit(ff, zz.real, 2); ci = np.polyfit(ff, zz.imag, 2)
    g = np.linspace(ff[0], ff[-1], 4001)
    m = np.abs(np.polyval(cr, g) + 1j * np.polyval(ci, g))
    j = int(np.argmin(m))
    return float(g[j]), float(20 * np.log10(m[j]))


def null_chord(f1, s1, f2, s2):
    ch = s2 - s1
    t = -(s1.conjugate() * ch).real / abs(ch) ** 2
    return f1 + t * (f2 - f1), db(s1 + t * ch), t


def chord_pair(xs, zs, fq):
    """The adjacent sweep pair astride the quadratic's minimum fq (t inside [0, 1])."""
    for a, b, za, zb in zip(xs, xs[1:], zs, zs[1:]):
        if a <= fq <= b:
            return a, b, null_chord(a, za, b, zb)
    return None


TEST_PAIRS = {'corner': (5.55, 5.60), 's11_5': (4.95, 5.05), 's21_7': (7.0, 7.1), 's21_8': (8.2, 8.3), 's11_11': (10.9, 11.0)}
WIN = {'s11_5': ('11', 4.75, 5.25), 's21_7': ('21', 6.75, 7.35), 's21_8': ('21', 7.75, 8.35), 's11_11': ('11', 10.55, 11.35)}
for key, lab in (('s', '50 ohm'), ('o', "line's own Z0 (as the test)")):
    print(f'\n  [{lab}]')
    band = [f for f in fs if 5.4 <= f <= 5.8]
    c = crossing(band, [db(filt[f][key + '21']) for f in band], -3.0)
    fm, _, ff, _ = REF['corner']
    print(f'    -3 dB corner {c:.4f} GHz: vs measured {pct(c, fm):+.2f} %, vs FD-TD {pct(c, ff):+.2f} %')
    for name, (q, lo, hi) in WIN.items():
        zs = [filt[f][key + q] for f in fs]
        fq, dq = null_quadratic(fs, zs, lo, hi)
        fm, vm, ff, vf = REF[name]
        vs = lambda x: f'vs measured {pct(x, fm):+.2f} %' + (f', vs FD-TD {pct(x, ff):+.2f} %' if ff else '')
        print(f'    {name:7s} quadratic       {fq:.4f} GHz ({dq:6.1f} dB): {vs(fq)}')
        a, b, (fc, dc, t) = chord_pair(fs, zs, fq)
        print(f'    {"":7s} chord {a:5.2f}-{b:5.2f} {fc:.4f} GHz ({dc:6.1f} dB): {vs(fc)}; {abs(fc - fq) * 1e3:.1f} MHz from the quadratic')
        tp = TEST_PAIRS[name]
        if tp != (a, b):
            print(f'    {"":7s} the test pair {tp[0]}-{tp[1]} GHz is not in the sweep')
print('  calibration at 3 GHz: eps_eff %.4f against the 2D solve %.4f (%+.2f %%)'
      % (filt[3.0]['eeff'], filt[3.0]['eeff2d'], pct(filt[3.0]['eeff'], filt[3.0]['eeff2d'])))

# ------------------------------------------------------------------ mesh study (mesh_study.txt)
print('\n== filter mesh study and calibration lines (mesh_study.txt)')
print('  run       mesh mm        f GHz  circuit unknowns  dut s   cal Z0  eps_eff (2D, diff)       50-ohm |S11| |S21|  own-Z0 |S11| |S21|')
run = None
LINE = re.compile(r'^(?:mesh ([\d./]+) )?f ([\d.]+) GHz: unknowns \d+/\d+/(\d+), (?:table [\d.]+ s, cal [\d.]+ s, dut )?([\d.]+) s; '
                  r'line Z0 <([-\d.]+); [-\d.]+> \(2D [\d.]+\), eeff ([\d.]+) \(2D ([\d.]+)\).*?\|S11\| ([-\d.]+) dB, \|S21\| ([-\d.]+) dB;'
                  r'(?: own-Z0 ([-\d.]+)/([-\d.]+);)?')
for line in open(os.path.join(HERE, 'mesh_study.txt'), encoding='utf-8'):
    if line.startswith('== '):
        run = line[3:].strip()
    m = LINE.match(line)
    if m and 'Zedge' not in line:
        g = m.groups()
        own = f'{float(g[9]):7.2f} {float(g[10]):6.2f}' if g[9] else '      -      -'
        print(f'  {run:9s} {g[0] or "0.4/0.1/1.3":13s} {float(g[1]):6.2f}  {g[2]:>15s} {float(g[3]):6.1f}   {float(g[4]):6.2f}  {float(g[5]):.4f} '
              f'({float(g[6]):.4f}, {pct(float(g[5]), float(g[6])):+.2f} %)   {float(g[7]):7.2f} {float(g[8]):6.2f}    {own}')
print('  (explore1: calibration lines 6 and 11 mm, and its time is the whole frequency point; explore2: 6 and 12 mm)')


# ------------------------------------------------------------------ patch
def resonance(f1, z1, f2, z2):
    y1, y2 = 1 / z1, 1 / z2
    if not (y1.imag < 0 < y2.imag):
        return None
    t = -y1.imag / (y2.imag - y1.imag); f0 = f1 + t * (f2 - f1)
    r0 = 1 / (y1.real + t * (y2.real - y1.real))
    return f0, r0, f0 / 2 * r0 * (y2.imag - y1.imag) / (f2 - f1)


def rlc_fit(f, Z):
    f = np.asarray(f); Z = np.asarray(Z)

    def res(p):
        f0, R0, Q, X0, X1 = p
        m = R0 / (1 + 1j * Q * (f / f0 - f0 / f)) + 1j * (X0 + X1 * (f - 2.275))
        return np.concatenate([(m - Z).real, (m - Z).imag])
    best = None
    for f0g in [2.22, 2.25, 2.275, 2.30]:
        for Qg in [30, 60, 100]:
            for R0g in [150, 300, 500]:
                s = least_squares(res, [f0g, R0g, Qg, 0.0, 0.0], bounds=([2.1, 10, 5, -200, -5000], [2.45, 3000, 1000, 200, 5000]))
                if best is None or s.cost < best.cost:
                    best = s
    p = best.x
    return f'f0 {p[0]:.5f} GHz, R0 {p[1]:.1f} ohm, Q {p[2]:.1f}, X0 {p[3]:.2f}, X1 {p[4]:.1f}, rms {np.sqrt(2 * best.cost / len(f)):.2f} ohm'


def fmt(r):
    return 'B does not straddle zero' if r is None else f'f0 {r[0]:.5f} GHz, R0 {r[1]:.1f} ohm, Q {r[2]:.1f}'


SEVEN = [2.10, 2.15, 2.20, 2.25, 2.30, 2.35, 2.40]
z414 = rows(os.path.join(THESIS, 'fig4_14_Zin_verified.csv'))
meas = {}
for r in z414:
    if r['curve'] == 'measured':
        meas.setdefault(round(float(r['freq_GHz']), 2), {})[r['quantity']] = float(r['value'])
meas = {f: complex(v['Zin_R_ohm'], v['Zin_X_ohm']) for f, v in sorted(meas.items())}
Rf, Xf = curve('fig4_14_Zin_verified.csv', 'Zin_R_ohm', 'fdtd'), curve('fig4_14_Zin_verified.csv', 'Zin_X_ohm', 'fdtd')
fd = {f: complex(at(Rf, f), at(Xf, f)) for f in SEVEN}
ver = {}
for r in rows(os.path.join(THESIS, 'my_checkpoints_fig4_14_4_15.csv')):
    if r['figure'] == 'Fig. 4.14':
        ver.setdefault(float(r['freq_GHz']), {})[r['quantity']] = float(r['mine'])
ver = {f: complex(v['R_meas_ohm'], v['X_meas_ohm']) for f, v in sorted(ver.items())}
smith = {}
for r in rows(os.path.join(THESIS, 'fig4_15_smith_measured.csv')):
    if r['quantity'].startswith('Zin_'):
        smith.setdefault(float(r['freq_GHz']), {})[r['quantity'][:5]] = float(r['value'])
smith = {f: complex(v['Zin_R'], v['Zin_X']) for f, v in sorted(smith.items())}

print('\n== patch, thesis (Fig. 4.14 p.162 markers and FD-TD curve; Fig. 4.15 p.163)')
print(f'  measured markers (fig4_14_Zin_verified.csv) at 2.25 / 2.30 GHz: {meas[2.25]:.3f} / {meas[2.3]:.3f} ohm')
print('  measured, two-point rule 2.25-2.30:', fmt(resonance(2.25, meas[2.25], 2.30, meas[2.3])))
print('  measured, five-parameter fit, 7 markers:', rlc_fit(SEVEN, [meas[f] for f in SEVEN]))
print('  measured, verifier re-read (my_checkpoints_fig4_14_4_15.csv), two-point:', fmt(resonance(2.25, ver[2.25], 2.30, ver[2.3])))
print('  measured, verifier re-read, five-parameter fit:', rlc_fit(SEVEN, [ver[f] for f in SEVEN]))
print('  measured, Fig. 4.15 Smith points (50 ohm), five-parameter fit:', rlc_fit(SEVEN, [smith[f] for f in SEVEN]))
print(f'  FD-TD curve at 2.25 / 2.30 GHz: {fd[2.25]:.3f} / {fd[2.3]:.3f} ohm')
print('  FD-TD, two-point rule:', fmt(resonance(2.25, fd[2.25], 2.30, fd[2.3])))
print('  FD-TD, five-parameter fit at the 7 frequencies:', rlc_fit(SEVEN, [fd[f] for f in SEVEN]))

M2 = resonance(2.25, meas[2.25], 2.30, meas[2.3]); F2 = resonance(2.25, fd[2.25], 2.30, fd[2.3])
pv = {}
for r in rows(os.path.join(HERE, 'patch_variants.csv')):
    key = (float(r['max_edge_mm']), float(r['min_edge_mm']), float(r['growth']), float(r['stub_mm']), float(r['port_distance_mm']))
    pv.setdefault(key, {})[float(r['f_GHz'])] = (complex(float(r['zedge_re']), float(r['zedge_im'])), float(r['cal_z0_re']), float(r['eps_eff']),
                                                 int(r['unknowns_dut']))
print('\n== patch, OpenSim (Zedge = load at the patch edge after taking off the port and the calibrated line)')
summary = []
for key in sorted(pv):
    d = pv[key]; fsx = sorted(d)
    print(f'  mesh {key[0]}/{key[1]}/{key[2]} mm, stub {key[3]} mm, port {key[4]} mm from the patch; circuit unknowns {d[fsx[0]][3]}')
    for f in fsx:
        z, z0, ee, _ = d[f]
        m = meas.get(round(f, 2)) if abs(f - round(f, 2)) < 1e-9 else None
        print(f'    {f:6.3f} GHz  Z {z.real:8.2f} {z.imag:+8.2f}j   cal Z0 {z0:.2f} eps_eff {ee:.4f}' +
              (f'   measured {m.real:7.2f} {m.imag:+8.2f}j' if m else ''))
    r = resonance(2.25, d[2.25][0], 2.30, d[2.3][0]) if 2.25 in d and 2.3 in d else None
    if r:
        summary.append((key, r))
        print(f'    two-point 2.25-2.30: {fmt(r)}; vs measured f0 {pct(r[0], M2[0]):+.2f} % R0 {pct(r[1], M2[1]):+.1f} % Q {pct(r[2], M2[2]):+.1f} %;'
              f' vs FD-TD f0 {pct(r[0], F2[0]):+.2f} % R0 {pct(r[1], F2[1]):+.1f} % Q {pct(r[2], F2[2]):+.1f} %')
    for a, b in zip(fsx, fsx[1:]):
        rr = resonance(a, d[a][0], b, d[b][0])
        if rr and (a, b) != (2.25, 2.3):
            print(f'    two-point {a}-{b}: {fmt(rr)}')
    if all(f in d for f in SEVEN):
        print('    five-parameter fit, 7 measured frequencies:', rlc_fit(SEVEN, [d[f][0] for f in SEVEN]))


def span(sel, i):
    v = [r[i] for k, r in summary if sel(k)]
    return min(v), max(v), len(v)


print('\n== patch, spread of the two-point results')
for lab, sel in (('2.5 mm cells, ports 10 / 16.75 mm, stubs 5 / 12 / 23 mm', lambda k: k[0] == 2.5),
                 ('stub 5 mm, port 10 mm, cells 2.5 / 2.0 / 1.5 mm', lambda k: k[3] == 5.0 and k[4] == 10.0),
                 ('stub 23 mm, port 10 mm, cells 2.5 / 2.0 mm', lambda k: k[3] == 23.0 and k[4] == 10.0)):
    a, b, c = span(sel, 0), span(sel, 1), span(sel, 2)
    print(f'  {lab}: {a[2]} runs; f0 {a[0]:.4f}-{a[1]:.4f} GHz, R0 {b[0]:.1f}-{b[1]:.1f} ohm, Q {c[0]:.1f}-{c[1]:.1f}')
r25 = dict(summary)[(2.5, 0.6, 1.4, 23.0, 10.0)]; r20 = dict(summary)[(2.0, 0.5, 1.35, 23.0, 10.0)]
print(f'  2.0 against 2.5 mm cells (stub 23, port 10): f0 {pct(r20[0], r25[0]):+.3f} %, R0 {pct(r20[1], r25[1]):+.2f} %, Q {pct(r20[2], r25[2]):+.2f} %')
r25 = dict(summary)[(2.5, 0.6, 1.4, 5.0, 10.0)]; r20 = dict(summary)[(2.0, 0.5, 1.35, 5.0, 10.0)]
print(f'  2.0 against 2.5 mm cells (stub 5, port 10):  f0 {pct(r20[0], r25[0]):+.3f} %, R0 {pct(r20[1], r25[1]):+.2f} %, Q {pct(r20[2], r25[2]):+.2f} %')
