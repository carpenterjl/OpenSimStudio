"""Write calibration configs for Figs 3.27-3.30 from approximate frame boxes (full-res px of the 5088-px renders).
Usage: python -I mkcfg.py <outdir>"""
import sys, json, os
base = "pages/"
figs = {
    "fig3_27": dict(page=110, box=[1766, 1160, 4026, 2855], y0=1445, y40=2575, q="S11_dB"),
    "fig3_28": dict(page=110, box=[1766, 3827, 4036, 5522], y0=4112, y40=5242, q="S21_dB"),
    "fig3_29": dict(page=111, box=[1690, 1033, 3954, 2728], y0=1318, y40=2448, q="S11_dB"),
    "fig3_30": dict(page=111, box=[1690, 3690, 3959, 5384], y0=3975, y40=5104, q="S21_dB"),
}
for name, f in figs.items():
    x0, y0, x1, y1 = f["box"]
    sp = (x1 - x0) / 20.0
    cfg = {"figure": name, "image": base + "p%d_pdf%d_render_w5088.png" % (f["page"], f["page"]),
           "thesis_page": f["page"], "pdf_page": f["page"], "quantity": f["q"], "box": f["box"],
           "xticks": {"sides": ["bottom", "top"], "first": [1, round(x0 + sp)], "last": [19, round(x0 + 19 * sp)], "step": 1, "band": [8, 17]},
           "yticks": {"sides": ["left", "right"], "first": [0, f["y0"]], "last": [-40, f["y40"]], "step": 10, "band": [8, 17]},
           "view_grid": {"x": [0, 20, 0.5], "y": [-50, 10, 2]}, "x_range": [0, 20], "y_range": [-50, 10],
           "tick_blank_len": 54, "masks": [], "track_grid": True, "curves": []}
    json.dump(cfg, open(os.path.join(sys.argv[1], "cfg_%s.json" % name), "w"), indent=1)
    print(name)
