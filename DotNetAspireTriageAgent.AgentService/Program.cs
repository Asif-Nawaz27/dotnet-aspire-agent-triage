// DotNetAspireTriageAgent.AgentService/Program.cs
// All configuration is injected by AppHost/appsettings.json via WithEnvironment().
// No hardcoded fallback values — every setting must be present at startup.
using DotNetAspireTriageAgent.AgentService.Agents;
using DotNetAspireTriageAgent.AgentService.Filters;
using DotNetAspireTriageAgent.AgentService.Models;
using Microsoft.SemanticKernel;
using ModelContextProtocol.Client;
using OpenAI;
using Serilog;
using Serilog.Events;
using System.ClientModel;

// ── Serilog bootstrap logger ──────────────────────────────────────────────────
// Captures log output that occurs before the host/DI is fully constructed.
// Replaced by the full logger configured inside UseSerilog().
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
    // Log path: <SolutionRoot>\Logs\AgentService\agentservice-YYYYMMDD.log
    // AppContext.BaseDirectory = <proj>\bin\Debug\net10.0\  → 4 levels up = solution root
    var agentLogPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory,
            "../../../../Logs/AgentService/agentservice-.log"));

    builder.Host.UseSerilog((ctx, services, cfg) => cfg
        .Enrich.FromLogContext()
        .MinimumLevel.Override("Microsoft",                  LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
        .MinimumLevel.Override("Grpc",                       LogEventLevel.Warning)
        .WriteTo.Console(
            outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}{NewLine}  {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(
            agentLogPath,
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

    // ── MCP Tool Server URL ────────────────────────────────────────────────────────
    // services:mcp-tools:http:0  → injected automatically by Aspire .WithReference(mcpServer)
    // McpClient:DefaultUrl       → injected by AppHost as McpClient__DefaultUrl
    var mcpServerUrl = cfg2["services:mcp-tools:http:0"]
        ?? cfg2["McpClient:DefaultUrl"]
        ?? throw new InvalidOperationException("MCP server URL is missing. Ensure McpClient:DefaultUrl is set in AppHost/appsettings.json.");

    Log.Information("AgentService starting — model={Model} mcpUrl={McpUrl} logPath={LogPath}",
        groqModel, mcpServerUrl, agentLogPath);

    // ── Scoped injection detection context (one per HTTP request) ─────────────────
    builder.Services.AddScoped<InjectionDetectionContext>();

    // ── MCP client (singleton) ────────────────────────────────────────────────────
    builder.Services.AddSingleton<McpClient>(_ =>
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint      = new Uri($"{mcpServerUrl}/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            Name          = "mcp-tools"
        });
        return McpClient.CreateAsync(transport, cancellationToken: CancellationToken.None)
                        .GetAwaiter().GetResult();
    });

    // ── Semantic Kernel — Groq via OpenAI-compatible connector ───────────────────
    builder.Services.AddScoped<Kernel>(sp =>
    {
        var kernelBuilder = Kernel.CreateBuilder();

        var groqClient = new OpenAIClient(
            new ApiKeyCredential(groqApiKey),
            new OpenAIClientOptions { Endpoint = new Uri(groqEndpoint) });

        kernelBuilder.AddOpenAIChatCompletion(
            modelId:      groqModel,
            openAIClient: groqClient);

        var kernel = kernelBuilder.Build();

        // Resolve filter from the outer scoped container (which has InjectionDetectionContext)
        kernel.PromptRenderFilters.Add(new PromptInjectionFilter(
            sp.GetRequiredService<InjectionDetectionContext>(),
            sp.GetRequiredService<ILogger<PromptInjectionFilter>>()));

        return kernel;
    });

    // ── Agent service ─────────────────────────────────────────────────────────────
    builder.Services.AddScoped<DotNetAspireTriageAgentService>();

    var app = builder.Build();
    app.MapDefaultEndpoints();

    app.MapPost("/triage", async (
        AlertPayload payload,
        DotNetAspireTriageAgentService agent,
        CancellationToken cancellationToken) =>
    {
        var result = await agent.TriageAsync(payload, cancellationToken);
        return Results.Ok(result);
    })
    .WithName("TriageAlert")
    .WithDescription("Accepts a raw alert payload and runs the five-step triage pipeline");

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "AgentService terminated unexpectedly during startup");
    throw;
}
finally
{
    Log.Information("AgentService shutting down — flushing Serilog");
    await Log.CloseAndFlushAsync();
}
