"""Step 3(c): run repo examples with the direct solve and compare with the reference outputs
shipped in the zip (Zc_pin_con7.mat, Zc_3d_examp2.mat) and printed in the user guide
(onebargp, guide printed pp. 19-20 = PDF pp. 24-25, values R + jL from ReadOutput).
usage: python -I run_examples.py <examples dir in the download> <out dir> [names...]
The decks are COPIED to <out dir>/<name>/ and run there (nothing runs inside the download).
"""
import json, math, os, shutil, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fh  # noqa: E402

src, out = os.path.abspath(sys.argv[1]), os.path.abspath(sys.argv[2])
names = sys.argv[3:] or ['pin-con7', 'onebargp', 'onebargp_guidefreq', '3d_example2']
# FU-13 recorded pin-con7, onebargp, onebargp_guidefreq, pin-con7_default and onebargp_guidefreq_default;
# examples_results.json has no 3d_example2 entry.
DIRECT = ['-s', 'ludecomp', '-a', 'off']

# onebargp as printed in the guide (ReadOutput: R + jL, L = Im/(2 pi f)); rows in Zc order.
GUIDE_ONEBARGP = {
    1e8: [[(0.00112838, 3.50478e-08), (-2.21062e-05, -7.0003e-09)], [(-2.23118e-05, -6.99564e-09), (4.83217e-05, 2.02958e-08)]],
    1e7: [[(0.00112837, 3.50478e-08), (-2.21055e-05, -7.0003e-09)], [(-2.2311e-05, -6.99564e-09), (4.83217e-05, 2.02958e-08)]],
    1e6: [[(0.0011272, 3.50501e-08), (-2.20335e-05, -7.00045e-09)], [(-2.22379e-05, -6.99578e-09), (4.83168e-05, 2.02958e-08)]],
    1e5: [[(0.00106093, 3.51782e-08), (-1.7938e-05, -7.00794e-09)], [(-1.80765e-05, -7.00341e-09), (4.80425e-05, 2.02964e-08)]],
    1e4: [[(0.000955377, 3.58555e-08), (-1.25489e-05, -7.01913e-09)], [(-1.25828e-05, -7.01518e-09), (4.75474e-05, 2.03045e-08)]],
}


def RL(z, f):
    return z.real, z.imag / (2 * math.pi * f)


def rel(a, b):
    return abs(a - b) / max(abs(b), 1e-300)


def run_one(name, deck_src, opts, edit=None, shipped=None):
    d = os.path.join(out, name)
    os.makedirs(d, exist_ok=True)
    deck = os.path.join(d, os.path.basename(deck_src))
    shutil.copyfile(deck_src, deck)
    if edit:
        text = open(deck).read()
        for a, b in edit:
            assert a in text, a
            text = text.replace(a, b)
        open(deck, 'w').write(text)
    res = fh.run(deck, opts)
    pts, ports = fh.parse_zc(res['zc'])
    rec = {'name': name, 'deck': deck, 'options': ' '.join(opts), 'edits': edit, 'exit': res['exit'],
           'wall_s': res['wall_s'], 'filaments': res.get('filaments'), 'meshes': res.get('meshes'),
           'ports': ports, 'points': []}
    for p in pts:
        f = p['f']
        rec['points'].append({'f': f, 'RL': [[RL(z, f) for z in row] for row in p['Z']]})
    if shipped:
        ref, _ = fh.parse_zc(shipped)
        shutil.copyfile(shipped, os.path.join(d, 'shipped_' + os.path.basename(shipped)))
        worstR = worstL = 0.0
        cmp = []
        for p, q in zip(pts, ref):
            assert abs(p['f'] - q['f']) <= 1e-6 * q['f']
            n = len(p['Z'])
            for i in range(n):
                zr, zs = p['Z'][i][i], q['Z'][i][i]
                dR, dL = rel(zr.real, zs.real), rel(zr.imag, zs.imag)
                worstR, worstL = max(worstR, dR), max(worstL, dL)
            cmp.append({'f': p['f'], 'Z11_run': [p['Z'][0][0].real, p['Z'][0][0].imag],
                        'Z11_shipped': [q['Z'][0][0].real, q['Z'][0][0].imag]})
        rec['vs_shipped'] = {'file': os.path.basename(shipped), 'max_rel_diff_diag_R': worstR,
                             'max_rel_diff_diag_ImZ': worstL, 'Z11': cmp}
    return rec


results = []
for name in names:
    if name == 'pin-con7':
        results.append(run_one(name, os.path.join(src, 'pin-con7.inp'), DIRECT,
                               shipped=os.path.join(src, 'Zc_pin_con7.mat')))
    elif name == '3d_example2':
        results.append(run_one(name, os.path.join(src, '3d_example2.inp'), DIRECT,
                               shipped=os.path.join(src, 'Zc_3d_examp2.mat')))
    elif name == 'onebargp':
        results.append(run_one(name, os.path.join(src, 'onebargp.inp'), DIRECT))
    elif name == 'pin-con7_default':
        # FastHenry's defaults (GMRES + multipole, order 2, tol 1e-3): how the shipped file was made?
        results.append(run_one(name, os.path.join(src, 'pin-con7.inp'), [],
                               shipped=os.path.join(src, 'Zc_pin_con7.mat')))
    elif name in ('onebargp_guidefreq', 'onebargp_guidefreq_default'):
        rec = run_one(name, os.path.join(src, 'onebargp.inp'), DIRECT if name == 'onebargp_guidefreq' else [],
                      edit=[('.freq fmin=1e2 fmax=1e10 ndec=0.125', '.freq fmin=1e4 fmax=1e8 ndec=1')])
        cmp = []
        for p in rec['points']:
            g = min(GUIDE_ONEBARGP, key=lambda k: abs(k - p['f']))
            gm = GUIDE_ONEBARGP[g]
            cmp.append({'f': p['f'], 'run_RL': p['RL'], 'guide_RL': gm,
                        'rel_diff_R11': rel(p['RL'][0][0][0], gm[0][0][0]), 'rel_diff_L11': rel(p['RL'][0][0][1], gm[0][0][1]),
                        'rel_diff_R22': rel(p['RL'][1][1][0], gm[1][1][0]), 'rel_diff_L22': rel(p['RL'][1][1][1], gm[1][1][1])})
        rec['vs_guide'] = cmp
        results.append(rec)
    print(json.dumps({k: v for k, v in results[-1].items() if k not in ('points',)}, default=str)[:3000])

prev = []
path = os.path.join(out, 'examples_results.json')
if os.path.exists(path):
    prev = [r for r in json.load(open(path)) if r['name'] not in names]
json.dump(prev + results, open(path, 'w'), indent=1, default=str)
