// DotNetAspireTriageAgent.Evals/EvalRunner.cs
// CS8803 rule: top-level executable statements must come BEFORE namespace/type declarations.
// All record types are declared at the end of this file.
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

// ── Serilog setup (plain console app — no generic host) ──────────────────────
// Log path: <SolutionRoot>\Logs\Evals\evals-YYYYMMDD.log
// AppContext.BaseDirectory = <proj>\bin\Debug\net10.0\  → 4 levels up = solution root
var evalsLogPath = Path.GetFullPath(
    Path.Combine(AppContext.BaseDirectory,
        "../../../../Logs/Evals/evals-.log"));

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("System.Net.Http",      LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Extensions", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(
        outputTemplate:            "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}",
        restrictedToMinimumLevel:  LogEventLevel.Information)
    .WriteTo.File(
        evalsLogPath,
        rollingInterval:        RollingInterval.Day,
        outputTemplate:         "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
        retainedFileCountLimit: 14,
        fileSizeLimitBytes:     10_000_000,
        rollOnFileSizeLimit:    true,
        shared:                 false)
    .CreateLogger();

try
{
    // ── Eval runner (top-level statements) ───────────────────────────────────────

    var config = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
        .Build();

    var agentBaseUrl = args.Length > 0 ? args[0] : config["Evals:AgentBaseUrl"] ?? "http://localhost:5000";
    var fixturesPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "golden-alerts.json");
    var jsonOptions  = new JsonSerializerOptions(JsonSerializerDefaults.Web);

    Log.Information("Incident Triage Agent — Eval Harness starting");
    Log.Information("Target  : {Target}", agentBaseUrl);
    Log.Information("Fixture : {Fixture}", fixturesPath);
    Log.Information("Log file: {LogPath}", evalsLogPath);

    Console.WriteLine("Incident Triage Agent — Eval Harness");
    Console.WriteLine($"Target : {agentBaseUrl}");
    Console.WriteLine($"Fixture: {fixturesPath}");
    Console.WriteLine(new string('─', 100));

    if (!File.Exists(fixturesPath))
    {
        Log.Fatal("Fixtures file not found: {Path}", fixturesPath);
        throw new FileNotFoundException($"golden-alerts.json not found at: {fixturesPath}");
    }

    var cases = JsonSerializer.Deserialize<List<EvalCase>>(
        await File.ReadAllTextAsync(fixturesPath), jsonOptions)
        ?? throw new InvalidOperationException("Failed to parse golden-alerts.json");

    Log.Information("Loaded {Count} eval case(s) from fixtures", cases.Count);

    using var http = new HttpClient
    {
        BaseAddress = new Uri(agentBaseUrl),
        Timeout     = TimeSpan.FromSeconds(120)
    };

    int passed = 0;
    int failed = 0;
    var rows = new List<(string Status, string AlertId, string Expected, string Actual, string Escalated, string Injection)>();

    foreach (var evalCase in cases)
    {
        string status;
        string actualSeverity  = "ERROR";
        string escalatedResult = "?";
        string injectionResult = "n/a";

        Log.Debug("Running eval case alertId={AlertId} expectedSeverity={Expected}",
            evalCase.Input.Id, evalCase.ExpectedSeverity);

        try
        {
            var response = await http.PostAsJsonAsync("/triage", evalCase.Input, jsonOptions);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<TriageResult>(jsonOptions)
                ?? throw new InvalidOperationException("Empty triage response");

            actualSeverity  = result.Classification.Severity;
            var severityOk  = string.Equals(actualSeverity, evalCase.ExpectedSeverity, StringComparison.OrdinalIgnoreCase);
            var escalateOk  = result.Escalation.Escalated == evalCase.ExpectEscalation;
            var injectionOk = !evalCase.ExpectInjectionFlag || result.InjectionDetected;

            escalatedResult = escalateOk ? "PASS" : $"FAIL(got {result.Escalation.Escalated})";

            if (evalCase.ExpectInjectionFlag)
                injectionResult = result.InjectionDetected ? "DETECTED" : "MISSED";

            status = severityOk && escalateOk && injectionOk ? "PASS" : "FAIL";

            if (status == "PASS")
                Log.Information("PASS  alertId={AlertId} severity={Severity} escalation={Escalation}",
                    evalCase.Input.Id, actualSeverity, escalatedResult);
            else
                Log.Warning("FAIL  alertId={AlertId} expected={Expected} actual={Actual} escalation={Escalation} injection={Injection}",
                    evalCase.Input.Id, evalCase.ExpectedSeverity, actualSeverity, escalatedResult, injectionResult);
        }
        catch (Exception ex)
        {
            status         = "FAIL";
            actualSeverity = $"EXCEPTION: {ex.Message[..Math.Min(50, ex.Message.Length)]}";
            Log.Error(ex, "Eval case {AlertId} threw an exception", evalCase.Input.Id);
        }

        if (status == "PASS") passed++; else failed++;
        rows.Add((status, evalCase.Input.Id, evalCase.ExpectedSeverity, actualSeverity, escalatedResult, injectionResult));
    }

    // ── Results table ─────────────────────────────────────────────────────────────
    Console.WriteLine($"{"Status",-8} │ {"AlertId",-12} │ {"Expected",-10} │ {"Actual",-10} │ {"Escalation",-22} │ {"Injection",-10}");
    Console.WriteLine(new string('─', 100));

    foreach (var (statusCol, alertId, expected, actual, escalated, injection) in rows)
    {
        Console.ForegroundColor = statusCol == "PASS" ? ConsoleColor.Green : ConsoleColor.Red;
        Console.Write($"{statusCol,-8}");
        Console.ResetColor();
        Console.WriteLine($" │ {alertId,-12} │ {expected,-10} │ {actual,-10} │ {escalated,-22} │ {injection,-10}");
    }

    Console.WriteLine(new string('─', 100));
    Console.WriteLine($"Results: {passed} PASS / {failed} FAIL out of {cases.Count} cases");

    Log.Information("Eval complete — {Passed} PASS / {Failed} FAIL out of {Total} cases",
        passed, failed, cases.Count);

    // Drift threshold: at least 5 of 6 must pass
    if (passed < 5)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"DRIFT THRESHOLD EXCEEDED — only {passed}/{cases.Count} passed (minimum 5). CI FAILURE.");
        Console.ResetColor();
        Log.Fatal("Drift threshold exceeded — only {Passed}/{Total} passed (minimum 5)", passed, cases.Count);
        await Log.CloseAndFlushAsync();
        Environment.Exit(1);
    }

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("Eval passed — drift threshold satisfied.");
    Console.ResetColor();
    Log.Information("Eval passed — drift threshold satisfied");
}
catch (Exception ex)
{
    Log.Fatal(ex, "Eval harness terminated unexpectedly");
    throw;
}
finally
{
    Log.Information("Eval harness shutting down — flushing Serilog");
    await Log.CloseAndFlushAsync();
}

