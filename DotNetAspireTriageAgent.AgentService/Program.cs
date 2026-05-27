// DotNetAspireTriageAgent.AgentService/Program.cs
// All configuration is injected by AppHost/appsettings.json via WithEnvironment().
// Serilog is configured centrally via ServiceDefaults/LoggingExtensions.cs.
using DotNetAspireTriageAgent.AgentService.Agents;
using DotNetAspireTriageAgent.AgentService.Filters;
using DotNetAspireTriageAgent.AgentService.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.SemanticKernel;
using ModelContextProtocol.Client;
using OpenAI;
using Serilog;
using System.ClientModel;

// ── Bootstrap logger (captures crashes before the host is built) ──────────────
SerilogLoggingExtensions.ConfigureBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.AddServiceDefaults();

    // ── Serilog full logger — wired via ServiceDefaults/LoggingExtensions.cs ─────
    builder.Host.UseSerilogLogging("AgentService");

    var cfg = builder.Configuration;

    // ── Groq (injected by AppHost: Groq__ApiKey, Groq__Endpoint, Groq__Model) ─────
    var groqApiKey   = cfg["Groq:ApiKey"]   ?? throw new InvalidOperationException("Groq:ApiKey is missing. Ensure Parameters:GroqApiKey is set in AppHost/appsettings.json.");
    var groqEndpoint = cfg["Groq:Endpoint"] ?? throw new InvalidOperationException("Groq:Endpoint is missing. Ensure Groq:Endpoint is set in AppHost/appsettings.json.");
    var groqModel    = cfg["Groq:Model"]    ?? throw new InvalidOperationException("Groq:Model is missing. Ensure Groq:Model is set in AppHost/appsettings.json.");

    // ── MCP Tool Server URL ────────────────────────────────────────────────────────
    // services:mcp-tools:http:0  → injected automatically by Aspire .WithReference(mcpServer)
    // McpClient:DefaultUrl       → injected by AppHost as McpClient__DefaultUrl
    var mcpServerUrl = cfg["services:mcp-tools:http:0"]
        ?? cfg["McpClient:DefaultUrl"]
        ?? throw new InvalidOperationException("MCP server URL is missing. Ensure McpClient:DefaultUrl is set in AppHost/appsettings.json.");

    Log.Information("AgentService starting — model={Model} mcpUrl={McpUrl} logFile={LogFile}",
        groqModel, mcpServerUrl, SerilogLoggingExtensions.GetLogFilePath("AgentService"));

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
