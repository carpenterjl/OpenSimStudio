"""Write geometry.json for the Sheen (1991) low-pass filter, line-fed patch and branch-line coupler.
Every printed value carries its source (thesis page = PDF page in this scan; figure or text). Derived values are
computed here and carry the arithmetic.  Usage: python -I make_geometry.py <out.json>"""
import sys, json


def r(v, n=4):
    return round(v, n)


# ---------------- low-pass filter (Sec. 3.4, thesis pp.108-116 = PDF pp.108-116)
dx, dy, dz = 0.3948, 0.4232, 0.265          # p.112 text
L_fig, W_patch, w_port, off_edge, h = 20.32, 2.54, 2.413, 5.65, 0.794   # Fig. 3.25 p.108
stub_tl, dl_tl, spacing_tl = 6.857, 0.378, 6.607                           # Fig. 3.26 p.109, text p.108/109
lpf = {
    "source": "Sec. 3.4 'Low Pass Filter', thesis pp.108-116 (PDF pp.108-116); page images pages/p108..p116_pdf*_render_w5088.png",
    "design_reference": "designed per criteria of ref.[25] D'Inzeo, Giannini, Sorrentino, 'Novel microwave integrated low-pass filters', Electronics Letters 15(9) 258-260, Apr. 1979 (p.108 text; bibliography p.233)",
    "layout": "two collinear-offset feed lines (ports 1 and 2, along y) attached to opposite long sides of a long rectangular patch (along x); each port line is set back 5.65 mm from the nearer patch end (Fig. 3.25)",
    "substrate": {"er": 2.2, "er_src": "Fig. 3.25 p.108 ('e = 2.2')", "h_mm": h, "h_src": "Fig. 3.25 p.108 (0.794 mm)",
                  "loss_tangent": None, "loss_note": "not stated; FD-TD excludes dielectric and conductor loss (p.136 text)"},
    "conductor": {"model": "perfect conductor, tangential E set to zero on strips (p.76 Sec.3.1.2); thickness not given (zero-thickness sheet in FD-TD)",
                  "metal_of_measured_board": "not stated", "thickness_of_measured_board": "not stated"},
    "dimensions_mm_drawing": {
        "patch_length_x": L_fig, "patch_length_src": "Fig. 3.25 p.108 ('20.32 mm', arrow parallel to the patch, x)",
        "patch_width_y": W_patch, "patch_width_src": "Fig. 3.25 p.108 ('2.54 mm')",
        "port_line_width": w_port, "port_line_width_src": "Fig. 3.25 p.108 ('2.413 mm')",
        "patch_end_to_near_edge_of_port_line": off_edge, "offset_src": "Fig. 3.25 p.108 ('5.65 mm', both ports)",
        "patch_end_to_port_centreline": r(off_edge + w_port / 2), "arith_centre": "5.65 + 2.413/2 = %.4f (matches TL-model stub 6.857, Fig. 3.26)" % (off_edge + w_port / 2),
        "port_centre_spacing_x": r(L_fig - 2 * (off_edge + w_port / 2)), "arith_spacing": "20.32 - 2*6.8565 = %.4f (TL model: 6.607 mm, Fig. 3.26)" % (L_fig - 2 * (off_edge + w_port / 2)),
        "inner_edge_gap_between_port_lines_x": r(L_fig - 2 * off_edge - 2 * w_port), "arith_gap": "20.32 - 2*5.65 - 2*2.413 = %.4f" % (L_fig - 2 * off_edge - 2 * w_port),
        "TL_model_check": "stub + spacing + stub = 6.857 + 6.607 + 6.857 = %.3f mm = patch length 20.32 (Fig. 3.26 p.109)" % (2 * stub_tl + spacing_tl),
        "port_line_length": "not stated (Fig. 3.25 draws lines to the board edge; board size not dimensioned)",
        "board_size": "not stated",
    },
    "transmission_line_model_p108_109": {"Z0_feed_ohm": 50.77, "Z0_filter_sections_ohm": 49.05, "eps_eff": 1.895, "open_end_dl_mm": dl_tl,
                                         "stub_length_mm": "6.857 + 0.378", "junction_spacing_mm": spacing_tl,
                                         "src": "text p.108-109 and Fig. 3.26 p.109"},
    "planar_model_p109": {"eps_eff": 1.895, "l_mm": 4.438, "b_mm": 21.078, "W1_W2_mm": 4.294, "p1_mm": 7.236, "p2_mm": 13.843,
                          "note": "effective (fringing-extended) planar-circuit dimensions, eqs.(A.3),(A.5),(A.6); p2-p1 = %.3f mm = port spacing" % (13.843 - 7.236),
                          "src": "text p.109"},
    "fdtd_model_p112": {
        "dx_mm": dx, "dy_mm": dy, "dz_mm": dz, "mesh_cells_xyz": [70, 45, 16],
        "domain_mm_xyz": [r(70 * dx), r(45 * dy), r(16 * dz)], "arith_domain": "70*0.3948=%.3f, 45*0.4232=%.3f, 16*0.265=%.3f" % (70 * dx, 45 * dy, 16 * dz),
        "patch_cells": [50, 6], "patch_mm": [r(50 * dx), r(6 * dy)], "arith_patch": "50*0.3948=%.3f mm, 6*0.4232=%.4f mm" % (50 * dx, 6 * dy),
        "port_width_cells": 6, "port_width_mm": r(6 * dx), "arith_port": "6*0.3948=%.4f mm (drawing 2.413)" % (6 * dx),
        "substrate_cells": 3, "substrate_mm": r(3 * dz), "arith_sub": "0.794/0.265 = %.3f -> 3 cells = %.3f mm (thickness not stated in cells for this circuit; corner p.94 uses 3 dz)" % (h / dz, 3 * dz),
        "source_plane_to_patch_edge_cells": 30, "source_plane_to_patch_edge_mm": r(30 * dy), "arith_src": "30*0.4232=%.3f" % (30 * dy),
        "port_reference_planes_from_patch_edges_cells": 10, "ref_plane_mm": r(10 * dy), "arith_ref": "10*0.4232=%.3f" % (10 * dy),
        "port_offset_in_cells_if_drawing_held": r(off_edge / dx, 2), "arith_off": "5.65/0.3948 = %.2f cells (thesis: port locations/widths modelled 'with some slight error smaller than the step size', p.109/112)" % (off_edge / dx),
        "source_inside_domain_cells": 4, "dt_ps": 0.6, "gaussian_T_ps": 15, "gaussian_t0_ps": 45, "source_off_and_line_connected_at_steps": 180,
        "n_steps": 2000, "boundaries": "first-order Mur-type ABC (2.51) on sides; top first- or second-order (2.62) (p.76; not stated which for this circuit)",
        "INCONSISTENCIES": [
            "50*dx = %.2f mm but Fig. 3.25 gives 20.32 mm (would need dx = 20.32/50 = %.4f mm); p.109 says dx, dy were chosen to match the patch exactly" % (50 * dx, 20.32 / 50),
            "45 cells in y cannot hold 4 (source) + 30 (source->patch) + 6 (patch) + 10 (patch->port-2 reference plane) = 50 cells",
        ],
        "src": "text p.112 (crop crops/p112_steps_hi.png)"},
    "measurement": {"who": "circuits and measurements provided by Mohamed Abouzahra (Acknowledgements p.5)",
                    "instrument": "a network analyzer, model not stated; analyzer and connectors rated only to 18 GHz (p.136)",
                    "connectors": "microstrip-to-coaxial transitions, type not stated; NOT compensated (no de-embedding) in the measurement (p.136)",
                    "calibration": "not stated (beyond the transitions being uncompensated)",
                    "reference_planes": "not stated for the measurement; FD-TD reference planes 10 dy = 4.232 mm from the patch edges (p.112)",
                    "frequency_range": "measured dash-dot curves begin at about 2 GHz and are plotted to 20 GHz (Figs. 3.27-3.30, 3.33, 3.34; first dash at 1.95-1.98 GHz digitised); data above 18 GHz beyond the analyzer rating (p.136)"},
    "figures": {"Fig. 3.25": "configuration, p.108", "Fig. 3.26": "TL model, p.109", "Fig. 3.27/3.28": "TL |S11|/|S21| vs measured, p.110",
                "Fig. 3.29/3.30": "planar |S11|/|S21| vs measured, p.111", "Fig. 3.31": "Ez distributions, p.114", "Fig. 3.32": "transients, p.115",
                "Fig. 3.33": "|S11| FD-TD (solid) vs Meas (dash-dot), p.115", "Fig. 3.34": "|S21| FD-TD (solid) vs Meas (dash-dot), p.116"},
}

