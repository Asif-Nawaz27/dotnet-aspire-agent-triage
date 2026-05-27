// DotNetAspireTriageAgent.PagerDutyStub/Program.cs
// Lightweight development stub that accepts PagerDuty-style incident payloads,
// logs them, and returns a synthetic 201 Created response.
// Registered in AppHost so Aspire injects the live URL into McpToolServer.
using Microsoft.Extensions.Hosting;
using Serilog;
using System.Text.Json;
using System.Text.Json.Serialization;

// ── Bootstrap logger ──────────────────────────────────────────────────────────
SerilogLoggingExtensions.ConfigureBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.AddServiceDefaults();

    builder.Host.UseSerilogLogging("PagerDutyStub");

    var app = builder.Build();
    app.MapDefaultEndpoints();

    // ── POST /pagerduty-stub/incidents ────────────────────────────────────────
    // Accepts the payload sent by PagerDutyTool and returns a synthetic ticket.
    app.MapPost("/pagerduty-stub/incidents", async (HttpRequest request) =>
    {
        string body;
        using (var reader = new StreamReader(request.Body))
            body = await reader.ReadToEndAsync();

        // Try to extract a correlation ID for structured logging
        string correlationId = "unknown";
        try
        {
            var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("correlationId", out var corr))
                correlationId = corr.GetString() ?? correlationId;
        }
        catch { /* body may be empty or malformed — log as-is */ }

        Log.Information(
            "PagerDutyStub received incident escalation — correlationId={CorrelationId} payload={Payload}",
            correlationId, body);

        var response = new PagerDutyStubResponse(
            Incident: new StubIncident(
                Id:     correlationId,
                Status: "triggered",
                Number: Random.Shared.Next(1000, 9999)));

        return Results.Created(
            $"/pagerduty-stub/incidents/{correlationId}",
            response);
    })
    .WithName("CreateIncident")
    .WithDescription("PagerDuty stub — accepts incident escalation payloads and returns a synthetic ticket");

    Log.Information(
        "PagerDutyStub starting — logFile={LogFile}",
        SerilogLoggingExtensions.GetLogFilePath("PagerDutyStub"));

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "PagerDutyStub terminated unexpectedly during startup");
    throw;
}
finally
{
    Log.Information("PagerDutyStub shutting down — flushing Serilog");
    await Log.CloseAndFlushAsync();
}

// ── Response contracts ────────────────────────────────────────────────────────

internal sealed record StubIncident(
    [property: JsonPropertyName("id")]     string Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("number")] int    Number);

internal sealed record PagerDutyStubResponse(
    [property: JsonPropertyName("incident")] StubIncident Incident);
