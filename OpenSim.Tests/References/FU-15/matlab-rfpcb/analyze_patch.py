"""De-embedding and feature extraction for the Sheen line-fed patch (thesis Sec. 4.3).

Usage:
  python -I analyze_patch.py <verify_dir> <thesis_dir> <results_dir> <out_prefix> \
         label=patch.csv[,thru_L1.csv,thru_L2.csv] [label=...]

The MoM S11 is at the edge port at the board edge, l = 16.75 mm from the patch front
edge (the FD-TD S11 reference plane, thesis p.158 = PDF 158), 50 ohm reference.
Two de-embeddings to the patch front edge (where the thesis plots Zin, p.159-160):
  A  thesis eq. (4.5), p.149 = PDF 149:  Z = Z0 (1 + S11 e^{j2kl}) / (1 - S11 e^{j2kl}),
     k = w sqrt(eps_eff)/c, eps_eff = 2.14 (p.158), Z0 = 50 ohm (line "nominally 50 ohm", p.158).
  B  MoM line model: gamma from two MoM thru lines (lengths L1, L2: eigenvalues of T2 T1^-1);
     Z0 and the edge-port parasitic from each thru (series model Z11 = Zs + Z0 coth(gL),
     Z21 = Z0 / sinh(gL)); the patch port impedance minus Zs is moved back by l.
Features: |S11| min at the port; R peak, X zero crossing and |S11| (50 ohm) min at the
patch edge; R, X at the 7 measured frequencies; the measured-style X = 0 estimate
(linear interpolation between the 50-MHz samples).
"""
import csv, json, os, sys
import numpy as np

C0 = 299792458.0
L_REF = 16.75e-3
EPS_EFF_THESIS = 2.14
FMEAS = [2.10, 2.15, 2.20, 2.25, 2.30, 2.35, 2.40]


def read_mom(path):
    rows = list(csv.DictReader(open(path, newline='')))
    f = np.array([float(r['freq_GHz']) for r in rows])
    if 'S21_re' in rows[0]:
        S = np.zeros((len(rows), 2, 2), complex)
        for i, r in enumerate(rows):
            S[i, 0, 0] = complex(float(r['S11_re']), float(r['S11_im']))
            S[i, 1, 0] = complex(float(r['S21_re']), float(r['S21_im']))
            S[i, 0, 1] = complex(float(r['S12_re']), float(r['S12_im']))
            S[i, 1, 1] = complex(float(r['S22_re']), float(r['S22_im']))
    else:
        S = np.array([complex(float(r['S11_re']), float(r['S11_im'])) for r in rows])
    o = np.argsort(f)
    return f[o], S[o]


def s2abcd(S, z0=50.0):
    s11, s12, s21, s22 = S[:, 0, 0], S[:, 0, 1], S[:, 1, 0], S[:, 1, 1]
    A = ((1 + s11) * (1 - s22) + s12 * s21) / (2 * s21)
    B = z0 * ((1 + s11) * (1 + s22) - s12 * s21) / (2 * s21)
    C = ((1 - s11) * (1 - s22) - s12 * s21) / (2 * s21) / z0
    D = ((1 - s11) * (1 + s22) + s12 * s21) / (2 * s21)
    return A, B, C, D


def line_model(f, S1, L1, S2, L2):
    A1, B1, C1, D1 = s2abcd(S1)
    A2, B2, C2, D2 = s2abcd(S2)
    gam = np.zeros(len(f), complex); out = {}
    for i in range(len(f)):
        T1 = np.array([[A1[i], B1[i]], [C1[i], D1[i]]])
        T2 = np.array([[A2[i], B2[i]], [C2[i], D2[i]]])
        M = T2 @ np.linalg.inv(T1)
        x = np.arccosh(np.trace(M) / 2 + 0j)
        if x.imag < 0:
            x = -x
        gam[i] = x / (L2 - L1)
    res = {'gamma': gam, 'eps_eff': (gam.imag * C0 / (2 * np.pi * f * 1e9)) ** 2}
    for tag, (A, C, L) in {'L1': (A1, C1, L1), 'L2': (A2, C2, L2)}.items():
        Z11, Z21 = A / C, 1 / C
        Z0 = Z21 * np.sinh(gam * L)
        Zs = Z11 - Z0 / np.tanh(gam * L)
        res['Z0_' + tag] = Z0
        res['Zs_' + tag] = Zs
    return res