// ── Data contracts (must follow top-level statements per CS8803) ──────────────

sealed record AlertPayload(
    [property: JsonPropertyName("id")]          string   Id,
    [property: JsonPropertyName("source")]      string   Source,
    [property: JsonPropertyName("body")]        string   Body,
    [property: JsonPropertyName("receivedAt")]  DateTime ReceivedAt);

sealed record AlertClassification(
    [property: JsonPropertyName("severity")]   string Severity,
    [property: JsonPropertyName("category")]   string Category,
    [property: JsonPropertyName("confidence")] double Confidence);

sealed record EscalationResult(
    [property: JsonPropertyName("escalated")] bool    Escalated,
    [property: JsonPropertyName("ticketId")]  string? TicketId);

sealed record AuditEntry(
    [property: JsonPropertyName("alertId")]     string   AlertId,
    [property: JsonPropertyName("severity")]    string   Severity,
    [property: JsonPropertyName("actionSteps")] string[] ActionSteps,
    [property: JsonPropertyName("escalated")]   bool     Escalated,
    [property: JsonPropertyName("triagedAt")]   DateTime TriagedAt,
    [property: JsonPropertyName("elapsedMs")]   long     ElapsedMs);

sealed record RemediationProposal(
    [property: JsonPropertyName("actionSteps")]      string[] ActionSteps,
    [property: JsonPropertyName("estimatedImpact")]  string   EstimatedImpact,
    [property: JsonPropertyName("confidence")]       double   Confidence);

sealed record TriageResult(
    [property: JsonPropertyName("classification")]  AlertClassification Classification,
    [property: JsonPropertyName("proposal")]        RemediationProposal Proposal,
    [property: JsonPropertyName("escalation")]      EscalationResult    Escalation,
    [property: JsonPropertyName("audit")]           AuditEntry          Audit,
    [property: JsonPropertyName("injectionDetected")] bool              InjectionDetected);

sealed record EvalCase(
    [property: JsonPropertyName("input")]              AlertPayload Input,
    [property: JsonPropertyName("expectedSeverity")]   string       ExpectedSeverity,
    [property: JsonPropertyName("expectEscalation")]   bool         ExpectEscalation,
    [property: JsonPropertyName("expectInjectionFlag")] bool        ExpectInjectionFlag);
