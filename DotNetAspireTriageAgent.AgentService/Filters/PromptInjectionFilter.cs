// DotNetAspireTriageAgent.AgentService/Filters/PromptInjectionFilter.cs
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.SemanticKernel;

namespace DotNetAspireTriageAgent.AgentService.Filters;

/// <summary>
/// Scans rendered prompts for injection patterns, replaces them with [SANITISED],
/// and tags the parent Activity. Never throws — sanitises and continues.
/// </summary>
public sealed partial class PromptInjectionFilter(
    InjectionDetectionContext ctx, ILogger<PromptInjectionFilter> log) : IPromptRenderFilter
{
    [GeneratedRegex(
        @"(ignore\s+previous\s+instructions?|disregard\s+above|new\s+task\s*:|forget\s+everything|override\s+instructions?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex Pattern();

    /// <summary>After prompt render: scan, sanitise, tag Activity if injection found.</summary>
    public async Task OnPromptRenderAsync(PromptRenderContext context, Func<PromptRenderContext, Task> next)
    {
        await next(context);

        var rendered = context.RenderedPrompt;
        if (string.IsNullOrEmpty(rendered) || !Pattern().IsMatch(rendered)) return;

        context.RenderedPrompt = Pattern().Replace(rendered, "[SANITISED]");
        ctx.InjectionDetected = true;
        Activity.Current?.SetTag("security.injection_detected", true);
        log.LogWarning("Prompt injection sanitised. TraceId={TraceId}", Activity.Current?.TraceId.ToString() ?? "n/a");
    }
}
