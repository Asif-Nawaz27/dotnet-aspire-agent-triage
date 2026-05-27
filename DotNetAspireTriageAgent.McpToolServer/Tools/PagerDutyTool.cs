// DotNetAspireTriageAgent.McpToolServer/Tools/PagerDutyTool.cs
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace DotNetAspireTriageAgent.McpToolServer.Tools;

/// <summary>Internal DTO for the PagerDuty escalation result.</summary>
internal sealed record EscalationResultDto(bool Escalated, string? TicketId);

/// <summary>
/// MCP tool that escalates Critical/High incidents via PagerDuty stub,
/// and only logs Medium/Low events.
/// </summary>
[McpServerToolType]
public sealed class PagerDutyTool(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<PagerDutyTool> logger)
{
    private static readonly ActivitySource ActivitySource = new("DotNetAspireTriageAgent.McpToolServer");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlySet<string> EscalateSeverities =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Critical", "High" };

    /// <summary>
    /// Escalates an incident to PagerDuty for Critical or High severity.
    /// For Medium or Low, logs the event only. Returns an EscalationResult JSON object.
    /// </summary>
    [McpServerTool]
    [Description("Escalates Critical/High incidents to PagerDuty. Logs Medium/Low events only. Returns {escalated: bool, ticketId: string|null}.")]
    public async Task<string> EscalateIncident(
        [Description("Human-readable incident summary")] string incidentSummary,
        [Description("Severity: Critical, High, Medium, or Low")] string severity,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("triage.escalate", ActivityKind.Client);
        activity?.SetTag("incident.severity", severity);

        if (!EscalateSeverities.Contains(severity))
        {
            logger.LogInformation(
                "Severity={Severity} does not require escalation — event logged only. Summary: {Summary}",
                severity, incidentSummary);

            return JsonSerializer.Serialize(new EscalationResultDto(false, null), JsonOptions);
        }

        try
        {
            var stubEndpoint = configuration["PagerDuty:StubEndpoint"]
                ?? "http://localhost:9999/pagerduty-stub/incidents";

            var client = httpClientFactory.CreateClient("pagerduty");
            var ticketId = "INC-" + Guid.NewGuid().ToString("N")[..8];

            var payload = new
            {
                incident = new
                {
                    type = "incident",
                    title = incidentSummary,
                    urgency = severity.Equals("Critical", StringComparison.OrdinalIgnoreCase) ? "high" : "low",
                    service = new { id = "stub-service", type = "service_reference" }
                },
                correlationId = ticketId
            };

            var response = await client.PostAsJsonAsync(stubEndpoint, payload, JsonOptions, cancellationToken);
            response.EnsureSuccessStatusCode();

            activity?.SetTag("incident.ticket_id", ticketId);
            logger.LogInformation("PagerDuty escalation succeeded. TicketId={TicketId}", ticketId);

            return JsonSerializer.Serialize(new EscalationResultDto(true, ticketId), JsonOptions);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("EscalateIncident cancelled");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "PagerDuty stub call failed — returning non-escalated result");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return JsonSerializer.Serialize(new EscalationResultDto(false, null), JsonOptions);
        }
    }
}
