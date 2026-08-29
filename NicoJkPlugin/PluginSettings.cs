using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace NicoJkPlugin;

internal sealed class PluginSettings
{
    public string LogDirectory { get; private set; } = string.Empty;
    public bool LogDirectoryAvailable { get; private set; } = true;
    public string RefugeUri { get; private set; } = "wss://nx-jikkyo.tsukumijima.net/api/v1/channels/{jkID}/ws/watch";
    public bool DropForwardedComment { get; private set; } = true;
    public int BacklogCount { get; private set; } = 200;
    public int PastToleranceSeconds { get; private set; } = 120;
    public bool EnableTvTestChannelAutoMapping { get; private set; } = true;
    public Dictionary<string, int> ChannelMapping { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, int> ServiceChannelMapping { get; } = new();
    public List<string> Ch2Files { get; } = new();

    public static PluginSettings Load(string appDirectory, string dataDirectory, Action<string> log)
    {
        var s = new PluginSettings();
        var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;
        var localIni = Path.Combine(pluginDir, "NicoJkPlugin.ini");
        if (!IniDocument.TryLoad(localIni, out var ini, out var localIniError))
            log($"[Settings] 設定ファイルを読み込めません: path={localIni} error={localIniError!.GetType().Name}: {localIniError.Message} 継続します");

        s.BacklogCount = ini.GetInt("General", "BacklogCommentRequestCount", s.BacklogCount, 0, 1000);
        s.PastToleranceSeconds = ini.GetInt("General", "TimeshiftPastToleranceSeconds", s.PastToleranceSeconds, 0, 600);
        s.EnableTvTestChannelAutoMapping = ini.GetBool("General", "EnableTvTestChannelAutoMapping", true);

        ReadChannelMappingSection(ini.Section("ChannelMapping"), s, allowTripletNameMap: true);

        var nicoIniPath = ResolveNicoJkIniPath(ini, appDirectory, dataDirectory, pluginDir, log);
        if (!string.IsNullOrEmpty(nicoIniPath) && File.Exists(nicoIniPath))
        {
            if (!IniDocument.TryLoad(nicoIniPath, out var nico, out var nicoIniError))
            {
                log($"[Settings] NicoJK.iniを読み込めません: path={nicoIniPath} error={nicoIniError!.GetType().Name}: {nicoIniError.Message} 継続します");
            }
            else
            {
                var folder = nico.Get("Setting", "logfileFolder", nico.Get("Settings", "logfileFolder"));
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    var baseDir = Path.GetDirectoryName(nicoIniPath) ?? pluginDir;
                    s.LogDirectory = NormalizePath(folder, baseDir);
                }

                var refuge = NormalizeIniValue(nico.Get("Setting", "refugeUri", nico.Get("Settings", "refugeUri")));
                if (IsTrustedRefugeUri(refuge)) s.RefugeUri = refuge;
                s.DropForwardedComment = nico.GetBool("Setting", "dropForwardedComment", nico.GetBool("Settings", "dropForwardedComment", true));
                ReadChannelMappingSection(nico.Section("Channels"), s, allowTripletNameMap: false);
                foreach (var mapFile in FindNicoJkChannelListFiles(nicoIniPath, pluginDir, appDirectory, dataDirectory))
                    ReadJkChannelListFile(mapFile, s, log);
                log($"[Settings] NicoJK.ini 読み込み: {nicoIniPath}");
                if (!string.IsNullOrWhiteSpace(s.LogDirectory)) log($"[Settings] logfileFolder={s.LogDirectory}");
            }
        }

        if (string.IsNullOrWhiteSpace(s.LogDirectory))
            s.LogDirectory = NormalizePath(Path.Combine(dataDirectory, "Plugins", PluginIdentity.Id, "NicoJK"), pluginDir);

        try
        {
            Directory.CreateDirectory(s.LogDirectory);
            s.LogDirectoryAvailable = true;
        }
        catch (Exception ex)
        {
            s.LogDirectoryAvailable = false;
            log($"[Settings] コメント保存先を使用できません: path={s.LogDirectory} error={ex.GetType().Name}: {ex.Message}");
        }

        foreach (var ch2 in FindCh2Files(ini, nicoIniPath, pluginDir, appDirectory, dataDirectory, log))
            s.Ch2Files.Add(ch2);

        log($"[Settings] 読み込み完了 ch2Files={s.Ch2Files.Count} serviceMaps={s.ServiceChannelMapping.Count} save={(s.LogDirectoryAvailable ? "enabled" : "disabled")}");
        return s;
    }

