# EnPDF 🚀

Engineering PDF Extraction Engine built in C#

## 🔍 Overview

EnPDF is a custom-built PDF parsing engine designed to extract structured data from engineering drawings.

## ⚙️ Features

* Native PDF parsing (no heavy external libraries)
* Extract:

  * Dimensions
  * Tolerances
  * Notes
  * Tables (BOM / revision)
* Detects:

  * Native vs Scanned PDFs
* Coordinate-based analysis

## 🧠 Architecture

* Low-level PDF parser (xref, objects, streams)
* Engineering-specific analyzer layer

## 🚧 Limitations

* No OCR support (scanned PDFs not supported)
* No Object Stream (/ObjStm) support
* Limited font decoding (no ToUnicode support)
* No encrypted PDF handling

## ▶️ How to Run

```bash
dotnet build
dotnet run -- path/to/file.pdf
```

## 🎯 Roadmap

* Add Object Stream support
* Improve font decoding
* Hybrid OCR integration (future)

## 👨‍💻 Author

Muthu Subramanian
