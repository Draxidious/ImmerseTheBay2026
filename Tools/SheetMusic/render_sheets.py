"""
Engrave MusicXML files into sheet-music PNGs + raw layout data (bar and note rectangles in PNG pixels).

Called by Unity (Tools > Songs > Generate Sheet Music), which first exports every song in the song database to MusicXML.
Uses only Verovio (engraving, LGPL-3.0) and resvg-py (SVG -> PNG, MPL-2.0) from Tools/SheetMusic/.venv; nothing ships in the app.

  python render_sheets.py --xml-dir <dir with <songId>.musicxml> --png-dir <Assets/Resources/Sheets> --raw-dir <dir> [--width 2048]

Output per song:
  <png-dir>/<songId>/page-01.png, page-02.png, ...      black notation on white, A4 pages filled one at a time
  <raw-dir>/<songId>.json                                pages, measures (in order) and notes (by MusicXML note id) with pixel rects
"""
import argparse
import json
import os
import re
import sys
import xml.etree.ElementTree as ET

import resvg_py
import verovio

SVG_NS = "{http://www.w3.org/2000/svg}"
TRANSLATE = re.compile(r"translate\(\s*(-?[\d.]+)[ ,]+(-?[\d.]+)\s*\)")

OPTIONS = {
    "pageWidth": 2100,        # A4 portrait (Verovio units = tenths of a mm)
    "pageHeight": 2970,
    "adjustPageHeight": False,  # fill the page, one page at a time
    "breaks": "auto",
    "header": "auto",         # title + composer from the MusicXML
    "footer": "none",         # no "Engraved by Verovio" line
    "mnumInterval": 1,        # number every bar (the coach talks in bar numbers)
    "svgBoundingBoxes": True, # invisible rects we read for the layout data
    "scale": 100,
}


def fix_text(svg: str) -> str:
    """Verovio puts font-size="0px" on <text> and the real size on an inner <tspan>; resvg then misplaces or drops the text."""
    def fix(m):
        block = m.group(0)
        size = re.search(r'<tspan[^>]*font-size="(\d+(?:\.\d+)?px)"', block)
        return block.replace('font-size="0px"', f'font-size="{size.group(1)}"', 1) if size else block
    return re.sub(r'<text\b[^>]*font-size="0px".*?</text>', fix, svg, flags=re.S)


def classes(el):
    return (el.get("class") or "").split()


def walk(el, tx, ty, ctx, out):
    """Collect bounding-box rects with the current translate offset. ctx tracks the enclosing measure/staff/note."""
    m = TRANSLATE.search(el.get("transform") or "")
    if m and el.tag == SVG_NS + "g":
        tx, ty = tx + float(m.group(1)), ty + float(m.group(2))
    cls = classes(el)
    ctx = dict(ctx)
    if el.tag == SVG_NS + "g" and "bounding-box" not in cls:
        if "measure" in cls:
            out["measure_order"] += 1
            ctx["measure"] = out["measure_order"]
        elif "note" in cls:
            ctx["note"] = el.get("id")
    if el.tag == SVG_NS + "g" and "bounding-box" in cls:
        rect = el.find(SVG_NS + "rect")
        if rect is not None:
            r = (float(rect.get("x")) + tx, float(rect.get("y")) + ty, float(rect.get("width")), float(rect.get("height")))
            if "staff" in cls and "measure" in ctx:
                out["staff_rects"].setdefault(ctx["measure"], []).append(r)
            elif "note" in cls and ctx.get("note") and el.get("id") == "bbox-" + ctx["note"]:
                out["note_rects"][ctx["note"]] = (r, ctx.get("measure"), ctx.get("staff"))
    staff_no = 0
    for child in el:
        child_ctx = ctx
        cc = classes(child)
        if child.tag == SVG_NS + "g" and "staff" in cc and "bounding-box" not in cc:
            staff_no += 1  # staves are numbered per measure: 1 = treble (RH), 2 = bass (LH)
            child_ctx = dict(ctx, staff=staff_no)
        walk(child, tx, ty, child_ctx, out)


