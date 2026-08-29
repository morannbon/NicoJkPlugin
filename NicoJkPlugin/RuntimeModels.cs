namespace NicoJkPlugin;

internal sealed class RecordingInfo
{
    public string ReservationId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public int NetworkId { get; init; }
    public int TransportStreamId { get; init; }
    public int ServiceId { get; init; }
    public string ServiceName { get; init; } = string.Empty;
    public DateTimeOffset ActualStartTime { get; init; }
    public DateTimeOffset? ActualEndTime { get; init; }
}

internal sealed class CommentPublish
{
    public string PluginId { get; init; } = string.Empty;
    public string ReservationId { get; init; } = string.Empty;
    public string ServiceName { get; init; } = string.Empty;
    public string ProgramTitle { get; init; } = string.Empty;
    public int NetworkId { get; init; }
    public int TransportStreamId { get; init; }
    public int ServiceId { get; init; }
    public int JkChannel { get; init; }
    public long UnixTime { get; init; }
    public int? Vpos { get; init; }
    public string UserId { get; init; } = string.Empty;
    public string Mail { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; init; }
}
