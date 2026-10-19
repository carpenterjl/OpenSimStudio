"""mk.py cal.json outprefix half_px f1,v1 f2,v2 ...
For each guess (f, v): crops a window of +-half_px around it, labels ink components (scipy.ndimage),
prints each component's bbox centre and ink centroid in data units (MY calibration), and saves a
zoomed crop with a cross at every component's bbox centre."""
import sys, json
import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage
Image.MAX_IMAGE_PIXELS = None
cal = json.load(open(sys.argv[1]))
a = np.array(cal['x_coef']); b = np.array(cal['y_coef'])
M = np.array([[a[1], a[2]], [b[1], b[2]]]); Minv = np.linalg.inv(M)
img = Image.open(cal['page']).convert('L')
im = np.asarray(img, dtype=np.float32)
to_px = lambda f, v: Minv @ np.array([f - a[0], v - b[0]])
to_d = lambda px, py: (a[0] + a[1] * px + a[2] * py, b[0] + b[1] * px + b[2] * py)
pref = sys.argv[2]; h = int(sys.argv[3])
for k, g in enumerate(sys.argv[4:]):
    f, v = map(float, g.split(','))
    cx, cy = to_px(f, v)
    X0, Y0 = int(cx - h), int(cy - h)
    sub = im[Y0:Y0 + 2 * h, X0:X0 + 2 * h] < 128
    lab, n = ndimage.label(sub)
    print('guess f=%.3f v=%.1f' % (f, v))
    crop = img.crop((X0, Y0, X0 + 2 * h, Y0 + 2 * h)).convert('RGB').resize((8 * h, 8 * h), Image.NEAREST)
    d = ImageDraw.Draw(crop)
    for i in range(1, n + 1):
        ys, xs = np.where(lab == i)
        if len(xs) < 30:
            continue
        bx = (xs.min() + xs.max()) / 2 + X0; by = (ys.min() + ys.max()) / 2 + Y0
        mx = xs.mean() + X0; my = ys.mean() + Y0
        fb, vb = to_d(bx, by); fm, vm = to_d(mx, my)
        print('  comp %d npx=%d bbox w=%d h=%d  bbox-centre f=%.4f v=%.2f   ink-centroid f=%.4f v=%.2f' % (
            i, len(xs), xs.max() - xs.min() + 1, ys.max() - ys.min() + 1, fb, vb, fm, vm))
        x = (bx - X0) * 4; y = (by - Y0) * 4
        d.line([(x - 10, y), (x + 10, y)], fill='red', width=2); d.line([(x, y - 10), (x, y + 10)], fill='red', width=2)
        d.text((x + 6, y + 6), str(i), fill='blue')
    crop.save('%s_%d.png' % (pref, k))