def render_song(path, song_id, png_dir, raw_dir, width):
    tk = verovio.toolkit()
    tk.setOptions(OPTIONS)
    if not tk.loadFile(path):
        raise RuntimeError(f"Verovio could not load {path}: {tk.getLog()}")

    timemap = tk.renderToTimemap()
    if isinstance(timemap, str):
        timemap = json.loads(timemap)
    qstamp = {}
    for entry in timemap:
        for nid in entry.get("on", []):
            qstamp.setdefault(nid, entry.get("qstamp", 0))

    song_png_dir = os.path.join(png_dir, song_id)
    os.makedirs(song_png_dir, exist_ok=True)
    pages, measures, notes = [], [], []
    measure_base = 0
    for p in range(1, tk.getPageCount() + 1):
        svg = fix_text(tk.renderToSVG(p))
        root = ET.fromstring(svg)
        inner = root.find(SVG_NS + "svg")
        vb_w, vb_h = [float(v) for v in inner.get("viewBox").split()[2:4]]
        out_w = float(root.get("width").replace("px", ""))
        out_h = float(root.get("height").replace("px", ""))
        height = round(width * out_h / out_w)
        sx, sy = width / vb_w, height / vb_h

        file = f"page-{p:02d}.png"
        png = resvg_py.svg_to_bytes(svg_string=svg, width=width, height=height, background="white")
        with open(os.path.join(song_png_dir, file), "wb") as fh:
            fh.write(bytes(png))
        pages.append({"index": p, "file": file, "width": width, "height": height})

        out = {"measure_order": measure_base - 1, "staff_rects": {}, "note_rects": {}}
        walk(root, 0.0, 0.0, {}, out)

        def px(r):
            x, y, w, h = r
            return {"x": round(x * sx, 1), "y": round(y * sy, 1), "w": round(w * sx, 1), "h": round(h * sy, 1)}

        for order in sorted(out["staff_rects"]):
            rs = out["staff_rects"][order]
            x0 = min(r[0] for r in rs); y0 = min(r[1] for r in rs)
            x1 = max(r[0] + r[2] for r in rs); y1 = max(r[1] + r[3] for r in rs)
            measures.append({"order": order, "page": p, **px((x0, y0, x1 - x0, y1 - y0))})
        for nid, (r, order, staff) in out["note_rects"].items():
            midi = tk.getMIDIValuesForElement(nid)
            if isinstance(midi, str):
                midi = json.loads(midi)
            notes.append({"id": nid, "page": p, "measureOrder": order, "staff": staff,
                          "qstamp": float(qstamp.get(nid, -1)), "pitch": int((midi or {}).get("pitch", -1)), **px(r)})
        measure_base = out["measure_order"] + 1

    os.makedirs(raw_dir, exist_ok=True)
    with open(os.path.join(raw_dir, song_id + ".json"), "w", encoding="utf-8") as fh:
        json.dump({"song": song_id, "pages": pages, "measures": measures, "notes": notes}, fh)
    return len(pages), len(measures), len(notes)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--xml-dir", required=True)
    ap.add_argument("--png-dir", required=True)
    ap.add_argument("--raw-dir", required=True)
    ap.add_argument("--width", type=int, default=2048)
    ap.add_argument("--only", default="", help="comma-separated song ids")
    a = ap.parse_args()
    only = {s for s in a.only.split(",") if s}
    failed = 0
    for name in sorted(os.listdir(a.xml_dir)):
        if not name.endswith(".musicxml"):
            continue
        song_id = name[: -len(".musicxml")]
        if only and song_id not in only:
            continue
        # Local/ songs keep their own subfolder (gitignored on the Unity side).
        local = song_id.startswith("Local__")
        out_id = song_id[len("Local__"):] if local else song_id
        png_dir = os.path.join(a.png_dir, "Local") if local else a.png_dir
        try:
            pages, measures, notes = render_song(os.path.join(a.xml_dir, name), out_id, png_dir, a.raw_dir, a.width)
            print(f"OK {song_id}: {pages} page(s), {measures} bars, {notes} notes")
        except Exception as ex:  # keep going with the other songs
            failed += 1
            print(f"FAILED {song_id}: {ex}")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
