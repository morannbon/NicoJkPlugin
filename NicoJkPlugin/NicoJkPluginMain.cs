using TvAIrPlugin;
using TvAIrPlugin.Runtime;

namespace NicoJkPlugin;

public sealed class NicoJkRuntimePlugin : ITvAirRuntimeCapabilityPlugin, ITvAirRuntimeUiPlugin, ITvAirRuntimeLifecyclePlugin
{
    private static readonly object Sync = new();
    private static ITvAirPluginRuntimeContext? _context;
    private static PluginSettings? _settings;
    private static JkChannelResolver? _resolver;
    private static CommentPublisher? _publisher;
    private static readonly Dictionary<string, RecordingSession> Sessions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> UnsupportedRecordings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<IDisposable> EventSubscriptions = new();
    private static string _statusWindowId = string.Empty;
    private static string _lastProjectedStatus = "正常";
    private static long _statusRevision;

    public TvAirPluginRuntimeDescriptor Descriptor { get; } = new()
    {
        PluginId = PluginIdentity.Id,
        DisplayName = PluginIdentity.Name,
        Version = PluginIdentity.Version,
        SdkContractVersion = TvAIrPluginSdkContract.SdkVersion,
        RequiredCapabilities = new[] { TvAirRuntimeCapabilities.BridgeEvents, TvAirRuntimeCapabilities.VideoOverlayWrite },
        RequiredPermissions = new[]
        {
            PluginPermission.ShowUi,
            PluginPermission.OpenToolWindow,
            PluginPermission.ReadChannels,
            PluginPermission.ReadRecordingStatus,
            PluginPermission.ReadSafePaths,
            PluginPermission.UseWindowApi,
            PluginPermission.UseSafeEvent,
            PluginPermission.WriteLogs,
            PluginPermission.ReadViewerSessions,
            PluginPermission.WriteVideoOverlay,
            PluginPermission.UseInternetAccess
        },
        Windows = new[]
        {
            new TvAIrPlugin.Windows.PluginWindowDefinition
            {
                WindowDefinitionId = "main", Title = "NicoJK",
                InitialSize = new TvAIrPlugin.Windows.PluginWindowSize(420, 260),
                MinimumSize = new TvAIrPlugin.Windows.PluginWindowSize(360, 220),
                ShowInTaskbar = false, RememberPlacement = true
            }
        },
        Surfaces = new[]
        {
            new TvAIrPlugin.Surfaces.PluginSurfaceDefinition
            {
                SurfaceDefinitionId = "main.web", Kind = TvAIrPlugin.Surfaces.PluginSurfaceKind.Web,
                EntryPoint = "nicojk"
            }
        },
        UiDefinitions = new[]
        {
            new RuntimeUiDefinition
            {
                UiDefinitionId = "main", Route = "nicojk", Kind = RuntimeUiKind.ToolWindow,
                WindowDefinitionId = "main", SurfaceDefinitionId = "main.web"
            }
        },
        MenuActions = new[]
        {
            new PluginMenuActionDefinition
            {
                ActionId = "open",
                Label = "NicoJK",
                Kind = PluginMenuActionKind.ToolWindow,
                Priority = 700,
                Route = "nicojk",
                WindowDefinitionId = "main",
                SurfaceDefinitionId = "main.web",
                ShowInTaskbar = false
            }
        },
        Lifecycle = new PluginLifecycleDefinition()
    };

    private readonly NicoJkStatusRenderer _ui = new(GetDisplayStatus);

    public string RenderHtml(RuntimeUiRenderContext context)
    {
        CaptureStatusWindow(context);
        return _ui.RenderHtml(context);
    }

    public Task<RuntimeUiActionResult> HandleActionAsync(RuntimeUiActionContext context, CancellationToken cancellationToken)
        => Task.FromResult(new RuntimeUiActionResult
        {
            Succeeded = false, ErrorCode = "UnsupportedAction", Message = "この画面に操作項目はありません。"
        });

