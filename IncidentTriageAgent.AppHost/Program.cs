// DotNetAspireTriageAgent.AppHost/Program.cs
var builder = DistributedApplication.CreateBuilder(args);

// ── Vector store ─────────────────────────────────────────────────────────────
var qdrant = builder.AddQdrant("vectorstore")
                    .WithLifetime(ContainerLifetime.Persistent);

// ── Local LLM (on-premise — no external API calls) ───────────────────────────
// Ollama runs locally; the model must be pulled before first use.
// Run: ollama pull llama3.2 && ollama pull nomic-embed-text
var ollama = builder.AddOllama("ollama")
                    .WithLifetime(ContainerLifetime.Persistent)
                    .AddModel("llama3.2")
                    .AddModel("nomic-embed-text");

// ── MCP tool server ───────────────────────────────────────────────────────────
var mcpServer = builder.AddProject<Projects.DotNetAspireTriageAgent_McpToolServer>("mcp-tools")
                       .WithReference(qdrant)
                       .WithReference(ollama)
                       .WaitFor(qdrant)
                       .WaitFor(ollama);

// ── Agent service ─────────────────────────────────────────────────────────────
builder.AddProject<Projects.DotNetAspireTriageAgent_AgentService>("agent-service")
       .WithReference(mcpServer)
       .WithReference(qdrant)
       .WithReference(ollama)
       .WaitFor(mcpServer);

builder.Build().Run();
