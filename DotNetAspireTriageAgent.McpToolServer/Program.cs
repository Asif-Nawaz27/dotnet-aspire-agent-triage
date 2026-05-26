// DotNetAspireTriageAgent.McpToolServer/Program.cs
// All configuration is injected by AppHost/appsettings.json via WithEnvironment().
// No hardcoded fallback values — every setting must be present at startup.
using DotNetAspireTriageAgent.McpToolServer;
using DotNetAspireTriageAgent.McpToolServer.Tools;
using Microsoft.Extensions.AI;
using OpenAI;
using Qdrant.Client;
using System.ClientModel;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

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
var qdrantEndpoint = cfg["ConnectionStrings:vectorstore"]
    ?? cfg["Qdrant:DefaultEndpoint"]
    ?? throw new InvalidOperationException("Qdrant endpoint is missing. Ensure Qdrant:DefaultEndpoint is set in AppHost/appsettings.json.");

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
