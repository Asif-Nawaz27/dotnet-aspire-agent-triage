// DotNetAspireTriageAgent.McpToolServer/Program.cs
// All configuration is injected by AppHost/appsettings.json via WithEnvironment().
// Serilog is configured centrally via ServiceDefaults/LoggingExtensions.cs.
using DotNetAspireTriageAgent.McpToolServer;
using DotNetAspireTriageAgent.McpToolServer.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using OpenAI;
using Qdrant.Client;
using Serilog;
using System.ClientModel;

// ── Bootstrap logger (captures crashes before the host is built) ──────────────
SerilogLoggingExtensions.ConfigureBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.AddServiceDefaults();

    // ── Serilog full logger — wired via ServiceDefaults/LoggingExtensions.cs ─────
    builder.Host.UseSerilogLogging("McpToolServer");

    var cfg = builder.Configuration;

    // ── Groq (injected by AppHost: Groq__ApiKey, Groq__Endpoint, Groq__Model) ─────
    var groqApiKey   = cfg["Groq:ApiKey"]   ?? throw new InvalidOperationException("Groq:ApiKey is missing. Ensure Parameters:GroqApiKey is set in AppHost/appsettings.json.");
    var groqEndpoint = cfg["Groq:Endpoint"] ?? throw new InvalidOperationException("Groq:Endpoint is missing. Ensure Groq:Endpoint is set in AppHost/appsettings.json.");
    var groqModel    = cfg["Groq:Model"]    ?? throw new InvalidOperationException("Groq:Model is missing. Ensure Groq:Model is set in AppHost/appsettings.json.");

    // ── Nomic AI (injected by AppHost: Nomic__ApiKey, Nomic__Endpoint, Nomic__Model)
    var nomicApiKey   = cfg["Nomic:ApiKey"]   ?? throw new InvalidOperationException("Nomic:ApiKey is missing. Ensure Parameters:NomicApiKey is set in AppHost/appsettings.json.");
    var nomicEndpoint = cfg["Nomic:Endpoint"] ?? throw new InvalidOperationException("Nomic:Endpoint is missing. Ensure Nomic:Endpoint is set in AppHost/appsettings.json.");
    var nomicModel    = cfg["Nomic:Model"]    ?? throw new InvalidOperationException("Nomic:Model is missing. Ensure Nomic:Model is set in AppHost/appsettings.json.");

    // ── Qdrant (ConnectionStrings:vectorstore injected by Aspire .WithReference(qdrant);
    //           Qdrant:DefaultEndpoint injected by AppHost as Qdrant__DefaultEndpoint)
    // Aspire connection string format: "Endpoint=http://host:port;Key=<api-key>"
    // The Key is the generated API key — must be forwarded to QdrantClient or it returns 401.
    static (string Host, int Port, bool Https, string? ApiKey) ParseQdrantConnectionString(string raw)
    {
        string? endpoint = null;
        string? apiKey   = null;

        foreach (var segment in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = segment.Split('=', 2);
            if (kv.Length == 2)
            {
                if (kv[0].Trim().Equals("Endpoint", StringComparison.OrdinalIgnoreCase))
                    endpoint = kv[1].Trim();
                else if (kv[0].Trim().Equals("Key", StringComparison.OrdinalIgnoreCase))
                    apiKey = kv[1].Trim();
            }
        }

        var uri = new Uri(endpoint ?? raw);
        return (uri.Host, uri.Port < 0 ? 6334 : uri.Port, uri.Scheme == "https", apiKey);
    }

    var qdrantRaw = cfg["ConnectionStrings:vectorstore"]
        ?? cfg["Qdrant:DefaultEndpoint"]
        ?? throw new InvalidOperationException("Qdrant endpoint is missing. Ensure Qdrant:DefaultEndpoint is set in AppHost/appsettings.json.");

    var (qdrantHost, qdrantPort, qdrantHttps, qdrantApiKey) = ParseQdrantConnectionString(qdrantRaw);

    Log.Information(
        "McpToolServer starting — model={Model} qdrant={Host}:{Port} hasApiKey={HasKey} logFile={LogFile}",
        groqModel, qdrantHost, qdrantPort, qdrantApiKey is not null,
        SerilogLoggingExtensions.GetLogFilePath("McpToolServer"));

    // ── Groq chat client ──────────────────────────────────────────────────────────
    var groqClient = new OpenAIClient(
        new ApiKeyCredential(groqApiKey),
        new OpenAIClientOptions { Endpoint = new Uri(groqEndpoint) });

    builder.Services.AddChatClient(
        groqClient.GetChatClient(groqModel).AsIChatClient());

    // ── Nomic AI embedding client ─────────────────────────────────────────────────
    // Nomic's native endpoint is POST /v1/embedding/text — the OpenAI SDK calls
    // /v1/embeddings and gets HTTP 404.  We use a custom IEmbeddingGenerator instead.
    builder.Services.AddHttpClient("nomic", c =>
    {
        // Base address ends with /v1/ so relative requests resolve to /v1/embedding/text
        c.BaseAddress = new Uri(nomicEndpoint.TrimEnd('/') + "/");
        c.DefaultRequestHeaders.Add("Authorization", $"Bearer {nomicApiKey}");
    });

    builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        new NomicEmbeddingGenerator(
            sp.GetRequiredService<IHttpClientFactory>(),
            nomicModel,
            sp.GetRequiredService<ILogger<NomicEmbeddingGenerator>>()));

    // ── Qdrant ────────────────────────────────────────────────────────────────────
    // Pass the Aspire-generated API key so Qdrant accepts the connection.
    builder.Services.AddSingleton(_ =>
        new QdrantClient(qdrantHost, qdrantPort, qdrantHttps, apiKey: qdrantApiKey));

    // ── Shared audit log singleton ────────────────────────────────────────────────
    builder.Services.AddSingleton<AuditLog>();

    // ── HTTP client for PagerDuty stub ────────────────────────────────────────────
    builder.Services.AddHttpClient("pagerduty");

    // ── MCP server — streamable HTTP ─────────────────────────────────────────────
    builder.Services.AddMcpServer()
        .WithHttpTransport()
        .WithToolsFromAssembly();

    // ── Runbook seeder (hosted service) ───────────────────────────────────────────
    builder.Services.AddHostedService<RunbookSeeder>();

    var app = builder.Build();
    app.MapDefaultEndpoints();

    app.MapMcp("/mcp");

    app.MapGet("/audit", (AuditLog auditLog) => Results.Ok(auditLog.GetAll()))
       .WithName("GetAuditLog")
       .WithDescription("Returns all audit entries written by AuditWriterTool");

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "McpToolServer terminated unexpectedly during startup");
    throw;
}
finally
{
    Log.Information("McpToolServer shutting down — flushing Serilog");
    await Log.CloseAndFlushAsync();
}
