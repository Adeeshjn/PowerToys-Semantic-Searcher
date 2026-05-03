# 🔍 PowerToys Semantic Searcher Plugin

<p align="center">
  <img src="Community.PowerToys.Run.Plugin.SemanticSearcher/Images/logo.svg" alt="Semantic Searcher Logo" width="256" height="256" />
</p>

## 📝 Problem Statement
Traditional file search relies heavily on exact keyword matching, file names, or metadata. When you are looking for a document based on its *meaning* or a specific concept discussed within it, standard search tools often fall short. They require you to remember exactly which words were used, leading to wasted time and frustration — especially when managing large knowledge bases, research papers, or scattered notes.

Even worse, standard search completely ignores your images and scanned documents. A screenshot of a receipt, a photo of a whiteboard, or a scanned Aadhaar card are invisible to conventional tools.

## 💡 Value Add / Unique Selling Proposition (USP)
**Understand the *meaning* of your files — documents, code, images, and scanned pages — directly from PowerToys Run.**

- **Semantic Search:** Find files by concept, not just keywords. Query `how to fix memory leak in c#` and get relevant source files back.
- **Privacy First:** Supports fully local ONNX models and local Ollama instances — your documents never leave your machine.
- **Provider Agnostic:** Swap between ONNX, Ollama, OpenAI-compatible APIs, or any future provider without changing your workflow.
- **OCR for Images & Scanned PDFs:** Uses the built-in Windows OCR engine to extract text from images (PNG, JPG, TIFF, WEBP, etc.) and image-only/scanned PDFs — no third-party dependencies, no cloud calls.
- **Real-time Sync:** Background file watchers keep the index in sync as you add, edit, move, or rename files. Uses readability polling instead of arbitrary sleep timers — indexing fires the instant the OS confirms a file is accessible.
- **Seamless PowerToys Integration:** Works right from `Alt + Space` alongside your other PowerToys tools.

## 📖 Description
The PowerToys Semantic Searcher Plugin is a powerful extension for Microsoft PowerToys Run. It continuously indexes your local files and generates vector embeddings for their content. When you type a query into PowerToys Run, it performs a similarity search against a local SQLite vector database, returning results that are *conceptually related* to your query — even if the exact keywords don't match.

The indexing pipeline handles a wide range of file types:
- **Text-based PDFs** — via PdfPig (fast, no GPU)
- **Scanned / image-only PDFs** — via Windows OCR (automatic fallback)
- **Images** (PNG, JPG, JPEG, BMP, TIFF, GIF, WEBP) — via Windows OCR
- **Office documents** — DOCX, XLSX, PPTX via OpenXml
- **Code & plain text** — CS, PY, JS, TS, MD, TXT, JSON, YAML, and more

A dedicated CLI tool (`IndexerTool`) lets developers and power users inspect and manage the index manually.

## 🚀 Features

- **Semantic Search** — search by concept, not just keywords
- **OCR Support** — images and scanned PDFs are first-class citizens
- **Live Indexing** — file additions, edits, moves, and renames are reflected automatically
  - Uses `WaitUntilReadableAsync` polling: indexes the moment the OS grants access, not after an arbitrary timer
- **Multiple Embedding Providers** — ONNX (local), Ollama (local), OpenAI-compatible APIs
- **Broad Format Support** — PDF, Office, images, code, markdown, config files

## ⚙️ Installation & Deployment
We provide PowerShell scripts to automate building, configuring, and deploying the plugin into PowerToys.

**Install / Update:**
```powershell
# Compiles the plugin, deploys config.json, generates the semantic index, and restarts PowerToys
# (Note: This automatically clears the old index database and builds a fresh one)
.\install_and_verify.ps1

# Install and deploy WITHOUT re-indexing the database (faster updates)
.\install_and_verify.ps1 -SkipIndex
```

**Uninstall:**
```powershell
# Stops PowerToys, removes the plugin, but keeps your index database intact
.\uninstall.ps1

# Completely removes the plugin AND deletes the entire vector database and logs
.\uninstall.ps1 -WipeDatabase
```

## 🖥️ How to Use
1. Open PowerToys Run (default: `Alt + Space`).
2. Type the plugin keyword (e.g., `sem `).
3. Enter your semantic query:
   ```
   sem how to fix memory leak in c#
   sem my aadhaar card
   sem quarterly revenue chart
   ```
4. The plugin returns the most contextually relevant files from your indexed folders.

## 🛠️ IndexerTool CLI
```
dotnet run --project IndexerTool -- index     # full re-index
dotnet run --project IndexerTool -- inspect   # show index stats & per-file chunks
dotnet run --project IndexerTool -- search "your query"
dotnet run --project IndexerTool -- clear     # wipe the index
```
