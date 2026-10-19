"""Builds the *_verified files in this folder from the other agent's files plus MY re-digitised points.
python -I make_verified.py <their_dir> <my_dir>"""
import sys, csv, json, copy
import numpy as np

T, M = sys.argv[1], sys.argv[2]
COLS = ['freq_GHz', 'value', 'quantity', 'curve', 'figure', 'thesis_page', 'pdf_page', 'u_freq', 'u_value']


def read(p):
    return list(csv.DictReader(open(p)))


def write(p, rows):
    with open(p, 'w', newline='') as fh:
        w = csv.DictWriter(fh, fieldnames=COLS); w.writeheader()
        for r in rows:
            w.writerow({k: r[k] for k in COLS})


def segments_trim(rows):
    """drop the first and last point of each contiguous run of vertical-probe points (dash end caps)"""
    out = []
    vert = [r for r in rows if not r['method'].startswith('h:')]
    hor = [r for r in rows if r['method'].startswith('h:')]
    seg = []
    for r in vert:
        if seg and float(r['freq_GHz']) - float(seg[-1]['freq_GHz']) > 0.0301:
            out += seg[1:-1] if len(seg) > 2 else []
            seg = []
        seg.append(r)
    out += seg[1:-1] if len(seg) > 2 else []
    return out + hor


log = {}
# ---------- Fig. 3.34 |S21| ----------
th = read(T + '/fig3_34_S21_dB.csv')
mine = read(M + '/work/redig334_pb.csv')
mine = [r for r in mine if 1.95 <= float(r['freq_GHz']) < 5.52]
# exclude: -1.3 dB horizontal point (FD-TD-only run misread as merged), -2.2/-2.3 (lump top cap)
mine = [r for r in mine if not (r['method'].startswith('h:') and float(r['value']) in (-1.3, -2.2, -2.3))]
mine = segments_trim(mine)
dropped = [r for r in th if r['curve'] == 'measured' and 1.95 <= float(r['freq_GHz']) < 5.52]
keep = [r for r in th if not (r['curve'] == 'measured' and 1.95 <= float(r['freq_GHz']) < 5.52)]
new = [dict(freq_GHz=r['freq_GHz'], value=r['value'], quantity='S21_dB', curve='measured', figure='Fig. 3.34', thesis_page='116',
            pdf_page='116', u_freq=r['u_freq'], u_value=r['u_value']) for r in mine]
out = keep + new
out.sort(key=lambda r: (r['curve'], float(r['freq_GHz'])))
write(M + '/fig3_34_S21_dB_verified.csv', out)
log['fig3_34'] = dict(removed_measured_rows=len(dropped), removed_range_GHz=[1.95, 5.52], added_measured_rows=len(new),
                      note='measured rows 1.95-5.52 GHz replaced by verifier re-digitisation (dash-dot line below the FD-TD line; '
                           'dash gaps 2.63-2.93, 3.05-3.35, 4.41-4.69, 5.27-5.46 GHz left empty instead of copying the FD-TD trace)')

# ---------- Fig. 3.33 |S11| ----------
th = read(T + '/fig3_33_S11_dB.csv')
mine = read(M + '/work/redig333_hi.csv')
mine = segments_trim([r for r in mine if 18.88 <= float(r['freq_GHz']) <= 20.0])
drop_meas = [r for r in th if r['curve'] == 'measured' and float(r['freq_GHz']) >= 18.88]
drop_fd = [r for r in th if r['curve'] == 'fdtd' and float(r['freq_GHz']) < 0.19]
keep = [r for r in th if r not in drop_meas and r not in drop_fd]
new = [dict(freq_GHz=r['freq_GHz'], value=r['value'], quantity='S11_dB', curve='measured', figure='Fig. 3.33', thesis_page='115',
            pdf_page='115', u_freq=r['u_freq'], u_value=r['u_value']) for r in mine]
tip = dict(freq_GHz='0.130', value='-33.26', quantity='S11_dB', curve='fdtd', figure='Fig. 3.33', thesis_page='115', pdf_page='115',
           u_freq='0.040', u_value='0.250')
out = keep + new + [tip]
out.sort(key=lambda r: (r['curve'], float(r['freq_GHz'])))
write(M + '/fig3_33_S11_dB_verified.csv', out)
log['fig3_33'] = dict(removed_measured_rows=len(drop_meas), removed_measured_range_GHz=[18.88, 20.0], added_measured_rows=len(new),
                      removed_fdtd_rows_below_0p19GHz=len(drop_fd), added_fdtd_tip='0.130 GHz, -33.26 dB (ink bottom -33.44..-33.55 dB '
                      'at 0.09-0.18 GHz plus half line width)')

# ---------- Fig. 4.14 ----------
th = read(T + '/fig4_14_Zin.csv')
n = 0
for r in th:
    if r['curve'] == 'measured' and r['quantity'] == 'Zin_R_ohm' and abs(float(r['freq_GHz']) - 2.2) < 0.01:
        log['fig4_14'] = dict(old=dict(r), new_value=21.92, new_freq=2.1996, new_u_value=0.7)
        r['value'] = '21.92'; r['freq_GHz'] = '2.1996'; r['u_value'] = '0.70'; n += 1
assert n == 1
write(M + '/fig4_14_Zin_verified.csv', th)

# ---------- combined ----------
allr = read(M + '/fig3_33_S11_dB_verified.csv') + read(M + '/fig3_34_S21_dB_verified.csv') + read(M + '/fig4_14_Zin_verified.csv') + \
    read(T + '/fig4_15_smith_measured.csv')
write(M + '/all_digitised_verified.csv', allr)
json.dump(log, open(M + '/work/make_verified_log.json', 'w'), indent=1)
print(json.dumps(log, indent=1))