# ---------------- line-fed patch (Sec. 4.3, pp.158-163)
d, dzp = 1.11667, 0.530
patch = {
    "source": "Sec. 4.3 'Line Fed Rectangular Patch Antenna', thesis pp.158-163 (PDF pp.158-163)",
    "substrate": {"er": 2.55, "h_mm": 1.59, "src": "text p.158 and Fig. 4.11 p.159", "loss_tangent": None, "loss_note": "not stated"},
    "conductor": "perfectly conducting, tangential E = 0 (p.144 Sec.4.1.2); thickness not stated",
    "dimensions_mm": {"patch_L": 40.2, "patch_W": 40.2, "src_patch": "text p.158, Fig. 4.11 p.159 (40.2 mm x 40.2 mm)",
                      "feed_width": 4.48, "src_feed": "text p.158 ('nominally 50 ohm', 'width of 4.48 mm'), Fig. 4.11",
                      "feed_position": "centre of one edge (p.158); Fig. 4.11: 20.1 mm from the patch corner to the feed centreline (20.1 = 40.2/2)",
                      "feed_offset_from_centre_mm": 0.0, "inset": "none (edge feed)",
                      "feed_line_length": "not stated", "ground_plane_size": "not stated"},
    "fdtd_model_p158_159": {"cells_xyz": [59, 66, 27], "dx_mm": d, "dy_mm": d, "dz_mm": dzp, "variable_grid": "3x these spacings surrounding the antenna (3.35 mm x/y, 1.59 mm z)",
                            "patch_cells": [36, 36], "patch_mm": [r(36 * d, 3), r(36 * d, 3)], "arith_patch": "36*1.11667 = %.3f mm" % (36 * d),
                            "substrate_cells": 3, "substrate_mm": r(3 * dzp, 3), "arith_sub": "3*0.530 = %.3f mm" % (3 * dzp),
                            "line_width_cells": 4, "line_width_mm": r(4 * d, 4), "arith_line": "4*1.11667 = %.4f mm (nominal 4.48)" % (4 * d),
                            "S11_reference_plane_mm_from_patch_edge": 16.75, "ref_cells": r(16.75 / d, 3), "arith_ref": "16.75/1.11667 = %.3f cells" % (16.75 / d),
                            "eps_eff_for_k_in_eq4.5": 2.14, "dt_ps": 1.4, "gaussian_T_ps": 60, "gaussian_t0_ps": 180,
                            "source_off_and_line_connected_at_steps": 300, "n_steps": 6000,
                            "Zin_reference": "input impedance referenced to the front (feed) edge of the patch (pp.159-160)",
                            "boundaries": "first-order ABC on the 4 side walls, second-order ABC on top, PEC ground (p.144)",
                            "src": "text pp.158-159 (crop crops/p158_text.png)"},
    "measurement": {"who": "Deshpande and Bailey, ref.[22] 'Input impedance of microstrip antennas', IEEE Trans. AP-30(4) 645-650, July 1982 (bibliography p.233); Fig. 4.15 legend 'Measured by Deshpande and Bailey 82'",
                    "instrument_connectors_calibration": "not stated in the thesis",
                    "reference_plane": "plotted relative to the patch front edge together with the FD-TD (Fig. 4.14); Fig. 4.15 Smith chart: same values (cross-check: R, X from Fig. 4.15 with Z0 = 50 ohm agree with Fig. 4.14 markers within a few ohm)",
                    "frequencies_GHz": [2.10, 2.15, 2.20, 2.25, 2.30, 2.35, 2.40],
                    "what_is_plotted": "Zin = R + jX (ohm) at 7 frequencies (markers); no |S11| in dB is plotted; Smith chart Fig. 4.15 (normalisation not printed; consistent with 50 ohm)",
                    "thesis_statements": "fundamental resonance approximately 2.275 GHz (p.158); FD-TD shows a small frequency shift of about 0.4 % (p.160)"},
    "figures": {"Fig. 4.11": "configuration p.159", "Fig. 4.12": "Ez distributions p.161", "Fig. 4.13": "transients p.162",
                "Fig. 4.14": "Zin R,X: FD-TD (solid R, dashed X) and measured (* R, o X), p.162",
                "Fig. 4.15": "Smith chart: x FD-TD, o measured (Deshpande & Bailey), * MoM (Deshpande & Bailey), p.163"},
}

