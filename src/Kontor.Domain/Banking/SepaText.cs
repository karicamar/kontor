using System.Globalization;
using System.Text;

namespace Kontor.Domain.Banking;

/// <summary>
/// SEPA files may only use the basic Latin character set of the EPC guidelines:
/// <c>a-z A-Z 0-9 / - ? : ( ) . , ' +</c> and space. Banks reject (or silently mangle) anything else,
/// so names and references are converted before they go into an XML file: German umlauts become "ae", "oe", "ue", "ss",
/// other accents are dropped ("José" → "Jose"), and characters with no equivalent become a space.
/// </summary>
public static class SepaText
{
    public const int MaxNameLength = 70;
    public const int MaxRemittanceLength = 140;
    public const int MaxIdLength = 35;

    public static bool IsAllowed(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '?' or ':' or '(' or ')' or '.' or ',' or '\'' or '+' or ' ';

    public static string Clean(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case 'ä': sb.Append("ae"); continue;
                case 'ö': sb.Append("oe"); continue;
                case 'ü': sb.Append("ue"); continue;
                case 'Ä': sb.Append("Ae"); continue;
                case 'Ö': sb.Append("Oe"); continue;
                case 'Ü': sb.Append("Ue"); continue;
                case 'ß': sb.Append("ss"); continue;
                case '&': sb.Append('+'); continue;
            }
            if (char.IsSurrogate(c))
            {
                sb.Append(' '); // emoji and other characters outside the basic plane: no SEPA equivalent
                continue;
            }
            if (IsAllowed(c))
            {
                sb.Append(c);
                continue;
            }
            // "é" decomposes into "e" + combining accent: keep the base letter if it is allowed
            var decomposed = c.ToString().Normalize(NormalizationForm.FormD);
            var letter = decomposed.FirstOrDefault(x => CharUnicodeInfo.GetUnicodeCategory(x) != UnicodeCategory.NonSpacingMark);
            sb.Append(decomposed.Length > 1 && IsAllowed(letter) ? letter : ' ');
        }

        var cleaned = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength].TrimEnd();
    }

    /// <summary>Ids (MsgId, EndToEndId): must not start or end with "/" and must not contain "//" (EPC rule).</summary>
    public static bool IsValidId(string id) =>
        id.Length is > 0 and <= MaxIdLength && id.All(IsAllowed) && !id.Contains(' ')
        && !id.StartsWith('/') && !id.EndsWith('/') && !id.Contains("//", StringComparison.Ordinal);
}
