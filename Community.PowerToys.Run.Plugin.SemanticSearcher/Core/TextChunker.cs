using System.Collections.Generic;

namespace Community.PowerToys.Run.Plugin.SemanticSearcher.Core;

/// <summary>
/// Splits raw text into overlapping chunks suitable for embedding.
/// </summary>
public sealed class TextChunker
{
    private readonly int _chunkSize;
    private readonly int _overlap;

    public TextChunker(int chunkSize = 512, int overlap = 64)
    {
        _chunkSize = chunkSize;
        _overlap = overlap;
    }

    /// <summary>
    /// Splits <paramref name="text"/> into overlapping character-level chunks.
    /// Each chunk is at most <c>ChunkSize</c> characters, with <c>Overlap</c>
    /// characters shared with the previous chunk to preserve context at boundaries.
    /// </summary>
    public IReadOnlyList<string> Chunk(string text)
    {
        var chunks = new List<string>();

        if (string.IsNullOrWhiteSpace(text))
            return chunks;

        // Normalise line endings and collapse excessive whitespace runs.
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');

        int step = _chunkSize - _overlap;
        if (step <= 0)
            step = _chunkSize; // guard: overlap ≥ chunkSize

        int start = 0;
        while (start < text.Length)
        {
            int length = System.Math.Min(_chunkSize, text.Length - start);
            string chunk = text.Substring(start, length).Trim();

            if (!string.IsNullOrEmpty(chunk))
                chunks.Add(chunk);

            start += step;
        }

        return chunks;
    }
}
