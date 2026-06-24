using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace NicoJkPlugin;

/// <summary>
/// Single shared comment text pipeline for NicoJK/NX-Jikkyo.
/// All inbound DTO creation, duplicate keys, save XML, and TvAIr publish boundaries must pass here.
/// Do not add character-by-character one-off fixes elsewhere.
/// </summary>
internal static class NicoJkCommentTextPipeline
{
    private const int MaxDecodePasses = 10;
    private const int MaxFinalDecodePasses = 4;

    private static readonly Regex MultiSpace = new("[ ]{2,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex EntityCandidate = new(@"&(?:amp;|#|#x|[a-zA-Z])", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex NumericEntity = new(@"&#(?<hex>x)?(?<value>[0-9a-fA-F]{1,8});?", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex BrokenAmpNumericEntity = new(@"&amp;(?=#(?:x[0-9a-fA-F]{1,8}|[0-9]{1,8});?)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ResidualNumericEntity = new(@"&#(?:x[0-9a-fA-F]{1,8}|[0-9]{1,8});?", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ResidualEncodedNumericEntity = new(@"&amp;#(?:x[0-9a-fA-F]{1,8}|[0-9]{1,8});?", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ResidualNamedEntity = new(@"&(?:amp|lt|gt|quot|apos|nbsp|#39);", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Dictionary<int, string> OverlaySafeScalarFallbacks = new()
    {
        // Frequently observed Nico/NX-Jikkyo emoji-like supplementary-plane comments.
        // TvAIr/WebUI/OSD layers can re-escape supplementary scalars into literal NCRs.
        // These fallbacks keep display readable without relying on individual route hacks.
        [0x1F480] = "☠",       // skull
        [0x1F631] = "‼",       // face screaming in fear
        [0x1F4A2] = "‼",       // anger symbol
        [0x1FAEA] = "困惑",    // distorted face
        [0x1F602] = "笑",      // tears of joy
        [0x1F923] = "笑",      // rolling on the floor laughing
        [0x1F914] = "？",      // thinking face
        [0x1F62D] = "泣",      // loudly crying face
        [0x1F62E] = "驚",      // face with open mouth
        [0x1F621] = "怒",      // enraged face
        [0x1F44D] = "良",      // thumbs up
        [0x1F44E] = "否",      // thumbs down
        [0x1F525] = "炎",      // fire
        [0x1F389] = "祝",      // party popper
    };

    /// <summary>
    /// Normalizes comment body text for all UI/publish/save paths.
    /// It decodes HTML/NCR text, never enables HTML markup behavior.
    /// </summary>
    public static string NormalizeForDisplay(string? value)
        => NormalizeCore(value, stripControls: true, collapseSpaces: true, overlaySafe: true, finalDisplayBoundary: true);

    public static string NormalizeForPublish(string? value)
        => NormalizeForDisplay(value);

    public static string NormalizeForSave(string? value)
        => NormalizeCore(value, stripControls: true, collapseSpaces: true, overlaySafe: false, finalDisplayBoundary: false);

    public static string NormalizeForDuplicateKey(string? value)
        => NormalizeCore(value, stripControls: true, collapseSpaces: true, overlaySafe: true, finalDisplayBoundary: true);

    public static string NormalizeXmlAttribute(string? value)
        => NormalizeCore(value, stripControls: true, collapseSpaces: true, overlaySafe: false, finalDisplayBoundary: false);

    public static bool HasResidualNumericReference(string? value)
        => !string.IsNullOrEmpty(value) && (ResidualNumericEntity.IsMatch(value) || ResidualEncodedNumericEntity.IsMatch(value));

    public static bool HasResidualHtmlReference(string? value)
        => !string.IsNullOrEmpty(value) && (HasResidualNumericReference(value) || ResidualNamedEntity.IsMatch(value));

    private static string NormalizeCore(string? value, bool stripControls, bool collapseSpaces, bool overlaySafe, bool finalDisplayBoundary)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var decoded = CanonicalizeEntityText(value);
        decoded = DecodeHtmlEntitiesRepeated(decoded, MaxDecodePasses);
        decoded = DecodeNumericEntities(decoded);
        decoded = DecodeResidualReferences(decoded, finalDisplayBoundary ? MaxDecodePasses : MaxFinalDecodePasses);
        if (decoded.Length == 0) return string.Empty;

        if (overlaySafe) decoded = NormalizeSupplementaryScalarsForOverlay(decoded);

        decoded = SanitizeCharacters(decoded, stripControls, collapseSpaces);

        if (finalDisplayBoundary)
        {
            // One final shared boundary before OSD/WebUI publication. This is intentionally generic:
            // it handles ASCII entities such as &#39; and encoded NCRs such as &amp;#128128;
            // without adding per-character route fixes.
            decoded = DecodeResidualReferences(decoded, MaxFinalDecodePasses);
            if (overlaySafe) decoded = NormalizeSupplementaryScalarsForOverlay(decoded);
            decoded = SanitizeCharacters(decoded, stripControls, collapseSpaces);
        }

        return decoded;
    }

    private static string SanitizeCharacters(string value, bool stripControls, bool collapseSpaces)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch == '\r' || ch == '\n' || ch == '\t')
            {
                sb.Append(' ');
                continue;
            }

