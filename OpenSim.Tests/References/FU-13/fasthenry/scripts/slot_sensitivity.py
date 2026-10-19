"""Slot-size sensitivity on the exact-slot 0.1 mm grid (x10, plane nhinc 1, trace 7x5): the slot made
0.2 mm longer (y -2.1..2.1) or 0.2 mm wider (x 2.6..3.4) by changing only the 'hole rect' line, so the
k-ladder's slightly-off effective slots can be corrected to 0.6 x 4.0 mm.
usage: python -I slot_sensitivity.py <pour root> [jobs]   -> appends to results.jsonl (configs x10s_*)"""
import json, os, sys, threading
from concurrent.futures import ThreadPoolExecutor

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fh  # noqa: E402

root = os.path.abspath(sys.argv[1])
jobs = int(sys.argv[2]) if len(sys.argv) > 2 else 4
OPTS = ['-s', 'iterative', '-m', 'direct', '-a', 'off', '-t', '1e-7', '-c', '1000']
BASE = '+ hole rect (2.75,-1.95,-0.0175,3.25,1.95,-0.0175)'
VARIANTS = {'x10s_long_p1_T1_slot': ('+ hole rect (2.75,-2.05,-0.0175,3.25,2.05,-0.0175)', 'slot 0.6 x 4.2 mm'),
            'x10s_wide_p1_T1_slot': ('+ hole rect (2.65,-1.95,-0.0175,3.35,1.95,-0.0175)', 'slot 0.8 x 4.0 mm')}
FREQS = [float(x) for x in os.environ.get('FREQS', '1e2,1e6,1e8,1e9').split(',')]
lock = threading.Lock()


def ftag(f):
    return f'{f:.0e}'.replace('+0', '').replace('+', '')


def one(name, f):
    new, label = VARIANTS[name]
    src = os.path.join(root, 'x10_p1_T1_slot', f'deck_f{ftag(f)}.inp')
    text = open(src).read()
    assert BASE in text
    text = text.replace(BASE, new).replace('* slotted pour', f'* VARIANT {label}; slotted pour', 1)
    d = os.path.join(root, name)
    os.makedirs(d, exist_ok=True)
    deck = os.path.join(d, f'deck_f{ftag(f)}.inp')
    open(deck, 'w').write(text)
    res = fh.run(deck, OPTS, suffix=f'_gmres_f{ftag(f)}', true_freqs=[f])
    pt = res['points'][0]
    rec = {'config': name, 'pitch_mm': 0.1, 'kind': 'exact-variant', 'plane_nhinc': 1, 'trace': 'T1', 'slot': True,
           'solver': 'gmres', 'tag': 'gmres', 'options': ' '.join(OPTS), 'f': f, 'R': pt['R'], 'L': pt['L'],
           'filaments': res.get('filaments'), 'meshes': res.get('meshes'), 'wall_s': res['wall_s'], 'freqs_in_run': 1,
           'exit': res['exit'], 'error': None, 'zc': res['zc'], 'log': res['log'], 'variant': label}
    with lock:
        with open(os.path.join(root, 'results.jsonl'), 'a') as fhd:
            fhd.write(json.dumps(rec) + '\n')
        print(json.dumps({k: rec[k] for k in ('config', 'f', 'R', 'L', 'filaments', 'wall_s')}), flush=True)


with ThreadPoolExecutor(max_workers=jobs) as ex:
    list(ex.map(lambda a: one(*a), [(n, f) for n in VARIANTS for f in FREQS]))
