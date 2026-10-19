"""FastHenry decks for OpenSim's AReturnThroughAPour_GoesRoundASlot problem
(OpenSim.Tests/Extraction/ExtractionTests.cs, AReturnThroughAPour_GoesRoundASlot;
LoopExtraction.ChainOverPlane). The test as written uses the default real end connections
(IdealEndConnections = false); the main decks short the far end ideally (IdealEndConnections = true,
as PeecReferenceTests' idealEnds cases), and the *_re configs add the real connection.

Geometry (mm; OpenSim's metres x 1e3):
  trace  : centre line (0,0,0.335) -> (6,0,0.335), w 0.4, h 0.035 (TraceSegment3D z is the copper
           mid-plane: z = 0.3e-3 + 35e-6)
  pour   : x -3..9, y -3..3, copper z -0.035..0 (planeZ = (-35e-6, 0)), mid-plane z = -0.0175
  slot   : x 2.7..3.3, y -2..2 (hole through the pour)
  port   : .external trace start -> plane node under it (no conductor across the gap)
  short  : .equiv trace end = plane node under it (ideal, IdealEndConnections = true; *_re decks use the real join instead)
  sigma  : 5.8e7 S/m = 5.8e4 /(mm*Ohm) with .units mm

Plane discretisation: FastHenry's uniform plane 'G' with its corner nodes inset half a cell from the
pour outline, so the node cells (pitch x pitch, segments one pitch wide) tile exactly the 12 x 6 mm
pour -- the same rule as OpenSim's PeecModel.AddPlane (nodes at cell centres, bars one pitch wide).
  'conv' grids: pitch h = 6/(2k+1) mm, which puts plane nodes exactly at (0,0) and (6,0).
  'matched' grid: OpenSim's own 0.3 mm grid (40 x 20 cells), with the port and short on the plane
                  nodes OpenSim itself picks (grid.NodeNear, replicated in IEEE doubles below).
Slot: the plane nodes whose cell centres lie inside the slot are removed with 'hole rect' (FastHenry
removes the nodes and every segment touching them) -- OpenSim's rule (index.Contains(cell centre)).

usage: python -I pour_decks.py <out root>   (writes decks + manifest.json; does not run anything)
"""
import json, math, os, sys

Z_PLANE = -0.0175
T_PLANE = 0.035
Z_TRACE = 0.335
W_TRACE, H_TRACE = 0.4, 0.035
SIGMA_MM = 5.8e4
FREQS = [1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 3e8, 1e9]
SLOT = (2.7, 3.3, -2.0, 2.0)


def ftag(f):
    return f'{f:.0e}'.replace('+0', '').replace('+', '')


def conv_grid(k):
    h = 6.0 / (2 * k + 1)
    nx, ny = 2 * (2 * k + 1), 2 * k + 1                  # cells along 12 mm and 6 mm
    xs = [-3 + (i + 0.5) * h for i in range(nx)]
    ys = [-3 + (j + 0.5) * h for j in range(ny)]
    i0, j0, i6 = k, k, 3 * k + 1
    assert abs(xs[i0]) < 1e-12 and abs(ys[j0]) < 1e-12 and abs(xs[i6] - 6) < 1e-12
    return {'kind': 'conv', 'k': k, 'h': h, 'xs': xs, 'ys': ys, 'start_node': (xs[i0], ys[j0]),
            'end_node': (xs[i6], ys[j0])}


def opensim_grid(pitch_m=0.3e-3):
    """OpenSim's AddPlane grid and the nodes NodeNear/MinBy picks, in IEEE doubles (metres)."""
    minX, maxX, minY, maxY = -3000000 / 1e9, 9000000 / 1e9, -3000000 / 1e9, 3000000 / 1e9   # Clipper, Scale 1e9
    area = (maxX - minX) * (maxY - minY)
    pitch = max(pitch_m, math.sqrt(area / 6000))
    nx = max(1, round_half_away((maxX - minX) / pitch))
    ny = max(1, round_half_away((maxY - minY) / pitch))
    px, py = (maxX - minX) / nx, (maxY - minY) / ny
    nodes = [((minX + (i + 0.5) * px, minY + (j + 0.5) * py), i, j) for i in range(nx) for j in range(ny)]

    def near(t):
        best, bd = None, None
        for (p, i, j) in nodes:                       # MinBy: first minimal element
            dx, dy = p[0] - t[0], p[1] - t[1]
            d = math.sqrt(dx * dx + dy * dy)
            if bd is None or d < bd:
                best, bd = (p, i, j), d
        return best
    s, e = near((0.0, 0.0)), near((6e-3, 0.0))
    h = px * 1e3
    xs = [(minX + (i + 0.5) * px) * 1e3 for i in range(nx)]
    ys = [(minY + (j + 0.5) * py) * 1e3 for j in range(ny)]
    return {'kind': 'matched', 'h': h, 'xs': xs, 'ys': ys, 'nx': nx, 'ny': ny,
            'start_node': (s[0][0] * 1e3, s[0][1] * 1e3), 'end_node': (e[0][0] * 1e3, e[0][1] * 1e3),
            'start_ij': (s[1], s[2]), 'end_ij': (e[1], e[2])}


