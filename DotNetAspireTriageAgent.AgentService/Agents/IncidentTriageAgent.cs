// DotNetAspireTriageAgent.AgentService/Agents/DotNetAspireTriageAgent.cs
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DotNetAspireTriageAgent.AgentService.Filters;
using DotNetAspireTriageAgent.AgentService.Models;
using Microsoft.SemanticKernel;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DotNetAspireTriageAgent.AgentService.Agents;

/// <summary>
/// Orchestrates the five-step incident triage pipeline:
/// classify → enrich → remediate → escalate → audit.
/// MCP tools are called directly via McpClient; SK is used for LLM reasoning only.
/// </summary>
public sealed class DotNetAspireTriageAgentService(
    Kernel kernel,
    McpClient mcpClient,
    InjectionDetectionContext detectionContext,
    ILogger<DotNetAspireTriageAgentService> logger)
{
    private static readonly ActivitySource ActivitySource = new("DotNetAspireTriageAgent.AgentService");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // C# 14: field keyword — removes explicit backing field for ServiceName
    public string ServiceName
    {
        get => field ??= nameof(DotNetAspireTriageAgentService);
        private set;
    }

    /// <summary>
    /// Runs the full triage pipeline for the given alert payload and returns a
    /// <see cref="TriageResult"/> with classification, remediation, escalation, and audit.
    /// </summary>
    public async Task<TriageResult> TriageAsync(AlertPayload payload, CancellationToken cancellationToken = default)
    {
        using var rootActivity = ActivitySource.StartActivity("triage", ActivityKind.Server);
        rootActivity?.SetTag("alert.id", payload.Id);
        rootActivity?.SetTag("alert.source", payload.Source);

        detectionContext.InjectionDetected = false;
        var sw = Stopwatch.StartNew();

        // ── Step 2: Classify severity ─────────────────────────────────────────
        var classification = await ClassifyAsync(payload, cancellationToken);

        // ── Step 3: Enrich with runbook context (Critical/High only) ──────────
        RunbookExcerpt[] runbooks = [];
        if (classification.Severity is "Critical" or "High")
        {
            runbooks = await LookupRunbooksAsync(classification.Category, cancellationToken);
        }

        // ── Step 4a: Propose remediation via LLM ─────────────────────────────
        var proposal = await ProposeRemediationAsync(payload, classification, runbooks, cancellationToken);

        // ── Step 4b: Escalate via MCP tool ────────────────────────────────────
        var escalation = await EscalateAsync(payload, classification, cancellationToken);

        // ── Step 5: Write audit record ────────────────────────────────────────
        sw.Stop();
        var auditEntry = new AuditEntry(
            payload.Id, classification.Severity, proposal.ActionSteps,
            escalation.Escalated, DateTime.UtcNow, sw.ElapsedMilliseconds);

        await WriteAuditAsync(auditEntry, classification, proposal, escalation, cancellationToken);

        rootActivity?.SetTag("triage.severity", classification.Severity);
        rootActivity?.SetTag("triage.escalated", escalation.Escalated);
        rootActivity?.SetTag("triage.elapsed_ms", sw.ElapsedMilliseconds);

        return new TriageResult(
            classification, proposal, escalation, auditEntry,
            detectionContext.InjectionDetected);
    }

    // ── Private step implementations ──────────────────────────────────────────

    private async Task<AlertClassification> ClassifyAsync(AlertPayload payload, CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity("triage.classify", ActivityKind.Internal);

        var toolResult = await CallMcpToolAsync("ClassifyAlert",
            new Dictionary<string, object?> { ["alertPayload"] = payload.Body },
            ct);

        activity?.SetTag("classify.response_length", toolResult.Length);
        return ParseJson<AlertClassification>(toolResult)
            ?? new AlertClassification("Low", "unknown", 0.5);
    }

    private async Task<RunbookExcerpt[]> LookupRunbooksAsync(string category, CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity("triage.enrich", ActivityKind.Internal);
        activity?.SetTag("enrich.category", category);

        var toolResult = await CallMcpToolAsync("LookupRunbook",
            new Dictionary<string, object?> { ["alertCategory"] = category },
            ct);

        var excerpts = ParseJson<RunbookExcerpt[]>(toolResult);
        activity?.SetTag("enrich.runbooks_found", excerpts?.Length ?? 0);
        return excerpts ?? [];
    }

    private async Task<RemediationProposal> ProposeRemediationAsync(
        AlertPayload payload,
        AlertClassification classification,
        RunbookExcerpt[] runbooks,
        CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity("triage.remediate", ActivityKind.Internal);

        var runbookContext = runbooks.Length > 0
            ? string.Join("\n", runbooks.Select(r => $"- {r.Title}: {r.Content}"))
            : "No specific runbook available.";

        // C# 14: params ReadOnlySpan<string> assembles the prompt
        var prompt = BuildPrompt(
            "You are an SRE triage agent. Propose a structured remediation plan as JSON.",
            $"Alert body: {payload.Body}",
            $"Severity: {classification.Severity} | Category: {classification.Category} | Confidence: {classification.Confidence:F2}",
            $"Runbook context:\n{runbookContext}",
            "Respond ONLY with valid JSON matching: {\"actionSteps\":[\"...\"],\"estimatedImpact\":\"...\",\"confidence\":0.0}");

        // Use SK for pure LLM reasoning (no function calling required here)
        var result = await kernel.InvokePromptAsync(prompt, cancellationToken: ct);
        var json = result.ToString();

        activity?.SetTag("remediate.response_length", json.Length);

        return ParseJson<RemediationProposal>(json)
            ?? new RemediationProposal(["Review alert manually"], "Unknown", 0.3);
    }

    private async Task<EscalationResult> EscalateAsync(
        AlertPayload payload,
        AlertClassification classification,
        CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity("triage.escalate", ActivityKind.Client);

        var summary = $"[{classification.Severity}] {payload.Source}: {payload.Body[..Math.Min(120, payload.Body.Length)]}";
        var toolResult = await CallMcpToolAsync("EscalateIncident",
            new Dictionary<string, object?>
            {
                ["incidentSummary"] = summary,
                ["severity"] = classification.Severity
            },
            ct);

        var escalation = ParseJson<EscalationResult>(toolResult)
            ?? new EscalationResult(false, null);
        activity?.SetTag("escalate.escalated", escalation.Escalated);
        return escalation;
    }

    private async Task WriteAuditAsync(
        AuditEntry auditEntry,
        AlertClassification classification,
        RemediationProposal proposal,
        EscalationResult escalation,
        CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity("triage.audit", ActivityKind.Internal);

        var triageResult = new TriageResult(classification, proposal, escalation, auditEntry);
        var triageJson = JsonSerializer.Serialize(triageResult, JsonOptions);

        try
        {
            await CallMcpToolAsync("WriteAuditEntry",
                new Dictionary<string, object?> { ["triageResultJson"] = triageJson },
                ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Audit write step failed — triage result will not be persisted");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Calls an MCP tool by name and returns the text result.</summary>
    private async Task<string> CallMcpToolAsync(
        string toolName,
        Dictionary<string, object?> args,
        CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity($"mcp.{toolName}", ActivityKind.Client);
        activity?.SetTag("mcp.tool_name", toolName);

        try
        {
            var result = await mcpClient.CallToolAsync(
                toolName,
                args.AsReadOnly(),
                progress: null,
                options: null,
                cancellationToken: ct);

            // Extract text from the content blocks (TextContentBlock.Text)
            var text = string.Concat(
                result.Content
                      .OfType<TextContentBlock>()
                      .Select(b => b.Text));

            // Fallback to structured content JSON if no text blocks
            // StructuredContent is JsonElement? — use JsonSerializer.Serialize, not ToJsonString()
            if (string.IsNullOrEmpty(text) && result.StructuredContent is { } sc)
                text = JsonSerializer.Serialize(sc, JsonOptions);

            return string.IsNullOrEmpty(text) ? "{}" : text;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MCP tool '{ToolName}' call failed", toolName);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return "{}";
        }
    }

    /// <summary>Assembles context segments into a single prompt string.</summary>
    private static string BuildPrompt(params ReadOnlySpan<string> segments)
    {
        var sb = new StringBuilder();
        foreach (var segment in segments)
        {
            if (!string.IsNullOrWhiteSpace(segment))
                sb.AppendLine(segment);
        }
        return sb.ToString().TrimEnd();
    }

    private static T? ParseJson<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;

        // Trim prose wrapper: find the first JSON object or array
        var start = json.IndexOfAny(['{', '[']);
        var end   = json.LastIndexOfAny(['}', ']']);
        if (start < 0 || end < 0 || end <= start) return default;

        try { return JsonSerializer.Deserialize<T>(json[start..(end + 1)], JsonOptions); }
        catch { return default; }
    }
}
