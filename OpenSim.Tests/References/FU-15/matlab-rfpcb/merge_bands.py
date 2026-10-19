"""Merge band CSVs written by run_sweep.m into one CSV (sorted, duplicate frequencies dropped:
the later band wins at a shared edge only if the earlier band does not have it).
Usage: python -I merge_bands.py out.csv in1.csv in2.csv ..."""
import csv, sys
rows, seen, header = [], set(), None
for path in sys.argv[2:]:
    with open(path, newline='') as fh:
        rd = csv.reader(fh)
        h = next(rd)
        header = header or h
        for r in rd:
            key = round(float(r[0]), 6)
            if key in seen:
                continue
            seen.add(key)
            rows.append(r)
rows.sort(key=lambda r: float(r[0]))
with open(sys.argv[1], 'w', newline='') as fh:
    w = csv.writer(fh)
    w.writerow(header)
    w.writerows(rows)
print(sys.argv[1], len(rows), 'rows')
