#!/usr/bin/env python3
"""Synthetic parts in the EasyEDA JSON format for the tests (no vendor data is stored in the repo).
Usage: make_fixtures.py <out dir>"""
import json, copy, os, sys

OX, OY = 4000.0, 3000.0
def U(mm): return mm / 0.254
def fx(mm): return round(OX + U(mm), 4)
def fy(mm): return round(OY - U(mm), 4)          # EasyEDA: Y grows down

def pad(num, xmm, ymm, wmm, hmm):
    x, y, w, h = fx(xmm), fy(ymm), U(wmm), U(hmm)
    pts = f"{x-w/2:.4f} {y-h/2:.4f} {x+w/2:.4f} {y-h/2:.4f} {x+w/2:.4f} {y+h/2:.4f} {x-w/2:.4f} {y+h/2:.4f}"
    return f"PAD~RECT~{x}~{y}~{w:.4f}~{h:.4f}~1~~{num}~0~{pts}~0~gge{num}00~0~~Y~0~0.0000~0.2000~{x},{y}"

def pin(n, name, x, y, rot):
    d = {0: "h -10", 180: "h 10", 90: "v 10", 270: "v -10"}[rot]
    return (f"P~show~0~{n}~{x}~{y}~{rot}~gge2{n}~0^^{x}~{y}^^M {x} {y} {d}~#880000^^1~{x}~{y}~0~{name}~start~~~#0000FF"
            f"^^1~{x}~{y}~0~{n}~end~~~#0000FF^^0~{x}~{y}^^0~M 0 0")

def mosfet():
    fp = []
    xs = [-1.905, -0.635, 0.635, 1.905]
    for i, x in enumerate(xs): fp.append(pad(i + 1, x, -2.75, 0.7, 1.1))
    for i, x in enumerate(reversed(xs)): fp.append(pad(i + 5, x, 2.75, 0.7, 1.1))
    fp.append(pad(9, 0, 0.3, 4.2, 3.4))
    bx, by = 5.2 / 2, 5.9 / 2
    fp.append(f"SOLIDREGION~99~~M {fx(-bx)} {fy(by)} L {fx(bx)} {fy(by)} L {fx(bx)} {fy(-by)} L {fx(-bx)} {fy(-by)} Z ~solid~gge990~~~~0")
    fp.append(f"TRACK~0.6~3~~{fx(-bx)} {fy(by-0.3)} {fx(-bx)} {fy(-by+0.3)}~gge991~0")
    fp.append(f"TRACK~0.6~3~~{fx(bx)} {fy(by-0.3)} {fx(bx)} {fy(-by+0.3)}~gge992~0")
    # the transistor glyph is listed BEFORE the body rectangle: the writer must reorder them
    sym = ["PL~420 350 420 380~#880000~1~0~none~gge10~0",
           "PL~425 348 425 382~#880000~1~0~none~gge11~0",
           "PG~428 365 436 361 436 369~#880000~1~0~#880000~gge14~0",
           "R~400~330~~~60~50~#880000~1~0~none~gge17~0~"]
    for i in range(4): sym.append(pin(i + 1, "S" if i < 3 else "G", 390, 340 + 10 * i, 180))
    for i in range(4): sym.append(pin(i + 5, "D", 470, 370 - 10 * i, 0))
    sym.append(pin(9, "D", 430, 320, 90))
    pkg = "TEST-DFN-8_L5.9-W5.2-P1.27-LS6.2-BL-EP"
    return {
        "title": "TESTFET-1", "description": "synthetic test MOSFET",
        "lcsc": {"id": 1, "number": "C90000001"},
        "dataStr": {"head": {"c_para": {"pre": "Q?", "name": "TESTFET-1", "package": pkg, "Manufacturer": "TestCo",
                                         "Manufacturer Part": "TESTFET-1", "Supplier Part": "C90000001"}, "x": 430, "y": 350},
                    "shape": sym},
        "packageDetail": {"title": pkg, "dataStr": {"head": {"c_para": {"package": pkg}, "x": OX, "y": OY}, "shape": fp}},
    }

def variant(base, i, name, fp):
    x = copy.deepcopy(base)
    x["title"] = name
    c = x["dataStr"]["head"]["c_para"]
    c["name"] = name; c["Manufacturer Part"] = name; c["Supplier Part"] = "C9000%04d" % (100 + i)
    x["lcsc"]["number"] = c["Supplier Part"]
    x["packageDetail"]["title"] = fp
    x["packageDetail"]["dataStr"]["head"]["c_para"]["package"] = fp
    return x

out = sys.argv[1]
os.makedirs(out, exist_ok=True)
base = mosfet()
json.dump({"result": base}, open(os.path.join(out, "fet.json"), "w"), ensure_ascii=False)
names = ['A*B/C\\D:E?F"G<H>I|J', 'Резистор 10кОм', 'X' * 300, 'Storage', 'FileHeader', 'semi;colon,comma', 'TESTFET-2']
fps = ['FP*1/2', 'Футпринт', 'Y' * 300, 'FP|pipe', 'SectionKeys', 'Library', 'TEST-DFN-8_L5.9-W5.2-P1.27-LS6.2-BL-EP']
for i, (n, f) in enumerate(zip(names, fps)):
    json.dump({"result": variant(base, i, n, f)}, open(os.path.join(out, "name%02d.json" % i), "w"), ensure_ascii=False)
print("fixtures:", len(names) + 1)
