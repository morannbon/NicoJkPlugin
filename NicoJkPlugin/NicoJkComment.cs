namespace NicoJkPlugin;

internal sealed record NicoJkComment(string Thread, long Vpos, long Date, long No, string UserId, string Mail, string Content)
{
    public static NicoJkComment Create(string thread, long vpos, long date, long no, string userId, string mail, string content)
        => new(
            NicoJkCommentTextPipeline.NormalizeXmlAttribute(thread),
            vpos,
            date,
            no,
            NicoJkCommentTextPipeline.NormalizeXmlAttribute(userId),
            NicoJkCommentTextPipeline.NormalizeXmlAttribute(mail),
            NicoJkCommentTextPipeline.NormalizeForDisplay(content));

    public string ToXmlLine()
    {
        var content = NicoJkCommentTextPipeline.EscapeXmlText(Content);
        var user = NicoJkCommentTextPipeline.EscapeXmlAttribute(UserId);
        var mail = NicoJkCommentTextPipeline.EscapeXmlAttribute(Mail);
        var thread = NicoJkCommentTextPipeline.EscapeXmlAttribute(Thread);
        return $"<chat thread=\"{thread}\" no=\"{No}\" vpos=\"{Vpos}\" date=\"{Date}\" date_usec=\"0\" user_id=\"{user}\" mail=\"{mail}\">{content}</chat>";
    }
}
