namespace Community.PowerToys.Run.Plugin.SemanticSearcher.Models;

/// <summary>
/// A single search result returned to PowerToys Run.
/// </summary>
public class SearchResult
{
    /// <summary>Absolute path to the matched file.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>The chunk text that best matched the query (used as subtitle).</summary>
    public string MatchSnippet { get; set; } = string.Empty;

    /// <summary>Cosine similarity score in the range [0, 1].</summary>
    public float Score { get; set; }

    /// <summary>Zero-based chunk index within the file for the best matching chunk.</summary>
    public int ChunkIndex { get; set; }
}
