using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Kontor.Domain.Banking;

/// <summary>
/// An International Bank Account Number (ISO 13616), validated the way a payment system has to:
/// known country, the exact length for that country, only letters and digits, and the mod-97 check digits.
/// For German IBANs the BBAN must be an 8-digit bank code (BLZ) followed by a 10-digit account number.
/// </summary>
public sealed record Iban
{
    /// <summary>
    /// IBAN lengths of the countries in the SEPA scheme (EPC list of countries and territories), plus a few
    /// non-SEPA countries that use IBANs, so that "valid IBAN, but SEPA cannot reach it" is a separate answer.
    /// </summary>
    private static readonly Dictionary<string, int> Lengths = new(StringComparer.Ordinal)
    {
        // SEPA
        ["AD"] = 24, ["AT"] = 20, ["BE"] = 16, ["BG"] = 22, ["CH"] = 21, ["CY"] = 28, ["CZ"] = 24, ["DE"] = 22,
        ["DK"] = 18, ["EE"] = 20, ["ES"] = 24, ["FI"] = 18, ["FR"] = 27, ["GB"] = 22, ["GI"] = 23, ["GR"] = 27,
        ["HR"] = 21, ["HU"] = 28, ["IE"] = 22, ["IS"] = 26, ["IT"] = 27, ["LI"] = 21, ["LT"] = 20, ["LU"] = 20,
        ["LV"] = 21, ["MC"] = 27, ["MT"] = 31, ["NL"] = 18, ["NO"] = 15, ["PL"] = 28, ["PT"] = 25, ["RO"] = 24,
        ["SE"] = 24, ["SI"] = 19, ["SK"] = 24, ["SM"] = 27, ["VA"] = 22,
        // Outside SEPA
        ["AE"] = 23, ["BA"] = 20, ["IR"] = 26, ["RS"] = 22, ["SA"] = 24, ["TR"] = 26, ["UA"] = 29,
    };

    private static readonly HashSet<string> Sepa = new(StringComparer.Ordinal)
    {
        "AD", "AT", "BE", "BG", "CH", "CY", "CZ", "DE", "DK", "EE", "ES", "FI", "FR", "GB", "GI", "GR", "HR", "HU", "IE", "IS",
        "IT", "LI", "LT", "LU", "LV", "MC", "MT", "NL", "NO", "PL", "PT", "RO", "SE", "SI", "SK", "SM", "VA",
    };

    private Iban(string value) => Value = value;

    /// <summary>Electronic format: upper case, no spaces ("DE89370400440532013000").</summary>
    public string Value { get; }

    public string CountryCode => Value[..2];

    public string Bban => Value[4..];

    public bool IsSepa => Sepa.Contains(CountryCode);

    /// <summary>The German bank code (Bankleitzahl), or null for other countries.</summary>
    public string? GermanBankCode => CountryCode == "DE" ? Value.Substring(4, 8) : null;

    /// <summary>Print format in groups of four ("DE89 3704 0044 0532 0130 00").</summary>
    public string Formatted
    {
        get
        {
            var sb = new StringBuilder(Value.Length + Value.Length / 4);
            for (var i = 0; i < Value.Length; i++)
            {
                if (i > 0 && i % 4 == 0) sb.Append(' ');
                sb.Append(Value[i]);
            }
            return sb.ToString();
        }
    }

    /// <summary>Only the country, check digits and last four characters: for logs and screens that do not need the full number.</summary>
    public string Masked => $"{Value[..4]} **** {Value[^4..]}";

    public static Iban Parse(string input) =>
        TryParse(input, out var iban, out var error) ? iban : throw new FormatException(error);

    public static bool TryParse(string? input, [NotNullWhen(true)] out Iban? iban, [NotNullWhen(false)] out string? error)
    {
        iban = null;
        var value = Normalize(input);
        if (value.Length < 5)
        {
            error = "IBAN is missing or too short.";
            return false;
        }
        if (!value.All(char.IsAsciiLetterOrDigit))
        {
            error = "IBAN may only contain letters and digits.";
            return false;
        }
        var country = value[..2];
        if (!char.IsAsciiLetter(country[0]) || !char.IsAsciiLetter(country[1]) || !char.IsAsciiDigit(value[2]) || !char.IsAsciiDigit(value[3]))
        {
            error = "IBAN must start with a two-letter country code and two check digits.";
            return false;
        }
        if (!Lengths.TryGetValue(country, out var length))
        {
            error = $"Country code {country} is not supported.";
            return false;
        }
        if (value.Length != length)
        {
            error = $"An IBAN from {country} has {length} characters, this one has {value.Length}.";
            return false;
        }
        if (country == "DE" && !value[4..].All(char.IsAsciiDigit))
        {
            error = "A German IBAN consists of the bank code (8 digits) and the account number (10 digits).";
            return false;
        }
        if (Mod97(value[4..] + value[..4]) != 1)
        {
            error = "IBAN check digits are wrong (probably a typo).";
            return false;
        }

        iban = new Iban(value);
        error = null;
        return true;
    }

    /// <summary>Builds an IBAN from a country and BBAN by computing the check digits (ISO 13616 / ISO 7064 MOD 97-10).</summary>
    public static Iban Create(string countryCode, string bban)
    {
        var check = 98 - Mod97(bban + countryCode + "00");
        return Parse($"{countryCode}{check:00}{bban}");
    }

    /// <summary>A German IBAN from bank code and account number (the account number is left-padded to 10 digits).</summary>
    public static Iban CreateGerman(string bankCode, string accountNumber) => Create("DE", bankCode + accountNumber.PadLeft(10, '0'));

    private static string Normalize(string? input) =>
        input is null ? "" : string.Concat(input.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();

    /// <summary>Remainder of the number formed by replacing letters with 10..35, computed piecewise (it has up to 68 digits).</summary>
    private static int Mod97(string s)
    {
        var remainder = 0;
        foreach (var c in s)
        {
            if (char.IsAsciiDigit(c))
                remainder = (remainder * 10 + (c - '0')) % 97;
            else
                remainder = (remainder * 100 + (c - 'A' + 10)) % 97;
        }
        return remainder;
    }

    public override string ToString() => Value;
}
