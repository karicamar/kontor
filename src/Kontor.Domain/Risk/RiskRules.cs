using Kontor.Domain.Banking;
using Kontor.Domain.Sanctions;

namespace Kontor.Domain.Risk;

/// <summary>KON-AML-001: the payee's name is on the sanctions list (or very close to an entry).</summary>
public sealed class SanctionsRule : IRiskRule
{
    public IEnumerable<RiskFinding> Evaluate(RiskContext c)
    {
        if (c.Sanctions.Screen(c.Payment.CreditorName) is not { } hit) yield break;
        var blocked = hit.Score >= SanctionsList.HitThreshold;
        yield return new RiskFinding("KON-AML-001",
            blocked ? "Payee is on the sanctions list" : "Payee name resembles a sanctions list entry",
            $"\"{c.Payment.CreditorName}\" matches \"{hit.MatchedName}\" ({hit.Party.Programme} {hit.Party.Id}, similarity {hit.Score.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)})",
            blocked ? RiskAction.Block : RiskAction.Review, blocked ? 100 : 60);
    }
}

/// <summary>KON-AML-002: the payee's account is in a high-risk country.</summary>
public sealed class HighRiskCountryRule : IRiskRule
{
    public IEnumerable<RiskFinding> Evaluate(RiskContext c)
    {
        var country = c.Payment.CreditorIban.CountryCode;
        if (c.Settings.HighRiskCountries.Contains(country))
            yield return new RiskFinding("KON-AML-002", "Payee account in a high-risk country",
                $"IBAN country {country} is on the high-risk country list", RiskAction.Review, 40);
    }
}

/// <summary>
/// KON-AML-003: one large payment split into several smaller ones to the same payee, so that none of them reaches the
/// four-eyes limit ("structuring"/"smurfing"). Each part is below the limit, together they are at or above it.
/// </summary>
public sealed class SplitPaymentRule : IRiskRule
{
    public IEnumerable<RiskFinding> Evaluate(RiskContext c)
    {
        var limit = c.Settings.ApprovalLimit;
        if (c.Payment.Amount >= limit) yield break; // needs approval anyway
        var since = c.Payment.At - c.Settings.SplitWindow;
        var parts = c.History
            .Where(p => p.At >= since && p.CreditorIban == c.Payment.CreditorIban.Value && p.Amount < limit)
            .ToList();
        if (parts.Count == 0) yield break;
        var total = parts.Aggregate(c.Payment.Amount, (s, p) => s + p.Amount);
        if (total >= limit)
            yield return new RiskFinding("KON-AML-003", "Payments split below the approval limit",
                $"{parts.Count + 1} payments to {c.Payment.CreditorIban.Masked} within {c.Settings.SplitWindow.TotalHours:0} h total {total.ToGerman()}, " +
                $"each below the {limit.ToGerman()} four-eyes limit", RiskAction.Review, 50);
    }
}

/// <summary>KON-FRD-001: many payments in a short time, typical for a taken-over account being emptied.</summary>
public sealed class VelocityRule : IRiskRule
{
    public IEnumerable<RiskFinding> Evaluate(RiskContext c)
    {
        var since = c.Payment.At - c.Settings.VelocityWindow;
        var count = c.History.Count(p => p.At >= since) + 1;
        if (count >= c.Settings.VelocityCount)
            yield return new RiskFinding("KON-FRD-001", "Unusually many payments in a short time",
                $"{count} payments within {c.Settings.VelocityWindow.TotalMinutes:0} minutes", RiskAction.Review, 30);
    }
}

/// <summary>KON-FRD-002: a large amount to an IBAN this customer has never paid before (CEO fraud, invoice redirection).</summary>
public sealed class NewPayeeRule : IRiskRule
{
    public IEnumerable<RiskFinding> Evaluate(RiskContext c)
    {
        var threshold = Money.FromEuros(c.Settings.NewPayeeReviewEuros);
        if (c.Payment.Amount >= threshold && !c.KnownCreditorIbans.Contains(c.Payment.CreditorIban.Value))
            yield return new RiskFinding("KON-FRD-002", "Large payment to a new payee",
                $"{c.Payment.Amount.ToGerman()} to {c.Payment.CreditorIban.Masked}, never paid before", RiskAction.Review, 30);
    }
}

/// <summary>KON-FRD-003: far more than this customer usually sends.</summary>
public sealed class UnusualAmountRule : IRiskRule
{
    public IEnumerable<RiskFinding> Evaluate(RiskContext c)
    {
        if (c.History.Count < c.Settings.UnusualAmountMinHistory) yield break; // no baseline yet
        var largest = c.History.Max(p => p.Amount);
        if (c.Payment.Amount.Euros > largest.Euros * c.Settings.UnusualAmountFactor)
            yield return new RiskFinding("KON-FRD-003", "Amount far above this customer's usual payments",
                $"{c.Payment.Amount.ToGerman()} is more than {c.Settings.UnusualAmountFactor:0}× the largest payment of the last 90 days ({largest.ToGerman()})",
                RiskAction.Review, 25);
    }
}

public sealed class RiskEngine(IEnumerable<IRiskRule> rules)
{
    public static RiskEngine Default { get; } = new(
    [
        new SanctionsRule(), new HighRiskCountryRule(), new SplitPaymentRule(),
        new VelocityRule(), new NewPayeeRule(), new UnusualAmountRule(),
    ]);

    public RiskAssessment Assess(RiskContext context) =>
        new(rules.SelectMany(r => r.Evaluate(context)).OrderByDescending(f => f.Action).ThenByDescending(f => f.Points).ToList());
}