def deembed_A(f, s11, l=None):
    l = L_REF if l is None else l
    k = 2 * np.pi * f * 1e9 * np.sqrt(EPS_EFF_THESIS) / C0
    g = s11 * np.exp(2j * k * l)
    return 50 * (1 + g) / (1 - g)


def deembed_B(f, s11, lm):
    Zp = 50 * (1 + s11) / (1 - s11)
    Z0 = 0.5 * (lm['Z0_L1'] + lm['Z0_L2'])
    Zs = 0.5 * (lm['Zs_L1'] + lm['Zs_L2'])
    Zin = Zp - Zs
    t = np.tanh(lm['gamma'] * L_REF)
    return Z0 * (Zin - Z0 * t) / (Z0 - Zin * t)


def rlc_fit(f, Z):
    """Least-squares fit Z = R0/(1 + jQ(f/f0 - f0/f)) + j(X0 + X1 (f - 2.275)) to complex samples.
    Returns f0 (GHz), R0, Q, X0, X1 and the rms complex residual (ohm)."""
    from scipy.optimize import least_squares
    f = np.asarray(f, float); Z = np.asarray(Z, complex)
    k = int(np.argmax(Z.real))
    def model(p, ff):
        f0, R0, Q, X0, X1 = p
        return R0 / (1 + 1j * Q * (ff / f0 - f0 / ff)) + 1j * (X0 + X1 * (ff - 2.275))
    def res(p):
        d = model(p, f) - Z
        return np.concatenate([d.real, d.imag])
    best = None
    for f0g in [f[k], 2.25, 2.275, 2.30]:
        for Qg in [30, 60, 100]:
            for R0g in [150, 300, 500]:
                s = least_squares(res, [f0g, R0g, Qg, 0.0, 0.0], bounds=([2.1, 10, 5, -200, -5000], [2.45, 3000, 1000, 200, 5000]))
                if best is None or s.cost < best.cost:
                    best = s
    p = best.x
    rms = float(np.sqrt(np.mean(np.abs(model(p, f) - Z) ** 2)))
    return {'f0_GHz': round(float(p[0]), 5), 'R0_ohm': round(float(p[1]), 1), 'Q': round(float(p[2]), 1),
            'X0_ohm': round(float(p[3]), 2), 'X1_ohm_per_GHz': round(float(p[4]), 1), 'rms_resid_ohm': round(rms, 2), 'n': int(len(f))}


def feats_sparse(f, Z):
    """Features from a few direct-solve points: RLC fit on all points, then the fitted model
    evaluated on a 0.1 MHz grid for R peak, X = 0 and |S11| (50 ohm) minimum."""
    F = {'RLC_fit_direct_points': rlc_fit(f, Z)}
    p = F['RLC_fit_direct_points']
    fg = np.arange(2.0, 2.5, 0.0001)
    Zg = p['R0_ohm'] / (1 + 1j * p['Q'] * (fg / p['f0_GHz'] - p['f0_GHz'] / fg)) + 1j * (p['X0_ohm'] + p['X1_ohm_per_GHz'] * (fg - 2.275))
    G = (Zg - 50) / (Zg + 50)
    db = 20 * np.log10(np.abs(G))
    k = int(np.argmax(Zg.real))
    F['model_R_peak'] = [round(float(fg[k]), 5), round(float(Zg[k].real), 2)]
    k = int(np.argmin(db))
    F['model_S11_50ohm_min'] = [round(float(fg[k]), 5), round(float(db[k]), 3)]
    F['points_R_X'] = [[round(float(a), 4), round(z.real, 2), round(z.imag, 2)] for a, z in zip(f, Z)]
    return F


