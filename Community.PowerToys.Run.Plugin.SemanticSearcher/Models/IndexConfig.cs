using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Community.PowerToys.Run.Plugin.SemanticSearcher.Models;

/// <summary>
/// Which embedding provider to use.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EmbeddingProviderType
{
    /// <summary>Local ONNX model — fully offline, no setup required.</summary>
    Onnx,

    /// <summary>Ollama running locally (native /api/embeddings endpoint).</summary>
    Ollama,

    /// <summary>Any OpenAI-compatible /v1/embeddings endpoint (OpenAI, Ollama /v1, LM Studio, etc.).</summary>
    OpenAICompatible,
}

/// <summary>
/// Top-level configuration persisted to disk as JSON.
/// </summary>
public class IndexConfig
{
    // ── Watched locations ────────────────────────────────────────────────────

    /// <summary>
    /// Root directories to watch and index. Defaults to the user's Desktop.
    /// </summary>
    public List<string> WatchedPaths { get; set; } =
    [
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop),
    ];

    // ── Path filtering (regex) ───────────────────────────────────────────────

    /// <summary>
    /// Regex patterns applied to the full file path.
    /// A file is indexed only when it matches AT LEAST ONE include pattern.
    /// Default: match everything.
    /// </summary>
    public List<string> IncludePathPatterns { get; set; } = [".*"];

    /// <summary>
    /// Regex patterns applied to the full file path.
    /// A file is skipped when it matches ANY exclude pattern.
    /// Evaluated AFTER include patterns.
    /// </summary>
    public List<string> ExcludePathPatterns { get; set; } =
    [
        @"\\\.git\\",
        @"\\node_modules\\",
        @"\\bin\\",
        @"\\obj\\",
        @"\\\.vs\\",
        @"\\__pycache__\\",
    ];

    // ── File type filtering ──────────────────────────────────────────────────

    /// <summary>
    /// File extensions (lowercase, with leading dot) that are eligible for indexing.
    /// </summary>
    public List<string> SupportedExtensions { get; set; } =
    [
        ".txt", ".md", ".markdown",
        ".cs", ".py", ".js", ".ts", ".go", ".rs", ".java", ".cpp", ".c", ".h",
        ".json", ".xml", ".yaml", ".yml", ".toml", ".ini", ".env",
        ".html", ".css", ".sql",
        ".log",
    ];

    // ── Chunking ─────────────────────────────────────────────────────────────

    /// <summary>Maximum characters per chunk.</summary>
    public int ChunkSize { get; set; } = 512;

    /// <summary>Character overlap between consecutive chunks to preserve context.</summary>
    public int ChunkOverlap { get; set; } = 64;

    /// <summary>Files larger than this (in MB) are skipped entirely.</summary>
    public int MaxFileSizeMB { get; set; } = 10;

    // ── Embedding provider ───────────────────────────────────────────────────

    public EmbeddingProviderType ProviderType { get; set; } = EmbeddingProviderType.Onnx;

    /// <summary>Path to the ONNX model file. Defaults to the bundled all-MiniLM-L6-v2 model.</summary>
    public string OnnxModelPath { get; set; } = "Models/all-MiniLM-L6-v2.onnx";

    /// <summary>Ollama base URL.</summary>
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>Ollama model name (e.g. nomic-embed-text, mxbai-embed-large).</summary>
    public string OllamaModel { get; set; } = "nomic-embed-text";

    /// <summary>Base URL for OpenAI-compatible providers (OpenAI, LM Studio, Ollama /v1, etc.).</summary>
    public string OpenAIBaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>API key for OpenAI-compatible providers. Leave empty for local servers.</summary>
    public string OpenAIApiKey { get; set; } = string.Empty;

    /// <summary>Model name for OpenAI-compatible providers.</summary>
    public string OpenAIModel { get; set; } = "text-embedding-3-small";

    // ── Search ───────────────────────────────────────────────────────────────

    /// <summary>Number of top results to return per query.</summary>
    public int TopK { get; set; } = 7;

    /// <summary>Minimum cosine similarity score (0–1) for a result to be surfaced.</summary>
    public float MinSimilarityScore { get; set; } = 0.25f;
}
