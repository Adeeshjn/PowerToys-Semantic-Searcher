using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Community.PowerToys.Run.Plugin.SemanticSearcher.Models;

namespace Community.PowerToys.Run.Plugin.SemanticSearcher.Core;

/// <summary>
/// Orchestrates file watching, indexing, and search.
/// </summary>
public sealed class IndexManager : IDisposable
{
    private readonly IndexConfig _config;
    private readonly IEmbeddingProvider _embedder;
    private readonly VectorStore _store;
    private readonly DocumentReader _reader;
    private readonly TextChunker _chunker;

    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly List<Regex> _includeRegexes = [];
    private readonly List<Regex> _excludeRegexes = [];

    // Debounce: file path → pending timer
    private readonly Dictionary<string, Timer> _debounceTimers = [];
    private readonly object _debounceLock = new();
    private const int DebounceMs = 600;

    private bool _disposed;

    private static readonly string _logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SemanticSearcher", "plugin.log");

    private static readonly object _logLock = new();

    public static void Log(string level, string msg)
    {
        try
        {
            lock (_logLock)
            {
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {msg}";
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
        }
        catch { }
    }

    public IndexManager(IndexConfig config, IEmbeddingProvider embedder, string storePath)
    {
        _config = config;
        _embedder = embedder;
        _store = new VectorStore(storePath);
        _reader = new DocumentReader(config);
        _chunker = new TextChunker(config.ChunkSize, config.ChunkOverlap);

        CompileRegexes();
    }

    // ── Startup ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts file watchers and runs an initial full index on a background thread.
    /// </summary>
    public void Start()
    {
        Log("INFO", $"IndexManager starting. Watched paths: [{string.Join(", ", _config.WatchedPaths)}]");
        StartWatchers();
        Task.Run(FullIndexAsync);
    }

    // ── Search ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Embeds <paramref name="query"/> and returns the closest indexed files.
    /// Returns an empty list if the index is empty or the embedder is unavailable.
    /// </summary>
    public IReadOnlyList<SearchResult> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || _store.Count == 0)
            return [];