            if (ch == '\u00A0' || ch == '\u3000')
            {
                sb.Append(' ');
                continue;
            }

            if (stripControls && char.IsControl(ch)) continue;
            if (char.GetUnicodeCategory(ch) is UnicodeCategory.Format) continue;
            sb.Append(ch);
        }

        var result = sb.ToString().Normalize(NormalizationForm.FormC);
        return collapseSpaces ? MultiSpace.Replace(result, " ").Trim() : result.Trim();
    }

    private static string DecodeHtmlEntitiesRepeated(string value, int maxPasses)
    {
        var current = value;
        for (var i = 0; i < maxPasses; i++)
        {
            current = CanonicalizeEntityText(current);
            if (!EntityCandidate.IsMatch(current) && !HasResidualHtmlReference(current)) break;

            var ampFixed = BrokenAmpNumericEntity.Replace(current, "&");
            var webDecoded = WebUtility.HtmlDecode(ampFixed) ?? string.Empty;
            var numericDecoded = DecodeNumericEntities(webDecoded);

            if (string.Equals(numericDecoded, current, StringComparison.Ordinal)) break;
            current = numericDecoded;
        }
        return current;
    }

    private static string DecodeResidualReferences(string value, int maxPasses)
    {
        var current = value;
        for (var i = 0; i < maxPasses; i++)
        {
            var before = current;
            current = CanonicalizeEntityText(current);
            current = BrokenAmpNumericEntity.Replace(current, "&");
            current = WebUtility.HtmlDecode(current) ?? string.Empty;
            current = DecodeNumericEntities(current);
            if (!HasResidualHtmlReference(current)) break;
            if (string.Equals(current, before, StringComparison.Ordinal)) break;
        }
        return current;
    }

    private static string DecodeNumericEntities(string value)
    {
        return NumericEntity.Replace(value, match =>
        {
            var text = match.Groups["value"].Value;
            var isHex = match.Groups["hex"].Success;
            try
            {
                var codePoint = Convert.ToInt32(text, isHex ? 16 : 10);
                if (!IsValidUnicodeScalar(codePoint)) return string.Empty;
                return char.ConvertFromUtf32(codePoint);
            }
            catch
            {
                return string.Empty;
            }
        });
    }

    private static string NormalizeSupplementaryScalarsForOverlay(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (char.IsHighSurrogate(ch) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                var scalar = char.ConvertToUtf32(ch, value[i + 1]);
                if (OverlaySafeScalarFallbacks.TryGetValue(scalar, out var fallback))
                {
                    sb.Append(fallback);
                }
                else
                {
                    // Do not let unsupported supplementary-plane characters be re-escaped as literal NCRs in OSD.
                    // Keep a compact neutral marker instead of leaking "&#xxxxx;" to the viewer.
                    sb.Append('□');
                }
                i++;
                continue;
            }

            if (char.IsSurrogate(ch)) continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static string CanonicalizeEntityText(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Normalize(NormalizationForm.FormKC))
        {
            // Entity-like text can arrive through JSON/XML/HTML layers with mixed-width characters
            // or invisible format marks. Normalize only the text stream; HTML execution remains disabled.
            if (char.GetUnicodeCategory(ch) is UnicodeCategory.Format) continue;
            sb.Append(ch switch
            {
                '＆' => '&',
                '＃' => '#',
                '；' => ';',
                'ｘ' => 'x',
                'Ｘ' => 'X',
                _ => ch
            });
        }
        return sb.ToString();
    }

    private static bool IsValidUnicodeScalar(int codePoint)
    {
        if (codePoint <= 0 || codePoint > 0x10FFFF) return false;
        if (codePoint >= 0xD800 && codePoint <= 0xDFFF) return false;
        if (codePoint >= 0xFDD0 && codePoint <= 0xFDEF) return false;
        if ((codePoint & 0xFFFF) is 0xFFFE or 0xFFFF) return false;
        return true;
    }
}
