"""Writes filter_sweep.csv, patch_variants.csv and mesh_study.txt from the console output of the FU-15
exploration runs (temporary Explore_Filter / Explore_Patch theories; Release build, `dotnet test` with
console logging). The raw logs are not kept: they are mostly build output with local paths.

Usage: python -I extract_logs.py <outdir> <explore1.log> <explore2.log> <explore3.log> <explore4.log> <explore5.log>

Machine-readable lines:
  FILTER,mesh_mm,f_GHz, S11 re,im, S21 re,im (50 ohm), S11 re,im, S21 re,im (line's own Z0), cal Z0 re, eps_eff
  PATCH,mesh_mm,f_GHz, Zedge re,im, cal Z0 re, eps_eff, unknowns                          (explore3)
  PATCH,stub_mm,f_GHz, Zedge re,im, cal Z0 re, eps_eff, unknowns, Zport re,im, cal Z0 im,
        gamma re,im (1/m), port series re,im (ohm), port shunt re,im (S), 2D Z0          (explore4, explore5)
The configuration of each block comes from its "Passed ...Explore_*(maxEdge: .., minEdge: .., growth: ..,
[stub: ..], [portDistance: ..])" header. Explore_Patch's defaults as run were stub 5 mm and port distance
10 mm (explore3, and explore4's stub-5 rerun, which reproduces explore3's numbers).
"""
import csv, os, re, sys

out, logs = sys.argv[1], sys.argv[2:]
names = ['explore%d' % (i + 1) for i in range(len(logs))]
HDR = re.compile(r'Passed OpenSim\.Tests\.Rf\.SheenCircuitsTests\.(Explore_\w+)\((.*)\) \[')
HUMAN = re.compile(r'^ (mesh [\d./]+ )?f [\d.]+ GHz: ')
FHUM = re.compile(r'f ([\d.]+) GHz: unknowns (\d+)/(\d+)/(\d+).*line Z0 <([-\d.]+); ([-\d.]+)> \(2D ([\d.]+)\), eeff ([\d.]+) \(2D ([\d.]+)\)')


def args_of(s):
    d = {}
    for m in re.finditer(r'(\w+): ("[^"]*"|[^,]+)', s):
        d[m.group(1)] = m.group(2).strip().strip('"')
    return d


filt, patch, human = [], {}, []
for name, path in zip(names, logs):
    cur, conf = None, {}
    lines = open(path, encoding='utf-8-sig', errors='replace').read().splitlines()
    for i, line in enumerate(lines):
        m = HDR.search(line)
        if m:
            cur, conf = m.group(1), args_of(m.group(2))
            if name in ('explore1', 'explore2'):
                human.append((name, line.strip().replace('OpenSim.Tests.Rf.SheenCircuitsTests.', '')))
            continue
        if HUMAN.match(line) and name in ('explore1', 'explore2'):
            human.append((name, line.strip()))
        if line.startswith(' FILTER,') and name == 'explore3':
            p = line.strip().split(',')
            h = FHUM.search(lines[i - 1])
            assert h and abs(float(h.group(1)) - float(p[2])) < 1e-9, line
            filt.append(dict(max_edge_mm=float(conf['maxEdge']) * 1e3, min_edge_mm=float(conf['minEdge']) * 1e3,
                             growth=float(conf['growth']), f_GHz=float(p[2]),
                             s11_50_re=p[3], s11_50_im=p[4], s21_50_re=p[5], s21_50_im=p[6],
                             s11_line_re=p[7], s11_line_im=p[8], s21_line_re=p[9], s21_line_im=p[10],
                             cal_z0_re=p[11], cal_z0_im=h.group(6), eps_eff=p[12], z0_2d=h.group(7), eps_eff_2d=h.group(9),
                             unknowns_short=h.group(2), unknowns_long=h.group(3), unknowns_dut=h.group(4)))
        if line.startswith(' PATCH,'):
            p = line.strip().split(',')
            stub = float(conf.get('stub', 0.005)) * 1e3
            port = float(conf.get('portDistance', 0.010)) * 1e3
            mx, mn, g = float(conf['maxEdge']) * 1e3, float(conf['minEdge']) * 1e3, float(conf['growth'])
            if len(p) > 9:
                assert abs(float(p[1]) - stub) < 1e-9, line
            else:
                assert abs(float(p[1]) - mx) < 1e-9, line
            key = (round(mx, 4), round(mn, 4), round(g, 4), round(stub, 4), round(port, 4), float(p[2]))
            row = dict(max_edge_mm=mx, min_edge_mm=mn, growth=g, stub_mm=stub, port_distance_mm=port, f_GHz=float(p[2]),
                       zedge_re=p[3], zedge_im=p[4], cal_z0_re=p[5], eps_eff=p[6], unknowns_dut=p[7], source=name)
            if len(p) > 9:
                row.update(zport_re=p[8], zport_im=p[9], cal_z0_im=p[10], gamma_re=p[11], gamma_im=p[12], port_series_re=p[13],
                           port_series_im=p[14], port_shunt_re=p[15], port_shunt_im=p[16], z0_2d=p[17])
            if key in patch:
                old = patch[key]
                assert abs(complex(float(old['zedge_re']), float(old['zedge_im'])) - complex(float(p[3]), float(p[4]))) < 1e-6, line
                old.update({k: v for k, v in row.items() if k not in old})
                old['source'] += '+' + name
            else:
                patch[key] = row

