import fitz
pdf = fitz.open('C:/temp/marksmith/tests/MathTestHarness/test.pdf')
page = pdf[0]
pix = page.get_pixmap(dpi=150)
pix.save('C:/temp/marksmith/tests/MathTestHarness/test.png')
