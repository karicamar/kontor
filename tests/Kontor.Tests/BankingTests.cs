using System.Globalization;
using Kontor.Domain.Banking;

namespace Kontor.Tests;

public class IbanTests
{
    [Theory]
    // Example IBANs published by the national banking associations / the SWIFT IBAN registry
    [InlineData("DE89370400440532013000", "DE")]
    [InlineData("GB82WEST12345698765432", "GB")]
    [InlineData("FR1420041010050500013M02606", "FR")]
    [InlineData("NL91ABNA0417164300", "NL")]
    [InlineData("AT611904300234573201", "AT")]
    [InlineData("CH9300762011623852957", "CH")]
    [InlineData("BE68539007547034", "BE")]
    public void Accepts_valid_ibans(string value, string country)
    {
        Assert.True(Iban.TryParse(value, out var iban, out var error), error);
        Assert.Equal(country, iban.CountryCode);
        Assert.True(iban.IsSepa);
    }

    [Fact]
    public void Normalizes_spaces_and_case_and_formats_in_groups_of_four()
    {
        var iban = Iban.Parse(" de89 3704 0044 0532 0130 00 ");
        Assert.Equal("DE89370400440532013000", iban.Value);
        Assert.Equal("DE89 3704 0044 0532 0130 00", iban.Formatted);
        Assert.Equal("DE89 **** 3000", iban.Masked);
        Assert.Equal("37040044", iban.GermanBankCode);
    }

    [Theory]
    [InlineData("DE88370400440532013000", "check digits")]       // one digit off: the typo the check digits exist for
    [InlineData("DE89370400440532013", "has 22 characters")]
    [InlineData("XX89370400440532013000", "not supported")]
    [InlineData("DE89 3704 0044 0532 0130 0!", "letters and digits")]
    [InlineData("DE8937040044053201300A", "bank code")]          // German BBAN is digits only
    [InlineData("", "missing")]
    [InlineData("1234567890", "country code")]
    public void Rejects_invalid_ibans_with_a_reason(string value, string reason)
    {
        Assert.False(Iban.TryParse(value, out _, out var error));
        Assert.Contains(reason, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Swapped_digits_are_detected()
    {
        // mod 97 catches every transposition of two adjacent digits
        Assert.False(Iban.TryParse("DE89370400440532010300", out _, out _));
    }

    [Fact]
    public void Creates_iban_with_correct_check_digits()
    {
        Assert.Equal("DE89370400440532013000", Iban.CreateGerman("37040044", "532013000").Value);
        var created = Iban.Create("NL", "ABNA0417164300");
        Assert.Equal("NL91ABNA0417164300", created.Value);
    }

    [Fact]
    public void Turkish_iban_is_valid_but_outside_sepa()
    {
        var iban = Iban.Parse("TR330006100519786457841326");
        Assert.False(iban.IsSepa);
    }
}

public class BicTests
{
    [Theory]
    [InlineData("COBADEFF", true)]
    [InlineData("COBADEFFXXX", true)]
    [InlineData("deutdeff500", true)]
    [InlineData("COBADEF", false)]
    [InlineData("C0BADEFF", false)]   // digit in the bank code
    [InlineData("COBADEFF12", false)]
    public void Validates_format(string value, bool valid) => Assert.Equal(valid, Bic.TryParse(value, out _));
}

public class MoneyTests
{
    [Fact]
    public void Cents_are_exact() =>
        Assert.Equal(Money.FromEuros(0.30m), Money.FromEuros(0.10m) + Money.FromEuros(0.20m));

    [Fact]
    public void More_than_two_decimals_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Money.FromEuros(1.005m));
        Assert.False(Money.TryFromEuros(1.005m, out _));
    }

    [Fact]
    public void Xml_format_does_not_depend_on_the_server_culture()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("1234.50", Money.FromEuros(1234.5m).ToXml());
            Assert.Equal("1.234,50 €", Money.FromEuros(1234.5m).ToGerman());
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }
}

public class SepaTextTests
{
    [Theory]
    [InlineData("Bäckerei Müller & Söhne", "Baeckerei Mueller + Soehne")]
    [InlineData("Straße", "Strasse")]
    [InlineData("José Núñez", "Jose Nunez")]
    [InlineData("Rechnung #4711 — bitte 😀 zahlen", "Rechnung 4711 bitte zahlen")]
    [InlineData("  zu   viele   Leerzeichen ", "zu viele Leerzeichen")]
    [InlineData("Ref: 2026/11-A (Teil 1), Kd.'s Nr.+5?", "Ref: 2026/11-A (Teil 1), Kd.'s Nr.+5?")]
    public void Converts_to_the_sepa_character_set(string input, string expected) =>
        Assert.Equal(expected, SepaText.Clean(input, 140));

    [Fact]
    public void Cuts_to_the_maximum_length() =>
        Assert.Equal(70, SepaText.Clean(new string('a', 100), SepaText.MaxNameLength).Length);

