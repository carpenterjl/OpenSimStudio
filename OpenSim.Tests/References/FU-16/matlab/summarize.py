"""Merge the per-case CSVs into all_results.csv and write convergence_summary.csv/json.

Usage: python -I summarize.py <dir>
For every (case, parameter, quantity) with numeric mesh levels: the values per
level, the change between the finest two levels (relative), the ratio of the last
two successive changes, and (when that ratio > 1.2 and the changes keep their
sign) a Richardson estimate v_f + d_f/(ratio - 1).
"""
import csv
import glob
import json
import os
import sys

d = sys.argv[1]
files = sorted(glob.glob(os.path.join(d, "case*.csv")))
files += sorted(glob.glob(os.path.join(d, "meshA_no_window_box", "case*.csv")))
rows = []
for fn in files:
    if os.path.basename(fn).startswith("case3_scan"):
        continue
    prefix = "meshA:" if "meshA_no_window_box" in fn else ""
    with open(fn, newline="") as fh:
        for r in csv.DictReader(fh):
            r["case"] = prefix + r["case"]
            r["source_file"] = os.path.relpath(fn, d)
            rows.append(r)

with open(os.path.join(d, "all_results.csv"), "w", newline="") as fh:
    w = csv.DictWriter(fh, fieldnames=["case", "parameter", "quantity", "value", "mesh_level", "nodes", "source_file"])
    w.writeheader()
    w.writerows(rows)

groups = {}
for r in rows:
    try:
        lev = int(r["mesh_level"])
    except ValueError:
        continue
    key = (r["case"], r["parameter"], r["quantity"])
    groups.setdefault(key, {})[lev] = (float(r["value"]), int(r["nodes"]))

summary = []
for key, levs in sorted(groups.items()):
    ls = sorted(levs)
    vals = [levs[l][0] for l in ls]
    item = {"case": key[0], "parameter": key[1], "quantity": key[2],
            "levels": ls, "values": vals, "nodes": [levs[l][1] for l in ls]}
    if len(ls) >= 2 and vals[-1] != 0:
        item["finest_rel_change"] = (vals[-1] - vals[-2]) / abs(vals[-1])
    if len(ls) >= 3:
        d1, d2 = vals[-2] - vals[-3], vals[-1] - vals[-2]
        if d2 != 0:
            ratio = d1 / d2
            item["ratio_last_changes"] = ratio
            if ratio > 1.2:
                item["richardson"] = vals[-1] + d2 / (ratio - 1)
    summary.append(item)

with open(os.path.join(d, "convergence_summary.json"), "w") as fh:
    json.dump(summary, fh, indent=1)
with open(os.path.join(d, "convergence_summary.csv"), "w", newline="") as fh:
    w = csv.writer(fh)
    w.writerow(["case", "parameter", "quantity", "levels", "finest_value", "finest_nodes",
                "finest_rel_change", "ratio_last_changes", "richardson_estimate"])
    for s in summary:
        w.writerow([s["case"], s["parameter"], s["quantity"], " ".join(map(str, s["levels"])),
                    "%.12g" % s["values"][-1], s["nodes"][-1],
                    "%.3e" % s["finest_rel_change"] if "finest_rel_change" in s else "",
                    "%.3f" % s["ratio_last_changes"] if "ratio_last_changes" in s else "",
                    "%.12g" % s["richardson"] if "richardson" in s else ""])
print("rows", len(rows), "groups", len(summary))
