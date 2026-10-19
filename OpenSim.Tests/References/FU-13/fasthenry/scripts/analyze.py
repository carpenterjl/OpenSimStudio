"""Convergence analysis of the pour runs (results.jsonl, solver tag 'gmres' unless noted).
usage: python -I analyze.py <pour root>  -> prints tables, writes convergence.json"""
import json, math, os, sys

root = sys.argv[1]
latest = {}
for l in open(os.path.join(root, 'results.jsonl')):
    r = json.loads(l)
    if r['R'] is not None:
        latest[(r['config'], r['tag'], r['f'])] = r
man = {m['name']: m for m in json.load(open(os.path.join(root, 'manifest.json')))}
F = [1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 3e8, 1e9]
SIGMA, T = 5.8e7, 35e-6
RS = 1 / (SIGMA * T)                      # sheet resistance of the pour [ohm/sq]


def get(cfg, f, q, tag='gmres'):
    r = latest.get((cfg, tag, f))
    return None if r is None else r[q]


def name(k, p, tr, slot):
    return f'k{k:02d}_p{p}_{tr}_{"slot" if slot else "solid"}'


out = {'Rs_ohm_per_sq': RS, 'tables': {}}
lines = []


def table(title, rows, q, scale, unit):
    lines.append(f'\n### {title} -- {q} [{unit}]')
    lines.append('| config | pitch mm | ' + ' | '.join(f'{f:.0e}' for f in F) + ' |')
    lines.append('|---|---|' + '---|' * len(F))
    for cfg in rows:
        if cfg not in man:
            continue
        vals = [get(cfg, f, q) for f in F]
        if all(v is None for v in vals):
            continue
        lines.append(f'| {cfg} | {man[cfg]["pitch_mm"]:.4f} | ' + ' | '.join('' if v is None else f'{v * scale:.4f}' for v in vals) + ' |')


def change_row(label, a, b, q):
    """relative change b vs a (b finer) per frequency"""
    vals = []
    for f in F:
        va, vb = get(a, f, q), get(b, f, q)
        vals.append(None if va is None or vb is None else (vb - va) / va)
    lines.append(f'| {label} | ' + ' | '.join('' if v is None else f'{v * 100:+.3f}%' for v in vals) + ' |')
    return vals


levels = [6, 9, 20, 29, 41]
for slot in (False, True):
    s = 'slotted' if slot else 'solid'
    for p in (1, 3):
        rows = [name(k, p, 'T1', slot) for k in levels]
        for q, sc, u in (('L', 1e9, 'nH'), ('R', 1e3, 'mOhm')):
            table(f'{s} pour, plane nhinc {p}, trace 7x5 r2: plane pitch sweep', rows, q, sc, u)
            lines.append('\nchange between successive pitch levels:')
            lines.append('| step | ' + ' | '.join(f'{f:.0e}' for f in F) + ' |')
            lines.append('|---|' + '---|' * len(F))
            have = [c for c in rows if c in man and any(get(c, f, q) is not None for f in F)]
            for a, b in zip(have, have[1:]):
                out['tables'][f'{s}_p{p}_{q}_{a}_to_{b}'] = change_row(f'{a} -> {b}', a, b, q)

# low-frequency resistance against the point-contact spreading law
lines.append('\n### Low-frequency R against the two point contacts\' spreading resistance')
lines.append('Two point contacts (port and short) in a sheet of resistance Rs = %.5g mOhm/sq: each adds (Rs/2pi) ln(1/h),'
             ' so refining the pitch from h1 to h2 should add (Rs/pi) ln(h1/h2).' % (RS * 1e3))
lines.append('| step | pitch mm | dR(100 Hz) FastHenry mOhm | (Rs/pi) ln(h1/h2) mOhm |')
lines.append('|---|---|---|---|')
for slot in (False, True):
    for p in (1,):
        rows = [name(k, p, 'T1', slot) for k in levels]
        have = [c for c in rows if get(c, 1e2, 'R') is not None]
        for a, b in zip(have, have[1:]):
            h1, h2 = man[a]['pitch_mm'], man[b]['pitch_mm']
            d = get(b, 1e2, 'R') - get(a, 1e2, 'R')
            pred = RS / math.pi * math.log(h1 / h2)
            lines.append(f'| {a} -> {b} | {h1:.4f} -> {h2:.4f} | {d * 1e3:.4f} | {pred * 1e3:.4f} |')

# trace and plane-thickness discretisation at fixed pitch
for slot in (False, True):
    s = 'slotted' if slot else 'solid'
    for k in (6, 9, 20, 29):
        rows = [name(k, 1, 'T1', slot), name(k, 3, 'T1', slot), name(k, 3, 'T2', slot), name(k, 3, 'T3', slot), name(k, 5, 'T2', slot)]
        for q, sc, u in (('L', 1e9, 'nH'), ('R', 1e3, 'mOhm')):
            table(f'{s} pour, pitch level k={k}: trace and plane-thickness filaments', rows, q, sc, u)
        lines.append('\nrelative changes:')
        lines.append('| change | ' + ' | '.join(f'{f:.0e}' for f in F) + ' |')
        lines.append('|---|' + '---|' * len(F))
        pairs = [(name(k, 1, 'T1', slot), name(k, 3, 'T1', slot), 'plane nhinc 1 -> 3'),
                 (name(k, 3, 'T1', slot), name(k, 3, 'T2', slot), 'trace 7x5 -> 15x9'),
                 (name(k, 3, 'T2', slot), name(k, 3, 'T3', slot), 'trace 15x9 r2 -> 21x11 r1.6'),
                 (name(k, 3, 'T2', slot), name(k, 5, 'T2', slot), 'plane nhinc 3 -> 5')]
        for a, b, lab in pairs:
            if a in man and b in man:
                for q in ('L', 'R'):
                    if any(get(a, f, q) is not None and get(b, f, q) is not None for f in F):
                        out['tables'][f'{s}_k{k}_{lab}_{q}'] = change_row(f'{lab} ({q})', a, b, q)

# OpenSim-matched grid
rows = ['os03_p1_T1_solid', 'os03_p3_T1_solid', 'os03_p1_T1_slot', 'os03_p3_T1_slot']
for q, sc, u in (('L', 1e9, 'nH'), ('R', 1e3, 'mOhm')):
    table('OpenSim-matched grid (0.3 mm, OpenSim\'s end nodes)', rows, q, sc, u)
lines.append('\nslot / solid inductance ratio:')
lines.append('| grid | ' + ' | '.join(f'{f:.0e}' for f in F) + ' |')
lines.append('|---|' + '---|' * len(F))
for a, b in (('os03_p3_T1_solid', 'os03_p3_T1_slot'), ('k09_p3_T1_solid', 'k09_p3_T1_slot'), ('k20_p3_T1_solid', 'k20_p3_T1_slot'),
             ('k20_p3_T2_solid', 'k20_p3_T2_slot'), ('k29_p1_T1_solid', 'k29_p1_T1_slot'), ('k41_p1_T1_solid', 'k41_p1_T1_slot')):
    vals = [None if get(a, f, 'L') is None or get(b, f, 'L') is None else get(b, f, 'L') / get(a, f, 'L') for f in F]
    if any(v is not None for v in vals):
        lines.append(f'| {a[:-6]} | ' + ' | '.join('' if v is None else f'{v:.4f}' for v in vals) + ' |')
        out['tables'][f'ratio_{a}'] = vals

text = '\n'.join(lines)
print(text)
json.dump(out, open(os.path.join(root, 'convergence.json'), 'w'), indent=1)
