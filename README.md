# Engineering Drawing PDF Extractor — v1.2
**Framework**: .NET 8.0-windows  |  **Language**: C# only  |  **Dependencies**: 1 NuGet package

---

## How to run

### Option A — Windows UI (recommended)
Build once, then run `EngineeringPdfExtractor.exe`.

The app opens a **Windows UI** with:
- `Browse PDF` button
- `Extract` button
- `Save extracted images` checkbox
- Output panel with the extraction report

### Option B — Build and run from source
```cmd
cd EngineeringPdfPoC
dotnet build
dotnet run
```
`dotnet run` opens the same Windows UI.

---

## UI behaviour

1. Click `Browse PDF` to choose a file.
2. (Optional) Enable/disable `Save extracted images`.
3. Click `Extract`.
4. Report is shown in the output panel and saved as `<pdfname>.extraction.txt`.
5. If enabled, images are saved in `<pdfname>_images\` next to the PDF file.

---

## Project structure — C# only

```
EngineeringPdfPoC\
├── EngineeringPdfExtractor.csproj   .NET 8-windows, UseWindowsForms enabled
├── Program.cs                        File dialog + CLI + orchestrator
├── Models.cs                         Data structures
├── PdfParser.cs                      xref, objects, page tree
├── PdfStream.cs                      Decompression + text state machine
└── EngineeringDrawingAnalyzer.cs     Title block, tables, dims, notes
```

No Python. No scripts. Pure C#.

---

## Requirements

- Windows 10 or Windows 11
- .NET 8.0 Runtime (or SDK to build from source)
  Download: https://dotnet.microsoft.com/download/dotnet/8.0

The file dialog (`System.Windows.Forms.OpenFileDialog`) is built into .NET 8 on Windows.
No additional installation required.

---

## What it extracts

| Content              | Native PDF | Scanned/Image PDF |
|----------------------|------------|-------------------|
| All text (with X/Y)  | ✅          | ❌ needs OCR      |
| Title block fields   | ✅          | ❌ needs OCR      |
| Dimensions           | ✅          | ❌ needs OCR      |
| Tolerances           | ✅          | ❌ needs OCR      |
| Tables (BOM, Rev)    | ✅          | ❌ needs OCR      |
| General notes        | ✅          | ❌ needs OCR      |
| Embedded images      | ✅          | ✅ (the drawing itself) |
| Document metadata    | ✅          | ✅                |
| PDF type detection   | ✅          | ✅ (reports clearly) |

---

## Changes in v1.2 (this release)

| Change | Detail |
|--------|--------|
| **File dialog added** | `System.Windows.Forms.OpenFileDialog` opens when no argument supplied |
| **STAThread attribute** | Added to `Main()` — required for Windows Forms dialogs |
| **Console kept open** | When run interactively (no args), "Press any key" holds the window |
| **Image output folder** | Images now saved to `<name>_images\` subfolder, not working directory |
| **Python removed** | `run_extractor.py` removed — C# only |
| **Target updated** | `net8.0` → `net8.0-windows` for Windows Forms support |

---

## Known limitations (PoC scope)

- **Scanned PDFs**: Image detected and extracted; text requires OCR (next: Windows.Media.Ocr)
- **Compressed xref (PDF 1.5+)**: Fallback full-file scan (works on tested files)
- **Encrypted PDFs**: Not supported
- **CJK fonts**: No ToUnicode CMap — Latin drawings unaffected
- **Windows only**: File dialog requires Windows. The extraction logic itself is cross-platform.
