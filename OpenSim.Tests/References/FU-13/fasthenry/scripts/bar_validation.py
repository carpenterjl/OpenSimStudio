"""Step 3(a)/(b): one straight copper bar, 10 mm x 1 mm x 35 um, sigma 5.8e7 S/m.
Writes decks to <out>/, runs FastHenry direct (-s ludecomp -a off), and compares with
R = l/(sigma w t) and with an independent quadrature of the bar's DC self-inductance.
usage: python -I bar_validation.py <out dir>
"""
import json, math, os, sys
import numpy as np
from scipy import integrate

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fh  # noqa: E402

out = os.path.abspath(sys.argv[1])
os.makedirs(out, exist_ok=True)
L_mm, W_mm, T_mm = 10.0, 1.0, 0.035
sigma = 5.8e7
mu0 = 4e-7 * math.pi
l, w, t = L_mm * 1e-3, W_mm * 1e-3, T_mm * 1e-3

R_dc = l / (sigma * w * t)


def f_at(t_over_delta):
    # delta = 1/sqrt(pi f mu0 sigma)  ->  f = (t/delta)^2 / (pi mu0 sigma t^2)
    return t_over_delta ** 2 / (math.pi * mu0 * sigma * t * t)


# Independent DC self-inductance (uniform current): L = (1/A^2) int_A int_A M(rho) dA dA',
# M(rho) = mu0/(2 pi) [l asinh(l/rho) - sqrt(l^2+rho^2) + rho] (two aligned parallel filaments of
# length l), reduced to the difference coordinates (u, v) with weight (w-|u|)(t-|v|).
def M(rho):
    return mu0 / (2 * math.pi) * (l * math.asinh(l / rho) - math.sqrt(l * l + rho * rho) + rho)


def L_quad():
    f = lambda v, u: (w - u) * (t - v) * M(math.hypot(u, v))
    val, err = integrate.dblquad(f, 0, w, 0, t, epsabs=0, epsrel=1e-11)
    A = w * t
    return 4 * val / (A * A), 4 * err / (A * A)


def deck(name, nw, nh, rw, rh, freq):
    path = os.path.join(out, name + '.inp')
    with open(path, 'w') as fhd:
        fhd.write(f"""* straight copper bar 10 mm x 1 mm x 35 um, sigma 5.8e7 S/m ({nw} x {nh} filaments, rw={rw} rh={rh})
.units mm
* sigma in 1/(mm*Ohm) because .units is mm: 5.8e7 S/m = 5.8e4 /(mm*Ohm)
.default sigma=5.8e4
N1 x=0 y=0 z=0
N2 x=10 y=0 z=0
E1 N1 N2 w=1 h=0.035 nwinc={nw} nhinc={nh} rw={rw} rh={rh}
.external N1 N2
.freq fmin={freq!r} fmax={freq!r} ndec=1
.end
""")
    return path


opts = ['-s', 'ludecomp', '-a', 'off']
rows = []
Lq, Lq_err = L_quad()
cases = [('bar_1x1_1Hz', 1, 1, 2, 2, 1.0, None), ('bar_5x3_1Hz', 5, 3, 2, 2, 1.0, None)]
for td in (1, 3):
    fr = f_at(td)
    for (nw, nh) in ((5, 3), (11, 5), (21, 9)):
        for r in (2, 1):
            cases.append((f'bar_{nw}x{nh}_r{r}_td{td}', nw, nh, r, r, fr, td))
    # finer, gentler grading: a converged reference for the rows above
    for (nw, nh, r) in ((41, 15, 1.3), (61, 21, 1.2)):
        cases.append((f'bar_{nw}x{nh}_r{r}_td{td}', nw, nh, r, r, fr, td))
for name, nw, nh, rw, rh, fr, td in cases:
    p = deck(name, nw, nh, rw, rh, fr)
    res = fh.run(p, opts, suffix='_' + name, true_freqs=[fr])     # Zc_<case>.mat kept per case
    pt = res['points'][0]
    rows.append({'case': name, 'nwinc': nw, 'nhinc': nh, 'rw': rw, 'rh': rh, 't_over_delta': td,
                 'f_Hz': pt['f'], 'R_ohm': pt['R'], 'L_H': pt['L'], 'R_over_Rdc': pt['R'] / R_dc,
                 'filaments': res.get('filaments'), 'wall_s': res['wall_s'], 'exit': res['exit'],
                 'options': ' '.join(opts)})
    print(json.dumps(rows[-1]))
summary = {'R_dc_formula_ohm': R_dc, 'L_dc_quadrature_H': Lq, 'L_dc_quadrature_errest_H': Lq_err,
           'f_td1_Hz': f_at(1), 'f_td3_Hz': f_at(3), 'rows': rows}
with open(os.path.join(out, 'bar_results.json'), 'w') as fhd:
    json.dump(summary, fhd, indent=1)
with open(os.path.join(out, 'bar_results.csv'), 'w') as fhd:
    fhd.write('case,nwinc,nhinc,rw,rh,t_over_delta,f_Hz,R_ohm,L_H,R_over_Rdc,filaments,wall_s,options\n')
    for r in rows:
        fhd.write(f"{r['case']},{r['nwinc']},{r['nhinc']},{r['rw']},{r['rh']},{r['t_over_delta']},{r['f_Hz']:.9g},"
                  f"{r['R_ohm']:.6g},{r['L_H']:.6g},{r['R_over_Rdc']:.6g},{r['filaments']},{r['wall_s']},{r['options']}\n")
print(json.dumps({k: v for k, v in summary.items() if k != 'rows'}))
