// DotNetAspireTriageAgent.McpToolServer/RunbookSeeder.cs
using Microsoft.Extensions.AI;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace DotNetAspireTriageAgent.McpToolServer;

/// <summary>Seeds the Qdrant runbook collection on startup if it does not already exist.</summary>
public sealed class RunbookSeeder(
    QdrantClient qdrantClient,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    ILogger<RunbookSeeder> logger) : IHostedService
{
    private static readonly (string Title, string Category, string Content)[] RunbookData =
    [
        ("CPU Spike Runbook", "cpu_spike",
            "1. Identify top CPU consumers via `top -b -n1`. 2. Check for runaway processes. 3. Scale out horizontally if load is legitimate. 4. Review cgroup limits and throttle noisy neighbours."),
        ("Memory OOM Runbook", "memory_oom",
            "1. Capture heap dump before OOM kill: `jmap -dump:live,format=b`. 2. Restart affected pods with memory limit increase. 3. Analyse heap for leaks using Eclipse MAT. 4. Add JVM GC logging."),
        ("Disk I/O Degradation Runbook", "disk_io",
            "1. Run `iostat -x 1 5` to identify saturation. 2. Check for large sequential writes from batch jobs. 3. Move hot data to SSD-backed volumes. 4. Enable I/O throttling for non-critical workloads."),
        ("Network Packet Loss Runbook", "network_packet_loss",
            "1. Run `ping -c 100` to quantify loss. 2. Check NIC driver errors via `ethtool -S eth0`. 3. Review MTU settings. 4. Escalate to network team if loss > 5%."),
        ("Elevated Error Rate Runbook", "elevated_error_rate",
            "1. Query Grafana for 5xx rate by endpoint. 2. Correlate with recent deployments. 3. Roll back if error spike matches deploy timestamp. 4. Enable detailed trace sampling for affected endpoints."),
        ("General Incident Response", "general",
            "1. Acknowledge alert and create incident ticket. 2. Gather initial facts: what changed, when, who is affected. 3. Communicate status via status page. 4. Begin RCA within 24 h of resolution."),
    ];

    /// <summary>Seeds the runbooks collection in Qdrant if it does not yet exist.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var collections = await qdrantClient.ListCollectionsAsync(cancellationToken);
            if (collections.Any(c => c == Tools.RunbookLookupTool.CollectionName))
            {
                logger.LogInformation("Runbook collection already exists — skipping seed");
                return;
            }

            logger.LogInformation("Seeding runbook collection…");

            // Microsoft.Extensions.AI 9.7: GenerateVectorAsync returns ReadOnlyMemory<float>
            var sampleVector = await embeddingGenerator.GenerateVectorAsync(
                RunbookData[0].Category, cancellationToken: cancellationToken);
            var vectorSize = (ulong)sampleVector.Length;

            // Qdrant.Client 1.12: CreateCollectionAsync takes VectorParams directly (no VectorsConfig wrapper)
            await qdrantClient.CreateCollectionAsync(
                Tools.RunbookLookupTool.CollectionName,
                new VectorParams { Size = vectorSize, Distance = Distance.Cosine },
                cancellationToken: cancellationToken);

            var points = new List<PointStruct>();
            for (var i = 0; i < RunbookData.Length; i++)
            {
                var (title, category, content) = RunbookData[i];
                var vec = await embeddingGenerator.GenerateVectorAsync(
                    $"{category} {title}", cancellationToken: cancellationToken);

                // Qdrant.Client 1.12: implicit operator float[] → Vectors is available
                var point = new PointStruct
                {
                    Id = new PointId { Num = (ulong)(i + 1) },
                    Vectors = vec.ToArray()   // float[] implicit → Vectors
                };
                point.Payload["title"] = title;
                point.Payload["category"] = category;
                point.Payload["content"] = content;
                points.Add(point);
            }

            await qdrantClient.UpsertAsync(
                Tools.RunbookLookupTool.CollectionName,
                points,
                cancellationToken: cancellationToken);

            logger.LogInformation("Seeded {Count} runbook entries into Qdrant", points.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Runbook seeding failed — agent will operate without runbook context");
        }
    }

    /// <summary>No teardown required.</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