    [Theory]
    [InlineData("KON-20261110-ABC", true)]
    [InlineData("/KON", false)]
    [InlineData("KON/", false)]
    [InlineData("KON//1", false)]
    [InlineData("KON 1", false)]
    [InlineData("KONÄ", false)]
    [InlineData("123456789012345678901234567890123456", false)] // 36 characters
    public void Validates_ids(string id, bool valid) => Assert.Equal(valid, SepaText.IsValidId(id));
}

public class BusinessCalendarTests
{
    [Theory]
    [InlineData(2024, 3, 31)]
    [InlineData(2025, 4, 20)]
    [InlineData(2026, 4, 5)]
    [InlineData(2027, 3, 28)]
    [InlineData(2038, 4, 25)]
    public void Computes_easter_sunday(int year, int month, int day) =>
        Assert.Equal(new DateOnly(year, month, day), BusinessCalendar.EasterSunday(year));

    [Theory]
    [InlineData("2026-04-03", false)] // Good Friday
    [InlineData("2026-04-06", false)] // Easter Monday
    [InlineData("2026-05-01", false)]
    [InlineData("2026-12-25", false)]
    [InlineData("2026-12-26", false)] // Saturday anyway
    [InlineData("2027-01-01", false)]
    [InlineData("2026-11-07", false)] // Saturday
    [InlineData("2026-11-10", true)]
    [InlineData("2026-10-03", false)] // German Unity Day is a Saturday in 2026
    [InlineData("2025-10-03", true)]  // ...and not a TARGET2 holiday: SEPA runs on German public holidays
    public void Knows_target2_business_days(string date, bool businessDay) =>
        Assert.Equal(businessDay, BusinessCalendar.IsBusinessDay(DateOnly.Parse(date, CultureInfo.InvariantCulture)));

    [Fact]
    public void Moves_to_the_next_business_day()
    {
        Assert.Equal(new DateOnly(2026, 11, 9), BusinessCalendar.OnOrAfter(new DateOnly(2026, 11, 7)));  // Sat -> Mon
        Assert.Equal(new DateOnly(2026, 4, 7), BusinessCalendar.OnOrAfter(new DateOnly(2026, 4, 3)));   // Good Friday -> Tuesday
        Assert.Equal(new DateOnly(2026, 12, 28), BusinessCalendar.OnOrAfter(new DateOnly(2026, 12, 24).AddDays(1)));
    }
}

public class NameMatcherTests
{
    [Theory]
    [InlineData("Müller GmbH", "MUELLER")]
    [InlineData("Mueller G.m.b.H.", "Müller")]
    [InlineData("Schmidt, Anna", "Anna Schmidt")]
    [InlineData("Herr Dr. Jürgen Weiß", "Jurgen Weiss")]
    public void Same_name_after_normalization(string a, string b)
    {
        Assert.True(NameMatcher.Similarity(a, b) >= 0.95, $"{a} / {b}: {NameMatcher.Similarity(a, b)}");
    }

    [Fact]
    public void Jaro_winkler_matches_the_published_examples()
    {
        // Winkler (1990): MARTHA/MARHTA 0.961, DWAYNE/DUANE 0.84, DIXON/DICKSONX 0.813
        Assert.Equal(0.961, Math.Round(NameMatcher.JaroWinkler("MARTHA", "MARHTA"), 3));
        Assert.Equal(0.840, Math.Round(NameMatcher.JaroWinkler("DWAYNE", "DUANE"), 3));
        Assert.Equal(0.813, Math.Round(NameMatcher.JaroWinkler("DIXON", "DICKSONX"), 3));
    }

    [Fact]
    public void Initials_and_missing_middle_names_are_close_but_not_equal()
    {
        var initial = NameMatcher.Similarity("A. Schmidt", "Anna Schmidt");
        Assert.InRange(initial, 0.85, 0.99);
        var middle = NameMatcher.Similarity("Boris Musterow", "Boris Testowitsch Musterow");
        Assert.InRange(middle, 0.9, 0.99);
    }

    [Fact]
    public void Different_people_score_low()
    {
        Assert.True(NameMatcher.Similarity("Anna Schmidt", "Mehmet Yilmaz") < 0.6);
        Assert.True(NameMatcher.Similarity("Anna Schmidt", "Anna Schneider") < 0.95);
        Assert.Equal(0, NameMatcher.Similarity("", "Anna"));
    }
}

public class PayeeVerificationTests
{
    [Fact]
    public void Match_close_match_and_no_match()
    {
        Assert.Equal(PayeeMatch.Match, PayeeVerificationResult.Check("anna schmidt", "Anna Schmidt").Result);

        var close = PayeeVerificationResult.Check("Anna Schmitt", "Anna Schmidt");
        Assert.Equal(PayeeMatch.CloseMatch, close.Result);
        Assert.Equal("Anna Schmidt", close.ActualName);

        // A different person with a similar name must not get the real holder name shown
        Assert.Equal(PayeeMatch.NoMatch, PayeeVerificationResult.Check("Anna Schneider", "Anna Schmidt").Result);

        var none = PayeeVerificationResult.Check("Max Mustermann", "Anna Schmidt");
        Assert.Equal(PayeeMatch.NoMatch, none.Result);
        Assert.Null(none.ActualName); // never reveal who owns an IBAN to someone who guessed wrong
    }
}
