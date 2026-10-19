"""FastHenry runner and Zc.mat reader.

    python -I fh.py run <deck.inp> [--suffix S] [-- <fasthenry options...>]
    python -I fh.py parse <Zc.mat>

run: executes ../fasthenry.exe (the exe next to this scripts/ folder) with the deck's folder as
the working directory, so Zc<S>.mat lands beside the deck; stdout+stderr go to
<deck stem><S>.log; prints a JSON line with wall time, options and R, L per frequency.
"""
import json, math, os, re, subprocess, sys, time

HERE = os.path.dirname(os.path.abspath(__file__))
EXE = os.path.join(os.path.dirname(HERE), 'fasthenry.exe')

_num = r'[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?'


def parse_zc(path):
    """Returns [{'f': Hz, 'Z': [[complex]]}] and the port lines."""
    out, ports = [], []
    with open(path) as fh:
        lines = fh.read().split('\n')
    i = 0
    while i < len(lines):
        line = lines[i]
        m = re.match(r'\s*Impedance matrix for frequency = (' + _num + r') (\d+) x (\d+)', line)
        if m:
            f, n, k = float(m.group(1)), int(m.group(2)), int(m.group(3))
            rows = []
            for r in range(n):
                i += 1
                vals = re.findall(r'(' + _num + r')\s*(' + _num + r')j', lines[i])
                rows.append([complex(float(a), float(b)) for a, b in vals])
            out.append({'f': f, 'Z': rows})
        elif line.startswith('Row '):
            ports.append(line.strip())
        i += 1
    return out, ports


def run(deck, opts=(), suffix='', timeout=None, true_freqs=None):
    """true_freqs: the deck's exact frequencies (Zc.mat prints f with 6 digits); used for L."""
    deck = os.path.abspath(deck)
    folder, stem = os.path.dirname(deck), os.path.splitext(os.path.basename(deck))[0]
    args = [EXE, os.path.basename(deck)] + list(opts)
    if suffix:
        args += ['-S', suffix]
    log = os.path.join(folder, f'{stem}{suffix}.log')
    t0 = time.perf_counter()
    with open(log, 'w') as fh:
        p = subprocess.run(args, cwd=folder, stdout=fh, stderr=subprocess.STDOUT, timeout=timeout)
    wall = time.perf_counter() - t0
    zc = os.path.join(folder, f'Zc{suffix}.mat')
    res = {'deck': deck, 'options': list(opts), 'suffix': suffix, 'exit': p.returncode,
           'wall_s': round(wall, 3), 'log': log, 'zc': zc}
    if os.path.exists(zc):
        pts, ports = parse_zc(zc)
        if true_freqs is not None and len(true_freqs) == len(pts):
            for q, f in zip(pts, true_freqs):
                if abs(q['f'] - f) > 1e-5 * f:
                    raise RuntimeError(f'frequency mismatch {q["f"]} vs {f}')
                q['f'] = f
        res['ports'] = ports
        res['points'] = [{'f': q['f'], 'R': q['Z'][0][0].real,
                          'L': q['Z'][0][0].imag / (2 * math.pi * q['f']) if q['f'] > 0 else None,
                          'Zre': q['Z'][0][0].real, 'Zim': q['Z'][0][0].imag} for q in pts]
    # FastHenry's own counts and times from the log
    with open(log, errors='replace') as fh:
        text = fh.read()
    for key, pat in [('filaments', r'Number of filaments:\s*(\d+)'), ('segments', r'Number of segments:\s*(\d+)'),
                     ('nodes', r'Number of nodes:\s*(\d+)'), ('meshes', r'Number of meshes:\s*(\d+)'),
                     ('fh_total_s', r'Total:\s*(' + _num + ')')]:
        m = re.search(pat, text)
        if m:
            res[key] = float(m.group(1)) if key == 'fh_total_s' else int(m.group(1))
    return res


if __name__ == '__main__':
    if sys.argv[1] == 'parse':
        pts, ports = parse_zc(sys.argv[2])
        print(json.dumps({'ports': ports, 'points': [{'f': p['f'], 'Z': [[(z.real, z.imag) for z in r] for r in p['Z']]} for p in pts]}, indent=1))
    elif sys.argv[1] == 'run':
        deck = sys.argv[2]
        rest = sys.argv[3:]
        suffix = ''
        if rest[:1] == ['--suffix']:
            suffix, rest = rest[1], rest[2:]
        if rest[:1] == ['--']:
            rest = rest[1:]
        print(json.dumps(run(deck, rest, suffix)))
