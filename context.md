# 🧠 Technical Context & Architecture: Semantic Searcher

## Overview
A PowerToys Run plugin in C# that performs semantic search over local files. Three projects:
- `Community.PowerToys.Run.Plugin.SemanticSearcher` — the main plugin
- `IndexerTool` — CLI for managing and inspecting the index
- `Community.PowerToys.Run.Plugin.SemanticSearcher.UnitTests` — test suite

---

## Architecture & Components

### 1. Indexing Pipeline (`IndexManager` + `DocumentReader`)
- `DocumentReader` extracts text from each file type:
  - **Text PDFs** → PdfPig
  - **Scanned / image-only PDFs** → PdfPig returns empty → automatic OCR fallback via `Windows.Data.Pdf` + `Windows.Media.Ocr`
  - **Images** (PNG, JPG, JPEG, BMP, TIFF, GIF, WEBP) → `Windows.Media.Ocr` directly (no extra NuGet packages — built into `net9.0-windows10.0.26100.0`)
  - **Office docs** → `DocumentFormat.OpenXml` (DOCX, XLSX, PPTX)
  - **Code & text** → plain `StreamReader`
- `TextChunker` splits text into overlapping chunks (`ChunkSize`, `ChunkOverlap` from config)
- `IEmbeddingProvider` converts each chunk into a float[] vector

### 2. Vector Store (`VectorStore` — SQLite)
- Thread-safe via a single-writer `Channel<Action>` queue + `lock`
- WAL mode + `busy_timeout = 5000` for concurrent read safety
- Cosine similarity computed in-process over a full table scan (streaming, low memory)
- Operations: `Upsert`, `Remove`, `RemoveDirectory`, `Search`, `GetIndexedPaths`, `GetIndexedModificationTimes`

### 3. File System Monitoring (`IndexManager`)
- One `FileSystemWatcher` per watched root with `IncludeSubdirectories = true`
- **Created / Changed** → `ScheduleReindex` (600ms debounce to coalesce rapid saves)
- **Deleted** → `_store.Remove` + `_store.RemoveDirectory`
- **Renamed / Moved** → removes old DB entry, then polls for file readability via `WaitUntilReadableAsync` before indexing the new path
  - `WaitUntilReadableAsync`: tries `FileStream.Open` every 100ms up to 20 attempts (2s max) — fires the instant the OS grants access, no fixed sleep
- **Directory rename/move** → `RemoveDirectory(oldPath)` + `IndexDirectoryAsync(newPath)`

### 4. Embedding Providers
| Provider | Notes |
|---|---|
| `OnnxEmbeddingProvider` | Local ONNX model, zero network calls |
| `OllamaEmbeddingProvider` | Local HTTP to Ollama instance |
| `OpenAICompatibleEmbeddingProvider` | Any OpenAI-spec API |

---

## Configuration (`config.json`)
| Key | Description |
|---|---|
| `WatchedPaths` | Directories to monitor and index |
| `ExcludePathPatterns` | Regex patterns for paths to skip (`.git`, `bin`, `obj`, `project`, etc.) |
| `SupportedExtensions` | Full list including `.pdf`, `.docx`, `.png`, `.jpg`, `.cs`, `.md`, etc. |
| `ChunkSize` / `ChunkOverlap` | Tokenizer chunking parameters |
| `MaxFileSizeMB` | Files above this limit are skipped |
| `ProviderType` | 0 = ONNX, 1 = Ollama, 2 = OpenAI-compatible |
| `TopK` / `MinSimilarityScore` | Search result tuning |

---

## Logging
All events written to `%LOCALAPPDATA%\SemanticSearcher\plugin.log`:
- `[INFO]` — FullIndex completion, file renamed/moved/indexed
- `[WARN]` — Files skipped (too large, no extractable text, scanned-only, unreadable)
- `[READER]` — Parse failures with exception details
- `[ERROR]` — Unexpected failures in indexing or embedding

---

## Recent Engineering Focus
| Area | Work Done |
|---|---|
| **OCR** | Windows.Media.Ocr fallback for scanned PDFs; direct OCR for all image formats |
| **Rename/Move reliability** | Fixed silent failure where renamed files were removed from DB but never re-indexed; replaced fixed 800ms delay with `WaitUntilReadableAsync` polling |
| **Skip diagnostics** | `IndexFileAsync` now returns `bool`; distinct log messages for "no text" vs "read error"; `IndexerTool` prints full skip-reason report at end of run |
| **Indexing exclusions** | Added `\\project\\` exclude pattern; removed `.ini` from supported extensions to avoid junk files |
| **Memory & concurrency** | Thread-safe SQLite write queue; stable VRAM usage with background embedding providers |
| **Install script** | PowerToys stopped only after indexing completes (not before), so live index isn't interrupted |

## Command to verify the indexed files
- ```dotnet run --project IndexerTool\IndexerTool.csproj -- inspect```