def feats_Z(f, Z):
    R, X = Z.real, Z.imag
    G = (Z - 50) / (Z + 50)
    db = 20 * np.log10(np.abs(G))
    F = {}
    k = int(np.argmax(R))
    F['R_peak'] = [round(float(f[k]), 5), round(float(R[k]), 2)]
    zc = [i for i in range(len(f) - 1) if X[i] > 0 >= X[i + 1] and R[i] > 50]
    F['X_zero_cross_GHz'] = round(float(f[zc[0]] - X[zc[0]] * (f[zc[0] + 1] - f[zc[0]]) / (X[zc[0] + 1] - X[zc[0]])), 5) if zc else None
    k = int(np.argmin(db))
    F['S11_50ohm_edge_min'] = [round(float(f[k]), 5), round(float(db[k]), 3)]
    pts = []
    for fm in FMEAS:
        z = complex(np.interp(fm, f, R), np.interp(fm, f, X))
        g = (z - 50) / (z + 50)
        pts.append([fm, round(z.real, 2), round(z.imag, 2), round(20 * np.log10(abs(g)), 3)])
    F['RX_dB_at_measured_freqs'] = pts
    # measured-style X = 0 estimate from 50 MHz samples
    for a, b in zip(pts[:-1], pts[1:]):
        if a[2] > 0 >= b[2]:
            F['X_zero_linear_from_50MHz_samples'] = round(a[0] - a[2] * (b[0] - a[0]) / (b[2] - a[2]), 4)
    # parallel-RLC resonance fit: on the 7 measured frequencies (like-for-like) and on 2.15-2.40 GHz
    F['RLC_fit_7pts'] = rlc_fit([p[0] for p in pts], [complex(p[1], p[2]) for p in pts])
    m = (f >= 2.15) & (f <= 2.40)
    F['RLC_fit_2.15_2.40'] = rlc_fit(f[m][::5], Z[m][::5])
    return F


def digitised(vdir, tdir):
    rows = list(csv.DictReader(open(os.path.join(vdir, 'fig4_14_Zin_verified.csv'), newline='')))
    out = {}
    for r in rows:
        out.setdefault((r['quantity'], r['curve']), []).append((float(r['freq_GHz']), float(r['value'])))
    for k in out:
        out[k].sort()
    sm = list(csv.DictReader(open(os.path.join(tdir, 'fig4_15_smith_measured.csv'), newline='')))
    smith = {}
    for r in sm:
        smith.setdefault(r['quantity'], []).append((float(r['freq_GHz']), float(r['value'])))
    return out, smith


