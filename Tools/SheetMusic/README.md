# Sheet-music generator

Turns every song in the song database (`Assets/Resources/SongDB/*.json`) into sheet-music PNGs + highlight data in
`Assets/Resources/Sheets/<songId>/` (see `SheetLibrary` and docs/INTERFACES.md). Editor-only tool, nothing here ships in the app.

**One-time setup** (from the project root; no admin, nothing system-wide — a private venv in this folder, gitignored):

    uv venv Tools/SheetMusic/.venv --python 3.13
    uv pip install --python Tools/SheetMusic/.venv/Scripts/python.exe verovio==6.4.1 resvg-py==0.5.0

**Run:** Unity → Tools ▸ Songs ▸ Generate Sheet Music (after any song changes).

Pipeline: `MusicXmlExporter` (C#) → MusicXML → `render_sheets.py`: Verovio 6.4.1 (engraving, LGPL-3.0) → SVG → resvg-py 0.5.0
(rasterizing, MPL-2.0) → PNG (2048 px wide A4 pages, black on white) + raw layout → `SheetMusicMenu` maps bars/notes back to
the song database → `layout.json`.
