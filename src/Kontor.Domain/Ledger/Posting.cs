using Kontor.Domain.Banking;

namespace Kontor.Domain.Ledger;

/// <summary>Which side of the balance sheet an account is on; decides how debits and credits change its balance.</summary>
public enum AccountSide
{
    /// <summary>What the platform owns, e.g. its money at the partner bank. Debits increase it.</summary>
    Asset,
    /// <summary>What the platform owes, e.g. customer deposits. Credits increase it.</summary>
    Liability,
    Equity,
}

/// <summary>One line of a journal entry. Positive = debit, negative = credit.</summary>
public sealed record Posting(Guid AccountId, Money Amount)
{
    public bool IsDebit => Amount.IsPositive;
}

/// <summary>
/// Double-entry bookkeeping: every journal entry has at least two postings and its debits equal its credits (the signed
/// amounts sum to exactly zero). Money is never created or destroyed, so assets = liabilities + equity at all times and
/// every balance can be recomputed from its postings.
/// <para>
/// Example, a customer receives 100 € by SEPA: <b>debit</b> the partner bank account (asset +100, the platform holds more
/// money) and <b>credit</b> the customer account (liability +100, the platform owes the customer more).
/// </para>
/// </summary>
public static class JournalRules
{
    public static IReadOnlyList<Posting> Validate(IReadOnlyCollection<Posting> postings)
    {
        if (postings.Count < 2)
            throw new LedgerException("A journal entry needs at least two postings.");
        if (postings.Any(p => p.Amount == Money.Zero))
            throw new LedgerException("A posting of zero is meaningless.");
        var sum = postings.Aggregate(Money.Zero, (s, p) => s + p.Amount);
        if (sum != Money.Zero)
            throw new LedgerException($"Journal entry does not balance: debits and credits differ by {sum}.");
        if (postings.GroupBy(p => p.AccountId).Any(g => g.Count() > 1))
            throw new LedgerException("Each account appears once per journal entry.");
        return postings.ToList();
    }

    /// <summary>The usual entry with two lines: debit one account, credit another, same amount.</summary>
    public static IReadOnlyList<Posting> DebitCredit(Guid debit, Guid credit, Money amount)
    {
        if (!amount.IsPositive) throw new LedgerException("Amount must be positive.");
        if (debit == credit) throw new LedgerException("Debit and credit account are the same.");
        return Validate([new Posting(debit, amount), new Posting(credit, -amount)]);
    }

    /// <summary>How a posting changes an account's balance in its natural direction (assets: debit +, liabilities and equity: credit +).</summary>
    public static Money BalanceEffect(AccountSide side, Money posting) => side == AccountSide.Asset ? posting : -posting;
}

public sealed class LedgerException(string message) : Exception(message);
