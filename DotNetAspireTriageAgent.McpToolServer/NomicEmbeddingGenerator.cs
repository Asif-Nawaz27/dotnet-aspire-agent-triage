// DotNetAspireTriageAgent.McpToolServer/NomicEmbeddingGenerator.cs
// Nomic AI's native embedding endpoint is POST /v1/embedding/text (not /v1/embeddings).
// The OpenAI SDK client calls /v1/embeddings and gets HTTP 404 — so we call the native endpoint directly.
using Microsoft.Extensions.AI;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace DotNetAspireTriageAgent.McpToolServer;

/// <summary>
/// <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> that calls Nomic AI's native
/// <c>POST /v1/embedding/text</c> endpoint.  The OpenAI-compatible SDK calls
/// <c>/v1/embeddings</c> which returns HTTP 404 on Nomic's API.
/// </summary>
internal sealed class NomicEmbeddingGenerator(
    IHttpClientFactory httpClientFactory,
    string model,
    ILogger<NomicEmbeddingGenerator> logger)
    : IEmbeddingGenerator<string, Embedding<float>>
{
    // ── IEmbeddingGenerator metadata ─────────────────────────────────────────
    public EmbeddingGeneratorMetadata Metadata { get; } =
        new("nomic", providerUri: null, defaultModelId: model);

    // ── Core generate method ─────────────────────────────────────────────────
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var texts = values.ToList();

        logger.LogDebug("NomicEmbeddingGenerator: generating embeddings for {Count} text(s)", texts.Count);

        var client = httpClientFactory.CreateClient("nomic");

        var requestBody = new NomicEmbedRequest(model, texts, "search_document");

        using var response = await client.PostAsJsonAsync(
            "embedding/text", requestBody,
            NomicJsonContext.Default.NomicEmbedRequest,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "Nomic embedding request failed — status={Status} body={Body}",
                (int)response.StatusCode, body);
            response.EnsureSuccessStatusCode();
        }

        var result = await response.Content.ReadFromJsonAsync(
            NomicJsonContext.Default.NomicEmbedResponse,
            cancellationToken)
            ?? throw new InvalidOperationException("Nomic returned an empty response body");

        logger.LogDebug(
            "NomicEmbeddingGenerator: received {Count} embedding vector(s)", result.Embeddings.Count);

        return new GeneratedEmbeddings<Embedding<float>>(
            result.Embeddings.Select(v => new Embedding<float>(v)).ToList());
    }

    // ── IEmbeddingGenerator service locator (unused — return null) ────────────
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    // ── IDisposable (nothing to dispose) ─────────────────────────────────────
    public void Dispose() { }
}

// ── JSON contracts (file-scoped so the source generator's partial class works) ──

internal sealed record NomicEmbedRequest(
    [property: JsonPropertyName("model")]     string       Model,
    [property: JsonPropertyName("texts")]     List<string> Texts,
    [property: JsonPropertyName("task_type")] string       TaskType);

internal sealed record NomicEmbedResponse(
    [property: JsonPropertyName("embeddings")] List<float[]> Embeddings);

// Source-generated JSON serializer context — must be at file/namespace scope (not nested)
// so the source generator can emit the required partial class members.
[JsonSerializable(typeof(NomicEmbedRequest))]
[JsonSerializable(typeof(NomicEmbedResponse))]
internal sealed partial class NomicJsonContext : JsonSerializerContext { }
