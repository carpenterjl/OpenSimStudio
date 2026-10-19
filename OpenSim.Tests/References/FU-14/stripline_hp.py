"""Rautio (1994) stripline standard: arbitrary-precision check of Table I and the 15 GHz quarter wave.

mpmath is NOT installed in this environment (and no download other than the paper is approved), so
this uses Python's decimal module with an explicit working precision:
  * pi by the Gauss-Legendre AGM iteration,
  * K(k')/K(k) = AGM(1,k')/AGM(1,k)   (since K(k) = pi / (2 AGM(1, sqrt(1-k^2))), the pi cancels),
  * k = tanh(x), k' = sech(x) evaluated from exp(-x) directly (no 1-k^2 cancellation), x = pi W / (2 B).
The paper's formula (p. 2047): Z0 sqrt(er) = (eta0/4) K(k')/K(k), k = tanh(pi w / (2 b)), k' = sqrt(1-k^2),
eta0 = 376.7303136.  (Cohn's form with k = sech, k' = tanh and K(k)/K(k') is the same expression.)
Run twice (two precisions) and compare -> convergence evidence.
usage: python -I stripline_hp.py <outdir>
"""
import sys, os, json, csv
from decimal import Decimal as D, getcontext, localcontext

OUT = sys.argv[1]


def pi_gl():
    """Gauss-Legendre AGM for pi at the current context precision."""
    getcontext().prec += 10
    a, b, t, p = D(1), D(1) / D(2).sqrt(), D(1) / D(4), D(1)
    eps = D(10) ** (-(getcontext().prec - 2))
    while True:
        an = (a + b) / 2
        b = (a * b).sqrt()
        t -= p * (a - an) ** 2
        a = an
        p *= 2
        if abs(a - b) < eps:
            break
    r = (a + b) ** 2 / (4 * t)
    getcontext().prec -= 10
    return +r


def agm(a, b):
    eps = D(10) ** (-(getcontext().prec - 3))
    while abs(a - b) > eps * abs(a):
        a, b = (a + b) / 2, (a * b).sqrt()
    return (a + b) / 2


def ratio_Kp_over_K(x):
    """K(k')/K(k) with k = tanh(x), k' = sech(x)."""
    e = (-x).exp()
    e2 = e * e
    k = (1 - e2) / (1 + e2)          # tanh x
    kp = 2 * e / (1 + e2)            # sech x
    # K(k') / K(k) = [pi/(2 AGM(1,k))] / [pi/(2 AGM(1,k'))] = AGM(1,k') / AGM(1,k)
    return agm(D(1), kp) / agm(D(1), k), k, kp


def z0(W, B, eta0, er, PI):
    x = PI * W / (2 * B)
    r, k, kp = ratio_Kp_over_K(x)
    return eta0 / 4 * r / er.sqrt(), k, kp


def solve_w(target, B, eta0, er, PI, lo=D("0.01"), hi=D("20")):
    """Z0 decreases monotonically with W: bisection to the working precision."""
    tol = D(10) ** (-(getcontext().prec - 5))
    for _ in range(1000):
        mid = (lo + hi) / 2
        if z0(mid, B, eta0, er, PI)[0] > target:
            lo = mid
        else:
            hi = mid
        if hi - lo < tol:
            break
    return (lo + hi) / 2


def atan_small(u):
    """atan for |u| << 1 by its Taylor series (used for phase offsets ~1e-9 rad)."""
    s, term, n = D(0), u, 0
    while True:
        add = term / (2 * n + 1)
        if abs(add) < D(10) ** (-(getcontext().prec + 5)):
            break
        s += add if n % 2 == 0 else -add
        term *= u * u
        n += 1
    return s


def sin_cos(x):
    """Taylor series sin/cos at current precision (x moderate)."""
    getcontext().prec += 10
    s, c = D(0), D(0)
    term = x
    n = 1
    while abs(term) > D(10) ** (-(getcontext().prec + 2)):
        s += term
        term *= -x * x / ((n + 1) * (n + 2))
        n += 2
    term = D(1)
    n = 0
    while abs(term) > D(10) ** (-(getcontext().prec + 2)):
        c += term
        term *= -x * x / ((n + 1) * (n + 2))
        n += 2
    getcontext().prec -= 10
    return +s, +c