    public void Initialize(ITvAirPluginRuntimeContext context)
    {
        lock (Sync)
        {
            if (_context is not null) return;
            _context = context;
            var paths = context.Settings.GetPaths();
            _settings = PluginSettings.Load(paths.AppDirectory, paths.DataDirectory, Log);
            _resolver = new JkChannelResolver(_settings, Log);
            _publisher = new CommentPublisher(context, Log);
            EventSubscriptions.Add(context.Events.Subscribe(
                new TvAIrPlugin.Events.PluginEventSubscriptionRequest { EventType = TvAirEventType.RecordingStarted.ToString() },
                HandleRecordingStarted));
            EventSubscriptions.Add(context.Events.Subscribe(
                new TvAIrPlugin.Events.PluginEventSubscriptionRequest { EventType = TvAirEventType.RecordingCompleted.ToString() },
                _ => ReconcileRecordingSessions()));
            EventSubscriptions.Add(context.Events.Subscribe(
                new TvAIrPlugin.Events.PluginEventSubscriptionRequest { EventType = TvAirEventType.RecordingFailed.ToString() },
                _ => ReconcileRecordingSessions()));
            EventSubscriptions.Add(context.Events.Subscribe(
                new TvAIrPlugin.Events.PluginEventSubscriptionRequest { EventType = TvAirEventType.RecordingResultFinalized.ToString() },
                _ => ReconcileRecordingSessions()));
            EventSubscriptions.Add(context.Events.Subscribe(
                new TvAIrPlugin.Events.PluginEventSubscriptionRequest { EventType = TvAirEventType.ViewerSessionChanged.ToString() },
                _ => _publisher?.RefreshViewers()));
            EventSubscriptions.Add(context.Events.Subscribe(
                new TvAIrPlugin.Events.PluginEventSubscriptionRequest { EventType = TvAirEventType.RuntimeWindowLifecycleChanged.ToString() },
                HandleRuntimeWindowLifecycleChanged));
            EventSubscriptions.Add(context.Events.Subscribe(
                new TvAIrPlugin.Events.PluginEventSubscriptionRequest { EventType = TvAirEventType.PluginPermissionChanged.ToString() },
                _ => NotifyInternetAccessChanged()));
            ReconcileRecordingSessions();
        }
        Log($"[NicoJkPlugin] 起動完了 v{PluginIdentity.Version}");
    }

    public void OnStart() => ReconcileRecordingSessions();

    public void OnStop()
    {
        List<RecordingSession> sessions;
        lock (Sync)
        {
            sessions = Sessions.Values.ToList();
            Sessions.Clear();
            UnsupportedRecordings.Clear();
            foreach (var subscription in EventSubscriptions)
            {
                try { subscription.Dispose(); } catch { }
            }
            EventSubscriptions.Clear();
            _statusWindowId = string.Empty;
            _lastProjectedStatus = "正常";
            _publisher = null;
            _resolver = null;
            _settings = null;
            _context = null;
        }
        foreach (var session in sessions)
        {
            try { session.StopAsync().GetAwaiter().GetResult(); } catch { }
        }
    }

    private static void HandleRecordingStarted(TvAIrPlugin.Events.PluginEventEnvelope envelope)
    {
        if (envelope.Payload is not TvAirEventDto evt || evt.Reservation is null)
        {
            ReconcileRecordingSessions();
            return;
        }

        var reservation = evt.Reservation;
        StartSession(new TvAirRecordingSessionDto
        {
            ReservationId = reservation.ReservationId,
            ServiceName = reservation.ServiceName,
            ProgramTitle = reservation.EventTitle,
            NetworkId = reservation.NetworkId,
            TransportStreamId = reservation.TransportStreamId,
            ServiceId = reservation.ServiceId,
            Start = reservation.StartTime,
            ScheduledEnd = reservation.EndTime,
            State = "Recording"
        });
    }

    private static void ReconcileRecordingSessions()
    {
        var context = _context;
        if (context is null || _settings is null || _resolver is null || _publisher is null) return;

        var active = context.Recordings.ListActive();
        var activeIds = active.Select(session => session.ReservationId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var session in active) StartSession(session);
        StopSessionsExcept(activeIds);
        lock (Sync) UnsupportedRecordings.IntersectWith(activeIds);
        PublishDisplayStatus();
    }

