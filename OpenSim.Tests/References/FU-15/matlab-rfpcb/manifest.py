"""Build run_manifest.csv from the MATLAB logs in this folder (every MoM run: tag, structure,
MaxEdgeLength, triangles, tetrahedra, solver, sweep type, outputs, solved frequencies, time).
Usage: python -I manifest.py <folder> <out_csv>"""
import csv, glob, os, re, sys
d, out = sys.argv[1], sys.argv[2]
rows = []
pat_sweep = re.compile(r'^(\S+): kind (\w+) mel ([\d.]+) mm, tri (\d+) tet (\d+), solver (\w+), sweep (\w+), (\d+) outputs, (\d+) solved freqs, mesh ([\d.]+) s, solve ([\d.]+) s')
pat_dir = re.compile(r'^(\S+): mel ([\d.]+) mm, tri (\d+), tet (\d+)')
pat_pt = re.compile(r'^([\d.]+) GHz: .*\(([\d.]+) s\)')
for log in sorted(glob.glob(os.path.join(d, '*.log'))):
    name = os.path.basename(log)
    cur = None
    for line in open(log, errors='replace'):
        line = line.strip()
        m = pat_sweep.match(line)
        if m:
            rows.append({'log': name, 'tag': m.group(1), 'structure': m.group(2), 'MaxEdgeLength_mm': m.group(3), 'triangles': m.group(4),
                         'tetrahedra': m.group(5), 'solver': m.group(6), 'sweep': m.group(7), 'outputs': m.group(8),
                         'solved_freqs': m.group(9), 'freqs_GHz': '', 'solve_s': m.group(11)})
            continue
        m = pat_dir.match(line)
        if m:
            cur = {'log': name, 'tag': m.group(1), 'structure': 'filter' if '_F_' in m.group(1) else 'patch', 'MaxEdgeLength_mm': m.group(2),
                   'triangles': m.group(3), 'tetrahedra': m.group(4), 'solver': 'MoM', 'sweep': 'direct', 'outputs': 0,
                   'solved_freqs': 0, 'freqs_GHz': '', 'solve_s': 0.0}
            rows.append(cur)
            continue
        m = pat_pt.match(line)
        if m and cur is not None:
            cur['outputs'] = int(cur['outputs']) + 1; cur['solved_freqs'] = cur['outputs']
            cur['freqs_GHz'] = (cur['freqs_GHz'] + ' ' + m.group(1)).strip()
            cur['solve_s'] = round(float(cur['solve_s']) + float(m.group(2)), 1)
with open(out, 'w', newline='') as fh:
    w = csv.DictWriter(fh, fieldnames=['log', 'tag', 'structure', 'MaxEdgeLength_mm', 'triangles', 'tetrahedra', 'solver', 'sweep',
                                       'outputs', 'solved_freqs', 'freqs_GHz', 'solve_s'])
    w.writeheader(); w.writerows(rows)
print(out, len(rows), 'runs')
