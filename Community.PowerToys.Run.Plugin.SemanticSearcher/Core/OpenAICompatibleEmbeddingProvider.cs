using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Community.PowerToys.Run.Plugin.SemanticSearcher.Core;

/// <summary>
/// Embedding provider that calls any OpenAI-compatible <c>/v1/embeddings</c> endpoint.
/// Works with: OpenAI, Ollama (/v1), LM Studio, LocalAI, etc.
/// </summary>
public sealed class OpenAICompatibleEmbeddingProvider : IEmbeddingProvider
{
    public string ProviderName => $"OpenAI-compatible ({_baseUrl}, {_model})";
    public int Dimensions { get; private set; } = -1;

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _model;

    public OpenAICompatibleEmbeddingProvider(string baseUrl, string model, string apiKey = "")
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
    }

    /// <inheritdoc/>
    public async Task<float[]> GetEmbeddingAsync(string text)
    {
        var request = new { input = text, model = _model };
        var response = await _http.PostAsJsonAsync($"{_baseUrl}/v1/embeddings", request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<OpenAIEmbeddingResponse>()
                   ?? throw new InvalidOperationException("Empty response from embedding endpoint.");

        var embedding = body.Data[0].Embedding;

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

    // ── Response DTOs ────────────────────────────────────────────────────────

    private sealed class OpenAIEmbeddingResponse
    {
        [JsonPropertyName("data")]
        public EmbeddingData[] Data { get; set; } = [];
    }

    private sealed class EmbeddingData
    {
        [JsonPropertyName("embedding")]
        public float[] Embedding { get; set; } = [];
    }
}
