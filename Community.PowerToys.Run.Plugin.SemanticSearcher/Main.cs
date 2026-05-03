using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Community.PowerToys.Run.Plugin.SemanticSearcher.Core;
using Community.PowerToys.Run.Plugin.SemanticSearcher.Models;
using ManagedCommon;
using Wox.Plugin;

namespace Community.PowerToys.Run.Plugin.SemanticSearcher;

/// <summary>
/// Main entry point for the DocumentIndexer PowerToys Run plugin.
/// Wires up the embedding provider → IndexManager and handles queries.
/// </summary>
public class Main : IPlugin, IContextMenu, IDisposable
{
    /// <summary>ID of the plugin.</summary>
    public static string PluginID => "5A8D2A6B39C149F48B27D51996E2E109";

    public string Name => "Semantic Searcher";
    public string Description => "Semantic search over your files using AI embeddings.";

    private PluginInitContext? _context;
    private string _iconPath = string.Empty;
    private bool _disposed;

    private IndexManager? _indexManager;
    private IndexConfig _config = new();

    // ── IPlugin ───────────────────────────────────────────────────────────────

    public void Init(PluginInitContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _context.API.ThemeChanged += OnThemeChanged;
        UpdateIconPath(_context.API.GetCurrentTheme());

        InitIndexManager();
    }

    public List<Result> Query(Query query)
    {
        if (_indexManager is null || string.IsNullOrWhiteSpace(query.Search))
            return [];

        var results = _indexManager.Search(query.Search);

        if (results.Count == 0)
        {
            return
            [
                new Result
                {
                    Title = "No results found",
                    SubTitle = "Try rephrasing your query or wait for indexing to complete.",
                    IcoPath = _iconPath,
                    QueryTextDisplay = query.Search,
                    Action = _ => false,
                },
            ];
        }

        var list = new List<Result>(results.Count);
        foreach (var r in results)
        {
            var result = r; // capture
            list.Add(new Result
            {
                Title = Path.GetFileName(result.FilePath),
                SubTitle = $"[{result.Score:P0}] {result.MatchSnippet}",
                QueryTextDisplay = query.Search,
                IcoPath = _iconPath,
                ToolTipData = new ToolTipData(result.FilePath, result.MatchSnippet),
                ContextData = result,
                Action = _ =>
                {
                    OpenFile(result.FilePath);
                    return true;
                },
            });
        }

        return list;
    }

    // ── IContextMenu ──────────────────────────────────────────────────────────

    public List<ContextMenuResult> LoadContextMenus(Result selectedResult)
    {
        if (selectedResult.ContextData is not SearchResult r)
            return [];

        return
        [
            new ContextMenuResult
            {
                PluginName = Name,
                Title = "Open file (Enter)",
                Glyph = "\xE8A7",  // OpenFile
                FontFamily = "Segoe Fluent Icons,Segoe MDL2 Assets",
                Action = _ => { OpenFile(r.FilePath); return true; },
            },
            new ContextMenuResult
            {
                PluginName = Name,
                Title = "Open containing folder (Ctrl+Enter)",
                Glyph = "\xED25",  // FolderOpen
                FontFamily = "Segoe Fluent Icons,Segoe MDL2 Assets",
                AcceleratorKey = Key.Enter,
                AcceleratorModifiers = ModifierKeys.Control,
                Action = _ => { OpenFolder(r.FilePath); return true; },
            },
            new ContextMenuResult
            {
                PluginName = Name,
                Title = "Copy path (Ctrl+C)",
                Glyph = "\xE8C8",  // Copy
                FontFamily = "Segoe Fluent Icons,Segoe MDL2 Assets",
                AcceleratorKey = Key.C,
                AcceleratorModifiers = ModifierKeys.Control,
                Action = _ =>
                {
                    System.Windows.Clipboard.SetDataObject(r.FilePath);
                    return true;
                },
            },
        ];
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed || !disposing) return;

        if (_context?.API != null)
            _context.API.ThemeChanged -= OnThemeChanged;

        _indexManager?.Dispose();
        _disposed = true;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void InitIndexManager()
    {
        try
        {
            // Load (or create default) config
            _config = LoadConfig();

            // Build the embedding provider based on config
            IEmbeddingProvider provider = _config.ProviderType switch
            {
                EmbeddingProviderType.Ollama =>
                    new OllamaEmbeddingProvider(_config.OllamaBaseUrl, _config.OllamaModel),

                EmbeddingProviderType.OpenAICompatible =>
                    new OpenAICompatibleEmbeddingProvider(
                        _config.OpenAIBaseUrl, _config.OpenAIModel, _config.OpenAIApiKey),

                _ => // Default: ONNX
                    new OnnxEmbeddingProvider(ResolveModelPath(_config.OnnxModelPath)),
            };

            string storePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SemanticSearcher", "index.db");

            _indexManager = new IndexManager(_config, provider, storePath);
            _indexManager.Start();
        }
        catch (Exception ex)
        {
            // Log and continue — plugin will return empty results gracefully
            System.Diagnostics.Debug.WriteLine($"[SemanticSearcher] Init failed: {ex}");
        }
    }

    private static IndexConfig LoadConfig()
    {
        // 1. Look next to the plugin DLL (copied from repo on build — edit this one during dev)
        string pluginDir   = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!;
        string pluginLocal = Path.Combine(pluginDir, "config.json");

        // 2. Fallback: LocalAppData\SemanticSearcher\config.json
        string appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SemanticSearcher", "config.json");

        string? configPath = File.Exists(pluginLocal) ? pluginLocal
                           : File.Exists(appDataPath)  ? appDataPath
                           : null;

        if (configPath is not null)
        {
            try
            {
                string json = File.ReadAllText(configPath);
                return System.Text.Json.JsonSerializer.Deserialize<IndexConfig>(json) ?? new IndexConfig();
            }
            catch { /* Fall through to defaults */ }
        }

        // Write defaults to LocalAppData so the user has something to edit
        var defaults = new IndexConfig();
        Directory.CreateDirectory(Path.GetDirectoryName(appDataPath)!);
        File.WriteAllText(appDataPath,
            System.Text.Json.JsonSerializer.Serialize(defaults,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        return defaults;
    }

    private string ResolveModelPath(string configuredPath)
    {
        // If the path is relative, resolve it next to the plugin DLL
        if (!Path.IsPathRooted(configuredPath))
        {
            string pluginDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location)!;
            return Path.Combine(pluginDir, configuredPath);
        }

        return configuredPath;
    }

    private static void OpenFile(string filePath)
    {
        try { Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true }); }
        catch { }
    }

    private static void OpenFolder(string filePath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (dir is not null)
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"")
                    { UseShellExecute = true });
        }
        catch { }
    }

    private void UpdateIconPath(Theme theme) =>
        _iconPath = theme == Theme.Light || theme == Theme.HighContrastWhite
            ? "Images/SemanticSearcher.light.png"
            : "Images/SemanticSearcher.dark.png";

    private void OnThemeChanged(Theme currentTheme, Theme newTheme) =>
        UpdateIconPath(newTheme);
}