        try
        {
            var queryVector = _embedder.GetEmbeddingAsync(query).GetAwaiter().GetResult();
            return _store.Search(queryVector, _config.TopK, _config.MinSimilarityScore);
        }
        catch
        {
            return [];
        }
    }

    // ── Full index ───────────────────────────────────────────────────────────

    private async Task FullIndexAsync()
    {
        try
        {
            var indexed = _store.GetIndexedPaths();
            var mtimes  = _store.GetIndexedModificationTimes();
            int skipped = 0, queued = 0;

            foreach (var root in _config.WatchedPaths)
            {
                if (!Directory.Exists(root))
                {
                    Log("WARN", $"Watched path does not exist: {root}");
                    continue;
                }

                var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Where(IsEligible);

                foreach (var file in files)
                {
                    if (indexed.Contains(file))
                    {
                        var diskMtime = File.GetLastWriteTimeUtc(file);
                        if (mtimes.TryGetValue(file, out var storedMtime) && diskMtime <= storedMtime)
                        {
                            skipped++;
                            continue;
                        }
                    }

                    queued++;
                    await IndexFileAsync(file);
                }
            }

            Log("INFO", $"FullIndex complete. Queued: {queued}, Skipped (unchanged): {skipped}");
        }
        catch (Exception ex)
        {
            Log("ERROR", $"FullIndexAsync crashed: {ex}");
        }
    }

    // ── Per-file indexing ─────────────────────────────────────────────────────

    private async Task<bool> IndexFileAsync(string filePath)
    {
        try
        {
            string? text = _reader.ReadText(filePath);
            if (text is null)
            {
                Log("WARN", $"Could not extract text (too large, unsupported, or parse error): {filePath}. Indexing path only.");
                text = string.Empty;
            }

            var chunks = _chunker.Chunk(text);

            string relativePath = filePath;
            foreach (var root in _config.WatchedPaths)
            {
                if (filePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    relativePath = filePath.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    break;
                }
            }
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                relativePath = Path.GetFileName(filePath);
            }
            var mtime = File.GetLastWriteTimeUtc(filePath);
            var entries = new List<VectorEntry>();

            if (chunks.Count == 0)
            {
                // No extractable text (e.g. scanned PDF, blank image) —
                // index the relative path alone so the file is still findable by name/path.
                Log("INFO", $"No text extracted, indexing path only: {filePath}");
                var nameEmbedding = await _embedder.GetEmbeddingAsync(relativePath);
                entries.Add(new VectorEntry
                {
                    FilePath     = filePath,
                    ChunkIndex   = 0,
                    ChunkText    = relativePath,
                    Embedding    = nameEmbedding,
                    LastModified = mtime,
                });
            }
            else
            {
                for (int i = 0; i < chunks.Count; i++)
                {
                    string chunkContent = $"File: {relativePath}\n\n{chunks[i]}";
                    var embedding = await _embedder.GetEmbeddingAsync(chunkContent);
                    entries.Add(new VectorEntry
                    {
                        FilePath     = filePath,
                        ChunkIndex   = i,
                        ChunkText    = chunkContent,
                        Embedding    = embedding,
                        LastModified = mtime,
                    });
                }
            }

            _store.Upsert(filePath, entries);
            return true;
        }
        catch (Exception ex)
        {
            Log("ERROR", $"IndexFileAsync failed for {filePath}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // ── Path filtering ────────────────────────────────────────────────────────

    private bool IsEligible(string filePath)
    {
        // Extension check
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (!_config.SupportedExtensions.Contains(ext))
            return false;

        // Must match at least one include pattern
        if (_includeRegexes.Count > 0 && !_includeRegexes.Any(r => r.IsMatch(filePath)))
            return false;

        // Must not match any exclude pattern
        if (_excludeRegexes.Any(r => r.IsMatch(filePath)))
            return false;

        return true;
    }

    private void CompileRegexes()
    {
        const RegexOptions opts = RegexOptions.IgnoreCase | RegexOptions.Compiled;

        foreach (var pattern in _config.IncludePathPatterns)
        {
            try { _includeRegexes.Add(new Regex(pattern, opts)); }
            catch { /* ignore malformed regex */ }
        }

        foreach (var pattern in _config.ExcludePathPatterns)
        {
            try { _excludeRegexes.Add(new Regex(pattern, opts)); }
            catch { /* ignore malformed regex */ }
        }
    }

    // ── File watchers ─────────────────────────────────────────────────────────

    private void StartWatchers()
    {
        foreach (var root in _config.WatchedPaths)
        {
            if (!Directory.Exists(root)) continue;

            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true,
            };

            watcher.Created += OnFileEvent;
            watcher.Changed += OnFileEvent;
            watcher.Deleted += OnFileDeleted;
            watcher.Renamed += OnFileRenamed;

            _watchers.Add(watcher);
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (Directory.Exists(e.FullPath))
        {
            if (e.ChangeType == WatcherChangeTypes.Created)
                Task.Run(() => IndexDirectoryAsync(e.FullPath));
            return;
        }

        if (!IsEligible(e.FullPath)) return;
        ScheduleReindex(e.FullPath);
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        _store.Remove(e.FullPath);
        _store.RemoveDirectory(e.FullPath);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (Directory.Exists(e.FullPath))
        {
            // Directory renamed/moved: remove old paths, index new location
            Log("INFO", $"Directory renamed: {e.OldFullPath} → {e.FullPath}");
            _store.RemoveDirectory(e.OldFullPath);
            Task.Run(() => IndexDirectoryAsync(e.FullPath));
        }
        else
        {
            // File renamed/moved: remove old entry, index new path if eligible
            Log("INFO", $"File renamed: {e.OldFullPath} → {e.FullPath}");
            _store.Remove(e.OldFullPath);

            if (IsEligible(e.FullPath))
            {
                // Poll until the OS grants read access — fires the instant
                // the file is fully available, no arbitrary sleep needed.
                Task.Run(async () =>
                {
                    if (!await WaitUntilReadableAsync(e.FullPath))
                    {
                        Log("WARN", $"File never became readable after rename: {e.FullPath}");
                        return;
                    }
                    bool ok = await IndexFileAsync(e.FullPath);
                    Log("INFO", ok
                        ? $"Indexed renamed file: {e.FullPath}"
                        : $"Could not index renamed file (no extractable text or read error): {e.FullPath}");
                });
            }
            else
            {
                Log("INFO", $"Renamed file not eligible, skipping index: {e.FullPath}");
            }
        }
    }

    private async Task IndexDirectoryAsync(string dirPath)
    {
        if (!Directory.Exists(dirPath)) return;
        
        try
        {
            var files = Directory.EnumerateFiles(dirPath, "*", SearchOption.AllDirectories)
                .Where(IsEligible);

            foreach (var file in files)
            {
                await IndexFileAsync(file);
            }
        }
        catch { } // Ignore access denied on enumerating dirs
    }

    /// <summary>
    /// Debounces rapid save events — waits <see cref="DebounceMs"/> ms of silence
    /// before actually re-indexing the file.
    /// </summary>
    private void ScheduleReindex(string filePath)
    {
        lock (_debounceLock)
        {
            if (_debounceTimers.TryGetValue(filePath, out var existing))
                existing.Change(DebounceMs, Timeout.Infinite);
            else
                _debounceTimers[filePath] = new Timer(_ =>
                {
                    lock (_debounceLock) _debounceTimers.Remove(filePath);
                    Task.Run(() => IndexFileAsync(filePath));
                }, null, DebounceMs, Timeout.Infinite);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Polls <paramref name="filePath"/> until the OS grants shared-read access,
    /// confirming the file is fully written/moved. Returns false if the file
    /// never becomes readable within the timeout.
    /// </summary>
    private static async Task<bool> WaitUntilReadableAsync(
        string filePath, int maxAttempts = 20, int pollIntervalMs = 100)
    {
        for (int i = 0; i < maxAttempts; i++)
        {
            try
            {
                using var fs = new FileStream(
                    filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return true; // file is open and accessible — proceed immediately
            }
            catch (IOException)
            {
                // Still locked by the OS — wait one poll interval and retry
                await Task.Delay(pollIntervalMs);
            }
            catch
            {
                return false; // non-IO error (e.g. file deleted) — give up
            }
        }
        return false; // timed out (maxAttempts * pollIntervalMs = 2s default)
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();

        lock (_debounceLock)
        {
            foreach (var t in _debounceTimers.Values) t.Dispose();
            _debounceTimers.Clear();
        }

        if (_embedder is IDisposable d)
            d.Dispose();
    }
}
