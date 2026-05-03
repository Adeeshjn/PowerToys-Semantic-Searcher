using System.Threading.Tasks;

namespace Community.PowerToys.Run.Plugin.SemanticSearcher.Core;

/// <summary>
/// Common contract for all embedding providers.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>Dimensionality of vectors produced by this provider.</summary>
    int Dimensions { get; }

    /// <summary>Human-readable provider name shown in settings / logs.</summary>
    string ProviderName { get; }

    /// <summary>
    /// Returns a normalised (unit-length) embedding vector for <paramref name="text"/>.
    /// </summary>
    Task<float[]> GetEmbeddingAsync(string text);
}
