using Microsoft.Extensions.Logging;

namespace SdlVulkan.Renderer;

/// <summary>
/// Process-wide logger for the renderer's unconditional diagnostics — GPU-wedge forensics, recovery
/// decisions, driver anomalies. A host with logging infrastructure points it at its own factory once
/// at startup:
/// <code>SdlVulkanLog.Logger = loggerFactory.CreateLogger("SdlVulkan.Renderer");</code>
/// and the renderer's lines pick up that host's timestamps, levels and routing.
/// <para>
/// The default is a minimal stderr sink, not <c>NullLogger</c>, and that is deliberate: these lines
/// are the renderer's black box, and a host that never wires a logger (tests, probes, tools) must
/// still get them in its stderr capture — a wedge report with no breadcrumb is how this subsystem
/// stayed mis-diagnosed for a month. DEBUG-only diagnostics (<see cref="RenderDiag"/>) stay on raw
/// stderr on purpose: they are compile-removed in Release and their <c>[rdiag]</c> format is what
/// log-grepping tooling keys on.
/// </para>
/// Set once at startup. Reads are unsynchronized by design — a reference read is atomic, and
/// call sites include driver callbacks on arbitrary threads (ILogger implementations are
/// required to be thread-safe).
/// </summary>
public static class SdlVulkanLog
{
    public static ILogger Logger { get; set; } = StderrLogger.Instance;

    private sealed class StderrLogger : ILogger
    {
        public static readonly StderrLogger Instance = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Console.Error.WriteLine(formatter(state, exception));
    }
}
