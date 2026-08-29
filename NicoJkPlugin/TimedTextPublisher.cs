using System.Globalization;
using TvAIrPlugin;
using TvAIrPlugin.Overlay;
using TvAIrPlugin.Runtime;
using TvAIrPlugin.Viewers;
using RuntimeViewerSessionDto = TvAIrPlugin.Viewers.TvAirViewerSessionDto;

namespace NicoJkPlugin;

internal sealed class CommentPublisher
{
    private const string SceneDefinitionId = "timed-text-overlay";
    private const string LayerId = "comments";
    private readonly ITvAirPluginRuntimeContext _context;
    private readonly Action<string> _log;
    private readonly object _sync = new();
    private readonly Dictionary<string, SceneBinding> _scenes = new(StringComparer.OrdinalIgnoreCase);
    private long _elementSequence;

    public CommentPublisher(ITvAirPluginRuntimeContext context, Action<string> log)
    {
        _context = context;
        _log = log;
    }

    public void Publish(CommentPublish comment)
    {
        var normalized = NormalizeBoundary(comment);
        PublishTimedText(normalized);
        PublishVideoOverlay(normalized);
    }

    private void PublishTimedText(CommentPublish c)
    {
        _context.TimedTextStreams.Publish(new TvAirTimedTextPublishDto
        {
            StreamId = "recording-comments",
            GroupId = c.ReservationId,
            SourceOwnerId = c.PluginId,
            SourceKind = "comment",
            Title = c.ProgramTitle,
            Subtitle = c.ServiceName,
            PositionMilliseconds = c.Vpos.HasValue ? c.Vpos.Value * 10L : null,
            AuthorId = c.UserId,
            Style = c.Mail,
            Text = c.Content,
            OccurredAt = c.ReceivedAt,
            Attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["jkChannel"] = c.JkChannel.ToString(CultureInfo.InvariantCulture),
                ["unixTime"] = c.UnixTime.ToString(CultureInfo.InvariantCulture)
            }
        });
    }

    public void RefreshViewers()
    {
        IReadOnlyList<RuntimeViewerSessionDto> sessions;
        try { sessions = _context.Viewers.ListSessions(); }
        catch { return; }
        CloseStaleScenes(sessions);
    }

    private void PublishVideoOverlay(CommentPublish c)
    {
        if (!TryToServiceIdentity(c, out var nid, out var tsid, out var sid)) return;

        IReadOnlyList<RuntimeViewerSessionDto> sessions;
        try { sessions = _context.Viewers.ListSessions(); }
        catch (Exception ex)
        {
            _log($"[Overlay] viewer list failed {ex.GetType().Name}: {ex.Message}");
            return;
        }

        var targets = sessions
            .Where(IsUsableViewer)
            .Where(x => x.CurrentService is { } service &&
                        service.NetworkId == nid &&
                        service.TransportStreamId == tsid &&
                        service.ServiceId == sid)
            .ToArray();

        CloseStaleScenes(sessions);
        foreach (var target in targets) PublishToViewer(target, c);
    }

    private void PublishToViewer(RuntimeViewerSessionDto viewer, CommentPublish c)
    {
        var scene = EnsureScene(viewer);
        if (scene is null) return;

        var elementId = $"comment-{Interlocked.Increment(ref _elementSequence).ToString(CultureInfo.InvariantCulture)}";
        var result = _context.VideoOverlay.Elements.Add(new AddVideoOverlayElementsRequest(
            scene.SceneInstanceId,
            LayerId,
            new VideoOverlayElement[]
            {
                new VideoOverlayTextElement(
                    elementId,
                    c.Content,
                    30d,
                    ResolveColor(c.Mail),
                    "bottom-center",
                    TimeSpan.FromSeconds(8))
            },
            viewer.Generation));

        if (!result.Succeeded)
        {
            RemoveBinding(viewer.ViewerSessionId);
            _log($"[Overlay] add rejected viewerSession={viewer.ViewerSessionId} error={result.Error?.Code} message={result.Error?.Message}");
        }
    }

    private SceneBinding? EnsureScene(RuntimeViewerSessionDto viewer)
    {
        lock (_sync)
        {
            if (_scenes.TryGetValue(viewer.ViewerSessionId, out var current))
            {
                if (current.Generation == viewer.Generation) return current;
                if (!TryCloseBinding(current)) return null;
                _scenes.Remove(viewer.ViewerSessionId);
            }

            // Scene replacement is one lifecycle transaction.  Do not expose the
            // Close -> Create gap to another comment publish or viewer refresh.
            var created = _context.VideoOverlay.Scenes.Create(new CreateVideoOverlaySceneRequest(
                SceneDefinitionId,
                viewer.ViewerSessionId,
                viewer.Generation,
                "comments"));
            if (!created.Succeeded || created.Value is null)
            {
                _log($"[Overlay] scene create rejected viewerSession={viewer.ViewerSessionId} error={created.Error?.Code} message={created.Error?.Message}");
                return null;
            }

            var binding = new SceneBinding(created.Value.SceneInstanceId, viewer.ViewerSessionId, viewer.Generation);
            _scenes[viewer.ViewerSessionId] = binding;
            return binding;
        }
    }

    private void CloseStaleScenes(IReadOnlyList<RuntimeViewerSessionDto> sessions)
    {
        var live = sessions.Where(IsUsableViewer)
            .ToDictionary(x => x.ViewerSessionId, x => x.Generation, StringComparer.OrdinalIgnoreCase);
        lock (_sync)
        {
            var stale = _scenes.Values
                .Where(x => !live.TryGetValue(x.ViewerSessionId, out var generation) || generation != x.Generation)
                .ToList();
            foreach (var item in stale)
            {
                if (TryCloseBinding(item))
                    _scenes.Remove(item.ViewerSessionId);
            }
        }
    }

    private void RemoveBinding(string viewerSessionId)
    {
        lock (_sync)
        {
            if (!_scenes.TryGetValue(viewerSessionId, out var binding)) return;
            if (!TryCloseBinding(binding)) return;
            _scenes.Remove(viewerSessionId);
        }
    }

    private bool TryCloseBinding(SceneBinding binding)
    {
        try
        {
            var result = _context.VideoOverlay.Scenes.Close(new CloseVideoOverlaySceneRequest(
                binding.SceneInstanceId,
                binding.Generation));
            if (result.Succeeded) return true;
            _log($"[Overlay] scene close rejected viewerSession={binding.ViewerSessionId} error={result.Error?.Code} message={result.Error?.Message}");
            return false;
        }
        catch (Exception ex)
        {
            _log($"[Overlay] scene close failed viewerSession={binding.ViewerSessionId} {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool IsUsableViewer(RuntimeViewerSessionDto session)
        => session.ProcessId is > 0 &&
           !string.Equals(session.State, "closed", StringComparison.OrdinalIgnoreCase) &&
           !string.Equals(session.State, "stopped", StringComparison.OrdinalIgnoreCase);

    private static bool TryToServiceIdentity(CommentPublish c, out ushort nid, out ushort tsid, out ushort sid)
    {
        nid = 0;
        tsid = 0;
        sid = 0;
        if ((uint)c.NetworkId > ushort.MaxValue ||
            (uint)c.TransportStreamId > ushort.MaxValue ||
            (uint)c.ServiceId > ushort.MaxValue)
            return false;
        nid = (ushort)c.NetworkId;
        tsid = (ushort)c.TransportStreamId;
        sid = (ushort)c.ServiceId;
        return true;
    }

    private static string ResolveColor(string mail)
    {
        var value = mail ?? string.Empty;
        if (value.Contains("red", StringComparison.OrdinalIgnoreCase)) return "#ffff6b6b";
        if (value.Contains("blue", StringComparison.OrdinalIgnoreCase)) return "#ff6bb6ff";
        if (value.Contains("green", StringComparison.OrdinalIgnoreCase)) return "#ff72df72";
        if (value.Contains("yellow", StringComparison.OrdinalIgnoreCase)) return "#ffffe066";
        if (value.Contains("pink", StringComparison.OrdinalIgnoreCase)) return "#ffff8fc7";
        if (value.Contains("orange", StringComparison.OrdinalIgnoreCase)) return "#ffffad5a";
        return "#ffffffff";
    }

    private static CommentPublish NormalizeBoundary(CommentPublish c) => new()
    {
        PluginId = c.PluginId,
        ReservationId = c.ReservationId,
        ServiceName = c.ServiceName,
        ProgramTitle = c.ProgramTitle,
        NetworkId = c.NetworkId,
        TransportStreamId = c.TransportStreamId,
        ServiceId = c.ServiceId,
        JkChannel = c.JkChannel,
        UnixTime = c.UnixTime,
        Vpos = c.Vpos,
        UserId = NicoJkCommentTextPipeline.NormalizeXmlAttribute(c.UserId),
        Mail = NicoJkCommentTextPipeline.NormalizeXmlAttribute(c.Mail),
        Content = NicoJkCommentTextPipeline.NormalizeForPublish(c.Content),
        ReceivedAt = c.ReceivedAt
    };

    private sealed record SceneBinding(string SceneInstanceId, string ViewerSessionId, long Generation);
}
