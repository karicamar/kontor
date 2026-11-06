using System.Text;
using System.Xml;
using Kontor.Domain.Audit;
using Kontor.Domain.Banking;
using Kontor.Domain.Ledger;
using Kontor.Domain.Risk;
using Kontor.Domain.Sanctions;

namespace Kontor.Tests;

public class SanctionsTests
{
    internal static SanctionsList Demo()
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Data", "demo-sanctions.xml"));
        return SanctionsList.LoadEuXml(file);
    }

    [Fact]
    public void Reads_the_eu_list_format_with_all_aliases()
    {
        var list = Demo();
        Assert.Equal(4, list.Parties.Count);
        var boris = list.Parties.Single(p => p.Id == "DEMO.0001");
        Assert.Equal("person", boris.Type);
        Assert.Contains("Borys Musterov", boris.Names);
    }

    [Theory]
    [InlineData("Golden Lantern Holdings Ltd", "Golden Lantern Holdings Ltd")]
    [InlineData("GOLDEN LANTERN HOLDINGS", "Golden Lantern Holdings Ltd")]     // legal form left out
    [InlineData("Golden Lantern Trading", "Golden Lantern Trading FZE")]        // other alias
    [InlineData("Musterow, Boris Testowitsch", "Boris Testowitsch Musterow")] // order
    [InlineData("Borys Musterov", "Borys Musterov")]                           // transliteration alias
    public void Finds_hits(string name, string matched)
    {
        var hit = Demo().Screen(name);
        Assert.NotNull(hit);
        Assert.Equal(matched, hit.MatchedName);
        Assert.True(hit.Score >= SanctionsList.HitThreshold);
    }

    [Fact]
    public void A_missing_middle_name_is_still_found()
    {
        var hit = Demo().Screen("Boris Musterow");
        Assert.NotNull(hit);
        Assert.True(hit.Score >= SanctionsList.ReviewThreshold);
    }

    [Theory]
    [InlineData("Anna Schmidt")]
    [InlineData("Bäckerei Müller GmbH")]
    [InlineData("Golden Gate Bakery")]
    [InlineData("Viktor Petersen")]
    public void Ordinary_names_are_not_flagged(string name) => Assert.Null(Demo().Screen(name));

    [Fact]
    public void Rejects_xml_with_a_dtd()
    {
        // XXE: an attacker-supplied list must not be able to read local files
        var xml = "<?xml version=\"1.0\"?><!DOCTYPE export [<!ENTITY x SYSTEM \"file:///c:/windows/win.ini\">]><export><sanctionEntity><nameAlias wholeName=\"&x;\"/></sanctionEntity></export>";
        Assert.Throws<XmlException>(() => SanctionsList.LoadEuXml(new MemoryStream(Encoding.UTF8.GetBytes(xml))));
    }
}