def main():
    vdir, tdir, rdir, prefix = sys.argv[1:5]
    dig, smith = digitised(vdir, tdir)
    res = {'note': 'see analyze_patch.py docstring', 'digitised': {}, 'mom': {}}
    fR = np.array([a for a, _ in dig[('Zin_R_ohm', 'fdtd')]]); R = np.array([b for _, b in dig[('Zin_R_ohm', 'fdtd')]])
    fX = np.array([a for a, _ in dig[('Zin_X_ohm', 'fdtd')]]); X = np.array([b for _, b in dig[('Zin_X_ohm', 'fdtd')]])
    fg = np.arange(2.005, 2.495, 0.0005)
    Zf = np.interp(fg, fR, R) + 1j * np.interp(fg, fX, X)
    res['digitised']['fdtd_Fig4.14'] = feats_Z(fg, Zf)
    meas = {'R': dig[('Zin_R_ohm', 'measured')], 'X': dig[('Zin_X_ohm', 'measured')],
            'S11_dB_smith_Fig4.15': smith.get('S11_dB_Fig4.15_Smith')}
    res['digitised']['measured_Fig4.14_markers'] = meas
    zm = [complex(a[1], b[1]) for a, b in zip(meas['R'], meas['X'])]
    res['digitised']['measured_Fig4.14_RLC_fit'] = rlc_fit(FMEAS, zm)
    zs = [complex(a[1], b[1]) for a, b in zip(smith['Zin_R_ohm_from_Smith_Z0_50'], smith['Zin_X_ohm_from_Smith_Z0_50'])]
    res['digitised']['measured_Fig4.15_RLC_fit'] = rlc_fit(FMEAS, zs)
    res['digitised']['measured_S11_50ohm_from_Fig4.14'] = [[fm, round(20 * np.log10(abs((z - 50) / (z + 50))), 3)] for fm, z in zip(FMEAS, zm)]
    lines = []
    for arg in sys.argv[5:]:
        lab, files = arg.split('=', 1)
        lfeed = None
        if '@' in files:              # label=file.csv@6.0 -> feed length 6.0 mm used as l in eq. (4.5)
            files, lmm = files.split('@'); lfeed = float(lmm) * 1e-3
        files = files.split(',')
        f, s11 = read_mom(os.path.join(rdir, files[0]))
        db = 20 * np.log10(np.abs(s11))
        if len(f) < 50:   # direct-solve points: RLC-model features only (de-embedding A)
            res['mom'][lab] = {'direct_points': True, 'l_mm': (lfeed or L_REF) * 1e3,
                               'deembed_A_thesis_eq4.5': feats_sparse(f, deembed_A(f, s11, lfeed))}
            F = res['mom'][lab]['deembed_A_thesis_eq4.5']
            print(lab, 'sparse:', F['RLC_fit_direct_points'], 'model R peak', F['model_R_peak'], 'model |S11| min', F['model_S11_50ohm_min'])
            ZA = deembed_A(f, s11, lfeed)
            with open(prefix + '_' + lab + '_Zedge.csv', 'w', newline='') as fh:
                w = csv.writer(fh)
                w.writerow(['freq_GHz', 'S11_port_dB', 'R_A_ohm', 'X_A_ohm'])
                for i in range(len(f)):
                    w.writerow([f'{f[i]:.5f}', f'{db[i]:.4f}', f'{ZA[i].real:.3f}', f'{ZA[i].imag:.3f}'])
            continue
        k = int(np.argmin(db))
        F = {'S11_port_min': [round(float(f[k]), 5), round(float(db[k]), 3)]}
        ZA = deembed_A(f, s11)
        F['deembed_A_thesis_eq4.5'] = feats_Z(f, ZA)
        if len(files) == 3:
            f1, S1 = read_mom(os.path.join(rdir, files[1]))
            f2, S2 = read_mom(os.path.join(rdir, files[2]))
            L1 = float(files[1].split('_L')[1].split('_')[0]) * 1e-3 / 100   # tag ..._L1675_... = 16.75 mm
            L2 = float(files[2].split('_L')[1].split('_')[0]) * 1e-3 / 100
            S1i = np.stack([np.stack([np.interp(f, f1, S1[:, a, b].real) + 1j * np.interp(f, f1, S1[:, a, b].imag) for b in range(2)], -1) for a in range(2)], 1)
            S2i = np.stack([np.stack([np.interp(f, f2, S2[:, a, b].real) + 1j * np.interp(f, f2, S2[:, a, b].imag) for b in range(2)], -1) for a in range(2)], 1)
            lm = line_model(f, S1i, L1, S2i, L2)
            i0 = int(np.argmin(np.abs(f - 2.28)))
            F['line_model_at_2.28GHz'] = {
                'L1_L2_mm': [L1 * 1e3, L2 * 1e3],
                'eps_eff': round(float(lm['eps_eff'][i0]), 4),
                'alpha_Np_per_m': round(float(lm['gamma'][i0].real), 5),
                'Z0_L1': [round(lm['Z0_L1'][i0].real, 3), round(lm['Z0_L1'][i0].imag, 3)],
                'Z0_L2': [round(lm['Z0_L2'][i0].real, 3), round(lm['Z0_L2'][i0].imag, 3)],
                'Zs_L1': [round(lm['Zs_L1'][i0].real, 3), round(lm['Zs_L1'][i0].imag, 3)],
                'Zs_L2': [round(lm['Zs_L2'][i0].real, 3), round(lm['Zs_L2'][i0].imag, 3)]}
            ZB = deembed_B(f, s11, lm)
            F['deembed_B_mom_line'] = feats_Z(f, ZB)
            with open(prefix + '_' + lab + '_Zedge.csv', 'w', newline='') as fh:
                w = csv.writer(fh)
                w.writerow(['freq_GHz', 'S11_port_dB', 'R_A_ohm', 'X_A_ohm', 'R_B_ohm', 'X_B_ohm', 'eps_eff_mom', 'Z0_mom_re', 'Zs_mom_im'])
                for i in range(len(f)):
                    w.writerow([f'{f[i]:.5f}', f'{db[i]:.4f}', f'{ZA[i].real:.3f}', f'{ZA[i].imag:.3f}', f'{ZB[i].real:.3f}', f'{ZB[i].imag:.3f}',
                                f'{lm["eps_eff"][i]:.5f}', f'{(0.5*(lm["Z0_L1"][i]+lm["Z0_L2"][i])).real:.3f}', f'{(0.5*(lm["Zs_L1"][i]+lm["Zs_L2"][i])).imag:.3f}'])
        else:
            with open(prefix + '_' + lab + '_Zedge.csv', 'w', newline='') as fh:
                w = csv.writer(fh)
                w.writerow(['freq_GHz', 'S11_port_dB', 'R_A_ohm', 'X_A_ohm'])
                for i in range(len(f)):
                    w.writerow([f'{f[i]:.5f}', f'{db[i]:.4f}', f'{ZA[i].real:.3f}', f'{ZA[i].imag:.3f}'])
        res['mom'][lab] = F
        lines.append(lab)
    json.dump(res, open(prefix + '_features.json', 'w'), indent=1)
    D = res['digitised']
    print('measured RLC fit (Fig.4.14 markers):', D['measured_Fig4.14_RLC_fit'])
    print('measured RLC fit (Fig.4.15 Smith)  :', D['measured_Fig4.15_RLC_fit'])
    print('measured |S11| 50 ohm from Fig.4.14:', D['measured_S11_50ohm_from_Fig4.14'])
    def show(name, F):
        print('%-22s Rpk %-20s X0 %-9s S11edge_min %-20s X0lin %-8s fit7 f0 %.4f R0 %6.1f Q %5.1f rms %5.1f | fitfull f0 %.4f R0 %6.1f Q %5.1f rms %5.1f' % (
            name, F['R_peak'], F['X_zero_cross_GHz'], F['S11_50ohm_edge_min'], F.get('X_zero_linear_from_50MHz_samples'),
            F['RLC_fit_7pts']['f0_GHz'], F['RLC_fit_7pts']['R0_ohm'], F['RLC_fit_7pts']['Q'], F['RLC_fit_7pts']['rms_resid_ohm'],
            F['RLC_fit_2.15_2.40']['f0_GHz'], F['RLC_fit_2.15_2.40']['R0_ohm'], F['RLC_fit_2.15_2.40']['Q'], F['RLC_fit_2.15_2.40']['rms_resid_ohm']))
    show('FD-TD Fig.4.14', D['fdtd_Fig4.14'])
    for lab in lines:
        F = res['mom'][lab]
        print(lab, 'S11 port min', F['S11_port_min'], 'line', F.get('line_model_at_2.28GHz'))
        show(lab + ' A(eq4.5)', F['deembed_A_thesis_eq4.5'])
        if 'deembed_B_mom_line' in F:
            show(lab + ' B(MoM line)', F['deembed_B_mom_line'])
        for k in ['deembed_A_thesis_eq4.5', 'deembed_B_mom_line']:
            if k in F:
                print('   ', k[:9], 'R,X,dB at 7 f:', [p[1:] for p in F[k]['RX_dB_at_measured_freqs']])


if __name__ == '__main__':
    main()