# ---------------- branch-line coupler (Sec. 3.5, pp.117-121) - present, not digitised
blc = {
    "source": "Sec. 3.5 'Branch Line Coupler', thesis pp.117-121",
    "substrate": {"er": 2.2, "h_mm": 0.794, "src": "Fig. 3.35 p.118"},
    "dimensions_mm": {"centre_to_centre_both_directions": 9.75, "wide_strip_width": 3.96, "port_line_width": 2.413, "src": "Fig. 3.35 p.118, text p.117"},
    "fdtd_model": {"dx_mm": 0.390, "dy_mm": 0.390, "dz_mm": 0.265, "cells_xyz": [54, 70, 16], "centre_to_centre_cells": 24,
                   "arith": "24*0.390 = %.2f mm" % (24 * 0.390), "wide_strips_cells": 10, "wide_strips_mm": r(10 * 0.390, 3),
                   "port_width_cells": 6, "port_width_mm": r(6 * 0.390, 3), "source_to_coupler_cells": 30, "ref_planes_cells_from_edges": 10,
                   "dt_ps": 0.6, "gaussian_T_ps": 15, "gaussian_t0_ps": 45, "n_steps": 2000, "src": "text p.117"},
    "measured_figures": "Fig. 3.38 p.120 |S11|,|S21|,|S31|,|S41| FD-TD solid vs Meas dash-dot 0-10 GHz; Fig. 3.39 p.121 phase difference S31-S41 (not digitised)",
}

out = {"thesis": "D. M. Sheen, 'Numerical modeling of microstrip circuits and antennas', PhD thesis, MIT, 1991 (PDF 238 pages; printed page number = PDF page index in this scan, verified on pp.7, 73, 108, 112, 159, 162, 163, 233)",
       "units": "mm unless stated", "low_pass_filter": lpf, "line_fed_patch": patch, "branch_line_coupler": blc}
json.dump(out, open(sys.argv[1], "w"), indent=1)
print("ok")
