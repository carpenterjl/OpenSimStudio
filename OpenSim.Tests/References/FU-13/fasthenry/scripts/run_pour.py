"""Run pour decks in parallel and append results to <pour root>/results.jsonl.

usage: python -I run_pour.py <pour root> --solver {lu,gmres,multi} [--jobs N] [--timeout s]
                             [--tag TAG] [--freqs f1,f2,..] config1 [config2 ...]
solver options (recorded with every result):
  lu    : -s ludecomp -a off                       (dense LU of the mesh matrix; exact)
  gmres : -s iterative -m direct -a off -t 1e-7 -c 1000   (GMRES on the full dense mesh matrix)
  multi : -s iterative -m multi -o 3 -t 1e-6 -c 1000      (GMRES + multipole, auto refine on)
Each FastHenry process runs one deck (one frequency) with suffix _<tag>_<ftag>, so runs with
different solvers can share a folder.
"""
import argparse, json, os, sys, time, threading
from concurrent.futures import ThreadPoolExecutor

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fh  # noqa: E402

SOLVERS = {'lu': ['-s', 'ludecomp', '-a', 'off'],
           'gmres': ['-s', 'iterative', '-m', 'direct', '-a', 'off', '-t', '1e-7', '-c', '1000'],
           'multi': ['-s', 'iterative', '-m', 'multi', '-o', '3', '-t', '1e-6', '-c', '1000'],
           # multipole on the decks' own discretisation (no automatic segment refinement)
           'multi_aoff': ['-s', 'iterative', '-m', 'multi', '-a', 'off', '-o', '3', '-t', '1e-6', '-c', '1000'],
           'multi_aoff_o4': ['-s', 'iterative', '-m', 'multi', '-a', 'off', '-o', '4', '-t', '1e-7', '-c', '1000'],
           'multi_o5': ['-s', 'iterative', '-m', 'multi', '-o', '5', '-t', '1e-7', '-c', '1000'],
           'multi_o7': ['-s', 'iterative', '-m', 'multi', '-o', '7', '-t', '1e-7', '-c', '1000'],
           # dense product on the multipole-refined structure (isolates the multipole error)
           'gmres_aon': ['-s', 'iterative', '-m', 'direct', '-t', '1e-7', '-c', '1000']}

ap = argparse.ArgumentParser()
ap.add_argument('root')
ap.add_argument('configs', nargs='+')
ap.add_argument('--solver', default='gmres', choices=SOLVERS)
ap.add_argument('--jobs', type=int, default=4)
ap.add_argument('--timeout', type=float, default=None)
ap.add_argument('--tag', default=None)
ap.add_argument('--freqs', default=None)
ap.add_argument('--sweep', action='store_true', help='one process for 1e2..1e9 (deck_sweep.inp) + one for 3e8')
ap.add_argument('--suffix-extra', default='', help='extra text in the output suffix (parallel runs of one deck)')
a = ap.parse_args()
tag = a.tag or a.solver
manifest = {m['name']: m for m in json.load(open(os.path.join(a.root, 'manifest.json')))}
want = None if a.freqs is None else [float(x) for x in a.freqs.split(',')]
lock = threading.Lock()
out_path = os.path.join(a.root, 'results.jsonl')


def job(cfg, d):
    ft = os.path.splitext(os.path.basename(d['deck']))[0].split('_')[-1]
    suffix = f'_{tag}{a.suffix_extra}_{ft}'
    freqs = d['freqs']
    try:
        res = fh.run(d['deck'], SOLVERS[a.solver], suffix=suffix, timeout=a.timeout, true_freqs=freqs)
        err = None
    except Exception as e:  # noqa
        res, err = {}, repr(e)
    pts = res.get('points') or []
    by_f = {p['f']: p for p in pts}
    for f in freqs:
        pt = by_f.get(f, {})
        rec = {'config': cfg['name'], 'pitch_mm': cfg['pitch_mm'], 'kind': cfg['kind'], 'plane_nhinc': cfg['plane_nhinc'],
               'trace': cfg['trace'], 'slot': cfg['slot'], 'solver': a.solver, 'tag': tag,
               'options': ' '.join(SOLVERS[a.solver]), 'f': f, 'R': pt.get('R'), 'L': pt.get('L'),
               'filaments': res.get('filaments'), 'meshes': res.get('meshes'), 'wall_s': res.get('wall_s'),
               'freqs_in_run': len(freqs), 'fh_total_s': res.get('fh_total_s'), 'exit': res.get('exit'),
               'error': err if pt else (err or 'no result'),
               'zc': res.get('zc'), 'log': res.get('log'), 'finished': time.strftime('%Y-%m-%d %H:%M:%S')}
        with lock:
            with open(out_path, 'a') as fhd:
                fhd.write(json.dumps(rec) + '\n')
            print(json.dumps({k: rec[k] for k in ('config', 'solver', 'f', 'R', 'L', 'filaments', 'wall_s', 'exit', 'error')}), flush=True)


jobs = []
for name in a.configs:
    cfg = manifest[name]
    if a.sweep:
        jobs.append((cfg, {'deck': cfg['sweep_deck'], 'freqs': cfg['sweep_freqs']}))
        d3 = next(d for d in cfg['decks'] if d['f'] == 3e8)
        jobs.append((cfg, {'deck': d3['deck'], 'freqs': [3e8]}))
        continue
    for d in cfg['decks']:
        if want is None or any(abs(d['f'] - w) <= 1e-9 * w for w in want):
            jobs.append((cfg, {'deck': d['deck'], 'freqs': [d['f']]}))
with ThreadPoolExecutor(max_workers=a.jobs) as ex:
    list(ex.map(lambda j: job(*j), jobs))