def line_s(zline, zref, theta):
    """Ideal lossless TEM line of impedance zline, electrical length theta (rad), in zref.
    S11 = j(z-1/z) sin / (2cos + j(z+1/z) sin),  S21 = 2 / (2cos + j(z+1/z) sin).
    Returns |S11|, Ang(S21) in degrees (near -90), using atan for the small offset."""
    z = zline / zref
    s, c = sin_cos(theta)
    X = 2 * c                       # real part of denominator
    Y = (z + 1 / z) * s             # imaginary part of denominator
    mag_den = (X * X + Y * Y).sqrt()
    s11 = abs(z - 1 / z) * abs(s) / mag_den
    # arg(S21) = -atan2(Y, X); with Y>0 and |X|<<Y: atan2(Y,X) = pi/2 - atan(X/Y)
    return s11, X / Y


def run(prec):
    getcontext().prec = prec
    PI = pi_gl()
    deg = 180 / PI
    B = D(1)                      # mm (Table I: "1 MM GROUND PLANE SPACING"; Sec. V: 1.0 mm)
    er = D(1)                     # Sec. V: "relative dielectric constant unity"; Table I "in air"
    eta_paper = D("376.7303136")  # p. 2047, printed
    c0 = D(299792458)             # m/s, SI definition (exact)
    mu0_pre2019 = 4 * PI / D(10) ** 7
    eta_mu0c = mu0_pre2019 * c0   # what 4*pi*1e-7 * c gives (also OpenSim.Rf/RfConstants.cs)
    eta_scipy = D("376.730313412")  # scipy.constants 1.18.1 'characteristic impedance of vacuum' (CODATA 2022)
    eta_30pi = 120 * PI
    f = D(15) * D(10) ** 9
    table = [(D("25.00000"), D("3.3260319")), (D("50.00000"), D("1.4423896")), (D("100.0000"), D("0.50396767"))]
    L_print = D("4.99654097")     # mm, Sec. V
    res = {"prec": prec, "pi_check_first_40": str(PI)[:42]}
    res["eta_paper"] = str(eta_paper)
    res["eta_mu0c_4pi1e-7"] = str(+eta_mu0c)
    res["eta_paper_minus_mu0c"] = str(eta_paper - eta_mu0c)
    res["eta_paper_minus_scipy"] = str(eta_paper - eta_scipy)
    res["thirty_pi_rel_error_vs_paper_eta"] = str(eta_30pi / eta_paper - 1)
    rows = []
    for zt, wp in table:
        z_at_wp, k, kp = z0(wp, B, eta_paper, er, PI)
        w_exact = solve_w(zt, B, eta_paper, er, PI)
        w_exact_mu0c = solve_w(zt, B, eta_mu0c, er, PI)
        w_exact_scipy = solve_w(zt, B, eta_scipy, er, PI)
        # dZ0/dW by central difference
        h = D(10) ** (-(prec // 3))
        dz = (z0(wp + h, B, eta_paper, er, PI)[0] - z0(wp - h, B, eta_paper, er, PI)[0]) / (2 * h)
        z_mu0c = z0(wp, B, eta_mu0c, er, PI)[0]
        rows.append({
            "Z0_target_ohm": str(zt), "W_printed_mm": str(wp),
            "k_tanh": str(k), "kprime_sech": str(kp),
            "Z0_at_W_printed_ohm(eta_paper)": str(z_at_wp),
            "Z0_abs_diff_ohm": str(z_at_wp - zt), "Z0_rel_diff": str(z_at_wp / zt - 1),
            "W_exact_mm(eta_paper)": str(w_exact),
            "W_printed_minus_exact_mm": str(wp - w_exact),
            "W_rel_diff": str(wp / w_exact - 1),
            "W_exact_mm(eta=4pi1e-7*c)": str(w_exact_mu0c),
            "W_exact_mm(eta=scipy CODATA2022)": str(w_exact_scipy),
            "Z0_at_W_printed_ohm(eta=4pi1e-7*c)": str(z_mu0c),
            "dZ0_dW_ohm_per_mm": str(dz),
        })
    res["table1"] = rows
    # Quarter wave at 15 GHz in air
    lam_mm = c0 / f * 1000
    L_exact = lam_mm / 4
    res["lambda_15GHz_mm"] = str(lam_mm)
    res["L_quarter_exact_mm"] = str(L_exact)
    res["L_printed_minus_exact_mm"] = str(L_print - L_exact)
    res["L_rel_diff"] = str(L_print / L_exact - 1)
    theta = 2 * PI * L_print / lam_mm
    res["theta_printed_L_deg"] = str(theta * deg)
    res["theta_printed_minus_90_deg"] = str(theta * deg - 90)
    # Ideal TEM line with printed W and printed L, 50-ohm reference: the benchmark's own floor
    z50 = z0(D("1.4423896"), B, eta_paper, er, PI)[0]
    s11, u = line_s(z50, D(50), theta)
    ang_s21_deg = -(90 - atan_small(u) * deg)   # Ang(S21) = -90 + atan(X/Y)
    res["floor_S11_mag"] = str(s11)
    res["floor_ang_S21_deg"] = str(ang_s21_deg)
    E = 100 * s11 + abs(90 + ang_s21_deg) / D("0.9")
    res["floor_first_order_error_percent(web formula)"] = str(E)
    # Paper's Sec. V example: a quarter-wave 51-ohm line in 50 ohm (|S11| should read ~0.02 -> 2 %)
    s11_51, u51 = line_s(D(51), D(50), PI / 2)
    res["quarter_wave_51ohm_S11"] = str(s11_51)
    # Repo cross-check: OpenSim.Tests/Rf/ShieldedKernelTests.cs:215 quotes 60.57 ohm for w 0.5 mm, b 1.6 mm, er 4.4
    zrepo = z0(D("0.5"), D("1.6"), eta_mu0c, D("4.4"), PI)[0]
    res["repo_case_w0.5_b1.6_er4.4_Z0_ohm(eta=4pi1e-7*c)"] = str(zrepo)
    return res


def sig(x, n=30):
    return f"{D(x):.{n}g}"


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    r40 = run(40)
    r80 = run(80)
    # convergence: max |difference| of every numeric field between 40 and 80 digits
    def flat(d, pre=""):
        out = {}
        for kk, v in d.items():
            if isinstance(v, list):
                for i, row in enumerate(v):
                    out.update(flat(row, f"{pre}{kk}[{i}]."))
            elif isinstance(v, dict):
                out.update(flat(v, pre + kk + "."))
            else:
                out[pre + kk] = v
        return out
    f40, f80 = flat(r40), flat(r80)
    conv = {}
    getcontext().prec = 100
    for kk in f80:
        try:
            a, b = D(f40[kk]), D(f80[kk])
            denom = max(abs(b), D(10) ** -60)
            conv[kk] = f"{abs(a - b) / denom:.2e}"
        except Exception:
            pass
    with open(os.path.join(OUT, "stripline_hp_80digits.json"), "w") as fh:
        json.dump(r80, fh, indent=1)
    with open(os.path.join(OUT, "stripline_hp_40digits.json"), "w") as fh:
        json.dump(r40, fh, indent=1)
    with open(os.path.join(OUT, "stripline_hp_convergence_40_vs_80.json"), "w") as fh:
        json.dump(conv, fh, indent=1)
    # print a 32-significant-digit view of the 80-digit run
    for kk, v in f80.items():
        try:
            print(f"{kk:55s} {sig(v, 32)}   (rel. diff 40 vs 80 digits: {conv.get(kk)})")
        except Exception:
            print(f"{kk:55s} {v}")
