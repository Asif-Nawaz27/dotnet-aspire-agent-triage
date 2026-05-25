# IncidentTriageAgent

Production-grade incident triage agent built with:

- **.NET 10 / C# 14** — `field` keyword, `params ReadOnlySpan<string>`, collection expressions
- **Semantic Kernel 1.33** — prompt filters, FunctionChoiceBehavior, KernelPlugin
- **Microsoft.Extensions.AI 9.5** — `IChatClient`, structured output, `IEmbeddingGenerator`
- **MCP C# SDK 0.2 / 1.0** — `[McpServerTool]`, streamable HTTP transport
- **.NET Aspire 9.3** — AppHost orchestration, service discovery, OTEL dashboard
- **Qdrant** — vector similarity search for runbook lookup
- **Ollama** — on-premise LLM (`llama3.2`) + embeddings (`nomic-embed-text`)

## Architecture

```
[POST /triage]
      │
      ▼
IncidentTriageAgentService          (AgentService)
  ├─ Step 2: ClassifyAlert          → MCP: AlertClassifierTool  (IChatClient + structured output)
  ├─ Step 3: LookupRunbook          → MCP: RunbookLookupTool    (Qdrant vector search)
  ├─ Step 4: Remediate+Escalate     → MCP: PagerDutyTool        (HTTP stub)
  └─ Step 5: WriteAuditEntry        → MCP: AuditWriterTool      (ConcurrentQueue + /audit endpoint)

PromptInjectionFilter               (IPromptRenderFilter — scans every rendered prompt)
Aspire AppHost                      (Qdrant container + Ollama container + service discovery)
```

## Prerequisites

| Tool | Version | Notes |
|------|---------|-------|
| .NET SDK | 10.0+ | `dotnet --version` |
| Docker Desktop | 4.x+ | For Qdrant and Ollama containers |
| Ollama | latest | Pull models before first run |

### Pull Ollama models

```bash
ollama pull llama3.2
ollama pull nomic-embed-text
```

## Run the solution

```bash
# From the repo root — Aspire starts all services and containers
dotnet run --project IncidentTriageAgent.AppHost
```

Open the Aspire dashboard at **http://localhost:15888** to view traces, metrics, and logs.

## Trigger a triage

```bash
curl -X POST http://localhost:5000/triage \
  -H "Content-Type: application/json" \
  -d '{
    "id": "demo-001",
    "source": "prometheus",
    "body": "node_cpu_usage_percent on web-prod-01 = 97%. Sustained for 10 minutes.",
    "receivedAt": "2026-05-26T12:00:00Z"
  }'
```

Expected response shape:

```json
{
  "classification": { "severity": "Critical", "category": "cpu_spike", "confidence": 0.92 },
  "proposal": { "actionSteps": ["..."], "estimatedImpact": "...", "confidence": 0.85 },
  "escalation": { "escalated": true, "ticketId": "INC-demo001" },
  "audit": { "alertId": "demo-001", "severity": "Critical", "elapsedMs": 1234 },
  "injectionDetected": false
}
```

## Inspect the audit log

```bash
curl http://localhost:5100/audit
```

## Run evals

The eval harness posts all six golden cases to the live agent service and reports pass/fail.
Exits with code 1 if fewer than 5 of 6 cases pass (CI drift threshold).

```bash
# Ensure the full Aspire stack is running first, then:
dotnet run --project IncidentTriageAgent.Evals -- http://localhost:5000
```

Example output:

```
Status   │ AlertId      │ Expected   │ Actual     │ Escalation            │ Injection
──────────────────────────────────────────────────────────────────────────────────────────────────────
PASS     │ eval-001     │ Critical   │ Critical   │ PASS                  │ n/a
PASS     │ eval-002     │ Critical   │ Critical   │ PASS                  │ n/a
PASS     │ eval-003     │ High       │ High       │ PASS                  │ n/a
PASS     │ eval-004     │ High       │ High       │ PASS                  │ n/a
PASS     │ eval-005     │ Medium     │ Medium     │ PASS                  │ n/a
PASS     │ eval-006     │ High       │ High       │ PASS                  │ DETECTED
──────────────────────────────────────────────────────────────────────────────────────────────────────
Results: 6 PASS / 0 FAIL out of 6 cases
Eval passed — drift threshold satisfied.
```

## Project structure

```
IncidentTriageAgent.sln
├── IncidentTriageAgent.AppHost/          Aspire orchestration
├── IncidentTriageAgent.ServiceDefaults/  Shared OTEL + health checks
├── IncidentTriageAgent.AgentService/     Semantic Kernel triage pipeline
│   ├── Agents/IncidentTriageAgent.cs     Five-step triage loop
│   ├── Filters/PromptInjectionFilter.cs  IPromptRenderFilter (< 40 lines)
│   └── Models/AlertClassification.cs     All data contracts
├── IncidentTriageAgent.McpToolServer/    MCP C# SDK tool server
│   ├── RunbookSeeder.cs                  Seeds Qdrant collection on startup
│   └── Tools/
│       ├── AlertClassifierTool.cs        Structured severity classification
│       ├── RunbookLookupTool.cs          Qdrant vector search
│       ├── PagerDutyTool.cs              HTTP escalation stub
│       └── AuditWriterTool.cs            Append-only audit log
└── IncidentTriageAgent.Evals/            Deterministic eval harness
    └── Fixtures/golden-alerts.json       6 seeded test cases
```

## Key design decisions

**On-premise LLM only** — all AI calls route through Ollama. No `AZURE_OPENAI_*` or `OPENAI_API_KEY` env vars are read.

**Zero secrets in source** — Aspire passes connection strings via `services:*:http:0` environment variables at runtime.

**Prompt injection defence** — `PromptInjectionFilter` (38 lines) sanitises rendered prompts before they reach the LLM and tags the parent OTEL Activity with `security.injection_detected = true`.

**MCP tool isolation** — each tool is independently deployable; the agent communicates via MCP protocol only, not direct project references.

**Observability** — every triage step emits a named Activity span (`triage.classify`, `triage.enrich`, `triage.remediate`, `triage.escalate`, `triage.audit`) visible in the Aspire OTEL dashboard.

## Package version notes

Some packages target pre-release milestones aligned with .NET 10 / Aspire 9.3.
Run `dotnet restore` and update to the latest stable releases if build errors occur:

```bash
dotnet add IncidentTriageAgent.McpToolServer package ModelContextProtocol.AspNetCore --prerelease
dotnet add IncidentTriageAgent.AgentService package Microsoft.SemanticKernel.Connectors.Ollama --prerelease
```
