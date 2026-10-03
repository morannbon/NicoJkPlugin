using System.Text;
using System.Text.RegularExpressions;
using TvAIrPlugin;

namespace NicoJkPlugin;

internal sealed class RecordingSession : IAsyncDisposable
{
    private readonly RecordingInfo _info;
    private readonly int _jk;
    private readonly PluginSettings _settings;
    private readonly Action<CommentPublish> _publish;
    private readonly Action<string> _log;
    private readonly NicoJkClient _client;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private string? _filePath;
    private string? _sessionDirectory;
    private bool _saveEnabled;
    private bool _saveErrorLogged;
    private int _received;
    private int _saved;
    private int _displayed;
    private int _skippedPast;
    private int _skippedOld;

    public RecordingSession(RecordingInfo info, int jk, PluginSettings settings, ITvAirInternetAccessApi internetAccess, Action<CommentPublish> publish, Action<string> log, Action statusChanged)
    {
        _info = info;
        _jk = jk;
        _settings = settings;
        _publish = publish;
        _log = log;
        _client = new NicoJkClient(jk, settings.RefugeUri, settings.BacklogCount, settings.DropForwardedComment, internetAccess, log, statusChanged);
        _client.CommentReceived += OnComment;
    }

    public NicoJkClientStatus Status => _client.Status;

    public void NotifyInternetAccessChanged() => _client.NotifyInternetAccessChanged();

    public void Start()
    {
        PrepareStorage();
        _client.Start();
        _log($"[Session] 開始: {_info.ReservationId} service={_info.ServiceName} jk{_jk} backlog={_settings.BacklogCount} save={(_saveEnabled ? "enabled" : "disabled")}");
    }

    private void PrepareStorage()
    {
        if (!_settings.LogDirectoryAvailable || string.IsNullOrWhiteSpace(_settings.LogDirectory))
        {
            _saveEnabled = false;
            return;
        }

        try
        {
            _sessionDirectory = Path.Combine(_settings.LogDirectory, $"jk{_jk}");
            Directory.CreateDirectory(_sessionDirectory);
            _saveEnabled = true;
        }
        catch (Exception ex)
        {
            _saveEnabled = false;
            _sessionDirectory = null;
            _log($"[Session] コメント保存無効: {_info.ReservationId} jk{_jk} path={_settings.LogDirectory} error={ex.GetType().Name}: {ex.Message} action=display_only");
        }
    }

    public async Task StopAsync()
    {
        await _client.StopAsync().ConfigureAwait(false);
        FinalizeFile();
        _log($"[Session] 終了: {_info.ReservationId} jk{_jk} received={_received} displayed={_displayed} saved={_saved} skippedPast={_skippedPast} skippedOld={_skippedOld} finalSaved={CountLines(_filePath)} file={_filePath ?? "(no-comment)"}");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void OnComment(NicoJkComment c, bool isPastChatBlock)
    {
        Interlocked.Increment(ref _received);
        var lower = _info.ActualStartTime.AddSeconds(-_settings.PastToleranceSeconds).ToUnixTimeSeconds();
        var upper = (_info.ActualEndTime ?? DateTimeOffset.Now.AddMinutes(10)).AddSeconds(_settings.PastToleranceSeconds).ToUnixTimeSeconds();
        if (c.Date < lower) { Interlocked.Increment(ref _skippedOld); return; }

        Publish(c);
        Interlocked.Increment(ref _displayed);

        if (isPastChatBlock)
        {
            Interlocked.Increment(ref _skippedPast);
            return;
        }
        if (c.Date > upper) return;
        Save(c);
    }

    private void Publish(NicoJkComment c)
    {
        try
        {
            _publish(new CommentPublish
            {
                PluginId = PluginIdentity.Id,
                ReservationId = _info.ReservationId.ToString(),
                ServiceName = _info.ServiceName,
                ProgramTitle = _info.Title,
                NetworkId = _info.NetworkId,
                TransportStreamId = _info.TransportStreamId,
                ServiceId = _info.ServiceId,
                JkChannel = _jk,
                UnixTime = c.Date,
                Vpos = c.Vpos is >= int.MinValue and <= int.MaxValue ? (int)c.Vpos : null,
                UserId = c.UserId,
                Mail = c.Mail,
                Content = c.Content,
                ReceivedAt = DateTimeOffset.Now
            });
        }
        catch { }
    }

    private void Save(NicoJkComment c)
    {
        if (!_saveEnabled || string.IsNullOrWhiteSpace(_sessionDirectory)) return;

        lock (_sync)
        {
            var key = DuplicateKey(c);
            if (!_seen.Add(key)) return;
            try
            {
                _filePath ??= Path.Combine(_sessionDirectory, $"{c.Date}.txt");
                File.AppendAllText(_filePath, c.ToXmlLine() + Environment.NewLine, Encoding.UTF8);
                _saved++;
            }
            catch (Exception ex)
            {
                _saveEnabled = false;
                if (!_saveErrorLogged)
                {
                    _saveErrorLogged = true;
                    _log($"[Session] コメント保存停止: {_info.ReservationId} jk{_jk} error={ex.GetType().Name}: {ex.Message} action=display_continue");
                }
            }
        }
    }

    private void FinalizeFile()
    {
        if (_filePath is null || !File.Exists(_filePath)) return;
        var rows = File.ReadAllLines(_filePath, Encoding.UTF8)
            .Select(ParseLine)
            .Where(x => x is not null)
            .Select(x => x!)
            .OrderBy(x => x.Date)
            .ThenBy(x => x.No)
            .ToList();
        var outRows = new List<CommentRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var key = $"{row.Date / 3}:{NicoJkCommentTextPipeline.NormalizeForDuplicateKey(row.Body)}";
            if (row.Body.Trim().Length >= 6 && !seen.Add(key)) continue;
            outRows.Add(row);
        }
        File.WriteAllLines(_filePath, outRows.Select(x => x.Line), Encoding.UTF8);
    }

    private static CommentRow? ParseLine(string line)
    {
        var date = Attr(line, "date");
        var no = Attr(line, "no");
        if (!long.TryParse(date, out var d)) return null;
        long.TryParse(no, out var n);
        var body = NicoJkCommentTextPipeline.NormalizeForSave(Regex.Replace(line, "^.*?>|</chat>.*$", string.Empty, RegexOptions.Singleline));
        return new CommentRow(d, n, body, line);
    }

    private static string? Attr(string line, string name)
    {
        var m = Regex.Match(line, name + "=\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string DuplicateKey(NicoJkComment c)
    {
        var body = NicoJkCommentTextPipeline.NormalizeForDuplicateKey(c.Content);
        return body.Length >= 6 ? $"{c.Date / 3}:{body}" : $"{c.Thread}:{c.No}:{c.Date}:{body}";
    }

    private static int CountLines(string? path) => path is not null && File.Exists(path) ? File.ReadLines(path).Count() : 0;
    private sealed record CommentRow(long Date, long No, string Body, string Line);
}
