using System.Diagnostics.CodeAnalysis;

namespace Kontor.Domain.Banking;

/// <summary>
/// A Business Identifier Code (ISO 9362, "SWIFT code"): 4 letters bank, 2 letters country, 2 letters or digits location,
/// optionally 3 letters or digits branch. Optional in SEPA since 2016 (the IBAN is enough), so most payments have none.
/// </summary>
public sealed record Bic
{
    private Bic(string value) => Value = value;

    public string Value { get; }

    public string CountryCode => Value.Substring(4, 2);

    public static bool TryParse(string? input, [NotNullWhen(true)] out Bic? bic)
    {
        bic = null;
        var value = (input ?? "").Trim().ToUpperInvariant();
        if (value.Length is not (8 or 11)) return false;
        if (!value[..6].All(char.IsAsciiLetter)) return false;
        if (!value[6..].All(char.IsAsciiLetterOrDigit)) return false;
        bic = new Bic(value);
        return true;
    }

    public override string ToString() => Value;
}
