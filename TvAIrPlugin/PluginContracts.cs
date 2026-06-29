namespace TvAIrPlugin;

public enum TvAIrPluginKind
{
    Unknown = 0,
    Analysis = 1,
    Viewer = 2,
    Utility = 3,
    UI = 4,
    Companion = 5,
    Remote = 6,
    Headless = 7
}

public enum PluginLogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

public enum PluginPermission
{
    ReadEpg,
    ReadReservations,
    WriteReservations,
    PreviewAllocation,
    ReadTunerStatus,
    UsePluginStorage,
    ShowUi,
    ShowNotification,
    LaunchExternalProcess,
    ControlViewer,
    ReadChannels,
    ReadRecordingStatus,
    ReadRecordingHistory,
    ReadWakePlan,
    ReadLogs,
    ReadTheme,
    ReadPluginStorage,
    WritePluginStorage,
    ReadSafePaths,
    ManageReservations,
    ManageAutoSearch,
    ControlRecording,
    ControlEpg,
    ControlWake,
    ShowNotifications,
    ReadSystemStatus,
    ReadEpgStatus,
    ReadKeywordRules,
    ReadProgramRules,
    ReadRecordingQuality,
    ReadProgramGuideProjection,
    ReadViewerSessions,
    ReadViewerTuners,
    ReadViewerControlContracts,
    ReadHostContracts,
    OpenPage,
    OpenToolWindow,
    UseActionApi,
    UseWindowApi,
    UseAssetApi,
    UseSafeEvent,
    UseRemoteAccess,
    UsePairing,
    UseLocalNetwork
}

public sealed class PluginManifest
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Route { get; set; } = string.Empty;
    public string DefaultRoute { get; set; } = string.Empty;
    public string Entry { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Vendor { get; set; } = string.Empty;
    public string HostContractVersion { get; set; } = string.Empty;
    public string SdkVersion { get; set; } = string.Empty;
    public IReadOnlyList<string> Capabilities { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();
    public string Icon { get; set; } = string.Empty;
    public string PreferredOpenMode { get; set; } = string.Empty;
    public int ToolWindowWidth { get; set; } = 620;
    public int ToolWindowHeight { get; set; } = 760;
    public int ToolWindowMinWidth { get; set; } = 0;
    public int ToolWindowMinHeight { get; set; } = 0;
    public bool ToolWindowReuseExisting { get; set; } = true;
    public bool ToolWindowActivateExisting { get; set; } = true;
    public string DefaultMenuActionKind { get; set; } = string.Empty;
    public string DefaultMenuActionLabel { get; set; } = string.Empty;
    public int DefaultMenuActionPriority { get; set; } = 1000;
    public bool ToolWindowShowInTaskbar { get; set; } = false;
    public IReadOnlyList<string> Kind { get; set; } = Array.Empty<string>();
    public IReadOnlyList<PluginPermission> Permissions { get; set; } = Array.Empty<PluginPermission>();
}

public interface IManifestPlugin : ITvAIrPlugin
{
    PluginManifest Manifest { get; }
}

public sealed class PluginUiDescriptor
{
    public string RouteSegment { get; set; } = string.Empty;
    public string MenuText { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int DisplayOrder { get; set; } = 1000;
    public bool Enabled { get; set; } = true;
    public string PreferredOpenMode { get; set; } = string.Empty;
    public string DefaultMenuActionKind { get; set; } = string.Empty;
    public string DefaultMenuActionLabel { get; set; } = string.Empty;
    public int DefaultMenuActionPriority { get; set; } = 1000;
    public bool ToolWindowShowInTaskbar { get; set; } = false;
    public int ToolWindowWidth { get; set; } = 620;
    public int ToolWindowHeight { get; set; } = 760;
    public int ToolWindowMinWidth { get; set; } = 0;
    public int ToolWindowMinHeight { get; set; } = 0;
}

public sealed class PluginUiContext
{
    public string Route { get; init; } = string.Empty;
}

public interface IUiPlugin : ITvAIrPlugin
{
    PluginUiDescriptor Ui { get; }
    string RenderHtml(PluginUiContext context);
}

public sealed class PluginRecordingInfo
{
    public int ReservationId { get; init; }
    public string Title { get; init; } = string.Empty;
    public ushort NetworkId { get; init; }
    public ushort TransportStreamId { get; init; }
    public ushort ServiceId { get; init; }
    public string ServiceName { get; init; } = string.Empty;
    public DateTimeOffset ActualStartTime { get; init; }
    public DateTimeOffset? ActualEndTime { get; init; }
    public string OutputFilePath { get; init; } = string.Empty;
}

public sealed class PluginPlaybackInfo
{
    public string FilePath { get; init; } = string.Empty;
    public ushort NetworkId { get; init; }
    public ushort TransportStreamId { get; init; }
    public ushort ServiceId { get; init; }
    public string ServiceName { get; init; } = string.Empty;
    public DateTimeOffset BroadcastStartTime { get; init; }
    public TimeSpan Duration { get; init; }
    public nint WindowHandle { get; init; }
}

public sealed class PluginPlaybackPosition
{
    public string FilePath { get; init; } = string.Empty;
    public TimeSpan Position { get; init; }
    public DateTimeOffset BroadcastTime { get; init; }
}

public sealed class LiveCommentEvent
{
    public string PluginId { get; init; } = string.Empty;
    public string ReservationId { get; init; } = string.Empty;
    public string ServiceName { get; init; } = string.Empty;
    public string ProgramTitle { get; init; } = string.Empty;
    public int JkChannel { get; init; }
    public long UnixTime { get; init; }
    public int? Vpos { get; init; }
    public string UserId { get; init; } = string.Empty;
    public string Mail { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.Now;
}

public interface ILiveCommentPublisher
{
    void PublishLiveComment(LiveCommentEvent comment);
}

public sealed class PluginReservationQuery
{
    public bool IncludeEpgSystemEntries { get; set; }
}

public sealed class PluginReservation
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public ushort NetworkId { get; set; }
    public ushort TransportStreamId { get; set; }
    public ushort ServiceId { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public string? Status { get; set; }
    public DateTime EndTime { get; set; }
}

public sealed class PluginTunerStatus
{
    public int? ReservationId { get; set; }
    public string? UsageKind { get; set; }
}

public sealed class PluginEpgQuery { }
public sealed class PluginEpgEvent { }
public sealed class PluginReservationHistoryQuery { }
public sealed class PluginConflictInfo { }
public sealed class PluginReservationDraft { }
public sealed class PluginReservationPreview { }
public sealed class PluginChainInfo { }
public sealed class PluginChainQuery { }
public sealed class PluginChainPreview { }
public sealed class PluginReservationOperationResult { }
public sealed class PluginReservationUpdate { }
