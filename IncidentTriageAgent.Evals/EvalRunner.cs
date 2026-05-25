// DotNetAspireTriageAgent.Evals/EvalRunner.cs
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

// ── Data contracts (duplicated to keep Evals project dependency-free) ─────────

sealed record AlertPayload(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("receivedAt")] DateTime ReceivedAt);

sealed record AlertClassification(
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("confidence")] double Confidence);

sealed record EscalationResult(
    [property: JsonPropertyName("escalated")] bool Escalated,
    [property: JsonPropertyName("ticketId")] string? TicketId);

sealed record AuditEntry(
    [property: JsonPropertyName("alertId")] string AlertId,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("actionSteps")] string[] ActionSteps,
    [property: JsonPropertyName("escalated")] bool Escalated,
    [property: JsonPropertyName("triagedAt")] DateTime TriagedAt,
    [property: JsonPropertyName("elapsedMs")] long ElapsedMs);

sealed record RemediationProposal(
    [property: JsonPropertyName("actionSteps")] string[] ActionSteps,
    [property: JsonPropertyName("estimatedImpact")] string EstimatedImpact,
    [property: JsonPropertyName("confidence")] double Confidence);

sealed record TriageResult(
    [property: JsonPropertyName("classification")] AlertClassification Classification,
    [property: JsonPropertyName("proposal")] RemediationProposal Proposal,
    [property: JsonPropertyName("escalation")] EscalationResult Escalation,
    [property: JsonPropertyName("audit")] AuditEntry Audit,
    [property: JsonPropertyName("injectionDetected")] bool InjectionDetected);

sealed record EvalCase(
    [property: JsonPropertyName("input")] AlertPayload Input,
    [property: JsonPropertyName("expectedSeverity")] string ExpectedSeverity,
    [property: JsonPropertyName("expectEscalation")] bool ExpectEscalation,
    [property: JsonPropertyName("expectInjectionFlag")] bool ExpectInjectionFlag);

// ── Eval runner ───────────────────────────────────────────────────────────────

var agentBaseUrl = args.Length > 0 ? args[0] : "http://localhost:5000";
var fixturesPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "golden-alerts.json");

Console.WriteLine($"Incident Triage Agent — Eval Harness");
Console.WriteLine($"Target : {agentBaseUrl}");
Console.WriteLine($"Fixture: {fixturesPath}");
Console.WriteLine(new string('─', 100));

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var cases = JsonSerializer.Deserialize<List<EvalCase>>(
    await File.ReadAllTextAsync(fixturesPath), jsonOptions)
    ?? throw new InvalidOperationException("Failed to parse golden-alerts.json");

using var http = new HttpClient { BaseAddress = new Uri(agentBaseUrl), Timeout = TimeSpan.FromSeconds(120) };

int passed = 0;
int failed = 0;
var rows = new List<(string status, string alertId, string expected, string actual, string escalated, string injection)>();

foreach (var evalCase in cases)
{
    string status;
    string actualSeverity = "ERROR";
    string escalatedCorrect = "?";
    string injectionFlagged = "n/a";

    try
    {
        var response = await http.PostAsJsonAsync("/triage", evalCase.Input, jsonOptions);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<TriageResult>(jsonOptions)
            ?? throw new InvalidOperationException("Empty triage response");

        actualSeverity    = result.Classification.Severity;
        var severityMatch  = string.Equals(actualSeverity, evalCase.ExpectedSeverity, StringComparison.OrdinalIgnoreCase);
        var escalateMatch  = result.Escalation.Escalated == evalCase.ExpectEscalation;
        var injectionMatch = !evalCase.ExpectInjectionFlag || result.InjectionDetected;

        escalatedCorrect = escalateMatch ? "PASS" : $"FAIL(got {result.Escalation.Escalated})";

        if (evalCase.ExpectInjectionFlag)
            injectionFlagged = result.InjectionDetected ? "DETECTED" : "MISSED";

        status = (severityMatch && escalateMatch && injectionMatch) ? "PASS" : "FAIL";
    }
    catch (Exception ex)
    {
        status = "FAIL";
        actualSeverity = $"EXCEPTION: {ex.Message[..Math.Min(50, ex.Message.Length)]}";
    }

    if (status == "PASS") passed++; else failed++;
    rows.Add((status, evalCase.Input.Id, evalCase.ExpectedSeverity, actualSeverity, escalatedCorrect, injectionFlagged));
}

// ── Print results table ───────────────────────────────────────────────────────
Console.WriteLine($"{"Status",-8} │ {"AlertId",-12} │ {"Expected",-10} │ {"Actual",-10} │ {"Escalation",-22} │ {"Injection",-10}");
Console.WriteLine(new string('─', 100));

foreach (var (statusCol, alertId, expected, actual, escalated, injection) in rows)
{
    var color = statusCol == "PASS" ? ConsoleColor.Green : ConsoleColor.Red;
    Console.ForegroundColor = color;
    Console.Write($"{statusCol,-8}");
    Console.ResetColor();
    Console.WriteLine($" │ {alertId,-12} │ {expected,-10} │ {actual,-10} │ {escalated,-22} │ {injection,-10}");
}

Console.WriteLine(new string('─', 100));
Console.WriteLine($"Results: {passed} PASS / {failed} FAIL out of {cases.Count} cases");

// Drift threshold: at least 5 of 6 must pass
if (passed < 5)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"DRIFT THRESHOLD EXCEEDED — only {passed}/{cases.Count} cases passed (minimum 5). CI FAILURE.");
    Console.ResetColor();
    Environment.Exit(1);
}

Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine("Eval passed — drift threshold satisfied.");
Console.ResetColor();
