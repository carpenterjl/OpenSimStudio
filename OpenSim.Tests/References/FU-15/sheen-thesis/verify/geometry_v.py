"""geometry_verified.json = the other agent's geometry.json with the verifier's corrections/additions."""
import json, sys
T, M = sys.argv[1], sys.argv[2]
g = json.load(open(T + '/geometry.json'))
g['verification'] = ('Checked by the verifier against own 5088-px renders (pages/pdfNNN_w5088.png in sheen-thesis-verify): every '
                     'dimension, substrate value, cell size, time parameter and arithmetic in this file was re-read and recomputed. '
                     'Changes are listed under "verifier_changes".')
ch = []
fd = g['line_fed_patch']['fdtd_model_p158_159']
old = fd['variable_grid']
fd['variable_grid'] = ('3:1 variable gridding in x and y only: p.158 says spacings three times larger are used around the antenna, '
                       'and p.159 says the 3-to-1 variable gridding is in the x and y directions (visible in Fig. 4.12). '
                       'Outer cells 3*1.11667 = 3.35 mm in x/y; nothing in the text supports a 3x (1.59 mm) z cell.')
ch.append(dict(path='line_fed_patch.fdtd_model_p158_159.variable_grid', old=old, new=fd['variable_grid']))
fd['n_steps'] = 6000
fd['n_steps_note'] = 'p.159: the run stops when the reference-plane response has decayed to about zero, or at 6000 dt (6000 is a cap, not a fixed length)'
ch.append(dict(path='line_fed_patch.fdtd_model_p158_159.n_steps_note', new=fd['n_steps_note']))
bc = g['branch_line_coupler']['fdtd_model']
bc['INCONSISTENCIES'] = ['p.117 says dx and dy are chosen to match the 9.75 mm centre-to-centre distance exactly, but the stated '
                         'centre-to-centre distance 24*dx = 24*0.390 = 9.36 mm; 9.75 mm would need 25 cells of 0.390 mm or dx = 9.75/24 = 0.40625 mm',
                         'p.117 says dz matches the substrate exactly: 3*0.265 = 0.795 mm vs 0.794 mm (Fig. 3.35), i.e. exact to 0.001 mm']
ch.append(dict(path='branch_line_coupler.fdtd_model.INCONSISTENCIES', new=bc['INCONSISTENCIES']))
lp = g['low_pass_filter']
lp['fdtd_model_p112']['INCONSISTENCIES'].append(
    'p.109/p.112 also say the port locations and widths carry an error smaller than one step, '
    'which is consistent with 6 dx = 2.369 mm for the 2.413 mm lines, but not with 50 dx = 19.74 mm for the 20.32 mm patch')
lp['measurement']['frequency_range'] = ('measured dash-dot |S21| starts at 1.98-1.99 GHz and |S11| at about 1.98 GHz (Figs. 3.33/3.34); '
                                        'plotted to 20 GHz; data above 18 GHz are beyond the analyzer/connector rating (p.136)')
ch.append(dict(path='low_pass_filter.measurement.frequency_range', new=lp['measurement']['frequency_range']))
g['verifier_changes'] = ch
json.dump(g, open(M + '/geometry_verified.json', 'w'), indent=1)
print(json.dumps(ch, indent=1))

