namespace NicoJkPlugin;

/// <summary>
/// Keeps NicoJkPlugin's reservation polling observable without emitting a normal log every polling tick.
/// This gate is intentionally local to NicoJkPlugin and does not change TvAIr host-side scheduler/tuner logs.
/// </summary>
internal sealed class SessionPollingLogGate
{
    private static readonly TimeSpan ActiveHeartbeatInterval = TimeSpan.FromMinutes(10);

    private string? _lastSignature;
    private DateTimeOffset _lastActiveHeartbeat = DateTimeOffset.MinValue;

    public bool ShouldLogPollingSnapshot(int tick, string signature, bool hasActiveRecording, DateTimeOffset now, out string reason)
    {
        if (_lastSignature is null)
        {
            _lastSignature = signature;
            reason = "initial";
            if (hasActiveRecording)
                _lastActiveHeartbeat = now;
            return true;
        }

        if (!string.Equals(_lastSignature, signature, StringComparison.Ordinal))
        {
            _lastSignature = signature;
            reason = "state_changed";
            if (hasActiveRecording)
                _lastActiveHeartbeat = now;
            return true;
        }

        if (hasActiveRecording && now - _lastActiveHeartbeat >= ActiveHeartbeatInterval)
        {
            _lastActiveHeartbeat = now;
            reason = "active_heartbeat_10min";
            return true;
        }

        reason = "suppressed_normal_tick";
        return false;
    }
}
