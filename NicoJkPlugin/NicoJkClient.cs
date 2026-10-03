using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TvAIrPlugin;

namespace NicoJkPlugin;

internal enum NicoJkClientStatus
{
    Connecting,
    Receiving,
    Unsupported,
    ConnectionError
}

internal sealed class NicoJkClient : IAsyncDisposable
{
    private const string NxJikkyoHost = "nx-jikkyo.tsukumijima.net";
    private const int WatchInfoTimeoutSeconds = 15;
    private const int WebSocketConnectTimeoutSeconds = 10;
    private const int MaxWebSocketMessageBytes = 1024 * 1024;
    private static readonly TimeSpan BaseReconnectDelay = TimeSpan.FromSeconds(15);

    private readonly int _jk;
    private readonly string _watchTemplate;
    private readonly int _backlog;
    private readonly bool _dropForwarded;
    private readonly Action<string> _log;
    private readonly Action _statusChanged;
    private readonly ITvAirInternetAccessApi _internetAccess;
    private TaskCompletionSource<bool> _internetAccessChanged = CreateInternetAccessChangedSignal();
    private readonly Random _jitter = new();
    private CancellationTokenSource? _cts;
    private Task? _task;
    private bool _pastBlock;
    private int _status = (int)NicoJkClientStatus.Connecting;

    public event Action<NicoJkComment, bool>? CommentReceived;

    public NicoJkClientStatus Status => (NicoJkClientStatus)Volatile.Read(ref _status);

