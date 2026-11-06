using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kontor.Api.Data;
using Kontor.Domain.Banking;
using Kontor.Domain.Ledger;
using Kontor.Domain.Risk;
using Kontor.Domain.Sanctions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kontor.Api.Services;

public sealed record CreatePaymentOutcome(PaymentDto Payment, bool Replayed);

/// <summary>
/// The life of an outgoing payment: entry (with idempotency, validation, Verification of Payee, AML/fraud rules and a
/// reservation of the funds), the four-eyes approval, compliance decisions, and the internal booking of on-us transfers.
/// SEPA files and bank statements are in <see cref="SepaService"/>.
/// </summary>
public sealed class PaymentService(
    KontorDb db, UnitOfWork uow, LedgerService ledger, AuditTrail audit, TimeProvider time,
    SanctionsList sanctions, IOptions<KontorOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private RiskSettings Risk => options.Value.Risk;

    public async Task<CreatePaymentOutcome> CreateAsync(CreatePaymentRequest request, string? idempotencyKey, string user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 64 || !idempotencyKey.All(c => c is > ' ' and <= '~'))
            throw ApiProblem.Invalid("Send an Idempotency-Key header (1 to 64 visible ASCII characters, e.g. a UUID) so a retried request cannot create a second payment.");
        var requestHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, Json)));

        return await uow.RunAsync(async () =>
        {
            var existing = await db.Payments.SingleOrDefaultAsync(p => p.CreatedBy == user && p.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null)
            {
                if (existing.RequestHash != requestHash)
                    throw new ApiProblem(422, "idempotency-key-reused", "Idempotency-Key was already used for a different request");
                return new CreatePaymentOutcome(PaymentDto.From(existing), Replayed: true);
            }

            var payment = await NewPaymentAsync(request, idempotencyKey, requestHash, user, ct);
            return new CreatePaymentOutcome(PaymentDto.From(payment), Replayed: false);
        }, ct);
    }

    private async Task<Payment> NewPaymentAsync(CreatePaymentRequest r, string key, string requestHash, string user, CancellationToken ct)
    {
        // 1. The request itself
        if (!Money.TryFromEuros(r.Amount, out var amount) || !amount.IsPositive || amount > Money.SepaMaximum)
            throw ApiProblem.Invalid("Amount must be between 0.01 and 999,999,999.99 EUR with at most two decimals.");
        if (!Iban.TryParse(r.CreditorIban, out var creditorIban, out var ibanError))
            throw ApiProblem.Invalid(ibanError, new() { ["field"] = "creditorIban" });
        if (!creditorIban.IsSepa)
            throw ApiProblem.Invalid($"{creditorIban.CountryCode} is not part of the SEPA area; this platform sends SEPA transfers only.");
        Bic? bic = null;
        if (!string.IsNullOrWhiteSpace(r.CreditorBic) && !Bic.TryParse(r.CreditorBic, out bic))
            throw ApiProblem.Invalid("BIC must have 8 or 11 characters (e.g. COBADEFFXXX).", new() { ["field"] = "creditorBic" });
        var creditorName = (r.CreditorName ?? "").Trim();
        if (SepaText.Clean(creditorName, SepaText.MaxNameLength).Length == 0 || creditorName.Length > SepaText.MaxNameLength)
            throw ApiProblem.Invalid("Creditor name is required (at most 70 characters).", new() { ["field"] = "creditorName" });
        if ((r.RemittanceInformation?.Length ?? 0) > SepaText.MaxRemittanceLength)
            throw ApiProblem.Invalid("Remittance information has at most 140 characters.", new() { ["field"] = "remittanceInformation" });

        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var requested = r.RequestedExecutionDate ?? today;
        if (requested < today) throw ApiProblem.Invalid("Execution date is in the past.");
        if (requested > today.AddDays(365)) throw ApiProblem.Invalid("Execution date is more than a year ahead.");

        // 2. The accounts
        var debtor = await db.Accounts.SingleOrDefaultAsync(a => a.Id == r.DebtorAccountId && a.Kind == AccountKind.Customer, ct)
            ?? throw ApiProblem.NotFound("Debtor account");
        if (debtor.Status != AccountStatus.Active)
            throw new ApiProblem(422, "account-not-active", $"Debtor account is {debtor.Status.ToString().ToLowerInvariant()}");
        if (debtor.Iban == creditorIban.Value)
            throw ApiProblem.Invalid("Debtor and creditor account are the same.");

        var creditorAccount = await db.Accounts.SingleOrDefaultAsync(a => a.Iban == creditorIban.Value && a.Kind == AccountKind.Customer, ct);
        if (creditorAccount is { Status: AccountStatus.Closed })
            throw new ApiProblem(422, "creditor-account-closed", "The creditor account is closed");

        // 3. Verification of Payee: only possible here for accounts held in Kontor
        var findings = new List<RiskFinding>();
        var vop = creditorAccount is null
            ? new PayeeVerificationResult(PayeeMatch.NotAvailable, null, 0)
            : PayeeVerificationResult.Check(creditorName, creditorAccount.Holder);
        if (vop.Result is PayeeMatch.CloseMatch or PayeeMatch.NoMatch)
        {
            if (!r.ConfirmPayeeMismatch)
                throw new ApiProblem(422, "payee-name-mismatch",
                    vop.Result == PayeeMatch.CloseMatch ? $"The account holder is \"{vop.ActualName}\"" : "The name does not match the account holder",
                    "Check the payee. To send anyway, repeat the request with confirmPayeeMismatch = true.",
                    new() { ["verificationOfPayee"] = vop.Result.ToString(), ["suggestedName"] = vop.ActualName });
            findings.Add(vop.Result == PayeeMatch.NoMatch
                // Paying a name that does not belong to the IBAN is the classic pattern of authorised push payment fraud
                ? new RiskFinding("KON-VOP-001", "Sent despite a payee name mismatch", "Verification of Payee: no match, confirmed by the payer", RiskAction.Review, 40)
                : new RiskFinding("KON-VOP-002", "Sent despite a close payee name match", $"Account holder is \"{vop.ActualName}\"", RiskAction.Allow, 10));
        }

        // 4. AML and fraud rules, against the debtor's history
        var since = now.AddDays(-90);
        var history = (await db.Payments
                .Where(p => p.DebtorAccountId == debtor.Id && p.Status != PaymentStatus.Rejected && p.CreatedAt >= since)
                .Select(p => new { p.CreditorIban, p.AmountCents, p.CreatedAt })
                .ToListAsync(ct))
            .Select(p => new PastPayment(p.CreditorIban, new Money(p.AmountCents), p.CreatedAt)).ToList();
        var known = (await db.Payments
                .Where(p => p.DebtorAccountId == debtor.Id && (p.Status == PaymentStatus.Booked || p.Status == PaymentStatus.Submitted))
                .Select(p => p.CreditorIban).Distinct().ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var context = new RiskContext(new PaymentCandidate(debtor.Id, creditorName, creditorIban, amount, now), history, known, Risk, sanctions);
        findings.AddRange(RiskEngine.Default.Assess(context).Findings);
        var assessment = new RiskAssessment(findings.OrderByDescending(f => f.Action).ThenByDescending(f => f.Points).ToList());

        var status = assessment.Action switch
        {
            RiskAction.Block => PaymentStatus.Blocked,
            RiskAction.Review => PaymentStatus.PendingApproval,
            _ when amount >= Risk.ApprovalLimit => PaymentStatus.PendingApproval,
            _ => PaymentStatus.Approved,
        };

        // 5. Reserve the money now, so two payments cannot both spend the same balance
        if (debtor.AvailableCents < amount.Cents)
            throw new ApiProblem(422, "insufficient-funds", "Insufficient funds",
                $"Available: {new Money(debtor.AvailableCents).ToGerman()}, requested: {amount.ToGerman()}.");
        debtor.HeldCents += amount.Cents;
        debtor.Version++;

        var payment = new Payment
        {
            Id = Guid.CreateVersion7(now),
            EndToEndId = $"KON-{today:yyyyMMdd}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(6))}",
            DebtorAccountId = debtor.Id,
            CreditorName = creditorName,
            CreditorIban = creditorIban.Value,
            CreditorBic = bic?.Value,
            CreditorAccountId = creditorAccount?.Id,
            AmountCents = amount.Cents,
            RemittanceInformation = string.IsNullOrWhiteSpace(r.RemittanceInformation) ? null : r.RemittanceInformation.Trim(),
            RequestedExecutionDate = requested,
            ExecutionDate = BusinessCalendar.OnOrAfter(requested),
            Status = status,
            PayeeVerification = vop.Result.ToString(),
            RiskScore = assessment.Score,
            RiskFindingsJson = PaymentDto.SerializeFindings(assessment.Findings),
            CreatedBy = user,
            CreatedAt = now,
            IdempotencyKey = key,
            RequestHash = requestHash,
        };
        db.Payments.Add(payment);

        await audit.AppendAsync(user, "payment.created", payment.Id.ToString(), new
        {
            payment.EndToEndId, amount = amount.Euros, creditorIban = creditorIban.Masked, status = status.ToString(),
            onUs = creditorAccount is not null, verificationOfPayee = vop.Result.ToString(),
            riskScore = assessment.Score, rules = assessment.Findings.Select(f => f.RuleId).ToArray(),
        }, ct);
        if (assessment.Findings.FirstOrDefault(f => f.RuleId == "KON-AML-001") is { } hit)
            await audit.AppendAsync(user, "aml.sanctions_match", payment.Id.ToString(), new { hit.Title, hit.Detail, action = hit.Action.ToString() }, ct);

        if (status == PaymentStatus.Approved && creditorAccount is not null)
            await BookOnUsAsync(payment, user, ct);
        return payment;
    }

    public async Task<PaymentDto> ApproveAsync(Guid id, string user, CancellationToken ct)
    {
        string? denied = null;
        var dto = await uow.RunAsync(async () =>
        {
            var p = await LoadAsync(id, ct);
            if (p.Status != PaymentStatus.PendingApproval)
                throw ApiProblem.Conflict("invalid-state", $"Payment is {p.Status}, only payments pending approval can be approved");

            // Four-eyes principle: the second person must really be a second person
            denied = p.CreatedBy == user ? "You entered this payment yourself."
                : p.ComplianceDecisionBy == user ? "You released this payment as compliance officer."
                : null;
            if (denied is not null)
            {
                // Recorded and committed although the request fails: an attempt to approve one's own payment is a security event
                await audit.AppendAsync(user, "payment.self_approval_denied", p.Id.ToString(), new { p.EndToEndId, amount = p.AmountCents / 100m, reason = denied }, ct);
                return null;
            }

            p.Status = PaymentStatus.Approved;
            p.ApprovedBy = user;
            p.ApprovedAt = time.GetUtcNow();
            p.Version++;
            await audit.AppendAsync(user, "payment.approved", p.Id.ToString(), new { p.EndToEndId, amount = p.AmountCents / 100m, createdBy = p.CreatedBy }, ct);
            if (p.CreditorAccountId is not null)
                await BookOnUsAsync(p, user, ct);
            return PaymentDto.From(p);
        }, ct);

        return dto ?? throw ApiProblem.Forbidden("four-eyes", "Four-eyes principle: a second person has to approve this payment", denied);
    }

    public Task<PaymentDto> RejectAsync(Guid id, string? reason, string user, bool isCompliance, CancellationToken ct) =>
        uow.RunAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(reason)) throw ApiProblem.Invalid("A reason is required.");
            var p = await LoadAsync(id, ct);
            if (p.Status == PaymentStatus.Blocked && !isCompliance)
                throw ApiProblem.Forbidden("compliance-only", "Only Compliance can decide on a blocked payment");
            if (p.Status is not (PaymentStatus.PendingApproval or PaymentStatus.Blocked or PaymentStatus.Approved))
                throw ApiProblem.Conflict("invalid-state", $"Payment is {p.Status} and can no longer be rejected");

            await ReleaseHoldAsync(p, ct);
            var wasBlocked = p.Status == PaymentStatus.Blocked;
            p.Status = PaymentStatus.Rejected;
            p.RejectedBy = user;
            p.RejectReason = reason.Trim();
            if (wasBlocked) p.ComplianceDecisionBy = user;
            p.Version++;
            await audit.AppendAsync(user, "payment.rejected", p.Id.ToString(), new { p.EndToEndId, reason = p.RejectReason, byCompliance = wasBlocked }, ct);
            return PaymentDto.From(p);
        }, ct);

    /// <summary>Compliance cleared an AML block (e.g. a false positive name match). The payment still needs a second approver.</summary>
    public Task<PaymentDto> ReleaseAsync(Guid id, string? note, string user, CancellationToken ct) =>
        uow.RunAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(note)) throw ApiProblem.Invalid("Document why the block is released.");
            var p = await LoadAsync(id, ct);
            if (p.Status != PaymentStatus.Blocked)
                throw ApiProblem.Conflict("invalid-state", $"Payment is {p.Status}, only blocked payments can be released");
            if (p.CreatedBy == user)
                throw ApiProblem.Forbidden("four-eyes", "You cannot release a payment you entered yourself");

            p.Status = PaymentStatus.PendingApproval;
            p.ComplianceDecisionBy = user;
            p.ComplianceNote = note.Trim();
            p.Version++;
            await audit.AppendAsync(user, "payment.released_by_compliance", p.Id.ToString(), new { p.EndToEndId, note = p.ComplianceNote }, ct);
            return PaymentDto.From(p);
        }, ct);

    /// <summary>Both accounts are in Kontor: no SEPA file, the money moves in the ledger right away.</summary>
    private async Task BookOnUsAsync(Payment p, string user, CancellationToken ct)
    {
        await ReleaseHoldAsync(p, ct);
        // Customer accounts are liabilities: the debtor's goes down (debit), the creditor's goes up (credit)
        await ledger.PostAsync(JournalKind.OnUsTransfer, $"Transfer to {p.CreditorName}",
            JournalRules.DebitCredit(p.DebtorAccountId, p.CreditorAccountId!.Value, new Money(p.AmountCents)), user, p.Id, p.EndToEndId, ct: ct);
        p.Status = PaymentStatus.Booked;
        p.BookedAt = time.GetUtcNow();
        p.Version++;
        await audit.AppendAsync(user, "payment.booked", p.Id.ToString(), new { p.EndToEndId, onUs = true }, ct);
    }

    private Task ReleaseHoldAsync(Payment p, CancellationToken ct) => ledger.ReleaseHoldAsync(p.DebtorAccountId, p.AmountCents, ct);

    private async Task<Payment> LoadAsync(Guid id, CancellationToken ct) =>
        await db.Payments.SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw ApiProblem.NotFound("Payment");
}
