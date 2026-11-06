using System.Text.Json;
using Kontor.Api.Data;
using Kontor.Domain.Risk;

namespace Kontor.Api.Services;

public sealed record OpenAccountRequest(string Holder, decimal? OverdraftLimit = null);

public sealed record AccountDto(
    Guid Id, string? Iban, string Holder, string Kind, string? Code, string Side, string Status,
    decimal Balance, decimal Held, decimal Available, decimal OverdraftLimit)
{
    public static AccountDto From(Account a) => new(
        a.Id, a.Iban is null ? null : Kontor.Domain.Banking.Iban.Parse(a.Iban).Formatted, a.Holder, a.Kind.ToString(), a.Code,
        a.Side.ToString(), a.Status.ToString(), a.BalanceCents / 100m, a.HeldCents / 100m, a.AvailableCents / 100m, a.OverdraftLimitCents / 100m);
}

public sealed record CreatePaymentRequest(
    Guid DebtorAccountId,
    string CreditorName,
    string CreditorIban,
    decimal Amount,
    string? RemittanceInformation = null,
    DateOnly? RequestedExecutionDate = null,
    string? CreditorBic = null,
    /// <summary>The payer saw the Verification of Payee warning and wants to send anyway.</summary>
    bool ConfirmPayeeMismatch = false);

public sealed record DecisionRequest(string? Reason);

public sealed record PaymentDto(
    Guid Id, string EndToEndId, Guid DebtorAccountId, string CreditorName, string CreditorIban, decimal Amount,
    string? RemittanceInformation, DateOnly RequestedExecutionDate, DateOnly ExecutionDate, string Status, bool OnUs,
    string PayeeVerification, int RiskScore, IReadOnlyList<RiskFinding> RiskFindings, string CreatedBy, DateTimeOffset CreatedAt,
    string? ApprovedBy, string? ComplianceDecisionBy, string? ComplianceNote, string? RejectedBy, string? RejectReason,
    string? ReturnReason, Guid? BatchId)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string SerializeFindings(IReadOnlyList<RiskFinding> findings) => JsonSerializer.Serialize(findings, Json);

    public static PaymentDto From(Payment p) => new(
        p.Id, p.EndToEndId, p.DebtorAccountId, p.CreditorName, Kontor.Domain.Banking.Iban.Parse(p.CreditorIban).Formatted,
        p.AmountCents / 100m, p.RemittanceInformation, p.RequestedExecutionDate, p.ExecutionDate, p.Status.ToString(),
        p.CreditorAccountId is not null, p.PayeeVerification, p.RiskScore,
        JsonSerializer.Deserialize<List<RiskFinding>>(p.RiskFindingsJson, Json) ?? [],
        p.CreatedBy, p.CreatedAt, p.ApprovedBy, p.ComplianceDecisionBy, p.ComplianceNote, p.RejectedBy, p.RejectReason,
        p.ReturnReason, p.BatchId);
}

public sealed record BatchDto(Guid Id, string MessageId, DateOnly ExecutionDate, int Transactions, decimal Total, string CreatedBy, DateTimeOffset CreatedAt)
{
    public static BatchDto From(SepaBatch b) => new(b.Id, b.MessageId, b.ExecutionDate, b.TransactionCount, b.TotalCents / 100m, b.CreatedBy, b.CreatedAt);
}

public sealed record StatementLineResult(string? Reference, string Direction, decimal Amount, string Outcome, string? EndToEndId, Guid? PaymentId, string? Account);

public sealed record StatementImportResult(
    Guid ImportId, string StatementId, int Entries, int Matched, int OpenItems,
    decimal NostroBalanceInLedger, decimal? ClosingBalanceOnStatement, bool Reconciled, IReadOnlyList<StatementLineResult> Lines);
