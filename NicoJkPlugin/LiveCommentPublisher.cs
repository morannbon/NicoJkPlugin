using TvAIrPlugin;

namespace NicoJkPlugin;

internal sealed class LiveCommentPublisher
{
    private readonly IPluginContext _context;
    private readonly ILiveCommentPublisher? _publisher;

    public LiveCommentPublisher(IPluginContext context)
    {
        _context = context;
        _publisher = context as ILiveCommentPublisher;
    }

    public void Publish(LiveCommentEvent comment)
    {
        var safeComment = NormalizeBoundary(comment);
        if (_publisher is not null)
        {
            _publisher.PublishLiveComment(safeComment);
            return;
        }
        var method = _context.GetType().GetMethod("PublishLiveComment");
        if (method is not null) method.Invoke(_context, new object[] { safeComment });
    }

    private static LiveCommentEvent NormalizeBoundary(LiveCommentEvent c)
        => new()
        {
            PluginId = c.PluginId,
            ReservationId = c.ReservationId,
            ServiceName = c.ServiceName,
            ProgramTitle = c.ProgramTitle,
            JkChannel = c.JkChannel,
            UnixTime = c.UnixTime,
            Vpos = c.Vpos,
            UserId = NicoJkCommentTextPipeline.NormalizeXmlAttribute(c.UserId),
            Mail = NicoJkCommentTextPipeline.NormalizeXmlAttribute(c.Mail),
            Content = NicoJkCommentTextPipeline.NormalizeForPublish(c.Content),
            ReceivedAt = c.ReceivedAt
        };
}
