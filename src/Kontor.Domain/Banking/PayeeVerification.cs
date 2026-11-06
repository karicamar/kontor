namespace Kontor.Domain.Banking;

/// <summary>
/// Verification of Payee (EU Instant Payments Regulation 2024/886, mandatory for euro transfers since 9 October 2025):
/// before a transfer is sent, the payer is told whether the name they typed belongs to the IBAN.
/// </summary>
public enum PayeeMatch
{
    /// <summary>The receiving bank is not checked by Kontor (only accounts held in Kontor can be verified here).</summary>
    NotAvailable,
    Match,
    /// <summary>Almost the same; the payer is shown the real name ("Did you mean Anna Schmidt?").</summary>
    CloseMatch,
    NoMatch,
}

public sealed record PayeeVerificationResult(PayeeMatch Result, string? ActualName, double Similarity)
{
    /// <summary>High on purpose: a close match reveals the holder name, so it must not fire for a different person ("Anna Schneider" vs "Anna Schmidt" = 0.915).</summary>
    public const double CloseMatchThreshold = 0.93;

    public static PayeeVerificationResult Check(string enteredName, string accountHolder)
    {
        var score = NameMatcher.Similarity(enteredName, accountHolder);
        return score switch
        {
            1 => new(PayeeMatch.Match, null, score),
            >= CloseMatchThreshold => new(PayeeMatch.CloseMatch, accountHolder, score),
            // On a "no match" the holder's name is not revealed: that would let anyone look up who owns an IBAN
            _ => new(PayeeMatch.NoMatch, null, score),
        };
    }
}
