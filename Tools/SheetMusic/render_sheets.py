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
import base64
import json
import os
import re
import sys
import xml.etree.ElementTree as ET
import zlib

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
    """
    Verovio puts font-size="0px" on <text> and the real size on an inner <tspan>; resvg then misplaces or drops the text.
    Copy the inner size onto the <text>. Only for <text> elements with content: Verovio also writes empty self-closing
    <text .../> tags, and a match starting at one of those would run on to the NEXT element's </text> and fix the wrong
    tag (that left bar 1's fingering at size 0, drawn in the page corner).
    """
    def fix(m):
        block = m.group(0)
        size = re.search(r'<tspan[^>]*font-size="(\d+(?:\.\d+)?px)"', block)
        return block.replace('font-size="0px"', f'font-size="{size.group(1)}"', 1) if size else block
    return re.sub(r'<text\b[^>]*font-size="0px"[^>]*(?<!/)>.*?</text>', fix, svg, flags=re.S)


HEADER_TSPAN = re.compile(
    # Verovio puts empty/rect bounding-box <g> elements between these tspans, so allow anything up to the sized tspan.
    r'(<tspan id="[^"]*" class="rend" x="(-?[\d.]+)" y="(-?[\d.]+)" text-anchor="(\w+)">(?:(?!</tspan>).)*?<tspan font-size=")'
    r'([\d.]+)(px">)([^<]*)(</tspan>)', re.S)
EM_WIDTH = 0.52   # average glyph width of the header serif font, in em (generous, so estimates err on the safe side)


def fit_header(svg: str) -> str:
    """
    The page header has a centered title and left/right credits (tempo + arrangement, composer) on the same line, all at
    the same size. A long left credit runs into the title. Shrink each side credit (never the title) until its
    estimated width fits between the page edge and the title, with a gap.
    """
    items = list(HEADER_TSPAN.finditer(svg))
    titles = [m for m in items if m.group(4) == "middle"]
    if not titles:
        return svg
    t = titles[0]
    t_x, t_y, t_size = float(t.group(2)), float(t.group(3)), float(t.group(5))
    t_half = EM_WIDTH * t_size * len(t.group(7).strip()) / 2
    gap = 0.8 * t_size
    out, last = [], 0
    for m in items:
        x, y, anchor, size, text = float(m.group(2)), float(m.group(3)), m.group(4), float(m.group(5)), m.group(7).strip()
        new_size = size
        if anchor in ("start", "end") and abs(y - t_y) < t_size and text:
            room = (t_x - t_half - gap - x) if anchor == "start" else (x - (t_x + t_half + gap))
            need = EM_WIDTH * size * len(text)
            if room > 0 and need > room:
                new_size = max(0.45 * size, size * room / need)
        out.append(svg[last:m.start(5)])
        out.append(f"{new_size:.0f}")
        last = m.end(5)
    out.append(svg[last:])
    return "".join(out)


