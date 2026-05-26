// DotNetAspireTriageAgent.AgentService/Filters/InjectionDetectionContext.cs
namespace DotNetAspireTriageAgent.AgentService.Filters;

/// <summary>
/// Scoped per-request context that carries the injection detection flag
/// from PromptInjectionFilter to the triage pipeline.
/// </summary>
public sealed class InjectionDetectionContext
{
    /// <summary>Set to true if the injection filter sanitised the rendered prompt.</summary>
    public bool InjectionDetected { get; set; }
}
