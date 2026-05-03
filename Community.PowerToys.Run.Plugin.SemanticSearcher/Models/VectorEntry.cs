using System;

namespace Community.PowerToys.Run.Plugin.SemanticSearcher.Models;

/// <summary>
/// A single chunk of text extracted from a file, with its embedding vector.
/// </summary>
public class VectorEntry
{
    /// <summary>Absolute path to the source file.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Zero-based index of this chunk within the file.</summary>
    public int ChunkIndex { get; set; }

    /// <summary>
    /// The raw text of this chunk. Stored for display as a subtitle in results.
    /// </summary>
    public string ChunkText { get; set; } = string.Empty;

    /// <summary>The embedding vector produced by the embedding provider.</summary>
    public float[] Embedding { get; set; } = [];

    /// <summary>Last-modified timestamp of the file when it was indexed.</summary>
    public DateTime LastModified { get; set; }
}
