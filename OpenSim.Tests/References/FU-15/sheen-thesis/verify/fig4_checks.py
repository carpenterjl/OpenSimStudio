"""Writes my_checkpoints_fig4_14_4_15.csv: verifier readings of the Fig. 4.14 markers and Fig. 4.15 'o' markers vs the other agent.
Readings come from: mk2.py ring (hole centroid of 'o'), mk2.py star (top/bottom tip midpoint), mk.py component bbox (isolated stars at
2.25/2.30), the 2.20 star top tip (34.55 ohm at 2.1996 GHz) minus the tip-to-centre distance 12.63 ohm measured on the 4 isolated stars,
and smith_v.py (own circle fit; work/smith_v.json). Commands are listed in the 'source' column."""
import csv, json, sys
M, T = sys.argv[1], sys.argv[2]
mine_R = {2.10: (2.57, 'mk2 star'), 2.15: (8.41, 'mk2 star'), 2.20: (21.92, 'top tip 34.55 - 12.63'), 2.25: (117.11, 'mk comp bbox'),
          2.30: (118.55, 'mk comp bbox'), 2.35: (17.72, 'mk2 star'), 2.40: (7.70, 'mk2 star')}
mine_X = {2.10: 41.98, 2.15: 59.79, 2.20: 94.49, 2.25: 192.50, 2.30: -180.43, 2.35: -93.66, 2.40: -44.66}
th = {}
for r in csv.DictReader(open(T + '/fig4_14_Zin.csv')):
    if r['curve'] == 'measured':
        th[(r['quantity'], round(float(r['freq_GHz']), 2))] = float(r['value'])
sm = {}
for r in csv.DictReader(open(T + '/fig4_15_smith_measured.csv')):
    sm[(r['quantity'], round(float(r['freq_GHz']), 2))] = float(r['value'])
mys = json.load(open(M + '/work/smith_v.json'))['markers']
rows = []
for i, f in enumerate(sorted(mine_R)):
    rows.append(dict(figure='Fig. 4.14', page=162, quantity='R_meas_ohm', freq_GHz=f, mine=mine_R[f][0], theirs=th[('Zin_R_ohm', f)],
                     diff=round(mine_R[f][0] - th[('Zin_R_ohm', f)], 2), source=mine_R[f][1]))
    rows.append(dict(figure='Fig. 4.14', page=162, quantity='X_meas_ohm', freq_GHz=f, mine=mine_X[f], theirs=th[('Zin_X_ohm', f)],
                     diff=round(mine_X[f] - th[('Zin_X_ohm', f)], 2), source='mk2 ring'))
    for q, k in (('Gamma_re', 'G_re'), ('Gamma_im', 'G_im')):
        v = round(mys[i][k], 4); t = sm[(q + '_Fig4.15', f)]
        rows.append(dict(figure='Fig. 4.15', page=163, quantity=q, freq_GHz=f, mine=v, theirs=t, diff=round(v - t, 4), source='smith_v ring hole'))
with open(M + '/my_checkpoints_fig4_14_4_15.csv', 'w', newline='') as fh:
    w = csv.DictWriter(fh, fieldnames=list(rows[0].keys())); w.writeheader(); w.writerows(rows)
for r in rows:
    print(r)
