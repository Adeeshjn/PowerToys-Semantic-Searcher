using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Community.PowerToys.Run.Plugin.SemanticSearcher.Core;
using Community.PowerToys.Run.Plugin.SemanticSearcher.Models;

// ── Paths ─────────────────────────────────────────────────────────────────────
string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
string pluginData   = Path.Combine(localAppData, "SemanticSearcher");
string indexPath    = Path.Combine(pluginData, "index.db");

// Config: prefer the active plugin config in PowerToys, then fall back to LocalAppData
string ptPluginConfig = Path.Combine(localAppData, "PowerToys", "RunPlugins", "SemanticSearcher", "config.json");
string configPath  = File.Exists(ptPluginConfig) ? ptPluginConfig : Path.Combine(pluginData, "config.json");

// ── Parse CLI args ────────────────────────────────────────────────────────────
string command = args.Length > 0 ? args[0].ToLower() : "help";

switch (command)
{
    case "index":
        await RunIndexAsync(configPath, indexPath);
        break;

    case "inspect":
        InspectIndex(indexPath);
        break;

    case "search":
        if (args.Length < 2) { Console.WriteLine("Usage: IndexerTool search <query>"); break; }
        string query = string.Join(" ", args[1..]);
        await RunSearchAsync(configPath, indexPath, query);
        break;

    case "list":
        ListEligibleFiles(configPath);
        break;

    case "clear":
        if (File.Exists(indexPath))
        {
            try
            {
                // Connect via SQLite and drop contents so we don't hit OS file locks 
                // from PowerToys holding the db file open.
                var store = new VectorStore(indexPath);
                store.ClearSync();
                Console.WriteLine("Index database cleared.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to clear index: {ex.Message}");
            }
        }
        else Console.WriteLine("Index does not exist.");
        break;

    default:
        PrintHelp();
        break;
}

// ── Commands ──────────────────────────────────────────────────────────────────

