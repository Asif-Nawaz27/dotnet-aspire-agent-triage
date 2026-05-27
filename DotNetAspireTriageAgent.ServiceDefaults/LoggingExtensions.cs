// DotNetAspireTriageAgent.ServiceDefaults/LoggingExtensions.cs
// Single source-of-truth for Serilog configuration across the whole solution.
//
// Usage
// ─────
//  Web / hosted services (AgentService, McpToolServer)
//      // 1. Before CreateBuilder — catches startup crashes:
//      SerilogLoggingExtensions.ConfigureBootstrapLogger();
//      try {
//          var builder = WebApplication.CreateBuilder(args);
//          // 2. Full logger wired into the host DI pipeline:
//          builder.Host.UseSerilogLogging("AgentService");
//          ...
//      } catch (Exception ex) when (ex is not HostAbortedException) {
//          Log.Fatal(ex, "...");  throw;
//      } finally { await Log.CloseAndFlushAsync(); }
//
//  Plain console apps (Evals — no generic host)
//      Log.Logger = SerilogLoggingExtensions.CreateStandaloneLogger("Evals");
//      try { ... }
//      finally { await Log.CloseAndFlushAsync(); }

using Serilog;
using Serilog.Events;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Centralised Serilog helpers shared by every project in the solution.
/// </summary>
public static class SerilogLoggingExtensions
{
    // ── Console output templates ─────────────────────────────────────────────
    // Hosted services print SourceContext so you can see which class logged what.
    private const string HostedConsoleTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}{NewLine}  {Message:lj}{NewLine}{Exception}";

    // Standalone console apps (Evals) omit SourceContext — output is already compact.
    private const string StandaloneConsoleTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";

    // File template is the same for all projects — full timestamp, level, source, message.
    private const string FileTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}";

    // ── Bootstrap logger ─────────────────────────────────────────────────────

    /// <summary>
    /// Configures a minimal console-only Serilog logger that is active <em>before</em>
    /// the generic host is built.  Call this as the very first line in Program.cs.
    /// The bootstrap logger is automatically replaced by the full logger once
    /// <see cref="UseSerilogLogging"/> runs inside the host startup pipeline.
    /// </summary>
    public static void ConfigureBootstrapLogger()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: StandaloneConsoleTemplate)
            .CreateBootstrapLogger();
    }

    // ── Hosted services (WebApplication / generic host) ──────────────────────

    /// <summary>
    /// Wires Serilog into the generic host so that <c>ILogger&lt;T&gt;</c> injected by
    /// DI writes to both the console and a rolling file under
    /// <c>&lt;SolutionRoot&gt;\Logs\{projectName}\</c>.
    /// </summary>
    /// <param name="hostBuilder">The <see cref="IHostBuilder"/> from <c>builder.Host</c>.</param>
    /// <param name="projectName">
    ///     Folder name used for the log directory and file prefix, e.g. <c>"AgentService"</c>.
    /// </param>
    /// <param name="fileSizeLimitBytes">
    ///     Maximum size of a single log file before rolling on size (default 50 MB).
    /// </param>
    public static IHostBuilder UseSerilogLogging(
        this IHostBuilder hostBuilder,
        string projectName,
        long fileSizeLimitBytes = 50_000_000)
    {
        var logPath = GetLogFilePath(projectName);

        return hostBuilder.UseSerilog((_, _, cfg) =>
            ConfigureFullLogger(cfg, logPath, HostedConsoleTemplate, fileSizeLimitBytes,
                isHosted: true));
    }

    // ── Standalone console apps (Evals — no generic host) ────────────────────

    /// <summary>
    /// Creates a fully configured Serilog <see cref="Serilog.ILogger"/> for use in
    /// plain console apps that do not use the generic host.
    /// Assign the result to <c>Log.Logger</c> and call <c>Log.CloseAndFlushAsync()</c>
    /// in a <c>finally</c> block.
    /// </summary>
    /// <param name="projectName">
    ///     Folder name used for the log directory and file prefix, e.g. <c>"Evals"</c>.
    /// </param>
    /// <param name="fileSizeLimitBytes">
    ///     Maximum size of a single log file before rolling on size (default 10 MB).
    /// </param>
    public static Serilog.ILogger CreateStandaloneLogger(
        string projectName,
        long fileSizeLimitBytes = 10_000_000)
    {
        var logPath = GetLogFilePath(projectName);
        var cfg     = new LoggerConfiguration();
        ConfigureFullLogger(cfg, logPath, StandaloneConsoleTemplate, fileSizeLimitBytes,
            isHosted: false);
        return cfg.CreateLogger();
    }

    // ── Path helper (public — services log it on startup) ────────────────────

    /// <summary>
    /// Returns the absolute path for the rolling log file of the given project.
    /// <para>
    ///     Directory layout: <c>&lt;SolutionRoot&gt;\Logs\{projectName}\{projectName.lower}-.log</c>
    /// </para>
    /// <para>
    ///     The path is computed by walking 4 levels up from
    ///     <see cref="AppContext.BaseDirectory"/> (<c>bin\Debug\net10.0\</c>)
    ///     to reach the solution root.  Serilog appends the date automatically
    ///     when <c>rollingInterval</c> is set, producing names like
    ///     <c>agentservice-20260527.log</c>.
    /// </para>
    /// </summary>
    public static string GetLogFilePath(string projectName) =>
        Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                $"../../../../Logs/{projectName}/{projectName.ToLowerInvariant()}-.log"));

    // ── Private shared configuration ─────────────────────────────────────────

    private static void ConfigureFullLogger(
        LoggerConfiguration cfg,
        string logPath,
        string consoleTemplate,
        long fileSizeLimitBytes,
        bool isHosted)
    {
        cfg.Enrich.FromLogContext();

        // ── Minimum levels ────────────────────────────────────────────────────
        if (isHosted)
        {
            // Hosted services: suppress noisy framework namespaces, keep our own at Info
            cfg
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft",                  LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
                .MinimumLevel.Override("Grpc",                       LogEventLevel.Warning)
                .MinimumLevel.Override("System.Net.Http",            LogEventLevel.Warning);
        }
        else
        {
            // Standalone apps: capture Debug to the file; Info+ on the console
            cfg
                .MinimumLevel.Debug()
                .MinimumLevel.Override("System.Net.Http",      LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.Extensions", LogEventLevel.Warning);
        }

        // ── Sinks ─────────────────────────────────────────────────────────────
        cfg.WriteTo.Console(
            outputTemplate:           consoleTemplate,
            restrictedToMinimumLevel: isHosted
                ? LogEventLevel.Information  // no Debug noise on service console
                : LogEventLevel.Information  // same for standalone; Debug goes to file only
        );

        cfg.WriteTo.File(
            logPath,
            rollingInterval:        RollingInterval.Day,
            outputTemplate:         FileTemplate,
            retainedFileCountLimit: 14,
            fileSizeLimitBytes:     fileSizeLimitBytes,
            rollOnFileSizeLimit:    true,
            shared:                 false);
    }
}