def round_half_away(v):   # C# Math.Round is banker's rounding; irrelevant here (40.0, 20.0)
    return int(round(v))


def slot_nodes(g):
    x0, x1, y0, y1 = SLOT
    for v in g['xs']:
        assert min(abs(v - x0), abs(v - x1)) > 1e-6, 'cell centre on a slot edge'
    for v in g['ys']:
        assert min(abs(v - y0), abs(v - y1)) > 1e-6, 'cell centre on a slot edge'
    ii = [i for i, x in enumerate(g['xs']) if x0 < x < x1]
    jj = [j for j, y in enumerate(g['ys']) if y0 < y < y1]
    return ii, jj


def exact_grid(m=10):
    """Inset grid of pitch 1/m mm with m a multiple of 10: cell edges fall on x = 2.7, 3.3 and
    y = -2, 2, so the removed cells are exactly the 0.6 x 4 mm slot. The trace ends then sit on cell
    corners: port and short go to the four nodes round each end, shorted together (a 2h x 2h
    contact centred under the end, symmetric about the trace)."""
    assert m % 10 == 0
    h = 1.0 / m
    xs = [-3 + (i + 0.5) * h for i in range(12 * m)]
    ys = [-3 + (j + 0.5) * h for j in range(6 * m)]
    return {'kind': 'exact', 'm': m, 'h': h, 'xs': xs, 'ys': ys, 'start_node': (0.0, 0.0), 'end_node': (6.0, 0.0),
            'contacts4': True}


def realend_text(text, g):
    """OpenSim's real end connection (IdealEndConnections = false, the Join local function in
    LoopExtraction.ChainOverPlane): a round wire of the trace's width (radius 0.2 mm, one square
    filament of the same area, side r*sqrt(pi)) from the trace end straight down to the plane's mid-plane, then -- if the grid node
    is not right under the end -- a bar max(pitch, trace width) wide and plane-thick over to it."""
    ex, ey = g['end_node']
    side = 0.2 * math.sqrt(math.pi)
    add = ['* real end connection (OpenSim IdealEndConnections = false): wire of radius 0.2 mm as one',
           f'* square filament of the same area, from the trace end down to the plane mid-plane z={Z_PLANE:g}',
           f'Nv x=6 y=0 z={Z_PLANE:g}',
           f'Ewire Ne Nv w={side:.12g} h={side:.12g}']
    if abs(ex - 6) > 1e-9 or abs(ey) > 1e-9:
        add += ['* plane copper over to the grid node standing in for the spot (single filament)',
                f'Nb x={ex:.12g} y={ey:.12g} z={Z_PLANE:g}',
                f'Ebar Nv Nb w={max(g["h"], W_TRACE):.12g} h={T_PLANE:g}',
                '.equiv Nb npe']
    else:
        add += ['.equiv Nv npe']
    text = text.replace('.equiv Ne npe\n', '\n'.join(add) + '\n')
    assert 'Ewire' in text
    return text.replace('far end shorted ideally to the plane node under it', 'far end joined by the real connection below')