    public NicoJkClient(int jk, string watchTemplate, int backlog, bool dropForwarded, ITvAirInternetAccessApi internetAccess, Action<string> log, Action statusChanged)
    {
        _jk = jk;
        _watchTemplate = watchTemplate;
        _backlog = Math.Clamp(backlog, 0, 1000);
        _dropForwarded = dropForwarded;
        _internetAccess = internetAccess;
        _log = log;
        _statusChanged = statusChanged;
    }

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        SetStatus(NicoJkClientStatus.Connecting);
        _log(IsInternetAccessEffective()
            ? $"[NicoJkClient] jk{_jk} 接続開始 backlog={_backlog}"
            : $"[NicoJkClient] jk{_jk} ネットワーク利用OFFのため接続待機 backlog={_backlog}");
        _task = Task.Run(() => Loop(_cts.Token));
    }

    public void NotifyInternetAccessChanged()
    {
        // 通知理由は判定に使わない。状態変化をwakeの契機にするだけで、
        // 接続可否の正本は常に InternetAccess.GetState().Effective とする。
        var next = CreateInternetAccessChangedSignal();
        var previous = Interlocked.Exchange(ref _internetAccessChanged, next);
        previous.TrySetResult(true);
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        if (_task is not null)
        {
            try { await _task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log($"[NicoJkClient] jk{_jk} 停止時エラー: {ex.GetType().Name}: {ex.Message}"); }
        }
        _cts.Dispose();
        _cts = null;
        _task = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task Loop(CancellationToken sessionToken)
    {
        while (!sessionToken.IsCancellationRequested)
        {
            if (!IsInternetAccessEffective())
            {
                SetStatus(NicoJkClientStatus.Connecting);
                try
                {
                    await WaitForInternetAccessAsync(sessionToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            using var networkCts = _internetAccess.CreateLinkedCancellation(sessionToken);
            var networkToken = networkCts.Token;

            try
            {
                networkToken.ThrowIfCancellationRequested();
                if (!IsInternetAccessEffective()) continue;

                var watch = await GetWatchInfo(networkToken).ConfigureAwait(false);
                if (!IsInternetAccessEffective()) continue;

                var commentUrl = watch?.CommentUrl ?? DefaultCommentUrl();
                var threadId = watch?.ThreadId;
                if (watch is null)
                    _log($"[NicoJkClient] jk{_jk} room取得失敗 → comment直接接続 fallbackThread={_jk}");
                await ReceiveComments(commentUrl, threadId, networkToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!sessionToken.IsCancellationRequested)
            {
                SetStatus(NicoJkClientStatus.Connecting);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (UnsupportedChannelException)
            {
                SetStatus(NicoJkClientStatus.Unsupported);
                break;
            }
            catch (Exception ex)
            {
                SetStatus(NicoJkClientStatus.ConnectionError);
                var delay = NextReconnectDelay();
                _log($"[NicoJkClient] jk{_jk} error={ex.GetType().Name}: {ex.Message} retrySec={(int)delay.TotalSeconds}");
                try { await Task.Delay(delay, networkToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!sessionToken.IsCancellationRequested)
                {
                    SetStatus(NicoJkClientStatus.Connecting);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        _log($"[NicoJkClient] jk{_jk} 終了");
    }

    private bool IsInternetAccessEffective()
    {
        try { return _internetAccess.GetState().Effective; }
        catch { return false; }
    }

    private async Task WaitForInternetAccessAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (IsInternetAccessEffective()) return;

            // signal取得とEffective再評価を分け、通知直前/直後の競合でも
            // ONへの状態変化を取りこぼさない。
            var signal = Volatile.Read(ref _internetAccessChanged);
            if (IsInternetAccessEffective()) return;

            await signal.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private static TaskCompletionSource<bool> CreateInternetAccessChangedSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task<WatchInfo?> GetWatchInfo(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsInternetAccessEffective()) return null;

        var watchUrl = WatchUrl();
        _log($"[NicoJkClient] jk{_jk} /ws/watch 接続開始 url={watchUrl}");
        using var ws = CreateWebSocket();

        try
        {
            await ConnectWebSocket(ws, new Uri(watchUrl), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not TimeoutException)
        {
            // NX-Jikkyo が 403 / 404 を返したチャンネルは実況対象外として扱う。
            // 通常の通信障害へ落とさず、直接接続・再試行・接続エラー表示を行わない。
            if (IsUnsupportedChannelResponse(ex))
                throw new UnsupportedChannelException();

            _log($"[NicoJkClient] jk{_jk} /ws/watch 接続失敗: {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        const string startWatching = "{\"type\":\"startWatching\",\"data\":{\"room\":{\"protocol\":\"webSocket\",\"commentable\":true},\"reconnect\":false}}";
        await Send(ws, startWatching, ct).ConfigureAwait(false);
        _log($"[NicoJkClient] jk{_jk} /ws/watch startWatching送信");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(WatchInfoTimeoutSeconds));

        try
        {
            while (!timeout.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var text = await ReceiveText(ws, timeout.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(text)) continue;

                if (IsPingMessage(text))
                {
                    await Send(ws, "{\"type\":\"pong\"}", ct).ConfigureAwait(false);
                    continue;
                }

                if (TryParseRoom(text, out var url, out var thread))
                {
                    _log($"[NicoJkClient] jk{_jk} room取得 thread={thread} url={url}");
                    TryClose(ws, ct);
                    return new WatchInfo(url, thread);
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            _log($"[NicoJkClient] jk{_jk} /ws/watch タイムアウト sec={WatchInfoTimeoutSeconds}");
        }
        catch (Exception ex)
        {
            _log($"[NicoJkClient] jk{_jk} /ws/watch 受信失敗: {ex.GetType().Name}: {ex.Message}");
        }

        return null;
    }


    private static bool IsUnsupportedChannelResponse(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains("status code '403'", StringComparison.OrdinalIgnoreCase)
                || message.Contains("status code 403", StringComparison.OrdinalIgnoreCase)
                || message.Contains("status code '404'", StringComparison.OrdinalIgnoreCase)
                || message.Contains("status code 404", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private sealed class UnsupportedChannelException : Exception { }

    private async Task ReceiveComments(string url, string? threadId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsInternetAccessEffective()) return;

        if (!IsTrustedCommentWebSocket(url))
        {
            _log($"[NicoJkClient] jk{_jk} comment接続中止: untrustedUrl={url}");
            return;
        }

        _log($"[NicoJkClient] jk{_jk} /ws/comment 接続開始 url={url}");
        using var ws = CreateWebSocket();

        await ConnectWebSocket(ws, new Uri(url), ct).ConfigureAwait(false);

        var actualThread = string.IsNullOrWhiteSpace(threadId) ? _jk.ToString() : threadId;
        var cmd = JsonSerializer.Serialize(new object[]
        {
            new { ping = new { content = "rs:0" } },
            new { ping = new { content = "ps:0" } },
            new { thread = new { thread = actualThread, version = "20061206", user_id = "guest", res_from = -_backlog, with_global = 1, scores = 1, nicoru = 0 } },
            new { ping = new { content = "pf:0" } },
            new { ping = new { content = "rf:0" } }
        });
        await Send(ws, cmd, ct).ConfigureAwait(false);
        SetStatus(NicoJkClientStatus.Receiving);
        _log($"[NicoJkClient] jk{_jk} comment接続 thread={actualThread}");

        var count = 0;
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var text = await ReceiveText(ws, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (ProcessMessage(text))
            {
                count++;
                if (count == 1 || count % 100 == 0)
                    _log($"[NicoJkClient] jk{_jk} comment受信 count={count}");
            }
        }
        _log($"[NicoJkClient] jk{_jk} comment終了 count={count}");
    }

    private static async Task ConnectWebSocket(ClientWebSocket ws, Uri uri, CancellationToken sessionToken)
    {
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(WebSocketConnectTimeoutSeconds));

        try
        {
            await ws.ConnectAsync(uri, connectTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!sessionToken.IsCancellationRequested)
        {
            throw new TimeoutException($"WebSocket connect timed out after {WebSocketConnectTimeoutSeconds} seconds.", ex);
        }
    }

    private void SetStatus(NicoJkClientStatus status)
    {
        var previous = Interlocked.Exchange(ref _status, (int)status);
        if (previous == (int)status) return;
        try { _statusChanged(); } catch { }
    }

    private bool ProcessMessage(string payload)
    {
        var trimmed = payload.TrimStart();
        if (string.IsNullOrWhiteSpace(trimmed)) return false;
        if (trimmed.StartsWith('<')) return ProcessXmlLikeMessage(trimmed);

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                var hit = false;
                foreach (var item in root.EnumerateArray())
                    hit |= ProcessJsonElement(item);
                return hit;
            }
            return ProcessJsonElement(root);
        }
        catch { return false; }
    }

    private bool ProcessJsonElement(JsonElement root)
    {
        var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
        if (IsPastBeginType(type)) { _pastBlock = true; return false; }
        if (IsPastEndType(type)) { _pastBlock = false; return false; }
        if (root.TryGetProperty("ping", out var ping))
        {
            var pingContent = ping.TryGetProperty("content", out var c) ? c.GetString() : null;
            if (pingContent == "x_past_chat_begin") _pastBlock = true;
            if (pingContent == "x_past_chat_end") _pastBlock = false;
            return false;
        }
        if (!root.TryGetProperty("chat", out var chat) || chat.ValueKind != JsonValueKind.Object) return false;
        if (_dropForwarded && IsForwardedComment(chat)) return false;
        if (chat.TryGetProperty("deleted", out var del) && del.ValueKind == JsonValueKind.Number && del.GetInt32() == 1) return false;

        var comment = NicoJkComment.Create(
            GetString(chat, "thread", $"jk{_jk}"),
            GetLong(chat, "vpos"),
            GetLong(chat, "date", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            GetLong(chat, "no"),
            GetString(chat, "user_id", string.Empty),
            GetString(chat, "mail", string.Empty),
            GetStringAny(chat, string.Empty, "content", "text"));
        if (string.IsNullOrWhiteSpace(comment.Content)) return false;
        CommentReceived?.Invoke(comment, _pastBlock || IsPastMarkedComment(chat));
        return true;
    }

    private bool ProcessXmlLikeMessage(string xml)
    {
        if (xml.StartsWith("<x_past_chat_begin", StringComparison.OrdinalIgnoreCase)) { _pastBlock = true; return false; }
        if (xml.StartsWith("<x_past_chat_end", StringComparison.OrdinalIgnoreCase)) { _pastBlock = false; return false; }
        if (!xml.StartsWith("<chat ", StringComparison.OrdinalIgnoreCase)) return false;

        var contentStart = xml.IndexOf('>');
        var contentEnd = xml.LastIndexOf("</chat>", StringComparison.OrdinalIgnoreCase);
        if (contentStart < 0 || contentEnd <= contentStart) return false;
        var comment = NicoJkComment.Create(
            GetXmlAttr(xml, "thread") ?? $"jk{_jk}",
            ParseLong(GetXmlAttr(xml, "vpos")),
            ParseLong(GetXmlAttr(xml, "date"), DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            ParseLong(GetXmlAttr(xml, "no")),
            GetXmlAttr(xml, "user_id") ?? string.Empty,
            GetXmlAttr(xml, "mail") ?? string.Empty,
            xml[(contentStart + 1)..contentEnd]);
        if (string.IsNullOrWhiteSpace(comment.Content)) return false;
        CommentReceived?.Invoke(comment, _pastBlock || xml.Contains("past", StringComparison.OrdinalIgnoreCase));
        return true;
    }

    private string WatchUrl()
    {
        var u = (_watchTemplate ?? string.Empty)
            .Replace("{jkID}", $"jk{_jk}", StringComparison.OrdinalIgnoreCase)
            .Replace("{chatStreamID}", $"jk{_jk}", StringComparison.OrdinalIgnoreCase);
        return IsTrustedWatchWebSocket(u) ? u : $"wss://{NxJikkyoHost}/api/v1/channels/jk{_jk}/ws/watch";
    }

    private string DefaultCommentUrl() => $"wss://{NxJikkyoHost}/api/v1/channels/jk{_jk}/ws/comment";

    private static bool IsPingMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "ping";
        }
        catch { return false; }
    }

    private static bool TryParseRoom(string json, out string url, out string thread)
    {
        url = string.Empty;
        thread = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("type", out var t) || t.GetString() != "room") return false;
            if (!doc.RootElement.TryGetProperty("data", out var data)) return false;
            thread = FindString(data, "threadId") ?? FindString(data, "thread") ?? string.Empty;
            url = FindString(data, "webSocketUrl") ?? FindString(data, "messageServerUri") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(url) && data.TryGetProperty("messageServer", out var ms))
                url = FindString(ms, "uri") ?? string.Empty;
            return thread.Length > 0 && IsTrustedCommentWebSocket(url);
        }
        catch { return false; }
    }

    private static string? FindString(JsonElement e, string name)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            if (e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String) return p.GetString();
            foreach (var x in e.EnumerateObject())
            {
                var r = FindString(x.Value, name);
                if (r is not null) return r;
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in e.EnumerateArray())
            {
                var r = FindString(x, name);
                if (r is not null) return r;
            }
        }
        return null;
    }

    private static bool IsTrustedWatchWebSocket(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == "wss" && string.Equals(uri.Host, NxJikkyoHost, StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.Contains("/ws/watch", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(uri.UserInfo);
    }

    private static bool IsTrustedCommentWebSocket(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == "wss" && string.Equals(uri.Host, NxJikkyoHost, StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.Contains("/ws/comment", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(uri.UserInfo);
    }

    private static bool IsPastBeginType(string? type) => type is not null && type.Replace("-", "_").Equals("x_past_chat_begin", StringComparison.OrdinalIgnoreCase);
    private static bool IsPastEndType(string? type) => type is not null && type.Replace("-", "_").Equals("x_past_chat_end", StringComparison.OrdinalIgnoreCase);

    private static bool IsForwardedComment(JsonElement chat) => HasTruthy(chat, "x_refuge") || HasTruthy(chat, "refuge") || HasTruthy(chat, "forwarded") || HasTruthy(chat, "x_forwarded");
    private static bool IsPastMarkedComment(JsonElement chat) => HasTruthy(chat, "x_past") || HasTruthy(chat, "past") || HasTruthy(chat, "is_past") || HasTruthy(chat, "x_past_chat");

    private static bool HasTruthy(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => v.TryGetInt32(out var i) && i != 0,
            JsonValueKind.String => IsTruthyString(v.GetString()),
            _ => false
        };
    }

    private static bool IsTruthyString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim();
        return v is "1" or "true" or "yes" or "on" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
    }



    private static string? GetXmlAttr(string xml, string name)
    {
        var m = Regex.Match(xml, "\\b" + Regex.Escape(name) + "\\s*=\\s*(['\"])(.*?)\\1", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        return m.Success ? NicoJkCommentTextPipeline.NormalizeXmlAttribute(m.Groups[2].Value) : null;
    }

    private static string GetString(JsonElement e, string name, string fallback)
    {
        if (!e.TryGetProperty(name, out var p)) return fallback;
        return p.ValueKind == JsonValueKind.String ? p.GetString() ?? fallback : p.ToString();
    }

    private static string GetStringAny(JsonElement e, string fallback, params string[] names)
    {
        foreach (var name in names)
        {
            var value = GetString(e, name, string.Empty);
            if (!string.IsNullOrEmpty(value)) return value;
        }
        return fallback;
    }
    private static long GetLong(JsonElement e, string name, long fallback = 0)
    {
        if (!e.TryGetProperty(name, out var p)) return fallback;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var n)) return n;
        if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), out var s)) return s;
        return fallback;
    }
    private static long ParseLong(string? value, long fallback = 0) => long.TryParse(value, out var n) ? n : fallback;

    private ClientWebSocket CreateWebSocket()
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("User-Agent", PluginIdentity.UserAgent);
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        return ws;
    }

    private TimeSpan NextReconnectDelay()
    {
        lock (_jitter)
        {
            return BaseReconnectDelay + TimeSpan.FromSeconds(_jitter.Next(0, 11));
        }
    }

    private static async Task Send(ClientWebSocket ws, string text, CancellationToken ct) => await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

    private static async Task<string> ReceiveText(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return string.Empty;
            if (result.MessageType == WebSocketMessageType.Binary) return string.Empty;
            if (ms.Length + result.Count > MaxWebSocketMessageBytes)
                throw new InvalidDataException($"websocket message too large: limit={MaxWebSocketMessageBytes}");
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void TryClose(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            if (ws.State == WebSocketState.Open)
                ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, ct).GetAwaiter().GetResult();
        }
        catch { }
    }

    private readonly record struct WatchInfo(string CommentUrl, string ThreadId);
}
