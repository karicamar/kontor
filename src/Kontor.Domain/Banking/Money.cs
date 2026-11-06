using System.Globalization;

namespace Kontor.Domain.Banking;

/// <summary>
/// A euro amount stored as whole cents. Money is never a <see cref="double"/>: 0.1 + 0.2 must be exactly 0.30.
/// </summary>
public readonly record struct Money(long Cents) : IComparable<Money>
{
    /// <summary>The largest amount a SEPA credit transfer can carry (999,999,999.99 EUR, EPC rulebook).</summary>
    public static readonly Money SepaMaximum = new(99_999_999_999);

    public static Money Zero => new(0);

    public static Money FromEuros(decimal euros)
    {
        if (decimal.Round(euros, 2) != euros)
            throw new ArgumentException("Amounts have at most two decimal places.", nameof(euros));
        return new Money(checked((long)(euros * 100m)));
    }

    public static bool TryFromEuros(decimal euros, out Money money)
    {
        money = Zero;
        if (decimal.Round(euros, 2) != euros || Math.Abs(euros) > 90_000_000_000_000m) return false;
        money = new Money((long)(euros * 100m));
        return true;
    }

    public decimal Euros => Cents / 100m;

    public bool IsPositive => Cents > 0;

    public static Money operator +(Money a, Money b) => new(checked(a.Cents + b.Cents));
    public static Money operator -(Money a, Money b) => new(checked(a.Cents - b.Cents));
    public static Money operator -(Money a) => new(checked(-a.Cents));
    public static bool operator <(Money a, Money b) => a.Cents < b.Cents;
    public static bool operator >(Money a, Money b) => a.Cents > b.Cents;
    public static bool operator <=(Money a, Money b) => a.Cents <= b.Cents;
    public static bool operator >=(Money a, Money b) => a.Cents >= b.Cents;

    public int CompareTo(Money other) => Cents.CompareTo(other.Cents);

    /// <summary>"1234.50": the format ISO 20022 XML uses (dot, no thousands separator), independent of the server's culture.</summary>
    public string ToXml() => Euros.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>"1.234,50 €": German display format.</summary>
    public string ToGerman() => Euros.ToString("#,##0.00", CultureInfo.GetCultureInfo("de-DE")) + " €";

    public override string ToString() => ToXml() + " EUR";
}
