// DotNetAspireTriageAgent.AgentService/Models/AlertClassification.cs
namespace DotNetAspireTriageAgent.AgentService.Models;

/// <summary>Raw alert payload received from a monitoring system.</summary>
public sealed record AlertPayload(
    string Id,
    string Source,
    string Body,
    DateTime ReceivedAt);

/// <summary>Structured severity classification produced by AlertClassifierTool.</summary>
public sealed record AlertClassification(
    string Severity,
    string Category,
    double Confidence);

/// <summary>A single runbook excerpt retrieved from the vector store.</summary>
public sealed record RunbookExcerpt(
    string Title,
    string Content,
    double Score);

/// <summary>LLM-proposed remediation plan for an incident.</summary>
public sealed record RemediationProposal(
    string[] ActionSteps,
    string EstimatedImpact,
    double Confidence);

/// <summary>Outcome of the PagerDuty escalation step.</summary>
public sealed record EscalationResult(
    bool Escalated,
    string? TicketId);

/// <summary>Immutable audit record written at the end of every triage run.</summary>
public sealed record AuditEntry(
    string AlertId,
    string Severity,
    string[] ActionSteps,
    bool Escalated,
    DateTime TriagedAt,
    long ElapsedMs);

/// <summary>Full triage result returned to the caller and written to the audit log.</summary>
public sealed record TriageResult(
    AlertClassification Classification,
    RemediationProposal Proposal,
    EscalationResult Escalation,
    AuditEntry Audit,
    bool InjectionDetected = false);
