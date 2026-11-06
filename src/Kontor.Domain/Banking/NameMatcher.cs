using System.Globalization;
using System.Text;

namespace Kontor.Domain.Banking;

/// <summary>
/// Fuzzy comparison of person and company names, used for Verification of Payee and sanctions screening.
/// Names are normalized first (case, umlauts, accents, punctuation, legal forms and titles), then compared word by word
/// with Jaro-Winkler similarity, so "Müller GmbH", "MUELLER" and "Mueller G.m.b.H." are the same name and
/// "Schmidt, Anna" equals "Anna Schmidt".
/// </summary>
public static class NameMatcher
{
    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
    {
        // legal forms
        "gmbh", "mbh", "ag", "kg", "kgaa", "ohg", "ug", "ev", "se", "ltd", "limited", "llc", "inc", "corp", "corporation", "co", "plc",
        "sa", "sarl", "srl", "spa", "bv", "nv", "fze", "fzco", "jsc", "ooo", "oao", "pjsc",
        // titles and fillers
        "herr", "frau", "mr", "mrs", "ms", "dr", "prof", "und", "and",
    };

    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var lower = name.ToLowerInvariant()
            .Replace("ä", "ae", StringComparison.Ordinal).Replace("ö", "oe", StringComparison.Ordinal)
            .Replace("ü", "ue", StringComparison.Ordinal).Replace("ß", "ss", StringComparison.Ordinal)
            .Replace("g.m.b.h.", "gmbh", StringComparison.Ordinal).Replace("e.v.", "ev", StringComparison.Ordinal)
            .Replace("&", " und ", StringComparison.Ordinal);

        var sb = new StringBuilder(lower.Length);
        foreach (var c in lower.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        // "Müller" is written Mueller in Germany and Muller in many international lists: treat all three alike
        var canonical = sb.ToString().Replace("ae", "a", StringComparison.Ordinal).Replace("oe", "o", StringComparison.Ordinal).Replace("ue", "u", StringComparison.Ordinal);
        return string.Join(' ', Tokens(canonical));
    }

    private static IEnumerable<string> Tokens(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => !Noise.Contains(t));

    /// <summary>Similarity from 0 (unrelated) to 1 (same name after normalization).</summary>
    public static double Similarity(string? a, string? b)
    {
        var ta = Normalize(a).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tb = Normalize(b).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (ta.Length == 0 || tb.Length == 0) return 0;

        var sortedA = string.Join(' ', ta.Order(StringComparer.Ordinal));
        var sortedB = string.Join(' ', tb.Order(StringComparer.Ordinal));
        if (sortedA == sortedB) return 1;
        // Whole-string similarity only for names with the same number of words: otherwise the shared prefix of
        // "Exempelsson" and "Exempelsson Viktor" would make a bare surname look like a near-certain match
        var whole = ta.Length == tb.Length ? JaroWinkler(sortedA, sortedB) : 0;

        // Word by word: every word of the shorter name must have a close partner in the longer one.
        // Catches a missing middle name ("Boris Musterow" vs "Boris Testowitsch Musterow") and initials ("A. Schmidt")
        var (shorter, longer) = ta.Length <= tb.Length ? (ta, tb) : (tb, ta);
        var perWord = shorter.Average(s => longer.Max(l => WordSimilarity(s, l)));
        // Words missing from the shorter name lower the score: 3 of 3 words = 1.0, 2 of 3 = 0.93, 1 of 2 = 0.9
        var coverage = shorter.Length == 1 && longer.Length > 1 ? 0.8 : 0.8 + 0.2 * shorter.Length / longer.Length;

        return Math.Round(Math.Max(whole, perWord * coverage), 3);
    }

    private static double WordSimilarity(string a, string b)
    {
        if (a == b) return 1;
        // An initial matches a name with the same first letter, but never counts as a perfect match
        if ((a.Length == 1 || b.Length == 1) && a[0] == b[0]) return 0.9;
        return JaroWinkler(a, b);
    }

    /// <summary>Jaro-Winkler similarity (Winkler 1990), prefix scale 0.1 over at most four characters.</summary>
    public static double JaroWinkler(string s1, string s2)
    {
        if (s1 == s2) return 1;
        if (s1.Length == 0 || s2.Length == 0) return 0;

        var window = Math.Max(0, Math.Max(s1.Length, s2.Length) / 2 - 1);
        var matched1 = new bool[s1.Length];
        var matched2 = new bool[s2.Length];
        var matches = 0;
        for (var i = 0; i < s1.Length; i++)
        {
            var from = Math.Max(0, i - window);
            var to = Math.Min(i + window + 1, s2.Length);
            for (var j = from; j < to; j++)
            {
                if (matched2[j] || s1[i] != s2[j]) continue;
                matched1[i] = matched2[j] = true;
                matches++;
                break;
            }
        }
        if (matches == 0) return 0;

        var transpositions = 0;
        for (int i = 0, k = 0; i < s1.Length; i++)
        {
            if (!matched1[i]) continue;
            while (!matched2[k]) k++;
            if (s1[i] != s2[k]) transpositions++;
            k++;
        }

        double m = matches;
        var jaro = (m / s1.Length + m / s2.Length + (m - transpositions / 2.0) / m) / 3.0;
        var prefix = 0;
        while (prefix < Math.Min(4, Math.Min(s1.Length, s2.Length)) && s1[prefix] == s2[prefix]) prefix++;
        return jaro + prefix * 0.1 * (1 - jaro);
    }
}