def png_ink_rows(png: bytes, threshold=235):
    """Row indices (0-based) of a PNG that contain any non-white pixel. Minimal decoder: 8-bit RGB/RGBA, non-interlaced."""
    pos, ihdr, idat = 8, None, b""
    while pos < len(png):
        n = int.from_bytes(png[pos:pos + 4], "big"); kind = png[pos + 4:pos + 8]; data = png[pos + 8:pos + 8 + n]
        if kind == b"IHDR": ihdr = data
        elif kind == b"IDAT": idat += data
        pos += 12 + n
    w, h = int.from_bytes(ihdr[0:4], "big"), int.from_bytes(ihdr[4:8], "big")
    bpp = {2: 3, 6: 4}[ihdr[9]]
    raw, stride, prev, rows = zlib.decompress(idat), w * bpp, bytearray(w * bpp), []
    for r in range(h):
        f = raw[r * (stride + 1)]
        line = bytearray(raw[r * (stride + 1) + 1:(r + 1) * (stride + 1)])
        for i in range(stride):
            a = line[i - bpp] if i >= bpp else 0
            b = prev[i]
            c = prev[i - bpp] if i >= bpp else 0
            if f == 1: line[i] = (line[i] + a) & 255
            elif f == 2: line[i] = (line[i] + b) & 255
            elif f == 3: line[i] = (line[i] + ((a + b) >> 1)) & 255
            elif f == 4:
                p = a + b - c; pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                line[i] = (line[i] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        if min(line[k] for k in range(len(line)) if k % bpp < 3) < threshold:
            rows.append(r)
        prev = line
    return rows


def header_bottom_px(svg: str, width: int, height: int):
    """Real bottom of the page header in PNG pixels: render the page with every system hidden and find the last inked row."""
    header_only = re.sub(r'(<svg\b[^>]*>)', r'\1<style>g.system{display:none}</style>', svg, count=1)
    small_w = 512
    png = bytes(resvg_py.svg_to_bytes(svg_string=header_only, width=small_w, height=round(height * small_w / width), background="white"))
    rows = png_ink_rows(png)
    return (max(rows) + 1) * width / small_w if rows else None


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
        if "pgHead" in cls:
            ctx["head"] = True
        elif "measure" in cls:
            out["measure_order"] += 1
            ctx["measure"] = out["measure_order"]
        elif "note" in cls:
            ctx["note"] = el.get("id")
    if el.tag == SVG_NS + "g" and "bounding-box" in cls:
        rect = el.find(SVG_NS + "rect")
        if rect is not None:
            r = (float(rect.get("x")) + tx, float(rect.get("y")) + ty, float(rect.get("width")), float(rect.get("height")))
            if ctx.get("head"):   # anything in the page header: title, composer, tempo / arrangement line
                out.setdefault("header_rects", []).append(r)
            elif "staff" in cls and "measure" in ctx:
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


LINES_PER_PAGE = 5  # lines of music (systems) per page; Verovio still decides which bars go on each line


def line_starts(tk):
    """Index (document order, 0 = first measure) of the measure that starts each line in Verovio's automatic layout."""
    starts, order, new_line = [], -1, False
    for p in range(1, tk.getPageCount() + 1):
        for g in ET.fromstring(tk.renderToSVG(p)).iter(SVG_NS + "g"):
            c = classes(g)
            if "bounding-box" in c:
                continue
            if "system" in c:
                new_line = True
            elif "measure" in c:
                order += 1
                if new_line:
                    starts.append(order)
                    new_line = False
    return starts


def with_breaks(xml: str, starts, per_page: int) -> str:
    """Encode the automatic line breaks, plus a page break before every per_page-th line, as MusicXML <print> marks."""
    page = {starts[k] for k in range(per_page, len(starts), per_page)}
    line = set(starts[1:]) - page
    index = -1

    def mark(m):
        nonlocal index
        index += 1
        if index in page:
            return m.group(0) + '<print new-page="yes"/>'
        if index in line:
            return m.group(0) + '<print new-system="yes"/>'
        return m.group(0)
    return re.sub(r"<measure\b[^>]*>", mark, xml)


def render_song(path, song_id, png_dir, raw_dir, width):
    tk = verovio.toolkit()
    tk.setOptions(OPTIONS)
    if not tk.loadFile(path):
        raise RuntimeError(f"Verovio could not load {path}: {tk.getLog()}")

    # Fixed number of lines per page: keep Verovio's line breaks, but break pages every LINES_PER_PAGE lines.
    starts = line_starts(tk)
    if len(starts) > 1:
        with open(path, encoding="utf-8") as fh:
            xml = with_breaks(fh.read(), starts, LINES_PER_PAGE)
        tk.setOptions({**OPTIONS, "breaks": "encoded"})
        if not tk.loadData(xml):
            raise RuntimeError(f"Verovio could not load {path} with page breaks: {tk.getLog()}")
        lines = len(line_starts(tk))
        if lines != len(starts):
            print(f"  note {song_id}: {len(starts)} lines before page breaks, {lines} after")

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
        svg = fix_text(fit_header(tk.renderToSVG(p)))
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
        if out.get("header_rects"):
            # Verovio's header box uses its own font metrics and ends above the glyphs resvg actually draws
            # (descenders), so measure the rendered header's pixels and keep whichever is lower.
            bbox_bottom = max(r[1] + r[3] for r in out["header_rects"]) * sy
            ink_bottom = header_bottom_px(svg, width, height)
            pages[-1]["headerBottom"] = round(max(bbox_bottom, ink_bottom or 0.0), 1)

    systems = cut_systems(song_png_dir, pages, measures, notes)

    os.makedirs(raw_dir, exist_ok=True)
    with open(os.path.join(raw_dir, song_id + ".json"), "w", encoding="utf-8") as fh:
        json.dump({"song": song_id, "pages": pages, "measures": measures, "notes": notes, "systems": systems}, fh)
    return len(pages), len(measures), len(notes)


def cut_systems(song_png_dir, pages, measures, notes):
    """
    Cut each line of music ("system") out of its page as system-NN.png, for panels that show one line at a time
    (Main's SongNoteDisplay). All strips of a song share one width and height (same aspect ratio): full staff width
    plus room for the brace, bar numbers above and ledger notes/stems below.
    """
    if not measures:
        return []
    # Group measures into systems: same page, overlapping vertical extent.
    groups = []
    for m in sorted(measures, key=lambda m: (m["page"], m["y"], m["x"])):
        g = groups[-1] if groups else None
        if g and g["page"] == m["page"] and abs(m["y"] - g["y"]) < m["h"] * 0.5:
            g["measures"].append(m)
        else:
            groups.append({"page": m["page"], "y": m["y"], "measures": [m]})

    x0 = min(m["x"] for m in measures)
    x1 = max(m["x"] + m["w"] for m in measures)
    pad_left = 0.06 * (x1 - x0)       # brace + clef margin
    pad_right = 0.01 * (x1 - x0)

    # Each line owns the space halfway to its neighbours on the same page (stems, beams, ledger notes, bar numbers live
    # there; noteheads alone don't tell where stems end). Lines at a page's top/bottom get the song's typical half-gap.
    staff = [(min(m["y"] for m in g["measures"]), max(m["y"] + m["h"] for m in g["measures"])) for g in groups]
    half_gaps = [(staff[i + 1][0] - staff[i][1]) / 2 for i in range(len(groups) - 1)
                 if groups[i + 1]["page"] == groups[i]["page"] and staff[i + 1][0] > staff[i][1]]
    default_half = sorted(half_gaps)[len(half_gaps) // 2] if half_gaps else 0.45 * (staff[0][1] - staff[0][0])
    extents = []
    for i, g in enumerate(groups):
        top_s, bot_s = staff[i]
        same_prev = i > 0 and groups[i - 1]["page"] == g["page"]
        same_next = i + 1 < len(groups) and groups[i + 1]["page"] == g["page"]
        # No line above: the typical half-gap, but never into the page header (title / tempo / composer).
        top = (staff[i - 1][1] + top_s) / 2 if same_prev else top_s - default_half
        header_bottom = pages[g["page"] - 1].get("headerBottom")
        if not same_prev and header_bottom is not None and header_bottom < top_s:
            top = max(top, header_bottom + 4)
        bottom = (bot_s + staff[i + 1][0]) / 2 if same_next else bot_s + default_half
        orders = {m["order"] for m in g["measures"]}
        heads = [n for n in notes if n["measureOrder"] in orders]  # a very high/low note can reach past the halfway line
        if heads:
            top = min(top, min(n["y"] for n in heads) - 10)
            bottom = max(bottom, max(n["y"] + n["h"] for n in heads) + 10)
        extents.append((top, bottom))
    height = max(b - t for t, b in extents)
    width = (x1 + pad_right) - (x0 - pad_left)

    systems = []
    for i, (g, (top, bottom)) in enumerate(zip(groups, extents), start=1):
        page = pages[g["page"] - 1]
        # Whole-pixel crop: at a fractional edge the white mask's antialiasing let one row of the page header through.
        out_h = int(round(height))
        cx = int(max(0.0, x0 - pad_left))
        cw = int(round(min(width, page["width"] - cx)))
        out_w = cw
        cy = int(max(0, min(page["height"] - out_h, round((top + bottom) / 2 - out_h / 2))))
        page_png = os.path.join(song_png_dir, page["file"])
        with open(page_png, "rb") as fh:
            b64 = base64.b64encode(fh.read()).decode()
        # White out anything outside this line's band (bits of the neighbouring lines, the page header). The rects
        # reach 2 px past the strip's edges so nothing shows through at the border.
        band_top, band_bottom = int(round(top)), int(round(bottom))
        mask = (f'<rect x="{cx - 2}" y="{cy - 2}" width="{cw + 4}" height="{max(0, band_top - cy + 2)}" fill="white"/>'
                f'<rect x="{cx - 2}" y="{band_bottom}" width="{cw + 4}" height="{max(0, cy + out_h - band_bottom + 2)}" fill="white"/>')
        svg = (f'<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="{out_w}" '
               f'height="{out_h}" viewBox="{cx} {cy} {cw} {out_h}"><rect x="{cx}" y="{cy}" width="{cw}" height="{out_h}" '
               f'fill="white"/><image width="{page["width"]}" height="{page["height"]}" xlink:href="data:image/png;base64,{b64}"/>'
               f'{mask}</svg>')
        file = f"system-{i:02d}.png"
        with open(os.path.join(song_png_dir, file), "wb") as fh:
            fh.write(bytes(resvg_py.svg_to_bytes(svg_string=svg, width=out_w, height=out_h)))
        systems.append({"index": i, "file": file, "page": g["page"], "x": round(cx, 1), "y": round(cy, 1),
                        "w": cw, "h": out_h, "width": out_w, "height": out_h,
                        "measureOrders": sorted(m["order"] for m in g["measures"])})
    return systems


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
