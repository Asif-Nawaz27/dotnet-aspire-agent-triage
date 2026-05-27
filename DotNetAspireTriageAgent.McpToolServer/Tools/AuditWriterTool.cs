// DotNetAspireTriageAgent.McpToolServer/Tools/AuditWriterTool.cs
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace DotNetAspireTriageAgent.McpToolServer.Tools;

/// <summary>Thread-safe, append-only in-memory audit log singleton shared between tool invocations.</summary>
public sealed class AuditLog(ILogger<AuditLog> logger)
{
    // C# 14: field keyword removes the explicit backing field for Count
    public int Count
    {
        get => field;
        private set => field = value;
    }

    private readonly ConcurrentQueue<AuditLogEntry> _entries = new();

    /// <summary>Appends a new entry, writes it to the log file via ILogger, and returns the UTC timestamp.</summary>
    public DateTime Append(string alertId, string payload)
    {
        var timestamp = DateTime.UtcNow;
        _entries.Enqueue(new AuditLogEntry(alertId, payload, timestamp));
        Count++;

        // Serilog file sink is thread-safe — no lock needed
        logger.LogInformation("AUDIT | AlertId={AlertId} | Payload={Payload}", alertId, payload);

        return timestamp;
    }

    /// <summary>Returns a snapshot of all audit entries.</summary>
    public IReadOnlyList<AuditLogEntry> GetAll() => _entries.ToArray();
}

/// <summary>A single audit log entry.</summary>
public sealed record AuditLogEntry(string AlertId, string Payload, DateTime WrittenAt);

/// <summary>MCP tool that writes triage results to an append-only in-memory audit log.</summary>
[McpServerToolType]
public sealed class AuditWriterTool(AuditLog auditLog, ILogger<AuditWriterTool> logger)
{
    private static readonly ActivitySource ActivitySource = new("DotNetAspireTriageAgent.McpToolServer");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Appends the full triage result JSON to the audit log.
    /// Never throws — all exceptions are caught and logged.
    /// Returns {written: true, timestamp: string}.
    /// </summary>
    [McpServerTool]
    [Description("Appends the triage result JSON to the append-only audit log. Returns {written: bool, timestamp: string}.")]
    public async Task<string> WriteAuditEntry(
        [Description("Full triage result serialised as a JSON string")] string triageResultJson,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("triage.audit_write", ActivityKind.Internal);

        try
        {
            // Parse alert id from payload for correlation — best-effort
            string alertId = "unknown";
            try
            {
                using var doc = JsonDocument.Parse(triageResultJson);
                if (doc.RootElement.TryGetProperty("audit", out var audit)
                    && audit.TryGetProperty("alertId", out var id))
                {
                    alertId = id.GetString() ?? "unknown";
                }
            }
            catch { /* id extraction is non-critical */ }

            var timestamp = auditLog.Append(alertId, triageResultJson);

            activity?.SetTag("audit.alert_id", alertId);
            activity?.SetTag("audit.total_entries", auditLog.Count);

            logger.LogInformation("Audit entry written for AlertId={AlertId} at {Timestamp:O}", alertId, timestamp);

            // Await a completed task so the method stays genuinely async
            await Task.CompletedTask;

            return JsonSerializer.Serialize(
                new { written = true, timestamp = timestamp.ToString("O") },
                JsonOptions);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("WriteAuditEntry cancelled");
            throw;
        }
        catch (Exception ex)
        {
            // AuditWriterTool must never propagate exceptions
            logger.LogError(ex, "Audit write failed — suppressing exception per contract");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return JsonSerializer.Serialize(
                new { written = false, timestamp = DateTime.UtcNow.ToString("O") },
                JsonOptions);
        }
    }
}