def deck_text(g, slot, trace, plane_nhinc, f, title, realend=False):
    if realend:
        return realend_text(deck_text(g, slot, trace, plane_nhinc, f, title), g)
    xs, ys, h = g['xs'], g['ys'], g['h']
    sx, sy = g['start_node']
    ex, ey = g['end_node']
    nw, nh, rw, rh = trace
    if g.get('contacts4'):
        d = h / 2
        refs = [f'+ nps{q} ({sx + a:.12g},{sy + b:.12g},{Z_PLANE:g})' for q, (a, b) in enumerate(((-d, -d), (d, -d), (d, d), (-d, d)))]
        refs += [f'+ npe{q} ({ex + a:.12g},{ey + b:.12g},{Z_PLANE:g})' for q, (a, b) in enumerate(((-d, -d), (d, -d), (d, d), (-d, d)))]
        text = deck_text({**g, 'contacts4': False}, slot, trace, plane_nhinc, f, title)
        text = text.replace(f'+ nps ({sx:.12g},{sy:.12g},{Z_PLANE:g})\n+ npe ({ex:.12g},{ey:.12g},{Z_PLANE:g})',
                            '* four plane nodes round each trace end, shorted together below\n' + '\n'.join(refs))
        text = text.replace('.equiv Ne npe\n', '.equiv nps0 nps1 nps2 nps3\n.equiv Ne npe0 npe1 npe2 npe3\n')
        text = text.replace('.external Ns nps loop', '.external Ns nps0 loop')
        assert 'nps3 (' in text and '.equiv Ne npe0' in text and 'nps0 loop' in text
        return text
    lines = [f'* {title}',
             '* FastHenry deck written by pour_decks.py -- OpenSim pour loop (ExtractionTests.AReturnThroughAPour_GoesRoundASlot)',
             '.units mm',
             f'.default sigma={SIGMA_MM:g}',
             '* return pour: uniform plane, corner nodes half a cell inside the 12 x 6 mm outline so the',
             f'* node cells tile the pour exactly; pitch {h:.9g} mm, {len(xs)} x {len(ys)} nodes',
             f'Gpour x1={xs[0]:.12g} y1={ys[0]:.12g} z1={Z_PLANE:g}',
             f'+ x2={xs[-1]:.12g} y2={ys[0]:.12g} z2={Z_PLANE:g}',
             f'+ x3={xs[-1]:.12g} y3={ys[-1]:.12g} z3={Z_PLANE:g}',
             f'+ thick={T_PLANE:g} seg1={len(xs) - 1} seg2={len(ys) - 1} sigma={SIGMA_MM:g}',
             f'+ nhinc={plane_nhinc} rh=2',
             f'+ nps ({sx:.12g},{sy:.12g},{Z_PLANE:g})',
             f'+ npe ({ex:.12g},{ey:.12g},{Z_PLANE:g})']
    if slot:
        ii, jj = slot_nodes(g)
        lines.insert(-2, f'* slot: remove nodes i={ii[0]}..{ii[-1]}, j={jj[0]}..{jj[-1]} (cell centres inside x 2.7..3.3, y -2..2);'
                         f' removed cells span x {xs[ii[0]] - h / 2:.6g}..{xs[ii[-1]] + h / 2:.6g}, y {ys[jj[0]] - h / 2:.6g}..{ys[jj[-1]] + h / 2:.6g}')
        lines.insert(-2, f'+ hole rect ({xs[ii[0]]:.12g},{ys[jj[0]]:.12g},{Z_PLANE:g},{xs[ii[-1]]:.12g},{ys[jj[-1]]:.12g},{Z_PLANE:g})')
    lines += ['* the trace (one segment: its current is the same along its whole length)',
              f'Ns x=0 y=0 z={Z_TRACE:g}',
              f'Ne x=6 y=0 z={Z_TRACE:g}',
              f'Etr Ns Ne w={W_TRACE:g} h={H_TRACE:g} nwinc={nw} nhinc={nh} rw={rw:g} rh={rh:g}',
              '* far end shorted ideally to the plane node under it; port from the trace start to the plane under it',
              '.equiv Ne npe',
              '.external Ns nps loop',
              f'.freq fmin={f:.12g} fmax={f:.12g} ndec=1',
              '.end', '']
    return '\n'.join(lines)


TRACES = {'T1': (7, 5, 2.0, 2.0), 'T2': (15, 9, 2.0, 2.0), 'T3': (21, 11, 1.6, 1.6)}
LEVELS = (6, 9, 20, 29, 41)


