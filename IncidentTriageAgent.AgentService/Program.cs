// DotNetAspireTriageAgent.AgentService/Program.cs
using DotNetAspireTriageAgent.AgentService.Agents;
using DotNetAspireTriageAgent.AgentService.Filters;
using DotNetAspireTriageAgent.AgentService.Models;
using Microsoft.SemanticKernel;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol.Transport;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// ── Resolve service endpoints via Aspire service discovery ────────────────────
var ollamaEndpoint = builder.Configuration["services:ollama:http:0"] ?? "http://localhost:11434";
var mcpServerUrl   = builder.Configuration["services:mcp-tools:http:0"] ?? "http://localhost:5100";

// ── Scoped injection detection context (one per HTTP request) ─────────────────
builder.Services.AddScoped<InjectionDetectionContext>();

// ── Semantic Kernel — under 30 lines ─────────────────────────────────────────
builder.Services.AddScoped<Kernel>(sp =>
{
    var kernelBuilder = Kernel.CreateBuilder();

    kernelBuilder.AddOllamaChatCompletion(
        modelId: "llama3.2",
        endpoint: new Uri(ollamaEndpoint));

    // Register the prompt injection filter (scoped — resolves InjectionDetectionContext per request)
    kernelBuilder.Services.AddScoped<IPromptRenderFilter>(
        sp2 => new PromptInjectionFilter(
            sp2.GetRequiredService<InjectionDetectionContext>(),
            sp2.GetRequiredService<ILogger<PromptInjectionFilter>>()));

    var kernel = kernelBuilder.Build();

    // Import MCP tool server as a KernelPlugin
    var mcpClient = McpClientFactory.CreateAsync(
        new SseClientTransport(new SseClientTransportOptions
        {
            Endpoint = new Uri($"{mcpServerUrl}/mcp"),
            Name = "mcp-tools"
        })).GetAwaiter().GetResult();

    var mcpTools = mcpClient.ListToolsAsync().GetAwaiter().GetResult();
    var mcpFunctions = mcpTools.Select(t => t.AsKernelFunction(mcpClient)).ToList();
    var mcpPlugin = KernelPluginFactory.CreateFromFunctions("McpTools", mcpFunctions);
    kernel.Plugins.Add(mcpPlugin);

    return kernel;
});

// ── Agent service (scoped — shares Kernel + InjectionDetectionContext) ────────
builder.Services.AddScoped<DotNetAspireTriageAgentService>();

var app = builder.Build();
app.MapDefaultEndpoints();

// ── Step 1: Receive alert via POST /triage ────────────────────────────────────
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
