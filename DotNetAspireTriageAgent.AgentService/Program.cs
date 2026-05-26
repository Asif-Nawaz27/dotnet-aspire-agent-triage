// DotNetAspireTriageAgent.AgentService/Program.cs
using DotNetAspireTriageAgent.AgentService.Agents;
using DotNetAspireTriageAgent.AgentService.Filters;
using DotNetAspireTriageAgent.AgentService.Models;
using Microsoft.SemanticKernel;
using ModelContextProtocol.Client;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// ── Resolve service endpoints via Aspire service discovery ────────────────────
var ollamaEndpoint = builder.Configuration["services:ollama:http:0"] ?? "http://localhost:11434";
var mcpServerUrl   = builder.Configuration["services:mcp-tools:http:0"] ?? "http://localhost:5100";

// ── Scoped injection detection context (one per HTTP request) ─────────────────
builder.Services.AddScoped<InjectionDetectionContext>();

// ── MCP client (singleton) — ModelContextProtocol 1.3.0 API ──────────────────
// HttpClientTransport with StreamableHttp replaces the old SseClientTransport.
builder.Services.AddSingleton<McpClient>(_ =>
{
    var transport = new HttpClientTransport(new HttpClientTransportOptions
    {
        Endpoint = new Uri($"{mcpServerUrl}/mcp"),
        TransportMode = HttpTransportMode.StreamableHttp,
        Name = "mcp-tools"
    });
    return McpClient.CreateAsync(transport, cancellationToken: CancellationToken.None)
                    .GetAwaiter().GetResult();
});

// ── Semantic Kernel — under 30 lines ──────────────────────────────────────────
// SK is used only for LLM reasoning (remediation prompt).
// MCP tools are invoked directly via McpClient, not via FunctionChoiceBehavior.
builder.Services.AddScoped<Kernel>(sp =>
{
    var kernelBuilder = Kernel.CreateBuilder();

    kernelBuilder.AddOllamaChatCompletion(
        modelId: "llama3.2",
        endpoint: new Uri(ollamaEndpoint));

    // PromptInjectionFilter scans every rendered prompt for injection patterns
    kernelBuilder.Services.AddScoped<IPromptRenderFilter>(
        sp2 => new PromptInjectionFilter(
            sp2.GetRequiredService<InjectionDetectionContext>(),
            sp2.GetRequiredService<ILogger<PromptInjectionFilter>>()));

    return kernelBuilder.Build();
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
