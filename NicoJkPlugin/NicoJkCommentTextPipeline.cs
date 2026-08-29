using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace NicoJkPlugin;

/// <summary>
/// Single shared comment text pipeline for NicoJK/NX-Jikkyo.
/// The normalized Unicode string produced here is the canonical value used by
/// duplicate detection, XML save, TimedTextStreams, and VideoOverlay.
/// Do not add projection-specific character replacement or cleanup elsewhere.
/// </summary>
internal static class NicoJkCommentTextPipeline
{
    private const int MaxDecodePasses = 10;

    private static readonly Regex EntityCandidate = new(@"&(?:amp;|#|#x|[a-zA-Z])", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex NumericEntity = new(@"&#(?<hex>x)?(?<value>[0-9a-fA-F]{1,8});?", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex BrokenAmpNumericEntity = new(@"&amp;(?=#(?:x[0-9a-fA-F]{1,8}|[0-9]{1,8});?)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ResidualNumericEntity = new(@"&#(?:x[0-9a-fA-F]{1,8}|[0-9]{1,8});?", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ResidualEncodedNumericEntity = new(@"&amp;#(?:x[0-9a-fA-F]{1,8}|[0-9]{1,8});?", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ResidualNamedEntity = new(@"&(?:amp|lt|gt|quot|apos|nbsp|#39);", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Creates the canonical Unicode value shared by all comment projections.
    /// Valid supplementary-plane scalars, ZWJ, variation selectors, and combining
    /// characters are preserved. Only malformed surrogate code units and unwanted
    /// control characters are removed.
    /// </summary>
    public static string NormalizeForDisplay(string? value) => NormalizeCanonical(value);

    public static string NormalizeForPublish(string? value) => NormalizeCanonical(value);

    public static string NormalizeForSave(string? value) => NormalizeCanonical(value);

    public static string NormalizeForDuplicateKey(string? value) => NormalizeCanonical(value);

    public static string NormalizeXmlAttribute(string? value) => NormalizeCanonical(value);

    public static string EscapeXmlText(string? value)
    {
        var canonical = NormalizeCanonical(value);
        return canonical
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }

    public static string EscapeXmlAttribute(string? value)
    {
        var canonical = NormalizeCanonical(value);
        return canonical
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
    }

    public static bool HasResidualNumericReference(string? value)
        => !string.IsNullOrEmpty(value) && (ResidualNumericEntity.IsMatch(value) || ResidualEncodedNumericEntity.IsMatch(value));

    public static bool HasResidualHtmlReference(string? value)
        => !string.IsNullOrEmpty(value) && (HasResidualNumericReference(value) || ResidualNamedEntity.IsMatch(value));

    private static string NormalizeCanonical(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var decoded = DecodeHtmlEntitiesRepeated(value, MaxDecodePasses);
        if (decoded.Length == 0) return string.Empty;

        return SanitizeUnicode(decoded);
    }

    private static string SanitizeUnicode(string value)
    {
        var sb = new StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];

            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    sb.Append(ch);
                    sb.Append(value[++i]);
                }
                // An isolated high surrogate is malformed input and is discarded.
                continue;
            }

            // An isolated low surrogate is malformed input and is discarded.
            if (char.IsLowSurrogate(ch)) continue;

            // Preserve all valid Unicode characters, including ZWJ, variation selectors,
            // combining characters, supplementary-plane scalars, and original spacing.
            // Remove only control code units that do not belong in comment text.
            if (char.IsControl(ch)) continue;

            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static string DecodeHtmlEntitiesRepeated(string value, int maxPasses)
    {
        var current = value;
        for (var i = 0; i < maxPasses; i++)
        {
            if (!EntityCandidate.IsMatch(current) && !HasResidualHtmlReference(current)) break;

            var before = current;
            current = BrokenAmpNumericEntity.Replace(current, "&");
            current = WebUtility.HtmlDecode(current) ?? string.Empty;
            current = DecodeNumericEntities(current);

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

    private static bool IsValidUnicodeScalar(int codePoint)
    {
        if (codePoint <= 0 || codePoint > 0x10FFFF) return false;
        if (codePoint >= 0xD800 && codePoint <= 0xDFFF) return false;
        if (codePoint >= 0xFDD0 && codePoint <= 0xFDEF) return false;
        if ((codePoint & 0xFFFF) is 0xFFFE or 0xFFFF) return false;
        return true;
    }
}
