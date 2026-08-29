using System.Text;

namespace NicoJkPlugin;

internal sealed class IniDocument
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections = new(StringComparer.OrdinalIgnoreCase);

    static IniDocument()
    {
        TryRegisterCodePagesProvider();
    }

    public static bool TryLoad(string path, out IniDocument ini, out Exception? error)
    {
        ini = new IniDocument();
        error = null;
        if (!File.Exists(path)) return true;
        try
        {
            ini.Parse(ReadAllLinesSmart(path));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex;
            ini = new IniDocument();
            return false;
        }
    }

    public static IReadOnlyList<string> ReadAllLinesSmart(string path)
    {
        if (!File.Exists(path)) return Array.Empty<string>();
        var bytes = ReadAllBytesShared(path);
        return ReadTextSmart(bytes).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    private static byte[] ReadAllBytesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string ReadTextSmart(byte[] bytes)
    {
        if (bytes.Length == 0) return string.Empty;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetString(bytes, 3, bytes.Length - 3);

        if (LooksLikeValidUtf8(bytes))
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetString(bytes);

        try
        {
            return Encoding.GetEncoding(932).GetString(bytes);
        }
        catch
        {
            return Encoding.Default.GetString(bytes);
        }
    }

    private static bool LooksLikeValidUtf8(byte[] bytes)
    {
        try
        {
            _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static void TryRegisterCodePagesProvider()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // System.Text.Encoding.CodePages が利用できない環境ではUTF-8/既定Encodingだけで継続する。
        }
    }

    private void Parse(IEnumerable<string> lines)
    {
        var section = string.Empty;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                if (!_sections.ContainsKey(section)) _sections[section] = new(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            var p = line.IndexOf('=');
            if (p <= 0) continue;
            var key = line[..p].Trim();
            var value = line[(p + 1)..].Trim();
            if (!_sections.TryGetValue(section, out var map))
                _sections[section] = map = new(StringComparer.OrdinalIgnoreCase);
            map[key] = value;
        }
    }

    public string Get(string section, string key, string fallback = "")
        => _sections.TryGetValue(section, out var map) && map.TryGetValue(key, out var value) ? value : fallback;

    public bool GetBool(string section, string key, bool fallback)
    {
        var raw = Get(section, key, string.Empty).Trim();
        if (raw.Length == 0) return fallback;
        return raw.Equals("1") || raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw.Equals("yes", StringComparison.OrdinalIgnoreCase) || raw.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    public int GetInt(string section, string key, int fallback, int min, int max)
    {
        var raw = Get(section, key, string.Empty).Trim();
        return int.TryParse(raw, out var n) ? Math.Clamp(n, min, max) : fallback;
    }

    public IReadOnlyDictionary<string, string> Section(string name)
        => _sections.TryGetValue(name, out var map) ? map : new Dictionary<string, string>();
}
