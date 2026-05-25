// DotNetAspireTriageAgent.McpToolServer/Program.cs
using DotNetAspireTriageAgent.McpToolServer;
using DotNetAspireTriageAgent.McpToolServer.Tools;
using Microsoft.Extensions.AI;
using Qdrant.Client;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// ── Ollama endpoint via Aspire service discovery ──────────────────────────────
var ollamaEndpoint = builder.Configuration["services:ollama:http:0"] ?? "http://localhost:11434";

// ── Microsoft.Extensions.AI — IChatClient + IEmbeddingGenerator via Ollama ───
builder.Services.AddChatClient(
    new OllamaChatClient(new Uri(ollamaEndpoint), "llama3.2"));

builder.Services.AddEmbeddingGenerator<string, Embedding<float>>(
    new OllamaEmbeddingGenerator(new Uri(ollamaEndpoint), "nomic-embed-text"));

// ── Qdrant ────────────────────────────────────────────────────────────────────
var qdrantEndpoint = builder.Configuration["ConnectionStrings:vectorstore"] ?? "http://localhost:6334";
builder.Services.AddSingleton(_ => new QdrantClient(new Uri(qdrantEndpoint)));

// ── Shared audit log singleton ────────────────────────────────────────────────
builder.Services.AddSingleton<AuditLog>();

// ── HTTP client for PagerDuty stub ────────────────────────────────────────────
builder.Services.AddHttpClient("pagerduty");

// ── MCP server — under 10 lines, streamable HTTP ─────────────────────────────
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

// ── Runbook seeder (hosted service) ───────────────────────────────────────────
builder.Services.AddHostedService<RunbookSeeder>();

var app = builder.Build();
app.MapDefaultEndpoints();

// ── MCP streamable-HTTP endpoint ──────────────────────────────────────────────
app.MapMcp("/mcp");

// ── Audit inspection endpoint ─────────────────────────────────────────────────
app.MapGet("/audit", (AuditLog auditLog) => Results.Ok(auditLog.GetAll()))
   .WithName("GetAuditLog")
   .WithDescription("Returns all audit entries written by AuditWriterTool");

app.Run();
