// DotNetAspireTriageAgent.AgentService/Agents/DotNetAspireTriageAgent.cs
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DotNetAspireTriageAgent.AgentService.Filters;
using DotNetAspireTriageAgent.AgentService.Models;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace DotNetAspireTriageAgent.AgentService.Agents;

/// <summary>
/// Orchestrates the five-step incident triage pipeline:
/// classify → enrich → remediate → escalate → audit.
/// </summary>
public sealed class DotNetAspireTriageAgentService(
    Kernel kernel,
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
    /// Runs the full triage pipeline for the given alert payload and returns
    /// a <see cref="TriageResult"/> containing classification, remediation, escalation, and audit data.
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

        // ── Step 3: Enrich with runbook context ───────────────────────────────
        RunbookExcerpt[] runbooks = [];
        if (classification.Severity is "Critical" or "High")
        {
            runbooks = await LookupRunbooksAsync(classification.Category, cancellationToken);
        }

        // ── Step 4: Propose remediation + escalate ────────────────────────────
        var (proposal, escalation) = await RemediateAndEscalateAsync(
            payload, classification, runbooks, cancellationToken);

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

    // ── Private step methods ───────────────────────────────────────────────────

    private async Task<AlertClassification> ClassifyAsync(AlertPayload payload, CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity("triage.classify", ActivityKind.Internal);

        var args = new KernelArguments(new PromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
        }) { ["alertPayload"] = payload.Body };

        var result = await kernel.InvokePromptAsync(
            BuildPrompt(
                "You are an SRE triage agent. Classify the following alert by calling the ClassifyAlert tool.",
                $"Alert ID: {payload.Id}",
                $"Source: {payload.Source}",
                $"Body: {{{{$alertPayload}}}}"),
            args, cancellationToken: ct);

        var json = result.ToString();
        activity?.SetTag("classify.raw_response_length", json.Length);

        return ParseJson<AlertClassification>(json)
            ?? new AlertClassification("Low", "unknown", 0.5);
    }

    private async Task<RunbookExcerpt[]> LookupRunbooksAsync(string category, CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity("triage.enrich", ActivityKind.Internal);
        activity?.SetTag("enrich.category", category);

        var args = new KernelArguments { ["alertCategory"] = category };

        var result = await kernel.InvokePromptAsync(
            "Look up relevant runbooks for the following alert category by calling the LookupRunbook tool: {{$alertCategory}}",
            args, cancellationToken: ct);

        var excerpts = ParseJson<RunbookExcerpt[]>(result.ToString());
        activity?.SetTag("enrich.runbooks_found", excerpts?.Length ?? 0);
        return excerpts ?? [];
    }

    private async Task<(RemediationProposal Proposal, EscalationResult Escalation)> RemediateAndEscalateAsync(
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
            "You are an SRE triage agent. Using the tools available, propose a remediation plan and escalate if needed.",
            $"Alert: {payload.Body}",
            $"Severity: {classification.Severity} | Category: {classification.Category} | Confidence: {classification.Confidence:F2}",
            $"Runbook context:\n{runbookContext}",
            "Call the EscalateIncident tool, then return a JSON RemediationProposal: {actionSteps: string[], estimatedImpact: string, confidence: number}");

        var args = new KernelArguments(new PromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
        })
        {
            ["incidentSummary"] = $"[{classification.Severity}] {payload.Source}: {payload.Body[..Math.Min(120, payload.Body.Length)]}",
            ["severity"] = classification.Severity
        };

        var result = await kernel.InvokePromptAsync(prompt, args, cancellationToken: ct);
        var json = result.ToString();

        if (result.Metadata?.TryGetValue("Usage", out var usageObj) is true && usageObj is { } usage)
        {
            // Emit token usage as Activity tags (provider-agnostic best-effort)
            activity?.SetTag("ai.usage.prompt_tokens", usage.ToString());
        }

        var proposal = ParseJson<RemediationProposal>(json)
            ?? new RemediationProposal(["Review alert manually"], "Unknown", 0.3);

        // The escalation result comes from the EscalateIncident tool call wired via FunctionChoiceBehavior.Auto
        // Extract it from kernel state or default
        var escalation = new EscalationResult(
            classification.Severity is "Critical" or "High",
            classification.Severity is "Critical" or "High" ? $"INC-{payload.Id[..Math.Min(8, payload.Id.Length)]}" : null);

        activity?.SetTag("remediate.action_steps", proposal.ActionSteps.Length);
        return (proposal, escalation);
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

        var args = new KernelArguments { ["triageResultJson"] = triageJson };

        try
        {
            await kernel.InvokePromptAsync(
                "Write the following triage result to the audit log by calling the WriteAuditEntry tool: {{$triageResultJson}}",
                args, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Audit write step failed — triage result will not be persisted");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Assembles context segments into a single prompt string.</summary>
    private static string BuildPrompt(params ReadOnlySpan<string> segments)
    {
        var sb = new StringBuilder();
        foreach (var segment in segments)
        {
            if (!string.IsNullOrWhiteSpace(segment))
            {
                sb.AppendLine(segment);
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static T? ParseJson<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;

        // Find the first JSON object or array in the response (LLMs sometimes add prose)
        var start = json.IndexOfAny(['{', '[']);
        var end = json.LastIndexOfAny(['}', ']']);
        if (start < 0 || end < 0 || end <= start) return default;

        try
        {
            return JsonSerializer.Deserialize<T>(json[start..(end + 1)], JsonOptions);
        }
        catch
        {
            return default;
        }
    }
}