def configs():
    out = []
    # k with 2k+1 not a multiple of 3 (no cell centre on the slot's y edges) and h < 0.6 (k >= 6, so
    # the 0.6 mm slot holds at least one column of cell centres)
    for k in LEVELS:                             # plane levels, both plane nhinc, trace T1
        for pn in (1, 3):
            out.append(('conv', k, pn, 'T1'))
    for k in (6, 9, 20, 29):                     # second trace level
        out.append(('conv', k, 3, 'T2'))
    for k in (9, 20):                            # third trace level (graded 1.6)
        out.append(('conv', k, 3, 'T3'))
    for k in (6, 9, 20, 29):                     # plane skin effect: 5 strips through the copper
        out.append(('conv', k, 5, 'T2'))
    for k in (6, 9):                             # ... and 7, 9 strips (outermost 1.6, 0.76 um)
        for pn in (7, 9):
            out.append(('conv', k, pn, 'T2'))
    for pn in (1, 3):                            # OpenSim's own grid and strip counts
        out.append(('matched', None, pn, 'T1'))
    out.append(('exact', 10, 1, 'T1'))           # exact slot, 0.1 mm, four-node contacts
    # OpenSim's default real end connection instead of the ideal short (extra; the test as written)
    out.append(('matched_re', None, 3, 'T1'))
    out.append(('conv_re', 9, 3, 'T1'))
    out.append(('conv_re', 20, 1, 'T1'))
    return out


def write_all(root):
    manifest = []
    for kind, k, pn, tr in configs():
        realend = kind.endswith('_re')
        kind = kind[:-3] if realend else kind
        g = conv_grid(k) if kind == 'conv' else exact_grid(k) if kind == 'exact' else opensim_grid()
        for slot in (False, True):
            prefix = f'k{k:02d}' if kind == 'conv' else f'x{k:02d}' if kind == 'exact' else 'os03'
            name = prefix + f'_p{pn}_{tr}_{"slot" if slot else "solid"}' + ('_re' if realend else '')
            d = os.path.join(root, name)
            os.makedirs(d, exist_ok=True)
            decks = []
            for f in FREQS:
                title = (f'{"slotted" if slot else "solid"} pour, {kind} grid pitch {g["h"]:.6g} mm, plane nhinc {pn}, '
                         f'trace {TRACES[tr][0]}x{TRACES[tr][1]} r{TRACES[tr][2]:g}, f {f:g} Hz')
                p = os.path.join(d, f'deck_f{ftag(f)}.inp')
                with open(p, 'w') as fh:
                    fh.write(deck_text(g, slot, TRACES[tr], pn, f, title, realend))
                decks.append({'f': f, 'deck': p})
            # one deck for the eight decades 1e2..1e9 in a single process (setup paid once)
            p = os.path.join(d, 'deck_sweep.inp')
            text = deck_text(g, slot, TRACES[tr], pn, 1e2, title.rsplit(', f ', 1)[0] + ', f 1e2..1e9 Hz (8 decades)', realend)
            text = text.replace('.freq fmin=100 fmax=100 ndec=1', '.freq fmin=100 fmax=1e9 ndec=1')
            assert '.freq fmin=100 fmax=1e9 ndec=1' in text
            with open(p, 'w') as fh:
                fh.write(text)
            rec = {'sweep_deck': p, 'sweep_freqs': [1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9],'name': name, 'kind': kind, 'k': k, 'pitch_mm': g['h'], 'nodes': [len(g['xs']), len(g['ys'])],
                   'plane_nhinc': pn, 'trace': tr, 'trace_fils': TRACES[tr], 'slot': slot,
                   'start_node_mm': g['start_node'], 'end_node_mm': g['end_node'], 'decks': decks,
                   'end_connection': 'real (wire + bar, OpenSim default)' if realend else 'ideal (.equiv)'}
            if slot:
                ii, jj = slot_nodes(g)
                h = g['h']
                rec['slot_cells_mm'] = [g['xs'][ii[0]] - h / 2, g['xs'][ii[-1]] + h / 2, g['ys'][jj[0]] - h / 2, g['ys'][jj[-1]] + h / 2]
            if kind == 'matched':
                rec['opensim_start_ij'], rec['opensim_end_ij'] = g['start_ij'], g['end_ij']
            manifest.append(rec)
    with open(os.path.join(root, 'manifest.json'), 'w') as fh:
        json.dump(manifest, fh, indent=1)
    return manifest


if __name__ == '__main__':
    m = write_all(os.path.abspath(sys.argv[1]))
    g = opensim_grid()
    print(json.dumps({'configs': len(m), 'opensim_grid': {k: g[k] for k in ('h', 'nx', 'ny', 'start_node', 'end_node', 'start_ij', 'end_ij')},
                      'conv_pitches': {k: conv_grid(k)['h'] for k in LEVELS}}, indent=1))
