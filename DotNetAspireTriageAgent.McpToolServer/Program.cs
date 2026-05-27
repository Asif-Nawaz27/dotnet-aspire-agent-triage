// DotNetAspireTriageAgent.McpToolServer/Program.cs
// All configuration is injected by AppHost/appsettings.json via WithEnvironment().
// No hardcoded fallback values — every setting must be present at startup.
using DotNetAspireTriageAgent.McpToolServer;
using DotNetAspireTriageAgent.McpToolServer.Tools;
using Microsoft.Extensions.AI;
using OpenAI;
using Qdrant.Client;
using Serilog;
using Serilog.Events;
using System.ClientModel;

// ── Serilog bootstrap logger ──────────────────────────────────────────────────
// Captures log output that occurs before the host/DI is fully constructed.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.AddServiceDefaults();

    // ── Serilog full logger ───────────────────────────────────────────────────────
    // Log path: <SolutionRoot>\Logs\McpToolServer\mcptoolserver-YYYYMMDD.log
    // AppContext.BaseDirectory = <proj>\bin\Debug\net10.0\  → 4 levels up = solution root
    var mcpLogPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory,
            "../../../../Logs/McpToolServer/mcptoolserver-.log"));

    builder.Host.UseSerilog((ctx, services, cfg) => cfg
        .Enrich.FromLogContext()
        .MinimumLevel.Override("Microsoft",                  LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
        .MinimumLevel.Override("Grpc",                       LogEventLevel.Warning)
        .WriteTo.Console(
            outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}{NewLine}  {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(
            mcpLogPath,
            rollingInterval:        RollingInterval.Day,
            outputTemplate:         "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}",
            retainedFileCountLimit: 14,
            fileSizeLimitBytes:     50_000_000,
            rollOnFileSizeLimit:    true,
            shared:                 false));

    var cfg2 = builder.Configuration;

    // ── Groq (injected by AppHost: Groq__ApiKey, Groq__Endpoint, Groq__Model) ─────
    var groqApiKey   = cfg2["Groq:ApiKey"]   ?? throw new InvalidOperationException("Groq:ApiKey is missing. Ensure Parameters:GroqApiKey is set in AppHost/appsettings.json.");
    var groqEndpoint = cfg2["Groq:Endpoint"] ?? throw new InvalidOperationException("Groq:Endpoint is missing. Ensure Groq:Endpoint is set in AppHost/appsettings.json.");
    var groqModel    = cfg2["Groq:Model"]    ?? throw new InvalidOperationException("Groq:Model is missing. Ensure Groq:Model is set in AppHost/appsettings.json.");

    // ── Nomic AI (injected by AppHost: Nomic__ApiKey, Nomic__Endpoint, Nomic__Model)
    var nomicApiKey   = cfg2["Nomic:ApiKey"]   ?? throw new InvalidOperationException("Nomic:ApiKey is missing. Ensure Parameters:NomicApiKey is set in AppHost/appsettings.json.");
    var nomicEndpoint = cfg2["Nomic:Endpoint"] ?? throw new InvalidOperationException("Nomic:Endpoint is missing. Ensure Nomic:Endpoint is set in AppHost/appsettings.json.");
    var nomicModel    = cfg2["Nomic:Model"]    ?? throw new InvalidOperationException("Nomic:Model is missing. Ensure Nomic:Model is set in AppHost/appsettings.json.");

    // ── Qdrant (ConnectionStrings:vectorstore injected by Aspire .WithReference(qdrant);
    //           Qdrant:DefaultEndpoint injected by AppHost as Qdrant__DefaultEndpoint)
    // Aspire injects the Qdrant connection string as "Endpoint=http://host:port" — extract the URI.
    static string ExtractQdrantUri(string raw)
    {
        foreach (var part in raw.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals("Endpoint", StringComparison.OrdinalIgnoreCase))
                return kv[1].Trim();
        }
        return raw; // already a plain URI
    }

    var qdrantRaw = cfg2["ConnectionStrings:vectorstore"]
        ?? cfg2["Qdrant:DefaultEndpoint"]
        ?? throw new InvalidOperationException("Qdrant endpoint is missing. Ensure Qdrant:DefaultEndpoint is set in AppHost/appsettings.json.");
    var qdrantEndpoint = ExtractQdrantUri(qdrantRaw);

    Log.Information("McpToolServer starting — model={Model} qdrant={Qdrant} logPath={LogPath}",
        groqModel, qdrantEndpoint, mcpLogPath);

    // ── Groq chat client ──────────────────────────────────────────────────────────
    var groqClient = new OpenAIClient(
        new ApiKeyCredential(groqApiKey),
        new OpenAIClientOptions { Endpoint = new Uri(groqEndpoint) });

    builder.Services.AddChatClient(
        groqClient.GetChatClient(groqModel).AsIChatClient());

    // ── Nomic AI embedding client ─────────────────────────────────────────────────
    var nomicClient = new OpenAIClient(
        new ApiKeyCredential(nomicApiKey),
        new OpenAIClientOptions { Endpoint = new Uri(nomicEndpoint) });

    builder.Services.AddEmbeddingGenerator<string, Embedding<float>>(
        nomicClient.GetEmbeddingClient(nomicModel).AsIEmbeddingGenerator());

    // ── Qdrant ────────────────────────────────────────────────────────────────────
    builder.Services.AddSingleton(_ => new QdrantClient(new Uri(qdrantEndpoint)));

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
