using Kontor.Domain.Banking;
using Kontor.Domain.Sanctions;

namespace Kontor.Domain.Risk;

public enum RiskAction
{
    Allow = 0,
    /// <summary>Needs a second person (four-eyes) before it is executed.</summary>
    Review = 1,
    /// <summary>Stopped; only Compliance can release or reject it.</summary>
    Block = 2,
}

public sealed record RiskFinding(string RuleId, string Title, string Detail, RiskAction Action, int Points);

public sealed record RiskAssessment(IReadOnlyList<RiskFinding> Findings)
{
    public RiskAction Action => Findings.Count == 0 ? RiskAction.Allow : Findings.Max(f => f.Action);

    /// <summary>0..100, for sorting review queues; the decision itself comes from <see cref="Action"/>.</summary>
    public int Score => Math.Min(100, Findings.Sum(f => f.Points));
}

/// <summary>The payment being checked.</summary>
public sealed record PaymentCandidate(Guid DebtorAccountId, string CreditorName, Iban CreditorIban, Money Amount, DateTimeOffset At);

/// <summary>An earlier outgoing payment of the same debtor (rejected ones are not included).</summary>
public sealed record PastPayment(string CreditorIban, Money Amount, DateTimeOffset At);

public sealed class RiskSettings
{
    /// <summary>Payments from this amount always need a second approver.</summary>
    public decimal ApprovalLimitEuros { get; set; } = 10_000m;
    public TimeSpan SplitWindow { get; set; } = TimeSpan.FromHours(24);
    public int VelocityCount { get; set; } = 5;
    public TimeSpan VelocityWindow { get; set; } = TimeSpan.FromMinutes(10);
    public decimal NewPayeeReviewEuros { get; set; } = 5_000m;
    public decimal UnusualAmountFactor { get; set; } = 5m;
    public int UnusualAmountMinHistory { get; set; } = 3;
    /// <summary>
    /// Countries whose IBANs always get a second look. The default is an example; in production this follows the current
    /// EU list of high-risk third countries (Delegated Regulation (EU) 2016/1675, as amended) and the bank's own risk analysis.
    /// </summary>
    public HashSet<string> HighRiskCountries { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Money ApprovalLimit => Money.FromEuros(ApprovalLimitEuros);
}

public sealed record RiskContext(
    PaymentCandidate Payment,
    IReadOnlyList<PastPayment> History,
    IReadOnlySet<string> KnownCreditorIbans,
    RiskSettings Settings,
    SanctionsList Sanctions);

public interface IRiskRule
{
    IEnumerable<RiskFinding> Evaluate(RiskContext context);
}
