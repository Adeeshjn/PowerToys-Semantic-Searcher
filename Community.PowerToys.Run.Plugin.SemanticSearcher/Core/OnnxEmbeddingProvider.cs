using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Community.PowerToys.Run.Plugin.SemanticSearcher.Core;

/// <summary>
/// Embedding provider that runs the all-MiniLM-L6-v2 ONNX model locally.
/// Produces 384-dimensional normalised vectors with no network calls.
/// </summary>
public sealed class OnnxEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    public int Dimensions => 384;
    public string ProviderName => "ONNX (all-MiniLM-L6-v2, offline)";

    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private bool _disposed;

    /// <param name="modelPath">
    ///   Absolute path to the <c>.onnx</c> model file.
    ///   The tokenizer vocab is expected in the same directory as <c>vocab.txt</c>.
    /// </param>
    public OnnxEmbeddingProvider(string modelPath)
    {
        var opts = new SessionOptions();
        opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

        _session = new InferenceSession(modelPath, opts);

        // BertTokenizer expects vocab.txt next to the model file
        string vocabPath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(modelPath)!, "vocab.txt");

        _tokenizer = BertTokenizer.Create(vocabPath);
    }

    /// <inheritdoc/>
    public Task<float[]> GetEmbeddingAsync(string text)
    {
        // Truncate to 512 tokens (BERT limit). The overload requires both out params.
        var encoded = _tokenizer.EncodeToIds(
            text, maxTokenCount: 512, normalizedText: out _, charsConsumed: out _);

        int seqLen = encoded.Count;

        var inputIds = new DenseTensor<long>(new[] { 1, seqLen });
        var attentionMask = new DenseTensor<long>(new[] { 1, seqLen });
        var tokenTypeIds = new DenseTensor<long>(new[] { 1, seqLen });

        for (int i = 0; i < seqLen; i++)
        {
            inputIds[0, i] = encoded[i];
            attentionMask[0, i] = 1;
            tokenTypeIds[0, i] = 0;
        }

        var inputs = new NamedOnnxValue[]
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds),
        };

        using var results = _session.Run(inputs);

        // last_hidden_state → mean-pool over sequence dimension
        var lastHiddenState = results
            .First(r => r.Name == "last_hidden_state")
            .AsEnumerable<float>()
            .ToArray();

        // Shape: [1, seqLen, 384] — reshape and mean-pool
        float[] pooled = MeanPool(lastHiddenState, seqLen, Dimensions);
        Normalize(pooled);

        return Task.FromResult(pooled);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static float[] MeanPool(float[] tensor, int seqLen, int hiddenSize)
    {
        var pooled = new float[hiddenSize];
        for (int t = 0; t < seqLen; t++)
            for (int h = 0; h < hiddenSize; h++)
                pooled[h] += tensor[t * hiddenSize + h];

        for (int h = 0; h < hiddenSize; h++)
            pooled[h] /= seqLen;

        return pooled;
    }

    private static void Normalize(float[] v)
    {
        float norm = MathF.Sqrt(v.Sum(x => x * x));
        if (norm > 1e-8f)
            for (int i = 0; i < v.Length; i++)
                v[i] /= norm;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _session.Dispose();
        _disposed = true;
    }
}