    private static void ReadChannelMappingSection(IReadOnlyDictionary<string, string> section, PluginSettings settings, bool allowTripletNameMap)
    {
        foreach (var kv in section)
        {
            var key = NormalizeIniValue(kv.Key);
            var value = NormalizeIniValue(kv.Value).TrimStart('+');
            if (!int.TryParse(value, out var jk)) continue;
            if (TryNicoJkServiceKey(key, out var serviceKey))
            {
                if (jk <= 0) settings.ServiceChannelMapping.Remove(serviceKey);
                else settings.ServiceChannelMapping[serviceKey] = jk;
                continue;
            }
            if (!allowTripletNameMap || jk <= 0) continue;
            settings.ChannelMapping[key] = jk;
        }
    }

    private static void ReadJkChannelListFile(string path, PluginSettings settings, Action<string> log)
    {
        try
        {
            foreach (var raw in IniDocument.ReadAllLinesSmart(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
                var match = Regex.Match(line, @"^(?<jk>\d+)\s+(?<network>\d+)\s+(?<sid>0x[0-9a-fA-F]+|\d+)\b");
                if (!match.Success) continue;
                if (!int.TryParse(match.Groups["jk"].Value, out var jk) || jk <= 0) continue;
                if (!int.TryParse(match.Groups["network"].Value, out var network)) continue;
                if (!TryParseIntFlexible(match.Groups["sid"].Value, out var sid)) continue;
                settings.ServiceChannelMapping[ServiceKey(network, sid)] = jk;
            }
        }
        catch (Exception ex)
        {
            log($"[Settings] jkch読み込み失敗: path={path} error={ex.GetType().Name}: {ex.Message} action=continue");
        }
    }

    private static IEnumerable<string> FindNicoJkChannelListFiles(string nicoIniPath, string pluginDir, string appDirectory, string dataDirectory)
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pluginDir };
        if (!string.IsNullOrEmpty(nicoIniPath))
        {
            var pluginFolder = Path.GetDirectoryName(nicoIniPath);
            if (!string.IsNullOrEmpty(pluginFolder))
            {
                dirs.Add(pluginFolder);
                var tvtestRoot = Directory.GetParent(pluginFolder)?.FullName;
                if (!string.IsNullOrEmpty(tvtestRoot)) dirs.Add(tvtestRoot);
            }
        }
        AddTvTestRootCandidates(dirs, pluginDir, appDirectory, dataDirectory);
        foreach (var dir in dirs.Where(Directory.Exists))
        {
            foreach (var name in new[] { "jkch.sh.txt", "jkch.txt" })
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path)) yield return path;
            }
        }
    }

    internal static int ServiceKey(int network, int serviceId) => ((network & 0xFFFF) << 16) | (serviceId & 0xFFFF);

    private static bool TryNicoJkServiceKey(string raw, out int serviceKey)
    {
        serviceKey = 0;
        if (!TryParseIntFlexible(raw, out var key)) return false;
        var network = (key >> 16) & 0xFFFF;
        var serviceId = key & 0xFFFF;
        if (network <= 0 || serviceId <= 0) return false;
        serviceKey = ServiceKey(network, serviceId);
        return true;
    }

    private static bool TryParseIntFlexible(string raw, out int value)
    {
        raw = raw.Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(raw[2..], System.Globalization.NumberStyles.HexNumber, null, out value);
        return int.TryParse(raw, out value);
    }

    private static string ResolveNicoJkIniPath(IniDocument ini, string appDirectory, string dataDirectory, string pluginDir, Action<string> log)
    {
        var candidates = new List<string>();
        AddCandidate(candidates, NormalizePath(ini.Get("General", "NicoJkIniPath"), pluginDir));

        var pluginsDir = NormalizePath(ini.Get("General", "TvTestPluginsDirectory"), pluginDir);
        if (Directory.Exists(pluginsDir)) AddCandidate(candidates, Path.Combine(pluginsDir, "NicoJK.ini"));

        var tvtest = NormalizePath(ini.Get("General", "TVTestPath"), pluginDir);
        if (File.Exists(tvtest)) AddCandidate(candidates, Path.Combine(Path.GetDirectoryName(tvtest)!, "Plugins", "NicoJK.ini"));

        foreach (var root in BuildTvTestRootCandidates(pluginDir, appDirectory, dataDirectory))
            AddCandidate(candidates, Path.Combine(root, "Plugins", "NicoJK.ini"));

        foreach (var procName in new[] { "TVTest", "LIVETest" })
        {
            foreach (var p in Process.GetProcessesByName(procName))
            {
                try
                {
                    var exe = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exe)) AddCandidate(candidates, Path.Combine(Path.GetDirectoryName(exe)!, "Plugins", "NicoJK.ini"));
                }
                catch { }
            }
        }

        AddCandidate(candidates, Path.Combine(pluginDir, "NicoJK.ini"));
        var distinct = candidates.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var found = distinct.FirstOrDefault(File.Exists);
        if (!string.IsNullOrEmpty(found)) return found;

        var sample = string.Join(" | ", distinct.Take(8));
        log($"[Settings] NicoJK.ini 探索失敗: candidates={distinct.Length} sample={sample} action=continue_with_builtin_mapping");
        return string.Empty;
    }

    private static IEnumerable<string> FindCh2Files(IniDocument ini, string nicoIniPath, string pluginDir, string appDirectory, string dataDirectory, Action<string> log)
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tvtest = NormalizePath(ini.Get("General", "TVTestPath"), pluginDir);
        if (File.Exists(tvtest)) dirs.Add(Path.GetDirectoryName(tvtest)!);
        if (!string.IsNullOrEmpty(nicoIniPath))
        {
            var tvtestRoot = Directory.GetParent(Path.GetDirectoryName(nicoIniPath) ?? string.Empty)?.FullName;
            if (!string.IsNullOrEmpty(tvtestRoot)) dirs.Add(tvtestRoot);
        }
        AddTvTestRootCandidates(dirs, pluginDir, appDirectory, dataDirectory);

        foreach (var d in dirs.ToArray())
        {
            var tvini = Path.Combine(d, "TVTest.ini");
            if (!File.Exists(tvini)) continue;
            if (!IniDocument.TryLoad(tvini, out var tv, out var tvIniError))
            {
                log($"[Settings] TVTest.iniを読み込めません: path={tvini} error={tvIniError!.GetType().Name}: {tvIniError.Message} 継続します");
                continue;
            }
            var driverDir = tv.Get("Settings", "DriverDirectory");
            if (!string.IsNullOrWhiteSpace(driverDir)) dirs.Add(NormalizePath(driverDir, d));
        }

        var files = dirs
            .Where(Directory.Exists)
            .SelectMany(d => EnumerateCh2Files(d, log))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (files.Length == 0)
        {
            var sample = string.Join(" | ", dirs.Take(8));
            log($"[Settings] ch2探索結果: ch2Files=0 searchedDirs={dirs.Count} sample={sample} action=continue_with_builtin_mapping");
        }
        return files;
    }

    private static IReadOnlyList<string> EnumerateCh2Files(string directory, Action<string> log)
    {
        try
        {
            return Directory.GetFiles(directory, "*.ch2", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"[Settings] チャンネル設定を確認できません: path={directory} error={ex.GetType().Name}: {ex.Message} 継続します");
            return Array.Empty<string>();
        }
    }

    private static void AddTvTestRootCandidates(HashSet<string> dirs, string pluginDir, string appDirectory, string dataDirectory)
    {
        foreach (var root in BuildTvTestRootCandidates(pluginDir, appDirectory, dataDirectory))
            dirs.Add(root);
    }

    private static IEnumerable<string> BuildTvTestRootCandidates(string pluginDir, string appDirectory, string dataDirectory)
    {
        var roots = new List<string>();
        foreach (var baseDir in new[] { pluginDir, AppContext.BaseDirectory, appDirectory, dataDirectory })
        {
            if (string.IsNullOrWhiteSpace(baseDir)) continue;
            var current = Directory.Exists(baseDir) ? new DirectoryInfo(baseDir) : Directory.GetParent(baseDir);
            for (var i = 0; current is not null && i < 5; i++, current = current.Parent)
            {
                if (current.Name.Equals("TVTest", StringComparison.OrdinalIgnoreCase)) AddCandidate(roots, current.FullName);
                AddCandidate(roots, Path.Combine(current.FullName, "TVTest"));
                AddCandidate(roots, Path.Combine(current.FullName, "TVTest_x64"));
            }
        }
        return roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static void AddCandidate(ICollection<string> list, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { list.Add(Path.GetFullPath(path)); }
        catch { }
    }

    internal static string NormalizeIniValue(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        while (value.Length >= 2 && IsWrappedQuote(value))
            value = value[1..^1].Trim();
        return Environment.ExpandEnvironmentVariables(value);
    }

    internal static string NormalizePath(string? raw, string baseDirectory)
    {
        var value = NormalizeIniValue(raw);
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        value = value.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(value)) return Path.GetFullPath(value);
        return Path.GetFullPath(Path.Combine(baseDirectory, value));
    }

    private static bool IsWrappedQuote(string value)
        => (value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'');

    private static bool IsTrustedRefugeUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return false;
        var u = uri.Replace("{jkID}", "jk1").Replace("{chatStreamID}", "jk1");
        return Uri.TryCreate(u, UriKind.Absolute, out var parsed)
            && parsed.Scheme == "wss"
            && parsed.Host.EndsWith("jikkyo.tsukumijima.net", StringComparison.OrdinalIgnoreCase);
    }
}