fcols = ['max_edge_mm', 'min_edge_mm', 'growth', 'f_GHz', 's11_50_re', 's11_50_im', 's21_50_re', 's21_50_im', 's11_line_re', 's11_line_im',
         's21_line_re', 's21_line_im', 'cal_z0_re', 'cal_z0_im', 'eps_eff', 'z0_2d', 'eps_eff_2d', 'unknowns_short', 'unknowns_long', 'unknowns_dut']
with open(os.path.join(out, 'filter_sweep.csv'), 'w', newline='') as fh:
    w = csv.DictWriter(fh, fieldnames=fcols, lineterminator='\n'); w.writeheader(); w.writerows(filt)
pcols = ['max_edge_mm', 'min_edge_mm', 'growth', 'stub_mm', 'port_distance_mm', 'f_GHz', 'zedge_re', 'zedge_im', 'cal_z0_re', 'cal_z0_im',
         'eps_eff', 'unknowns_dut', 'zport_re', 'zport_im', 'gamma_re', 'gamma_im', 'port_series_re', 'port_series_im', 'port_shunt_re',
         'port_shunt_im', 'z0_2d', 'source']
with open(os.path.join(out, 'patch_variants.csv'), 'w', newline='') as fh:
    w = csv.DictWriter(fh, fieldnames=pcols, restval='', lineterminator='\n'); w.writeheader()
    for k in sorted(patch):
        w.writerow(patch[k])
with open(os.path.join(out, 'mesh_study.txt'), 'w', newline='\n') as fh:
    fh.write('FU-15 exploration output (OpenSim, Release build, 2026-10-18), human-readable lines only.\n'
             'explore1: the filter on the 0.4 mm mesh with calibration lines of 6 and 11 mm (the 11 mm line off the grid; see the\n'
             '  comment in SheenCircuitsTests.cs). explore2: the same mesh and 0.6 and 0.8 mm meshes with lines of 6 and 12 mm, and the\n'
             '  first patch pass (2.5 mm cells, stub 5 mm, port 10 mm). Mesh a/b/c = largest edge mm / smallest edge mm / growth.\n'
             'unknowns = short calibration line / long calibration line / circuit. line Z0 <re; im> and eeff come from the two-line\n'
             '  calibration, "2D" from the 2D cross-section solve. |S11|, |S21| = 50 ohm reference; own-Z0 = the line\'s own Z0;\n'
             '  raw = before de-embedding.\n')
    last = None
    for name, line in human:
        if name != last:
            fh.write('\n== %s\n' % name); last = name
        fh.write(line + '\n')
print(len(filt), 'filter rows;', len(patch), 'patch rows;', len(human), 'human-readable lines')
