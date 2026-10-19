"""mk2.py cal.json mode half f1,v1 ...
mode 'ring': centre = centroid of the enclosed white hole of the 'o' marker.
mode 'star': centre = midpoint of the topmost and bottommost ink of the star, taken over the columns
            within +-6 px of the column where the ink reaches highest; f from those columns' mean.
All in MY calibration."""
import sys, json
import numpy as np
from PIL import Image
from scipy import ndimage
Image.MAX_IMAGE_PIXELS = None
cal = json.load(open(sys.argv[1]))
a = np.array(cal['x_coef']); b = np.array(cal['y_coef'])
M = np.array([[a[1], a[2]], [b[1], b[2]]]); Minv = np.linalg.inv(M)
im = np.asarray(Image.open(cal['page']).convert('L'), dtype=np.float32)
to_px = lambda f, v: Minv @ np.array([f - a[0], v - b[0]])
to_d = lambda px, py: (a[0] + a[1] * px + a[2] * py, b[0] + b[1] * px + b[2] * py)
mode = sys.argv[2]; h = int(sys.argv[3])
for g in sys.argv[4:]:
    f, v = map(float, g.split(','))
    cx, cy = to_px(f, v)
    X0, Y0 = int(cx - h), int(cy - h)
    sub = im[Y0:Y0 + 2 * h, X0:X0 + 2 * h] < 128
    if mode == 'ring':
        lab, n = ndimage.label(~sub)
        best = None
        for i in range(1, n + 1):
            ys, xs = np.where(lab == i)
            if xs.min() == 0 or ys.min() == 0 or xs.max() == 2 * h - 1 or ys.max() == 2 * h - 1:
                continue
            if len(xs) < 40:
                continue
            if best is None or len(xs) > best[0]:
                best = (len(xs), xs.mean() + X0, ys.mean() + Y0, xs.max() - xs.min() + 1, ys.max() - ys.min() + 1)
        if best is None:
            print('f=%.3f v=%.1f: no hole' % (f, v)); continue
        fd, vd = to_d(best[1], best[2])
        print('ring guess %.3f,%.1f -> hole npx=%d (%dx%d) centre f=%.4f v=%.2f' % (f, v, best[0], best[3], best[4], fd, vd))
    else:
        cols = np.where(sub.any(axis=0))[0]
        tops = np.array([np.where(sub[:, c])[0].min() if sub[:, c].any() else 10 ** 6 for c in range(2 * h)])
        bots = np.array([np.where(sub[:, c])[0].max() if sub[:, c].any() else -1 for c in range(2 * h)])
        # restrict to central part of window
        mid = slice(h - 25, h + 25)
        c_top = h - 25 + int(np.argmin(tops[mid]))
        c_bot = h - 25 + int(np.argmax(bots[mid]))
        sel = [c for c in range(c_top - 6, c_top + 7)]
        ytop = tops[c_top]; ybot = bots[c_bot]
        pxm = (c_top + c_bot) / 2 + X0
        fd, vd = to_d(pxm, (ytop + ybot) / 2 + Y0)
        ft, vt = to_d(c_top + X0, ytop + Y0); fb, vb = to_d(c_bot + X0, ybot + Y0)
        print('star guess %.3f,%.1f -> top v=%.2f (f %.4f) bottom v=%.2f (f %.4f) height %d px; centre f=%.4f v=%.2f' % (
            f, v, vt, ft, vb, fb, ybot - ytop + 1, fd, vd))
