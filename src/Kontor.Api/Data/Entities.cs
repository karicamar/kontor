namespace Kontor.Api.Data;

public enum AccountKind { Customer, Internal }

public enum AccountStatus { Active, Frozen, Closed }

/// <summary>
/// A ledger account. Amounts are whole cents. <see cref="BalanceCents"/> is the cached sum of the account's postings in its
/// natural direction (a customer account with 100 € on it shows +100, although in the books it is a credit balance).
/// </summary>
public class Account
{
    public Guid Id { get; set; }
    public AccountKind Kind { get; set; }
    public Kontor.Domain.Ledger.AccountSide Side { get; set; }
    /// <summary>For internal accounts: a fixed code (NOSTRO, SEPA_OUT, ...). Null for customers.</summary>
    public string? Code { get; set; }
    public string? Iban { get; set; }
    public required string Holder { get; set; }
    public AccountStatus Status { get; set; }
    public long BalanceCents { get; set; }
    /// <summary>Reserved by payments that are not booked yet (pending approval, approved, blocked).</summary>
    public long HeldCents { get; set; }
    public long OverdraftLimitCents { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Optimistic concurrency token: two requests that change the same account cannot both win.</summary>
    public long Version { get; set; }

    public long AvailableCents => BalanceCents + OverdraftLimitCents - HeldCents;
}

public enum JournalKind { Opening, OnUsTransfer, SepaSubmitted, SepaSettled, SepaIncoming, SepaReturned, Unallocated, Correction }

public class JournalEntry
{
    public long Id { get; set; }
    public DateTimeOffset BookedAt { get; set; }
    public DateOnly ValueDate { get; set; }
    public JournalKind Kind { get; set; }
    public required string Description { get; set; }
    public Guid? PaymentId { get; set; }
    public string? Reference { get; set; }
    public string? CreatedBy { get; set; }
    public List<PostingRow> Postings { get; set; } = [];
}

public class PostingRow
{
    public long Id { get; set; }
    public long JournalEntryId { get; set; }
    public Guid AccountId { get; set; }
    /// <summary>Signed: the postings of one entry sum to zero.</summary>
    public long AmountCents { get; set; }
}

public enum PaymentStatus
{
    /// <summary>Waiting for a second person (four-eyes principle).</summary>
    PendingApproval,
    /// <summary>Stopped by an AML rule; Compliance decides.</summary>
    Blocked,
    /// <summary>Ready to go out in the next SEPA file.</summary>
    Approved,
    /// <summary>In a pain.001 file sent to the partner bank; debited from the customer.</summary>
    Submitted,
    /// <summary>Final: confirmed by the bank statement, or booked internally for an on-us transfer.</summary>
    Booked,
    Rejected,
    /// <summary>The receiving bank sent it back; the customer got the money back.</summary>
    Returned,
}

public class Payment
{
    public Guid Id { get; set; }
    public required string EndToEndId { get; set; }
    public Guid DebtorAccountId { get; set; }
    public required string CreditorName { get; set; }
    public required string CreditorIban { get; set; }
    public string? CreditorBic { get; set; }
    /// <summary>Set when the creditor IBAN is another Kontor account (booked internally, no SEPA file).</summary>
    public Guid? CreditorAccountId { get; set; }
    public long AmountCents { get; set; }
    public string? RemittanceInformation { get; set; }
    public DateOnly RequestedExecutionDate { get; set; }
    public DateOnly ExecutionDate { get; set; }
    public PaymentStatus Status { get; set; }
    public string PayeeVerification { get; set; } = "NotAvailable";
    public int RiskScore { get; set; }
    public string RiskFindingsJson { get; set; } = "[]";
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string RequestHash { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? ComplianceDecisionBy { get; set; }
    public string? ComplianceNote { get; set; }
    public string? RejectedBy { get; set; }
    public string? RejectReason { get; set; }
    public Guid? BatchId { get; set; }
    public DateTimeOffset? BookedAt { get; set; }
    public string? ReturnReason { get; set; }
    public long Version { get; set; }
}

/// <summary>One pain.001 file sent to the partner bank.</summary>
public class SepaBatch
{
    public Guid Id { get; set; }
    public required string MessageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string CreatedBy { get; set; }
    public DateOnly ExecutionDate { get; set; }
    public int TransactionCount { get; set; }
    public long TotalCents { get; set; }
    public required string Xml { get; set; }
}

/// <summary>An imported camt.053 statement (the statement id is unique, so the same file cannot be booked twice).</summary>
public class StatementImport
{
    public Guid Id { get; set; }
    public required string MessageId { get; set; }
    public required string StatementId { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
    public required string ImportedBy { get; set; }
    public int Entries { get; set; }
    public int Matched { get; set; }
    public int OpenItems { get; set; }
}

/// <summary>A statement line that could not be matched automatically and needs a person (reconciliation).</summary>
public class OpenItem
{
    public long Id { get; set; }
    public Guid StatementImportId { get; set; }
    public string? Reference { get; set; }
    public long AmountCents { get; set; }
    public bool IsCredit { get; set; }
    public DateOnly BookingDate { get; set; }
    public required string Reason { get; set; }
    public string? CounterpartyName { get; set; }
    public string? CounterpartyIban { get; set; }
    public string? EndToEndId { get; set; }
    public string? RemittanceInformation { get; set; }
    public bool Resolved { get; set; }
}

public class AuditRecord
{
    public long Sequence { get; set; }
    public DateTimeOffset At { get; set; }
    public required string Actor { get; set; }
    public required string Action { get; set; }
    public required string Subject { get; set; }
    public required string Details { get; set; }
    public required string PreviousHash { get; set; }
    public required string Hash { get; set; }
}
