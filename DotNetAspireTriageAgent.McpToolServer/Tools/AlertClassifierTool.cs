// DotNetAspireTriageAgent.McpToolServer/Tools/AlertClassifierTool.cs
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace DotNetAspireTriageAgent.McpToolServer.Tools;

/// <summary>Internal DTO matching the AlertClassification shape returned to callers.</summary>
internal sealed record AlertClassificationDto(string Severity, string Category, double Confidence);

/// <summary>MCP tool that classifies an alert payload into severity, category, and confidence.</summary>
[McpServerToolType]
public sealed class AlertClassifierTool(IChatClient chatClient, ILogger<AlertClassifierTool> logger)
{
    private static readonly ActivitySource ActivitySource = new("DotNetAspireTriageAgent.McpToolServer");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Classifies a raw alert payload and returns severity, category, and confidence as JSON.
    /// Uses json_object response mode — supported by all Groq models.
    /// The system prompt enforces the exact JSON shape; ParseJson on the caller side handles any prose.
    /// </summary>
    [McpServerTool]
    [Description("Classifies a raw alert payload into severity (Critical|High|Medium|Low), category, and confidence score. Returns AlertClassification JSON.")]
    public async Task<string> ClassifyAlert(
        [Description("Raw alert payload as plain text or JSON")] string alertPayload,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("triage.classify", ActivityKind.Internal);
        activity?.SetTag("alert.payload.length", alertPayload.Length);

        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System,
                    """
                    You are an SRE alert classifier. Analyse the alert and respond ONLY with valid JSON — no prose, no markdown.
                    Severity must be exactly one of: Critical, High, Medium, Low.
                    Category must be a concise snake_case noun phrase (e.g. "cpu_spike", "memory_oom", "disk_io").
                    Confidence must be a decimal between 0.0 and 1.0.
                    Output format (no other text): {"severity":"...","category":"...","confidence":0.0}
                    """),
                new(ChatRole.User, alertPayload)
            };

            // ChatResponseFormat.Json → response_format:{type:"json_object"} — supported by all Groq models.
            // ForJsonSchema (json_schema strict mode) is NOT supported by llama models on Groq
            // even though the docs list them; Groq rejects it with HTTP 400 invalid_request_error.
            var options = new ChatOptions
            {
                ResponseFormat = ChatResponseFormat.Json
            };

            // Microsoft.Extensions.AI 9.7: GetResponseAsync replaces CompleteAsync
            var response = await chatClient.GetResponseAsync(messages, options, cancellationToken);
            var rawJson = response.Text ?? "{}";

            var classification = JsonSerializer.Deserialize<AlertClassificationDto>(rawJson, JsonOptions)
                ?? new AlertClassificationDto("Low", "unknown", 0.5);

            activity?.SetTag("classification.severity", classification.Severity);
            activity?.SetTag("classification.confidence", classification.Confidence);

            if (response.Usage is { } usage)
            {
                activity?.SetTag("ai.usage.prompt_tokens", usage.InputTokenCount);
                activity?.SetTag("ai.usage.completion_tokens", usage.OutputTokenCount);
            }

            logger.LogInformation(
                "Alert classified: severity={Severity} category={Category} confidence={Confidence:F2}",
                classification.Severity, classification.Category, classification.Confidence);

            return JsonSerializer.Serialize(classification, JsonOptions);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("ClassifyAlert cancelled");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Alert classification failed — returning Low/unknown fallback");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return JsonSerializer.Serialize(new AlertClassificationDto("Low", "unknown", 0.0), JsonOptions);
        }
    }
}
