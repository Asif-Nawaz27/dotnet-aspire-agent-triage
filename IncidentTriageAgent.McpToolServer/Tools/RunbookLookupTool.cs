// DotNetAspireTriageAgent.McpToolServer/Tools/RunbookLookupTool.cs
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace DotNetAspireTriageAgent.McpToolServer.Tools;

/// <summary>Internal DTO for a runbook excerpt returned to callers.</summary>
internal sealed record RunbookExcerptDto(string Title, string Content, double Score);

/// <summary>MCP tool that performs vector similarity search against the Qdrant runbook collection.</summary>
[McpServerToolType]
public sealed class RunbookLookupTool(
    QdrantClient qdrantClient,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    ILogger<RunbookLookupTool> logger)
{
    private static readonly ActivitySource ActivitySource = new("DotNetAspireTriageAgent.McpToolServer");

    internal const string CollectionName = "runbooks";
    private const int TopK = 3;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Searches the Qdrant runbook collection for the top-3 most relevant excerpts for the given alert category.
    /// Returns an empty array if Qdrant is unreachable.
    /// </summary>
    [McpServerTool]
    [Description("Searches the vector store for the top-3 runbook excerpts most relevant to the alert category. Returns a JSON array of RunbookExcerpt objects.")]
    public async Task<string> LookupRunbook(
        [Description("Alert category string, e.g. 'cpu_spike' or 'memory_oom'")] string alertCategory,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("triage.runbook_lookup", ActivityKind.Internal);
        activity?.SetTag("alert.category", alertCategory);

        try
        {
            var embedding = await embeddingGenerator.GenerateEmbeddingAsync(alertCategory, cancellationToken: cancellationToken);
            var queryVector = embedding.Vector.ToArray();

            var results = await qdrantClient.SearchAsync(
                collectionName: CollectionName,
                vector: queryVector,
                limit: (ulong)TopK,
                cancellationToken: cancellationToken);

            // C# 14: collection expression with spread to merge results with fallback
            RunbookExcerptDto[] excerpts = results.Count > 0
                ? [.. results.Select(r => new RunbookExcerptDto(
                    Title: r.Payload.GetValueOrDefault("title")?.StringValue ?? "Untitled",
                    Content: r.Payload.GetValueOrDefault("content")?.StringValue ?? "",
                    Score: r.Score))]
                : [new RunbookExcerptDto("General Runbook", "Follow standard incident response procedures.", 0.0)];

            activity?.SetTag("runbook.results_count", excerpts.Length);

            logger.LogInformation("Runbook lookup for '{Category}' returned {Count} excerpt(s)", alertCategory, excerpts.Length);

            return JsonSerializer.Serialize(excerpts, JsonOptions);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("LookupRunbook cancelled for category {Category}", alertCategory);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Qdrant unavailable for category '{Category}' — returning empty runbook list", alertCategory);
            activity?.SetStatus(ActivityStatusCode.Error, "Qdrant unavailable");
            return "[]";
        }
    }
}
