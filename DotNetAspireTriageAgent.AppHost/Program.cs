// DotNetAspireTriageAgent.AppHost/Program.cs
// All configuration is centralised in appsettings.json.
// This file reads every setting and pushes it into the correct service
// via WithEnvironment() so neither child project needs its own config.
var builder = DistributedApplication.CreateBuilder(args);
Console.WriteLine("STEP 1: Start building...");
Console.WriteLine(builder);

// ── Vector store ─────────────────────────────────────────────────────────────
var qdrant = builder.AddQdrant("vectorstore")
                    .WithLifetime(ContainerLifetime.Persistent);
Console.WriteLine("STEP 2: Vector Store enabled...");
// ── API keys (Aspire Parameters — resolved from appsettings.json) ─────────────
var groqApiKey  = builder.AddParameter("GroqApiKey",  secret: true);
var nomicApiKey = builder.AddParameter("NomicApiKey", secret: true);

// ── Read all other settings from the central appsettings.json ─────────────────

// Groq — shared by both services
var groqEndpoint = builder.Configuration["Groq:Endpoint"] ?? "https://api.groq.com/openai/v1";
var groqModel    = builder.Configuration["Groq:Model"]    ?? "llama-3.2-3b-preview";

// Nomic AI — McpToolServer only
var nomicEndpoint = builder.Configuration["Nomic:Endpoint"] ?? "https://api-atlas.nomic.ai/v1";
var nomicModel    = builder.Configuration["Nomic:Model"]    ?? "nomic-embed-text-v1.5";

// Qdrant — McpToolServer only
var qdrantDefaultEndpoint = builder.Configuration["Qdrant:DefaultEndpoint"] ?? "http://localhost:6334";
var qdrantCollection      = builder.Configuration["Qdrant:CollectionName"]  ?? "runbooks";
var qdrantTopK            = builder.Configuration["Qdrant:TopK"]            ?? "3";

// PagerDuty — McpToolServer only
var pagerDutyEndpoint = builder.Configuration["PagerDuty:StubEndpoint"]
    ?? "http://localhost:9999/pagerduty-stub/incidents";

// MCP client — AgentService only
var mcpClientDefaultUrl     = builder.Configuration["McpClient:DefaultUrl"]      ?? "http://localhost:5100";
var mcpClientTimeoutSeconds = builder.Configuration["McpClient:TimeoutSeconds"]  ?? "120";

// Triage pipeline — AgentService only
var triageSeverities = builder.Configuration["Triage:RunbookLookupSeverities"] ?? "Critical,High";

// ── MCP Tool Server ───────────────────────────────────────────────────────────
var mcpServer = builder.AddProject<Projects.DotNetAspireTriageAgent_McpToolServer>("mcp-tools")
    .WithReference(qdrant)   // injects ConnectionStrings:vectorstore automatically
    .WaitFor(qdrant)
    // API keys
    .WithEnvironment("Groq__ApiKey",              groqApiKey)
    .WithEnvironment("Nomic__ApiKey",             nomicApiKey)
    // Groq LLM settings
    .WithEnvironment("Groq__Endpoint",            groqEndpoint)
    .WithEnvironment("Groq__Model",               groqModel)
    // Nomic embedding settings
    .WithEnvironment("Nomic__Endpoint",           nomicEndpoint)
    .WithEnvironment("Nomic__Model",              nomicModel)
    // Qdrant settings (standalone fallback endpoint + collection config)
    .WithEnvironment("Qdrant__DefaultEndpoint",   qdrantDefaultEndpoint)
    .WithEnvironment("Qdrant__CollectionName",    qdrantCollection)
    .WithEnvironment("Qdrant__TopK",              qdrantTopK)
    // PagerDuty stub
    .WithEnvironment("PagerDuty__StubEndpoint",   pagerDutyEndpoint);

// ── Agent Service ─────────────────────────────────────────────────────────────
builder.AddProject<Projects.DotNetAspireTriageAgent_AgentService>("agent-service")
    .WithReference(mcpServer)   // injects services:mcp-tools:http:0 automatically
    .WaitFor(mcpServer)
    // API key
    .WithEnvironment("Groq__ApiKey",              groqApiKey)
    // Groq LLM settings
    .WithEnvironment("Groq__Endpoint",            groqEndpoint)
    .WithEnvironment("Groq__Model",               groqModel)
    // MCP client settings (standalone fallback + timeout)
    .WithEnvironment("McpClient__DefaultUrl",      mcpClientDefaultUrl)
    .WithEnvironment("McpClient__TimeoutSeconds",  mcpClientTimeoutSeconds)
    // Triage pipeline settings
    .WithEnvironment("Triage__RunbookLookupSeverities", triageSeverities);

builder.Build().Run();
