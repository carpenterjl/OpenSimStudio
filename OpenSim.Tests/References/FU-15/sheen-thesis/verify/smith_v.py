"""Own Smith-chart calibration for Fig. 4.15 (p.163) and measured 'o' marker read-out.
smith_v.py page.png cx0 cy0 r0 out.json  x1,y1 x2,y2 ... (approx page px of the 'o' markers, in frequency order)"""
import sys, json
import numpy as np
from PIL import Image
from scipy import ndimage, optimize
Image.MAX_IMAGE_PIXELS = None
im = np.asarray(Image.open(sys.argv[1]).convert('L'), dtype=np.float32)
ink = im < 128
cx0, cy0, r0 = map(float, sys.argv[2:5])

# 1. outer circle: outermost ink run within r0*(0.93..1.07) along rays
pts = []
for ang in np.arange(0, 360, 0.5):
    t = np.deg2rad(ang)
    rs = np.arange(r0 * 0.93, r0 * 1.07, 0.5)
    xs = cx0 + rs * np.cos(t); ys = cy0 - rs * np.sin(t)
    v = ink[ys.round().astype(int), xs.round().astype(int)]
    if not v.any():
        continue
    idx = np.where(v)[0]
    # outermost run
    last = idx[-1]; first = last
    while first - 1 in set(idx):
        first -= 1
    rc = (rs[first] + rs[last]) / 2
    pts.append((cx0 + rc * np.cos(t), cy0 - rc * np.sin(t), ang, rs[last] - rs[first]))
P = np.array([(p[0], p[1]) for p in pts])


def resid(p, P):
    return np.hypot(P[:, 0] - p[0], P[:, 1] - p[1]) - p[2]


p = np.array([cx0, cy0, r0]); keep = np.ones(len(P), bool)
for _ in range(6):
    p = optimize.least_squares(resid, p, args=(P[keep],)).x
    r = resid(p, P)
    sd = 1.4826 * np.median(np.abs(r[keep]))
    keep = np.abs(r) < max(3 * sd, 1.0)
cx, cy, R = p
r = resid(p, P)
print('outer circle: cx=%.2f cy=%.2f R=%.2f  rms=%.2f px  used %d/%d rays' % (cx, cy, R, np.sqrt(np.mean(r[keep] ** 2)), keep.sum(), len(P)))

# 2. real axis: centre of the horizontal line in columns between -0.95R..-0.6R and 0.6R..0.95R
ax = []
for x in list(np.arange(cx - 0.95 * R, cx - 0.62 * R, 2)) + list(np.arange(cx + 0.62 * R, cx + 0.95 * R, 2)):
    col = ink[int(cy - 30):int(cy + 30), int(x)]
    if col.any():
        ys = np.where(col)[0]
        ax.append((x, cy - 30 + ys.mean()))
ax = np.array(ax)
m, c = np.polyfit(ax[:, 0], ax[:, 1], 1)
tilt = np.degrees(np.arctan(m))
yc_axis = m * cx + c
print('real axis: slope %.5f (tilt %.3f deg), axis y at cx = %.2f (cy=%.2f, offset %.2f px)' % (m, tilt, yc_axis, cy, yc_axis - cy))
th = np.arctan(m)


def gamma(x, y):
    z = ((x - cx) - 1j * (y - cy)) / R
    return z * np.exp(1j * th)  # undo tilt (image y down -> rotate by +th)


# 3. r-circle crossings of the real axis
xs = np.arange(cx - R * 0.99, cx + R * 0.99, 0.5)
ys = m * xs + c
# sample a 2D band: ink on vertical-ish circle strokes crossing the axis; use rows 40 px above axis to avoid the axis itself
for off in (-40, 40):
    v = ink[(ys + off).round().astype(int), xs.round().astype(int)]
    d = np.diff(np.concatenate([[0], v.astype(int), [0]]))
    s = np.where(d == 1)[0]; e = np.where(d == -1)[0]
    print('row offset %+d px crossings (Gamma re):' % off, ['%.4f' % gamma((xs[a] + xs[b - 1]) / 2, ys[a] + off).real for a, b in zip(s, e)])

# 4. markers: hole centroid of the 'o'
out = {'cx': cx, 'cy': cy, 'R': R, 'tilt_deg': tilt, 'markers': []}
for g in sys.argv[6:]:
    gx, gy = map(float, g.split(','))
    h = 45
    X0, Y0 = int(gx - h), int(gy - h)
    sub = ink[Y0:Y0 + 2 * h, X0:X0 + 2 * h]
    lab, n = ndimage.label(~sub)
    best = None
    for i in range(1, n + 1):
        yy, xx = np.where(lab == i)
        if xx.min() == 0 or yy.min() == 0 or xx.max() == 2 * h - 1 or yy.max() == 2 * h - 1 or len(xx) < 30:
            continue
        if best is None or len(xx) > best[0]:
            best = (len(xx), xx.mean() + X0, yy.mean() + Y0, xx.max() - xx.min() + 1, yy.max() - yy.min() + 1)
    G = gamma(best[1], best[2])
    out['markers'].append({'guess': [gx, gy], 'px': best[1], 'py': best[2], 'hole_px': best[0], 'hole_wh': [int(best[3]), int(best[4])],
                           'G_re': G.real, 'G_im': G.imag})
    print('marker at (%.1f,%.1f): hole %d px %dx%d  Gamma = %.4f %+.4fj  |G| = %.4f' % (best[1], best[2], best[0], best[3], best[4], G.real, G.imag, abs(G)))
json.dump(out, open(sys.argv[5], 'w'), indent=1)
