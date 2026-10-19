"""Write the Fig. 8 digitiser config (seeds read by eye from ink-map zooms and column peak lists).
Usage: python -I make_cfg_fig8.py <base cfg_fig8.json> <out.json>
"""
import sys, json

base = json.load(open(sys.argv[1]))
trunk = [(415, 1557), (420, 1556.6), (440, 1553.3), (460, 1550.5), (480, 1545.0), (500, 1538.4), (520, 1530.7), (540, 1521.5),
         (560, 1509.7), (580, 1494.4), (600, 1478.7), (608, 1471)]
s1 = [(612, 1458), (650, 1421.9), (700, 1372.9), (750, 1327.4), (800, 1282), (850, 1236), (900, 1186.3), (950, 1139.5), (1000, 1091.3),
      (1050, 1042.8), (1075, 1018)]
s2 = [(612, 1466), (650, 1440), (700, 1396), (750, 1351.9), (800, 1305), (850, 1258), (900, 1209.4), (950, 1162), (1000, 1115),
      (1050, 1067.3), (1100, 1019.5)]
s6 = [(620, 1468), (650, 1460), (700, 1440), (750, 1413), (800, 1386), (850, 1360), (900, 1330.9), (950, 1292.3), (1000, 1255.7),
      (1050, 1216.0), (1100, 1175.3), (1150, 1134.4), (1185, 1100)]
s12 = [(630, 1478), (700, 1452), (750, 1435), (800, 1418.4), (850, 1398.2), (900, 1375.5), (950, 1354.0), (1000, 1330.5),
       (1050, 1304.8), (1100, 1277.4), (1150, 1247.3), (1200, 1215.4), (1250, 1181.8), (1300, 1146.5), (1350, 1109.4), (1388, 1076)]
s18 = [(630, 1484), (700, 1460), (750, 1442), (800, 1424.4), (850, 1406.5), (900, 1388.0), (950, 1370.7), (1000, 1354.5),
       (1050, 1337.4), (1100, 1319.8), (1150, 1299.3), (1200, 1275.7), (1250, 1250), (1300, 1219), (1350, 1192.7), (1400, 1158),
       (1450, 1124.6), (1480, 1102.7), (1490, 1096)]
d20 = [(1325, 1232), (1400, 1183), (1450, 1152.7), (1480, 1135.3), (1490, 1129)]
d1 = [(900, 1172), (1000, 1076), (1085, 995)]
d12 = [(1200, 1203), (1300, 1137), (1385, 1068)]

curves = [
    {"name": "trunk", "label": "common low-p trunk of the test curves (p < ~3)", "W": 5, "seeds": [list(map(float, q)) for q in trunk]},
    {"name": "sf1_test", "label": "Shape factor 1, test data (solid)", "W": 3.5, "seeds": [list(map(float, q)) for q in s1]},
    {"name": "sf2_test", "label": "Shape factor 2, test data (solid)", "W": 3.5, "seeds": [list(map(float, q)) for q in s2]},
    {"name": "sf6_test", "label": "Shape factor 6, test data (solid)", "W": 3.5, "seeds": [list(map(float, q)) for q in s6]},
    {"name": "sf12_test", "label": "Shape factor 12, test data (solid)", "W": 3.0, "seeds": [list(map(float, q)) for q in s12]},
    {"name": "sf18_test", "label": "Shape factor 18, test data (solid)", "W": 3.0, "seeds": [list(map(float, q)) for q in s18]},
    # dashed (Cockroft) lines: too fragmented to track reliably; not digitised`n    # {"name": "dashed_20", "label": "dashed line labelled 20 (Cockroft theory)", "W": 3.0, "seeds": [list(map(float, q)) for q in d20]},
    # {"name": "dashed_1", "label": "dashed line near shape factor 1 (Cockroft theory), upper part", "W": 4.0, "seeds": [list(map(float, q)) for q in d1]},
    # {"name": "dashed_12", "label": "dashed line near shape factor 12 (Cockroft theory), upper part", "W": 3.0, "seeds": [list(map(float, q)) for q in d12]},
]
base["curves"] = curves
base["resample_x"] = [1.0, 12.0, 0.1]
json.dump(base, open(sys.argv[2], "w"), indent=1)
print("curves", len(curves))

