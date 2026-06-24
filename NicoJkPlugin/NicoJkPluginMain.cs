using TvAIrPlugin;

namespace NicoJkPlugin;

public sealed class NicoJkPluginMain : ITvAIrPlugin, IUiPlugin, IManifestPlugin
{
    private IPluginContext? _context;
    private PluginSettings? _settings;
    private JkChannelResolver? _resolver;
    private LiveCommentPublisher? _publisher;
    private readonly Dictionary<int, RecordingSession> _sessions = new();
    private readonly object _sync = new();
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private int _pollCount;
    private readonly SessionPollingLogGate _pollingLogGate = new();

    public string Name => PluginIdentity.Name;
    public string Version => PluginIdentity.Version;

    public PluginUiDescriptor Ui { get; } = new()
    {
        RouteSegment = "nicojk",
        MenuText = "NicoJK",
        Description = "NicoJK compatible live comments",
        DisplayOrder = 700,
        Enabled = true,
        PreferredOpenMode = "toolWindow",
        DefaultMenuActionKind = "toolWindow",
        DefaultMenuActionLabel = "NicoJK",
        DefaultMenuActionPriority = 700,
        ToolWindowShowInTaskbar = false,
        ToolWindowWidth = 420,
        ToolWindowHeight = 260,
        ToolWindowMinWidth = 360,
        ToolWindowMinHeight = 220
    };

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginIdentity.Id,
        Name = PluginIdentity.Name,
        Version = PluginIdentity.Version,
        Vendor = "NicoJkPlugin",
        HostContractVersion = "0.11.315",
        Route = "nicojk",
        DefaultRoute = "nicojk",
        Entry = "NicoJkPlugin.dll",
        Description = "TvAIr上でニコニコ実況コメント連携を行うためのTvAIrプラグインです。",
        PreferredOpenMode = "toolWindow",
        DefaultMenuActionKind = "toolWindow",
        DefaultMenuActionLabel = "NicoJK",
        DefaultMenuActionPriority = 700,
        ToolWindowWidth = 420,
        ToolWindowHeight = 260,
        ToolWindowMinWidth = 360,
        ToolWindowMinHeight = 220,
        ToolWindowShowInTaskbar = false,
        Capabilities = new[]
        {
            "ui",
            "companion",
            "live-comment",
            "recording-session-watch",
            "comment-normalization"
        },
        Tags = new[]
        {
            "nicojk",
            "nx-jikkyo",
            "comments",
            "recording"
        },
        Kind = new[]
        {
            nameof(TvAIrPluginKind.UI),
            nameof(TvAIrPluginKind.Companion)
        },
        Permissions = new[]
        {
            PluginPermission.ShowUi,
            PluginPermission.OpenPage,
            PluginPermission.OpenToolWindow,
            PluginPermission.ReadReservations,
            PluginPermission.ReadTunerStatus,
            PluginPermission.ReadChannels,
            PluginPermission.ReadRecordingStatus,
            PluginPermission.ReadHostContracts
        }
    };

    public void Initialize(IPluginContext context)
    {
        _context = context;
        _settings = PluginSettings.Load(context, Log);
        _resolver = new JkChannelResolver(_settings, Log);
        _publisher = new LiveCommentPublisher(context);
        SdkHostContractProbe.LogIfAvailable(context, Log);
        Log($"[NicoJkPlugin] Initialize 完了 v{Version} hostContract=0.11.315 manifestDefaultMenuActionKind=toolWindow uiDefaultMenuActionKind=toolWindow releaseVersionPinned=True");
    }

    public void OnStart()
    {
        StartMonitor();
        Log("[NicoJkPlugin] OnStart 完了");
    }

    public void OnStop()
    {
        StopMonitor();
        StopAllSessions();
        Log("[NicoJkPlugin] OnStop 完了");
    }

    public void OnRecordingStarted(PluginRecordingInfo info)
    {
        StartSession(info, "event");
    }

    public void OnRecordingStopped(PluginRecordingInfo info)
    {
        StopSession(info.ReservationId, info);
    }

    public string RenderHtml(PluginUiContext context)
    {
        return """
<div class="nicojk-about" style="font-family:system-ui,'Yu Gothic UI','Meiryo',sans-serif;padding:18px 20px;line-height:1.55;color:#222;box-sizing:border-box;">
  <div style="font-size:20px;font-weight:700;margin-bottom:4px;">NicoJkPlugin</div>
  <div style="font-size:13px;margin-bottom:14px;">Version: 1.0.0</div>
  <div style="font-size:13px;">
    TvAIr上でニコニコ実況コメント連携を行うためのTvAIrプラグインです。<br>
    TVTest用プラグイン NicoJK 本体の同梱版、改変版、後継版ではありません。
  </div>
  <div style="font-size:12px;margin-top:14px;color:#555;">
    本プラグインに含まれる独自実装部分の利用、改変、再配布は許可します。外部ソフトウェアおよびTvAIr Plugin SDKに由来する部分は、それぞれの配布条件に従ってください。
  </div>
</div>
""";
    }

    private void StartMonitor()
    {
        if (_context is null) return;
        lock (_sync)
        {
            if (_monitorCts is not null) return;
            _monitorCts = new CancellationTokenSource();
            _monitorTask = Task.Run(() => MonitorLoop(_monitorCts.Token));
        }
        Log("[SessionManager] 録画セッション監視を開始 mode=polling intervalSeconds=5");
    }

    private void StopMonitor()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_sync)
        {
            cts = _monitorCts;
            task = _monitorTask;
            _monitorCts = null;
            _monitorTask = null;
        }
        if (cts is null) return;
        try { cts.Cancel(); } catch { }
        try { task?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        try { cts.Dispose(); } catch { }
    }

    private async Task MonitorLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                PollRecordingSessions();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log($"[SessionManager] 録画セッション監視エラー: {ex.GetType().Name}: {ex.Message}");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void PollRecordingSessions()
    {
        if (_context is null) return;

        var reservations = _context.GetReservations(new PluginReservationQuery { IncludeEpgSystemEntries = false });
        var tuners = _context.GetTunerStatus();
        var activeRecordingIds = tuners
            .Where(IsRecordingTuner)
            .Select(t => t.ReservationId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();

        var recording = reservations
            .Where(r => IsRecordingReservation(r) || activeRecordingIds.Contains(r.Id))
            .GroupBy(x => x.Id)
            .Select(g => g.First())
            .ToList();

        var activeIds = recording.Select(x => x.Id).ToHashSet();
        var signature = string.Join(",", activeIds.OrderBy(x => x));
        var tick = Interlocked.Increment(ref _pollCount);
        if (_pollingLogGate.ShouldLogPollingSnapshot(tick, signature, activeIds.Count > 0, DateTimeOffset.Now, out var pollLogReason))
        {
            Log($"[SessionManager] polling: reason={pollLogReason} tick={tick} reservations={reservations.Count} tunerRecordingIds={FormatIds(activeRecordingIds)} detected={FormatIds(activeIds)}");
        }

        if (recording.Count == 0)
        {
            StopSessionsExcept(new HashSet<int>());
            return;
        }

        foreach (var r in recording)
        {
            var source = activeRecordingIds.Contains(r.Id) && !IsRecordingReservation(r) ? "polling:tuner" : "polling:reservation";
            var info = ToRecordingInfo(r);
            StartSession(info, source);
        }

        StopSessionsExcept(activeIds);
    }

    private static bool IsRecordingReservation(PluginReservation reservation)
    {
        var status = reservation.Status ?? string.Empty;
        if (status.Equals("Recording", StringComparison.OrdinalIgnoreCase)) return true;
        if (status.Contains("Recording", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsRecordingTuner(PluginTunerStatus tuner)
    {
        if (!tuner.ReservationId.HasValue) return false;
        var kind = tuner.UsageKind ?? string.Empty;
        if (kind.Equals("Recording", StringComparison.OrdinalIgnoreCase)) return true;
        if (kind.Contains("Recording", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string FormatIds(IEnumerable<int> ids)
    {
        var list = ids.Distinct().OrderBy(x => x).ToArray();
        return list.Length == 0 ? "-" : string.Join(",", list.Select(x => "R" + x));
    }

    private PluginRecordingInfo ToRecordingInfo(PluginReservation r)
    {
        var now = DateTimeOffset.Now;
        var end = new DateTimeOffset(DateTime.SpecifyKind(r.EndTime, DateTimeKind.Local));
        return new PluginRecordingInfo
        {
            ReservationId = r.Id,
            Title = r.Title,
            NetworkId = r.NetworkId,
            TransportStreamId = r.TransportStreamId,
            ServiceId = r.ServiceId,
            ServiceName = r.ServiceName,
            ActualStartTime = now,
            ActualEndTime = end,
            OutputFilePath = string.Empty
        };
    }

    private void StartSession(PluginRecordingInfo info, string source)
    {
        if (_settings is null || _resolver is null || _publisher is null) return;

        lock (_sync)
        {
            if (_sessions.ContainsKey(info.ReservationId)) return;
        }

        var jk = _resolver.Resolve(info.NetworkId, info.TransportStreamId, info.ServiceId, info.ServiceName);
        if (jk is null)
        {
            Log($"[Session] 開始スキップ: R{info.ReservationId} service={info.ServiceName} nid={info.NetworkId} tsid={info.TransportStreamId} sid={info.ServiceId} reason=jk_channel_unresolved source={source}");
            return;
        }

        var session = new RecordingSession(info, jk.Value, _settings, _publisher.Publish, Log);
        lock (_sync)
        {
            if (_sessions.ContainsKey(info.ReservationId)) return;
            _sessions[info.ReservationId] = session;
        }

        Log($"[SessionManager] 録画セッション検出: R{info.ReservationId} service={info.ServiceName} jk{jk.Value} source={source}");
        session.Start();
    }

    private void StopSession(int reservationId, PluginRecordingInfo? info = null)
    {
        RecordingSession? session = null;
        lock (_sync)
        {
            if (_sessions.TryGetValue(reservationId, out session))
                _sessions.Remove(reservationId);
        }
        if (session is not null)
        {
            try { session.StopAsync(info).GetAwaiter().GetResult(); }
            catch (Exception ex) { Log($"[Session] 終了処理エラー: R{reservationId} {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    private void StopSessionsExcept(ISet<int> activeReservationIds)
    {
        List<int> stopIds;
        lock (_sync)
        {
            stopIds = _sessions.Keys.Where(id => !activeReservationIds.Contains(id)).ToList();
        }
        foreach (var id in stopIds) StopSession(id);
    }

    private void StopAllSessions()
    {
        List<int> ids;
        lock (_sync) ids = _sessions.Keys.ToList();
        foreach (var id in ids) StopSession(id);
    }

    private void Log(string message)
    {
        try { _context?.Log(PluginLogLevel.Info, message); }
        catch { try { _context?.Log(message); } catch { } }
    }
}
