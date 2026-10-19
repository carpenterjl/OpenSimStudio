"""Tabulate <pour root>/results.jsonl: one row per (config, solver), R [mOhm] and L [nH] per frequency.
usage: python -I summarize.py <pour root> [solver-tag ...]   -> prints tables, writes pour_results.csv"""
import json, os, sys
from collections import defaultdict

root = sys.argv[1]
tags = sys.argv[2:]
rows = [json.loads(l) for l in open(os.path.join(root, 'results.jsonl'))]
latest = {}
for r in rows:                       # keep the last result per (config, tag, f)
    latest[(r['config'], r['tag'], r['f'])] = r
freqs = sorted({k[2] for k in latest})
by = defaultdict(dict)
for (cfg, tag, f), r in latest.items():
    if tags and tag not in tags:
        continue
    by[(cfg, tag)][f] = r


def fmt(v, s):
    return '   --   ' if v is None else f'{v * s:8.4f}'


order = sorted(by, key=lambda k: (k[0][:4], k[0]))
with open(os.path.join(root, 'pour_results.csv'), 'w') as out:
    out.write('config,kind,pitch_mm,plane_nhinc,trace,slot,solver,options,f_Hz,R_ohm,L_H,filaments,meshes,wall_s,exit,error,zc\n')
    for k in order:
        for f in freqs:
            r = by[k].get(f)
            if r:
                out.write(f"{r['config']},{r['kind']},{r['pitch_mm']:.9g},{r['plane_nhinc']},{r['trace']},{r['slot']},{r['tag']},"
                          f"{r['options']},{f:g},{'' if r['R'] is None else format(r['R'], '.6g')},"
                          f"{'' if r['L'] is None else format(r['L'], '.6g')},{r['filaments']},{r['meshes']},{r['wall_s']},{r['exit']},"
                          f"{r['error'] or ''},{os.path.relpath(r['zc'], root) if r.get('zc') else ''}\n")
for q, s, unit in (('R', 1e3, 'mOhm'), ('L', 1e9, 'nH')):
    print(f'\n{q} [{unit}]' + ''.join(f'{f:>9.0e}' for f in freqs) + '   fils  max wall s')
    for k in order:
        rs = by[k]
        fils = next((r['filaments'] for r in rs.values() if r['filaments']), None)
        wall = max((r['wall_s'] or 0) for r in rs.values())
        print(f'{k[0]:<20s}{k[1]:<6s}' + ''.join(' ' + fmt(rs[f][q] if f in rs else None, s) for f in freqs) + f' {fils} {wall:.0f}')