static async Task RunIndexAsync(string configPath, string indexPath)
{
    var config = LoadConfig(configPath);
    var provider = BuildProvider(config);

    Console.WriteLine($"Provider : {provider.ProviderName}");
    Console.WriteLine($"Watching : {string.Join(", ", config.WatchedPaths)}");
    Console.WriteLine($"Index at : {indexPath}");
    Console.WriteLine();

    var reader  = new DocumentReader(config);
    var chunker = new TextChunker(config.ChunkSize, config.ChunkOverlap);
    var store   = new VectorStore(indexPath);

    int filesDone = 0, chunksTotal = 0, filesSkipped = 0;

    var includeRegexes = config.IncludePathPatterns.Select(p => new System.Text.RegularExpressions.Regex(p, System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();
    var excludeRegexes = config.ExcludePathPatterns.Select(p => new System.Text.RegularExpressions.Regex(p, System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();

    var eligibleFiles = config.WatchedPaths
        .Where(Directory.Exists)
        .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        .Where(f => config.SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
        .Where(f => includeRegexes.Count == 0 || includeRegexes.Any(r => r.IsMatch(f)))
        .Where(f => !excludeRegexes.Any(r => r.IsMatch(f)))
        .ToList();

    Console.WriteLine($"Found {eligibleFiles.Count} eligible files.\n");

    var skipReasons = new List<(string file, string reason)>();

    foreach (var file in eligibleFiles)
    {
        string? text = reader.ReadText(file);
        if (text is null)
        {
            skipReasons.Add((file, "could not read / unsupported format"));
            filesSkipped++;
            continue;
        }

        var chunks = chunker.Chunk(text);

        var entries = new List<VectorEntry>();
        int fileIndex = eligibleFiles.IndexOf(file) + 1;
        string fileName = Path.GetFileName(file);

        if (chunks.Count == 0)
        {
            // No extractable text — embed the filename so the file is still findable by name.
            Console.Write($"[{fileIndex,4}] {fileName,40} — name-only  ");
            try
            {
                var nameEmbedding = await provider.GetEmbeddingAsync(fileName);
                entries.Add(new VectorEntry
                {
                    FilePath     = file,
                    ChunkIndex   = 0,
                    ChunkText    = fileName,
                    Embedding    = nameEmbedding,
                    LastModified = File.GetLastWriteTimeUtc(file),
                });
                store.Upsert(file, entries);
                filesDone++;
                chunksTotal++;
                Console.WriteLine($"\r[{filesDone,4}] {fileName,40} — name-only  ✓");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n  !! FAILED (name-only) {fileName}: {ex.Message}");
                filesSkipped++;
            }
            continue;
        }

        // Print filename + chunk count BEFORE embedding so hangs are visible
        Console.Write($"[{fileIndex,4}] {fileName,40} — {chunks.Count} chunk(s)  ");
        try
        {
            for (int i = 0; i < chunks.Count; i++)
            {
                Console.Write($"\r[{fileIndex,4}] {fileName,40} — chunk {i + 1}/{chunks.Count}  ");
                var embedding = await provider.GetEmbeddingAsync(chunks[i]);
                entries.Add(new VectorEntry
                {
                    FilePath     = file,
                    ChunkIndex   = i,
                    ChunkText    = chunks[i],
                    Embedding    = embedding,
                    LastModified = File.GetLastWriteTimeUtc(file),
                });
            }

            store.Upsert(file, entries);
            filesDone++;
            chunksTotal += chunks.Count;
            Console.WriteLine($"\r[{filesDone,4}] {fileName,40} — {chunks.Count} chunk(s)  ✓");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n  !! FAILED {fileName}: {ex.Message}");
            filesSkipped++;
        }
    }

    Console.WriteLine();
    Console.WriteLine($"Done. Indexed {filesDone} files ({chunksTotal} chunks). Skipped {filesSkipped}.");

    if (skipReasons.Count > 0)
    {
        Console.WriteLine($"\n⚠  Skipped files ({skipReasons.Count}):");
        foreach (var (skippedFile, reason) in skipReasons)
            Console.WriteLine($"   [{reason}]  {skippedFile}");
    }
}

static void InspectIndex(string indexPath)
{
    if (!File.Exists(indexPath))
    {
        Console.WriteLine("No index found at: " + indexPath);
        return;
    }

    using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={indexPath}");
    conn.Open();

    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*), COUNT(DISTINCT FilePath) FROM Chunks";
    using var reader = cmd.ExecuteReader();
    reader.Read();
    int totalChunks = reader.GetInt32(0);
    int uniqueFiles = reader.GetInt32(1);

    Console.WriteLine($"Index path : {indexPath}");
    Console.WriteLine($"Total chunks: {totalChunks}");
    Console.WriteLine($"Unique files: {uniqueFiles}");
    Console.WriteLine();
    Console.WriteLine($"{"Chunks",6}  File");
    Console.WriteLine(new string('-', 60));

    using var groupCmd = conn.CreateCommand();
    groupCmd.CommandText = "SELECT FilePath, COUNT(*) as c FROM Chunks GROUP BY FilePath ORDER BY c DESC";
    using var groupReader = groupCmd.ExecuteReader();
    while (groupReader.Read())
    {
        Console.WriteLine($"{groupReader.GetInt32(1),6}  {groupReader.GetString(0)}");
    }
}

static async Task RunSearchAsync(string configPath, string indexPath, string query)
{
    if (!File.Exists(indexPath)) { Console.WriteLine("No index found. Run: IndexerTool index"); return; }

    var config   = LoadConfig(configPath);
    var provider = BuildProvider(config);
    var store    = new VectorStore(indexPath);

    Console.WriteLine($"Query    : \"{query}\"");
    Console.WriteLine($"Provider : {provider.ProviderName}");
    Console.WriteLine($"Index    : {store.Count} chunks\n");

    var queryVec = await provider.GetEmbeddingAsync(query);
    var results  = store.Search(queryVec, config.TopK, config.MinSimilarityScore);

    if (results.Count == 0)
    {
        Console.WriteLine("No results above the similarity threshold.");
        return;
    }

    Console.WriteLine($"{"Score",6}  File");
    Console.WriteLine(new string('-', 80));
    foreach (var r in results)
    {
        Console.WriteLine($"{r.Score,6:P0}  {r.FilePath}");
        Console.WriteLine($"         {r.MatchSnippet}");
        Console.WriteLine();
    }
}

// ── Helpers ───────────────────────────────────────────────────────────────────

static IndexConfig LoadConfig(string configPath)
{
    if (File.Exists(configPath))
    {
        try { return JsonSerializer.Deserialize<IndexConfig>(File.ReadAllText(configPath)) ?? new IndexConfig(); }
        catch { }
    }
    Console.WriteLine($"[warn] Config not found at {configPath}, using defaults.");
    return new IndexConfig();
}

static IEmbeddingProvider BuildProvider(IndexConfig config) => config.ProviderType switch
{
    EmbeddingProviderType.Ollama =>
        new OllamaEmbeddingProvider(config.OllamaBaseUrl, config.OllamaModel),
    EmbeddingProviderType.OpenAICompatible =>
        new OpenAICompatibleEmbeddingProvider(config.OpenAIBaseUrl, config.OpenAIModel, config.OpenAIApiKey),
    _ =>
        new OnnxEmbeddingProvider(config.OnnxModelPath),
};

static void ListEligibleFiles(string configPath)
{
    var config = LoadConfig(configPath);

    Console.WriteLine($"Config  : {configPath}");
    Console.WriteLine($"Watching: {string.Join(", ", config.WatchedPaths)}");
    Console.WriteLine($"Exclude : {string.Join(", ", config.ExcludePathPatterns)}");
    Console.WriteLine();

    var includeRegexes = config.IncludePathPatterns
        .Select(p => new System.Text.RegularExpressions.Regex(p, System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();
    var excludeRegexes = config.ExcludePathPatterns
        .Select(p => new System.Text.RegularExpressions.Regex(p, System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();

    var eligibleFiles = config.WatchedPaths
        .Where(Directory.Exists)
        .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        .Where(f => config.SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
        .Where(f => includeRegexes.Count == 0 || includeRegexes.Any(r => r.IsMatch(f)))
        .Where(f => !excludeRegexes.Any(r => r.IsMatch(f)))
        .OrderBy(f => Path.GetExtension(f))
        .ThenBy(f => f)
        .ToList();

    Console.WriteLine($"Found {eligibleFiles.Count} eligible file(s):\n");

    var byExt = eligibleFiles.GroupBy(f => Path.GetExtension(f).ToLowerInvariant());
    foreach (var grp in byExt.OrderBy(g => g.Key))
    {
        Console.WriteLine($"  [{grp.Key,6}]  {grp.Count(),3} file(s)");
        foreach (var f in grp)
            Console.WriteLine($"             {f}");
        Console.WriteLine();
    }
}

static void PrintHelp()
{
    Console.WriteLine("""
    DocumentIndexer CLI Tool
    ════════════════════════
    Commands:
      index            Scan watched paths and index all eligible files
      list             Show all eligible files the current config picks up (no indexing)
      inspect          Show index stats and per-file chunk counts
      search <query>   Embed a query and find the closest files
      clear            Delete the index (forces full re-index next time)

    Config & index are read from:
      %AppData%\Microsoft\PowerToys\Run\Plugins\SemanticSearcher\
    """);
}
