"""Render a Marksmith-generated .docx the way Microsoft Word itself lays it out.

The document is opened in Word via COM, exported to PDF by Word's own engine,
and rasterized with PyMuPDF. Nothing is re-implemented: if an equation shows up
here it is because Word's equation editor drew it from the OMML in the package.

    python tools/capture/capture_word.py <doc.docx> <out.png> [--pages 1,2] [--dpi 160] [--gap 28]
"""

import os
import sys
import tempfile

import fitz  # PyMuPDF
import win32com.client

WD_EXPORT_PDF = 17


def arg(name, default=None):
    return sys.argv[sys.argv.index(name) + 1] if name in sys.argv else default


def docx_to_pdf(docx, pdf):
    word = win32com.client.DispatchEx("Word.Application")
    word.Visible = False
    word.DisplayAlerts = 0
    try:
        doc = word.Documents.Open(docx, ReadOnly=True)
        # Table formulas and the index are field codes; Word fills them on open.
        try:
            doc.Fields.Update()
        except Exception:
            pass
        doc.ExportAsFixedFormat(pdf, WD_EXPORT_PDF)
        doc.Close(SaveChanges=0)
    finally:
        word.Quit()


def rasterize(pdf, out, pages, dpi, gap):
    book = fitz.open(pdf)
    zoom = dpi / 72
    tiles = [book[p - 1].get_pixmap(matrix=fitz.Matrix(zoom, zoom), alpha=False)
             for p in pages if p - 1 < len(book)]
    if not tiles:
        sys.exit(f"{pdf} has no page in {pages}")

    width = sum(t.width for t in tiles) + gap * (len(tiles) - 1)
    height = max(t.height for t in tiles)
    canvas = fitz.Pixmap(fitz.csRGB, fitz.IRect(0, 0, width, height), False)
    canvas.clear_with(255)

    x = 0
    for tile in tiles:
        tile.set_origin(x, 0)
        canvas.copy(tile, tile.irect)
        x += tile.width + gap

    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    canvas.save(out)
    print(f"[ok] {out}  {width}x{height}  (pages {pages} of {len(book)} @ {dpi}dpi)")
    book.close()


def main():
    docx = os.path.abspath(sys.argv[1])
    out = os.path.abspath(sys.argv[2])
    pages = [int(p) for p in arg("--pages", "1").split(",")]
    dpi = int(arg("--dpi", 160))
    gap = int(arg("--gap", 28))

    with tempfile.TemporaryDirectory() as tmp:
        pdf = os.path.join(tmp, "word.pdf")
        docx_to_pdf(docx, pdf)
        rasterize(pdf, out, pages, dpi, gap)


if __name__ == "__main__":
    main()
