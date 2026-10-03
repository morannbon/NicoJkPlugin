using System.Diagnostics;

namespace NicoJkPlugin;

/// <summary>
/// Canonical boundary for developer-only diagnostics.
///
/// Development builds define TVAIR_PLUGIN_DEVELOPER_DIAGNOSTICS. Public
/// release builds compile with that symbol disabled. Calls to Write are then
/// removed by the C# compiler, including evaluation of their arguments.
/// Diagnostic-only measurement, caches, timers, subscriptions, or other work
/// must also remain behind this same symbol boundary instead of introducing
/// separate feature flags. User-facing operational logging is not routed here.
/// </summary>
internal static class DeveloperDiagnostics
{
    [Conditional("TVAIR_PLUGIN_DEVELOPER_DIAGNOSTICS")]
    public static void Write(Action<string> log, string message)
    {
        ArgumentNullException.ThrowIfNull(log);
        log(message);
    }
}