    private static bool TryToServiceIdentity(int networkId, int transportStreamId, int serviceId, out ushort nid, out ushort tsid, out ushort sid)
    {
        nid = 0;
        tsid = 0;
        sid = 0;

        if ((uint)networkId > ushort.MaxValue ||
            (uint)transportStreamId > ushort.MaxValue ||
            (uint)serviceId > ushort.MaxValue)
            return false;

        nid = (ushort)networkId;
        tsid = (ushort)transportStreamId;
        sid = (ushort)serviceId;
        return true;
    }

    private static void StartSession(TvAirRecordingSessionDto reservation)
    {
        if (_settings is null || _resolver is null || _publisher is null) return;
        lock (Sync) { if (Sessions.ContainsKey(reservation.ReservationId)) return; }

        if (!TryToServiceIdentity(reservation.NetworkId, reservation.TransportStreamId, reservation.ServiceId, out var nid, out var tsid, out var sid))
        {
            Log($"[Session] skip reservation={reservation.ReservationId} service={reservation.ServiceName} nid={reservation.NetworkId} tsid={reservation.TransportStreamId} sid={reservation.ServiceId} reason=service_identity_out_of_range");
            return;
        }

        var jk = _resolver.Resolve(nid, tsid, sid, reservation.ServiceName);
        if (jk is null)
        {
            lock (Sync) UnsupportedRecordings.Add(reservation.ReservationId);
            PublishDisplayStatus();
            Log($"[Session] skip reservation={reservation.ReservationId} service={reservation.ServiceName} nid={nid} tsid={tsid} sid={sid} reason=jk_channel_unresolved");
            return;
        }

        lock (Sync) UnsupportedRecordings.Remove(reservation.ReservationId);
        PublishDisplayStatus();

        var info = new RecordingInfo
        {
            ReservationId = reservation.ReservationId,
            Title = reservation.ProgramTitle,
            NetworkId = reservation.NetworkId,
            TransportStreamId = reservation.TransportStreamId,
            ServiceId = reservation.ServiceId,
            ServiceName = reservation.ServiceName,
            ActualStartTime = reservation.Start,
            ActualEndTime = reservation.ScheduledEnd
        };
        var session = new RecordingSession(info, jk.Value, _settings, _context!.InternetAccess, _publisher.Publish, Log, PublishDisplayStatus);
        lock (Sync)
        {
            if (Sessions.ContainsKey(reservation.ReservationId)) return;
            Sessions[reservation.ReservationId] = session;
        }
        Log($"[SessionManager] 録画開始 reservation={reservation.ReservationId} service={reservation.ServiceName} jk{jk.Value}");
        session.Start();
        PublishDisplayStatus();
    }


    private static void NotifyInternetAccessChanged()
    {
        List<RecordingSession> sessions;
        lock (Sync) sessions = Sessions.Values.ToList();

        foreach (var session in sessions)
        {
            try { session.NotifyInternetAccessChanged(); }
            catch (Exception ex) { Log($"[Network] permission change notify failed: {ex.GetType().Name}: {ex.Message}"); }
        }
        PublishDisplayStatus();
    }

