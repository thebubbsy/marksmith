# Third-Party Notices

Marksmith is proprietary software (see [LICENSE](LICENSE)). It incorporates or relies on the
third-party components below, each licensed under its own terms. Those licenses continue to govern
those components and are not affected by Marksmith's EULA. Marksmith is not endorsed by any of the
projects or companies listed.

> This list is provided in good faith and is indicative. Before commercial distribution, confirm the
> exact version and license of every bundled/redistributed component (a license-scan of the build
> output is recommended).

## Bundled runtimes & libraries (redistributed in the installer)

| Component | Used for | License |
| --- | --- | --- |
| .NET 8 runtime (Microsoft) | Application runtime | MIT |
| Windows App SDK / WinUI 3 (Microsoft) | Desktop UI framework | MIT |
| Microsoft Edge WebView2 SDK | HTML/PDF rendering | Microsoft Software License Terms (WebView2 SDK) |
| Markdig | Markdown parsing | BSD-2-Clause |
| DocumentFormat.OpenXml (Microsoft) | Native DOCX generation | MIT |
| CommunityToolkit.Mvvm | MVVM source generators | MIT |
| H.NotifyIcon.WinUI | System tray icon | MIT |
| PdfPig | Reading PDFs on import (text, layout, pictures) | Apache-2.0 |
| ONNX Runtime (Microsoft) | Runs the PaddleOCR models in-process | MIT |
| PaddleOCR PP-OCRv5 models (PaddlePaddle): mobile text detection, English and Latin recognition (ONNX) | OCR engine choice "PaddleOCR"; downloaded at build time into `ocr-models/` | Apache-2.0 |
| Tesseract 5 native libraries (via the `Tesseract` NuGet package: tesseract50.dll, leptonica) | OCR engine choice "Tesseract" | Apache-2.0 (Tesseract), BSD-2-Clause (Leptonica) |
| Tesseract `tessdata_best` English data (eng.traineddata) | OCR engine choice "Tesseract"; downloaded at build time | Apache-2.0 |
| SCOWL English word list (derived, as in Ubuntu's `wamerican`) | MarkSmith OCR's dictionary correction | SCOWL permissive licence, notice in [legal/SCOWL-COPYRIGHT.txt](legal/SCOWL-COPYRIGHT.txt) |

## Loaded at runtime for preview/render (from a public CDN, not redistributed)

| Component | Used for | License |
| --- | --- | --- |
| KaTeX | Math typesetting in preview/PDF | MIT |
| highlight.js | Code syntax highlighting | BSD-3-Clause |
| Mermaid | Diagram rendering | MIT |
| NetOffice (NetOfficeFw.Core / NetOfficeFw.Word) | Office capability plugin — drives the installed Microsoft Word for 100%-accurate SmartArt/shape renders and docx verification | MIT (© Sebastian Lange, Jozef Izso) |

MarkSmith OCR (the default OCR engine's own code and its trained letter network) is MarkSmith's
own work and contains no third-party OCR code or models. Windows OCR (Windows.Media.Ocr) is part
of Windows and is not redistributed.

The WebView2 **runtime** is a Microsoft component that ships with current Windows and is not
redistributed by Marksmith.

Full license texts for each component are available from the respective projects. Requests for a
copy of any notice may be sent to mbubbtech@gmail.com.
