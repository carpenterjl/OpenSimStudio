"""Write the Fig. 6 digitiser config. Seeds come from ink-ridge peak lists (rows every 30 px) read by eye,
chained by continuity from each curve's labelled end. Only curves whose end label is legible are included.
Usage: python -I make_cfg_fig6.py <base cfg_fig6.json> <out.json>
"""
import sys, json

base = json.load(open(sys.argv[1]))
C = {
    "sf2400": [(985, 1601), (960, 1611), (930, 1622), (900, 1632), (860, 1645), (820, 1660), (790, 1673), (765, 1688)],
    "sf6": [(1083, 1345), (1056.3, 1380), (1031.5, 1410), (1006.1, 1440), (979.6, 1470), (952.9, 1500), (924.9, 1530), (894.4, 1560)],
    "sf4": [(985, 1402), (981.9, 1410), (963.8, 1440), (942.7, 1470), (922.7, 1500), (900.3, 1530), (879.4, 1560)],
    "sf5": [(966, 1467), (964.4, 1470), (938.6, 1500), (909.0, 1530), (879.4, 1560)],
    "sf8": [(1083, 1405), (1045.0, 1440), (1010.8, 1470), (976.4, 1500), (945.6, 1530), (909.6, 1560)],
    "sf12": [(1083.1, 1440), (1047.9, 1470), (1011.1, 1500), (972.6, 1530), (935.2, 1560)],
    "sf20": [(1083, 1462), (1075.0, 1470), (1032.8, 1500), (990.5, 1530), (942.8, 1560)],
}
labels = {
    "sf2400": "Shape factor 2400 (Haefner specimen 5)",
    "sf6": "Shape factor 6 (label at curve end)",
    "sf4": "Shape factor 4 (label at curve end)",
    "sf5": "Shape factor 5 (label beside curve; passes the p.445 worked-example point)",
    "sf8": "Shape factor 8 (label at curve end)",
    "sf12": "Shape factor 12 (label at curve end)",
    "sf20": "Shape factor 20 (label at curve end)",
}
curves = []
for k, s in C.items():
    s = sorted(s, key=lambda q: q[1])          # top to bottom
    curves.append({"name": k, "label": labels[k], "W": 3.0, "seeds": [[float(a), float(b)] for a, b in s]})
base["curves"] = curves
json.dump(base, open(sys.argv[2], "w"), indent=1)
print("curves", len(curves))