    private static void StopSessionsExcept(ISet<string> activeIds)
    {
        List<KeyValuePair<string, RecordingSession>> stopped;
        lock (Sync)
        {
            stopped = Sessions.Where(x => !activeIds.Contains(x.Key)).ToList();
            foreach (var x in stopped) Sessions.Remove(x.Key);
        }
        foreach (var x in stopped)
        {
            try { x.Value.StopAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { Log($"[Session] stop error reservation={x.Key} {ex.GetType().Name}: {ex.Message}"); }
        }
        if (stopped.Count > 0) PublishDisplayStatus();
    }

    private static string GetDisplayStatus()
    {
        lock (Sync) return GetDisplayStatusLocked();
    }

    private static string GetDisplayStatusLocked()
    {
        if (Sessions.Values.Any(x => x.Status == NicoJkClientStatus.ConnectionError)) return "接続できません";
        if (Sessions.Values.Any(x => x.Status == NicoJkClientStatus.Receiving)) return "実況受信中";
        if (Sessions.Values.Any(x => x.Status == NicoJkClientStatus.Unsupported) || UnsupportedRecordings.Count > 0) return "実況対象外";
        return "正常";
    }

    private static void CaptureStatusWindow(RuntimeUiRenderContext context)
    {
        if (!context.IsHostManagedWindowContent || string.IsNullOrWhiteSpace(context.CurrentWindowId)) return;
        lock (Sync)
        {
            _statusWindowId = context.CurrentWindowId;
            _lastProjectedStatus = GetDisplayStatusLocked();
        }
    }

    private static void HandleRuntimeWindowLifecycleChanged(TvAIrPlugin.Events.PluginEventEnvelope envelope)
    {
        if (envelope.Payload is not TvAirEventDto evt) return;
        var lifecycle = evt.RuntimeWindowLifecycle;
        if (lifecycle is null ||
            !string.Equals(lifecycle.PluginId, PluginIdentity.Id, StringComparison.OrdinalIgnoreCase) ||
            (lifecycle.State != TvAIrPlugin.Windows.PluginWindowLifecycleState.Closing &&
             lifecycle.State != TvAIrPlugin.Windows.PluginWindowLifecycleState.Closed))
            return;

        lock (Sync)
        {
            if (string.Equals(_statusWindowId, lifecycle.WindowInstanceId, StringComparison.OrdinalIgnoreCase))
                _statusWindowId = string.Empty;
        }
    }

    private static void PublishDisplayStatus()
    {
        ITvAirPluginRuntimeContext? context;
        string windowId;
        string status;

        lock (Sync)
        {
            status = GetDisplayStatusLocked();
            if (string.Equals(status, _lastProjectedStatus, StringComparison.Ordinal)) return;
            _lastProjectedStatus = status;
            context = _context;
            windowId = _statusWindowId;
        }

        if (context is null || string.IsNullOrWhiteSpace(windowId)) return;

        var revision = Interlocked.Increment(ref _statusRevision);
        try
        {
            context.Windows.PatchToolWindow(new TvAirToolWindowStatePatchRequestDto
            {
                WindowId = windowId,
                StateRevision = revision,
                UiPatches = new[] { RuntimeUiPatch.Text("nicojk-status", status) }
            });
        }
        catch { }
    }

    private static void Log(string message)
    {
        try { _context?.Logs.Write(new TvAirLogWriteDto { Level = "Info", Category = "NicoJkPlugin", Message = message }); }
        catch { }
    }
}

internal sealed class NicoJkStatusRenderer
{
    private readonly Func<string> _status;

    public NicoJkStatusRenderer(Func<string> status)
    {
        _status = status;
    }

    public string RenderHtml(RuntimeUiRenderContext context)
    {
        var pageBackground = ResolveThemeRole(context.ThemeContract, "pageBackground");
        var text = ResolveThemeRole(context.ThemeContract, "text");
        return $$"""
<div style="min-height:100vh;box-sizing:border-box;padding:18px 20px;font-family:system-ui,'Yu Gothic UI','Meiryo',sans-serif;line-height:1.55;background:{{pageBackground}};color:{{text}};">
  <div style="font-size:20px;font-weight:700;margin-bottom:4px;">NicoJK</div>
  <div style="font-size:13px;margin-bottom:14px;">バージョン {{PluginIdentity.Version}}</div>
  <div style="font-size:13px;margin-bottom:8px;">状態: <span id="nicojk-status" style="font-weight:600;">{{_status()}}</span></div>
  <div style="font-size:13px;">録画中の番組コメントを表示します。</div>
</div>
""";
    }

    private static string ResolveThemeRole(IReadOnlyDictionary<string, string> contract, string key)
    {
        if (contract.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            return value.Trim();

        return "inherit";
    }
}
