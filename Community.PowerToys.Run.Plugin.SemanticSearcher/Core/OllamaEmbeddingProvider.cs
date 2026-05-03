using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Community.PowerToys.Run.Plugin.SemanticSearcher.Core;

/// <summary>
/// Embedding provider that calls Ollama's native <c>/api/embeddings</c> endpoint.
/// </summary>
public sealed class OllamaEmbeddingProvider : IEmbeddingProvider
{
    public string ProviderName => $"Ollama ({_model})";

    /// <summary>
    /// Dimensions are determined at runtime from the first embedding call.
    /// Set to -1 until the first call resolves the actual dimension.
    /// </summary>
    public int Dimensions { get; private set; } = -1;

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _model;

    public OllamaEmbeddingProvider(string baseUrl = "http://localhost:11434", string model = "nomic-embed-text")
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <inheritdoc/>
    public async Task<float[]> GetEmbeddingAsync(string text)
    {
        var request = new { model = _model, prompt = text };
        var response = await _http.PostAsJsonAsync($"{_baseUrl}/api/embeddings", request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<OllamaEmbeddingResponse>()
                   ?? throw new InvalidOperationException("Empty response from Ollama.");

        var embedding = body.Embedding;
        if (Dimensions == -1)
            Dimensions = embedding.Length;

        Normalize(embedding);
        return embedding;
    }

    private static void Normalize(float[] v)
    {
        float norm = 0f;
        foreach (var x in v) norm += x * x;
        norm = MathF.Sqrt(norm);
        if (norm > 1e-8f)
            for (int i = 0; i < v.Length; i++)
                v[i] /= norm;
    }

    private sealed class OllamaEmbeddingResponse
    {
        [JsonPropertyName("embedding")]
        public float[] Embedding { get; set; } = [];
    }
}