public class RiskRuleTests
{
    private static readonly Guid Debtor = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);
    private static readonly Iban Payee = Iban.CreateGerman("09111100", "5550000001");

    private static RiskAssessment Assess(decimal amount, string name = "Kunsthandel Weber", Iban? iban = null,
        IEnumerable<PastPayment>? history = null, IEnumerable<string>? known = null, RiskSettings? settings = null) =>
        RiskEngine.Default.Assess(new RiskContext(
            new PaymentCandidate(Debtor, name, iban ?? Payee, Money.FromEuros(amount), Now),
            (history ?? []).ToList(), (known ?? [Payee.Value]).ToHashSet(), settings ?? new RiskSettings(), SanctionsTests.Demo()));

    private static PastPayment Past(decimal amount, TimeSpan ago, Iban? iban = null) => new((iban ?? Payee).Value, Money.FromEuros(amount), Now - ago);

    [Fact]
    public void An_ordinary_payment_has_no_findings()
    {
        var a = Assess(120, history: [Past(80, TimeSpan.FromDays(30))]);
        Assert.Empty(a.Findings);
        Assert.Equal(RiskAction.Allow, a.Action);
    }

    [Fact]
    public void Sanctioned_payee_is_blocked()
    {
        var a = Assess(500, "Golden Lantern Holdings");
        Assert.Equal(RiskAction.Block, a.Action);
        Assert.Equal("KON-AML-001", a.Findings[0].RuleId);
        Assert.Equal(100, a.Score);
    }

    [Fact]
    public void Possible_sanctions_match_goes_to_review_not_block()
    {
        var a = Assess(500, "Borris Mustarow"); // two typos: similar, but not certain
        Assert.Contains(a.Findings, f => f is { RuleId: "KON-AML-001", Action: RiskAction.Review });
    }

    [Fact]
    public void Split_payments_below_the_approval_limit_are_caught()
    {
        var a = Assess(4_000, history: [Past(9_000, TimeSpan.FromHours(3))]);
        var f = Assert.Single(a.Findings, f => f.RuleId == "KON-AML-003");
        Assert.Contains("13.000,00 €", f.Detail);
        Assert.Equal(RiskAction.Review, a.Action);
    }

    [Fact]
    public void Split_rule_ignores_old_payments_and_other_payees()
    {
        var other = Iban.CreateGerman("09111100", "5550000099");
        Assert.DoesNotContain(Assess(4_000, history: [Past(9_000, TimeSpan.FromHours(30))]).Findings, f => f.RuleId == "KON-AML-003");
        Assert.DoesNotContain(Assess(4_000, history: [Past(9_000, TimeSpan.FromHours(3), other)], known: [Payee.Value, other.Value]).Findings, f => f.RuleId == "KON-AML-003");
    }

    [Fact]
    public void High_risk_country_from_settings()
    {
        var settings = new RiskSettings { HighRiskCountries = new(StringComparer.OrdinalIgnoreCase) { "MT" } };
        var maltese = Iban.Create("MT", "MALT011000012345MTLCAST001S");
        var a = Assess(100, iban: maltese, known: [maltese.Value], settings: settings);
        Assert.Contains(a.Findings, f => f.RuleId == "KON-AML-002");
        Assert.DoesNotContain(Assess(100).Findings, f => f.RuleId == "KON-AML-002");
    }

    [Fact]
    public void Many_payments_in_minutes_trigger_velocity()
    {
        var history = Enumerable.Range(1, 4).Select(i => Past(20, TimeSpan.FromMinutes(i))).ToList();
        Assert.Contains(Assess(20, history: history).Findings, f => f.RuleId == "KON-FRD-001");
        Assert.DoesNotContain(Assess(20, history: history.Take(3)).Findings, f => f.RuleId == "KON-FRD-001");
    }

    [Fact]
    public void Large_amount_to_a_new_payee()
    {
        Assert.Contains(Assess(5_000, known: []).Findings, f => f.RuleId == "KON-FRD-002");
        Assert.DoesNotContain(Assess(4_999.99m, known: []).Findings, f => f.RuleId == "KON-FRD-002");
        Assert.DoesNotContain(Assess(5_000).Findings, f => f.RuleId == "KON-FRD-002"); // paid before
    }

    [Fact]
    public void Unusual_amount_needs_a_baseline()
    {
        var history = new[] { Past(100, TimeSpan.FromDays(10)), Past(150, TimeSpan.FromDays(20)), Past(90, TimeSpan.FromDays(40)) };
        Assert.Contains(Assess(800, history: history).Findings, f => f.RuleId == "KON-FRD-003");
        Assert.DoesNotContain(Assess(700, history: history).Findings, f => f.RuleId == "KON-FRD-003");
        Assert.DoesNotContain(Assess(800, history: history.Take(2)).Findings, f => f.RuleId == "KON-FRD-003");
    }

    [Fact]
    public void Score_is_capped_and_findings_are_ordered_by_severity()
    {
        var a = Assess(9_500, "Golden Lantern Holdings", known: [], history: [Past(9_000, TimeSpan.FromHours(1))]);
        Assert.Equal(100, a.Score);
        Assert.Equal(RiskAction.Block, a.Findings[0].Action);
    }
}

