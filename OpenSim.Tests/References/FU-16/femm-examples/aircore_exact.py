"""Inductance of a uniform-current-density air-core coil of rectangular cross-section (ri..ro, height h),
N turns, from the mutual inductance of coaxial filaments (Maxwell's elliptic-integral formula):
    L = (N/A)^2 * int_A int_A M dA1 dA2,
reduced in z to   int int g(z1-z2) dz1 dz2 = int_0^h 2 (h - s) g(s) ds,
and in r by symmetry to 2 * int_{ri}^{ro} dr1 int_{ri}^{r1} dr2.
Inner (s) and middle (r2) integrals: composite Gauss-Legendre geometrically graded toward the log
singularity (s = 0, r2 = r1); outer (r1): Gauss-Legendre graded toward r1 = ri, or scipy quad.
Usage: python -I aircore_exact.py <out_json>"""
import sys, json, math, time
import numpy as np
from scipy import special, integrate

sys.stdout.reconfigure(encoding='utf-8')
MU0 = 4e-7 * math.pi
IN = 0.0254


def M(r1, r2, s):
    """Mutual inductance of coaxial circular filaments [H]; arrays broadcast."""
    den = (r1 + r2) ** 2 + s ** 2
    p = ((r1 - r2) ** 2 + s ** 2) / den       # 1 - k^2, computed without cancellation
    m = 1 - p
    k = np.sqrt(m)
    K = special.ellipkm1(p)
    E = special.ellipe(m)
    return MU0 * np.sqrt(r1 * r2) * ((2 / k - k) * K - (2 / k) * E)


def M_neumann(r1, r2, s, n=4096):
    phi = (np.arange(n) + 0.5) * 2 * math.pi / n
    integrand = np.cos(phi) / np.sqrt(r1 * r1 + r2 * r2 - 2 * r1 * r2 * np.cos(phi) + s * s)
    return MU0 / 2 * r1 * r2 * integrand.sum() * 2 * math.pi / n


def graded_nodes(a, b, toward, q, levels, ratio):
    """Gauss-Legendre nodes/weights on [a,b], panels graded geometrically toward endpoint `toward`."""
    x, w = np.polynomial.legendre.leggauss(q)
    L = b - a
    # breakpoints measured from the singular end: L*ratio^k
    edges = [L * ratio ** k for k in range(levels)] + [0.0]
    edges = edges[::-1]  # 0, tiny, ..., L
    xs, ws = [], []
    for k in range(len(edges) - 1):
        lo, hi = edges[k], edges[k + 1]
        xs.append(lo + (x + 1) / 2 * (hi - lo))
        ws.append(w * (hi - lo) / 2)
    d = np.concatenate(xs)
    wt = np.concatenate(ws)
    if toward == 'a':
        return a + d, wt
    return b - d, wt


def L_coil(N, ri, ro, h, q=16, levels=30, ratio=0.2, outer='gl'):
    s, ws = graded_nodes(0.0, h, 'a', q, levels, ratio)
    kern_s = 2 * (h - s) * ws                    # weights of the z-reduction

    def middle(r1):
        if r1 <= ri:
            return 0.0
        r2, w2 = graded_nodes(ri, r1, 'b', q, levels, ratio)
        vals = M(r1, r2[:, None], s[None, :])     # (n_r2, n_s)
        return float(w2 @ (vals @ kern_s))

    if outer == 'gl':
        r1, w1 = graded_nodes(ri, ro, 'a', q, max(levels // 2, 4), ratio)
        tot = sum(w * middle(r) for r, w in zip(r1, w1))
    else:
        tot = integrate.quad(middle, ri, ro, epsabs=0, epsrel=1e-12, limit=200)[0]
    I = 2 * tot
    A = (ro - ri) * h
    return (N / A) ** 2 * I


out = {'formula_check': [], 'cases': []}
# 1) the elliptic formula against the Neumann double integral (exponentially convergent trapezoid)
for (r1, r2, s) in [(0.0254, 0.0508, 0.0), (0.0254, 0.0254, 0.01), (0.03, 0.05, 0.1), (0.0254, 0.02541, 0.0001)]:
    a = float(M(np.array(r1), np.array(r2), np.array(s)))
    b = M_neumann(r1, r2, s, n=1 << 16)
    out['formula_check'].append({'r1': r1, 'r2': r2, 's': s, 'M_elliptic_H': a, 'M_neumann_H': b, 'rel_diff': a / b - 1})
    print('formula check', r1, r2, s, a, b, a / b - 1)

cases = [(100, 1, 2, 1, 0.64707e-3, 0.64286e-3), (100, 1, 1.25, 4, 0.23081e-3, 0.22376e-3), (100, 0.25, 2, 0.25, 0.40955e-3, 0.38208e-3)]
for (N, ri, ro, z, Lf, Lw) in cases:
    res = {'n': N, 'ri_in': ri, 'ro_in': ro, 'z_in': z, 'FEMM_L_H': Lf, 'Wheeler_L_H': Lw, 'runs': []}
    for (q, levels, ratio, outer) in [(12, 20, 0.25, 'gl'), (16, 30, 0.2, 'gl'), (20, 40, 0.15, 'gl'), (16, 30, 0.2, 'quad')]:
        t = time.time()
        Lv = L_coil(N, ri * IN, ro * IN, z * IN, q, levels, ratio, outer)
        res['runs'].append({'q': q, 'levels': levels, 'ratio': ratio, 'outer': outer, 'L_H': Lv, 'seconds': time.time() - t})
        print(N, ri, ro, z, q, levels, ratio, outer, repr(Lv), f'{time.time()-t:.1f}s')
    Lbest = res['runs'][2]['L_H']
    res['L_exact_H'] = Lbest
    res['spread_between_runs_rel'] = (max(r['L_H'] for r in res['runs']) - min(r['L_H'] for r in res['runs'])) / Lbest
    res['FEMM_over_exact_minus_1'] = Lf / Lbest - 1
    res['Wheeler_over_exact_minus_1'] = Lw / Lbest - 1
    out['cases'].append(res)

json.dump(out, open(sys.argv[1], 'w', encoding='utf-8'), indent=1)
print(json.dumps(out, indent=1))
