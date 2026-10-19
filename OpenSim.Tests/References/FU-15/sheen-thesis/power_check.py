"""Passivity check |S11|^2 + |S21|^2 from the digitised curves (same curve type), on a frequency grid.
Usage: python -I power_check.py <s11.csv> <s21.csv>"""
import sys, csv
import numpy as np


def load(f, cv):
    r = [x for x in csv.DictReader(open(f)) if x["curve"] == cv]
    X = np.array([float(x["freq_GHz"]) for x in r]); Y = np.array([float(x["value"]) for x in r])
    o = np.argsort(X)
    return X[o], Y[o]


for cv in ("measured", "fdtd"):
    a = load(sys.argv[1], cv); b = load(sys.argv[2], cv)
    print(cv)
    for f in [2.0, 2.5, 3.0, 3.5, 4.0, 4.5, 6.0, 7.0, 8.0, 9.0, 10.0, 11.0, 12.0, 13.0, 14.0, 15.0, 16.0, 17.0, 18.0, 19.0]:
        if f < max(a[0].min(), b[0].min()):
            continue
        s11 = np.interp(f, *a); s21 = np.interp(f, *b)
        p = 10 ** (s11 / 10) + 10 ** (s21 / 10)
        print("  %5.1f GHz  S11 %7.2f dB  S21 %7.2f dB  |S11|^2+|S21|^2 = %.3f" % (f, s11, s21, p))