public class JournalRulesTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid();

    [Fact]
    public void Debit_credit_balances() =>
        Assert.Equal(Money.Zero, JournalRules.DebitCredit(A, B, Money.FromEuros(10)).Aggregate(Money.Zero, (s, p) => s + p.Amount));

    [Fact]
    public void Unbalanced_entries_are_refused()
    {
        var ex = Assert.Throws<LedgerException>(() => JournalRules.Validate([new Posting(A, Money.FromEuros(10)), new Posting(B, Money.FromEuros(-9.99m))]));
        Assert.Contains("0.01", ex.Message);
    }

    [Fact]
    public void Single_zero_or_duplicate_postings_are_refused()
    {
        Assert.Throws<LedgerException>(() => JournalRules.Validate([new Posting(A, Money.Zero), new Posting(B, Money.Zero)]));
        Assert.Throws<LedgerException>(() => JournalRules.Validate([new Posting(A, Money.FromEuros(1))]));
        Assert.Throws<LedgerException>(() => JournalRules.Validate([new Posting(A, Money.FromEuros(1)), new Posting(A, Money.FromEuros(-1))]));
        Assert.Throws<LedgerException>(() => JournalRules.DebitCredit(A, A, Money.FromEuros(1)));
        Assert.Throws<LedgerException>(() => JournalRules.DebitCredit(A, B, Money.FromEuros(-1)));
    }

    [Fact]
    public void Split_entries_with_three_lines_are_fine()
    {
        var entry = JournalRules.Validate([new Posting(A, Money.FromEuros(100)), new Posting(B, Money.FromEuros(-60)), new Posting(C, Money.FromEuros(-40))]);
        Assert.Equal(3, entry.Count);
    }

    [Fact]
    public void Debits_increase_assets_and_decrease_liabilities()
    {
        var debit = Money.FromEuros(50);
        Assert.Equal(debit, JournalRules.BalanceEffect(AccountSide.Asset, debit));
        Assert.Equal(-debit, JournalRules.BalanceEffect(AccountSide.Liability, debit));
        Assert.Equal(debit, JournalRules.BalanceEffect(AccountSide.Equity, -debit));
    }
}

public class AuditChainTests
{
    private static List<(AuditFields Fields, string PreviousHash, string Hash)> Chain(int n)
    {
        var list = new List<(AuditFields, string, string)>();
        var previous = AuditChain.GenesisHash;
        for (var i = 1; i <= n; i++)
        {
            var f = new AuditFields(i, new DateTimeOffset(2026, 11, 10, 9, 0, i, TimeSpan.Zero), "anna", "payment.created", $"p{i}", $"{{\"amount\":{i}}}");
            var hash = AuditChain.Hash(previous, f);
            list.Add((f, previous, hash));
            previous = hash;
        }
        return list;
    }

    [Fact]
    public void An_untouched_chain_verifies()
    {
        var result = AuditChain.Verify(Chain(5));
        Assert.True(result.Intact);
        Assert.Equal(5, result.RecordsChecked);
    }

    [Fact]
    public void A_changed_record_is_found()
    {
        var chain = Chain(5);
        chain[2] = (chain[2].Fields with { Details = "{\"amount\":3000}" }, chain[2].PreviousHash, chain[2].Hash);
        var result = AuditChain.Verify(chain);
        Assert.False(result.Intact);
        Assert.Equal(3, result.FirstBrokenSequence);
        Assert.Contains("changed", result.Problem);
    }

    [Fact]
    public void A_deleted_record_is_found()
    {
        var chain = Chain(5);
        chain.RemoveAt(1);
        var result = AuditChain.Verify(chain);
        Assert.False(result.Intact);
        Assert.Contains("deleted", result.Problem);
    }

    [Fact]
    public void Recomputing_one_hash_is_not_enough_to_hide_a_change()
    {
        // The attacker changes record 3 and fixes its hash; record 4 still points to the old one
        var chain = Chain(5);
        var changed = chain[2].Fields with { Actor = "mallory" };
        chain[2] = (changed, chain[2].PreviousHash, AuditChain.Hash(chain[2].PreviousHash, changed));
        var result = AuditChain.Verify(chain);
        Assert.Equal(4, result.FirstBrokenSequence);
    }

    [Fact]
    public void Field_boundaries_are_part_of_the_hash()
    {
        var at = DateTimeOffset.UnixEpoch;
        Assert.NotEqual(
            AuditChain.Hash(AuditChain.GenesisHash, new AuditFields(1, at, "ab", "c", "x", "{}")),
            AuditChain.Hash(AuditChain.GenesisHash, new AuditFields(1, at, "a", "bc", "x", "{}")));
    }
}
