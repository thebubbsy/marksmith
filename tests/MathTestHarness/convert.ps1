$w = New-Object -ComObject Word.Application
$w.Visible = $false
$doc = $w.Documents.Open('C:\temp\marksmith\tests\MathTestHarness\test.docx')
$doc.SaveAs([ref]'C:\temp\marksmith\tests\MathTestHarness\test.pdf', [ref]17)
$doc.Close()
$w.Quit()
