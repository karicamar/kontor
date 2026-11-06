using System.Security.Cryptography;
using Kontor.Api.Data;
using Kontor.Domain.Banking;
using Kontor.Domain.Ledger;
using Kontor.Domain.Sanctions;
using Kontor.Domain.Sepa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kontor.Api.Services;

/// <summary>
/// The connection to the partner bank: approved transfers go out as a pain.001 file, the bank's camt.053 statement comes
/// back and is matched against what was sent (settled, returned) and what came in (credits for customers).
/// Anything that cannot be matched automatically is booked to the suspense account and listed as an open item.
/// </summary>
public sealed class SepaService(
    KontorDb db, UnitOfWork uow, LedgerService ledger, AuditTrail audit, TimeProvider time,
    SanctionsList sanctions, IOptions<KontorOptions> options)
{
    private KontorOptions Options => options.Value;

    public Task<BatchDto> CreateBatchAsync(DateOnly? upTo, string user, CancellationToken ct) =>
        uow.RunAsync(async () =>
        {
            var now = time.GetUtcNow();
            var executionDate = BusinessCalendar.OnOrAfter(upTo ?? DateOnly.FromDateTime(now.UtcDateTime));
            // Transfers from an account frozen after approval wait (still reserved) until Compliance unfreezes it
            // or someone rejects them; they must not block everybody else's file
            var due = await db.Payments
                .Where(p => p.Status == PaymentStatus.Approved && p.CreditorAccountId == null && p.ExecutionDate <= executionDate
                            && db.Accounts.Any(a => a.Id == p.DebtorAccountId && a.Status == AccountStatus.Active))
                .OrderBy(p => p.CreatedAt)
                .ToListAsync(ct);
            if (due.Count == 0)
                throw new ApiProblem(422, "nothing-to-submit", "No approved transfers are due");

            var debtorIds = due.Select(p => p.DebtorAccountId).Distinct().ToList();
            var holders = await db.Accounts.Where(a => debtorIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Holder, ct);
            Bic? nostroBic = null;
            if (Options.NostroBic is not null) Bic.TryParse(Options.NostroBic, out nostroBic);

            var messageId = $"KON{now.UtcDateTime:yyyyMMddHHmmss}{Convert.ToHexString(RandomNumberGenerator.GetBytes(3))}";
            var batch = new CreditTransferBatch(
                messageId, now, new SepaAccount(Options.PlatformName, Iban.Parse(Options.NostroIban), nostroBic), executionDate,
                due.Select(p => new CreditTransfer(
                    p.EndToEndId, new Money(p.AmountCents), p.CreditorName, Iban.Parse(p.CreditorIban),
                    p.CreditorBic is not null && Bic.TryParse(p.CreditorBic, out var b) ? b : null,
                    holders[p.DebtorAccountId], p.RemittanceInformation)).ToList());
            var xml = Pain001Writer.Write(batch);

            var entity = new SepaBatch
            {
                Id = Guid.CreateVersion7(now), MessageId = messageId, CreatedAt = now, CreatedBy = user, ExecutionDate = executionDate,
                TransactionCount = due.Count, TotalCents = due.Sum(p => p.AmountCents), Xml = xml,
            };
            db.Batches.Add(entity);

            var outgoing = await ledger.InternalAsync(InternalAccounts.SepaOutgoing, ct);
            foreach (var p in due)
            {
                // The customer is debited when the file leaves; the money sits in "in transit" until the statement confirms it
                await ledger.ReleaseHoldAsync(p.DebtorAccountId, p.AmountCents, ct);
                await ledger.PostAsync(JournalKind.SepaSubmitted, $"SEPA transfer to {p.CreditorName}",
                    JournalRules.DebitCredit(p.DebtorAccountId, outgoing.Id, new Money(p.AmountCents)), user, p.Id, p.EndToEndId, executionDate, ct);
                p.Status = PaymentStatus.Submitted;
                p.BatchId = entity.Id;
                p.Version++;
            }
            await audit.AppendAsync(user, "sepa.file_created", entity.Id.ToString(),
                new { messageId, transactions = due.Count, total = entity.TotalCents / 100m, executionDate }, ct);
            return BatchDto.From(entity);
        }, ct);

    public async Task<StatementImportResult> ImportStatementAsync(Stream xml, string user, CancellationToken ct)
    {
        var statements = Camt053Reader.Read(xml);
        if (statements.Count != 1)
            throw new ApiProblem(422, "one-statement", $"Expected one statement per file, found {statements.Count}");
        var s = statements[0];

        var nostroIban = Iban.Parse(Options.NostroIban);
        if (s.AccountIban != nostroIban.Value)
            throw new ApiProblem(422, "wrong-account", "Statement is for another account", $"Expected {nostroIban.Formatted}.");
        if (!s.BalancesAgree)
            throw new ApiProblem(422, "statement-inconsistent", "Opening balance plus entries does not equal the closing balance",
                "The file is incomplete or corrupt; ask the bank to send it again.");

        return await uow.RunAsync(async () =>
        {
            if (await db.StatementImports.AnyAsync(i => i.StatementId == s.StatementId, ct))
                throw ApiProblem.Conflict("already-imported", $"Statement {s.StatementId} was already imported");

            var now = time.GetUtcNow();
            var import = new StatementImport { Id = Guid.CreateVersion7(now), MessageId = s.MessageId, StatementId = s.StatementId, ImportedAt = now, ImportedBy = user };
            db.StatementImports.Add(import);

            var nostro = await ledger.InternalAsync(InternalAccounts.Nostro, ct);
            var outgoing = await ledger.InternalAsync(InternalAccounts.SepaOutgoing, ct);
            var suspense = await ledger.InternalAsync(InternalAccounts.Suspense, ct);
            var lines = new List<StatementLineResult>();

            foreach (var entry in s.Entries)
            {
                foreach (var tx in entry.Transactions)
                {
                    var line = entry.IsReversal
                        ? await OpenItemAsync(import, entry, tx, "Reversal of an earlier entry: check manually", nostro, suspense, user, ct)
                        : entry.IsCredit
                            ? await CreditAsync(import, entry, tx, nostro, outgoing, suspense, user, ct)
                            : await DebitAsync(import, entry, tx, nostro, outgoing, suspense, user, ct);
                    lines.Add(line);
                }
            }

            import.Entries = lines.Count;
            import.OpenItems = lines.Count(l => l.Outcome.StartsWith("Open item", StringComparison.Ordinal));
            import.Matched = lines.Count - import.OpenItems;
            await audit.AppendAsync(user, "sepa.statement_imported", import.Id.ToString(),
                new { s.StatementId, entries = import.Entries, matched = import.Matched, openItems = import.OpenItems }, ct);

            // Reconciliation: after the import the ledger must show exactly what the bank shows
            var ledgerBalance = new Money(nostro.BalanceCents);
            var reconciled = s.ClosingBalance is null || s.ClosingBalance.Value == ledgerBalance;
            if (!reconciled)
                await audit.AppendAsync(user, "sepa.reconciliation_difference", import.Id.ToString(),
                    new { s.StatementId, ledger = ledgerBalance.Euros, bank = s.ClosingBalance!.Value.Euros }, ct);

            return new StatementImportResult(import.Id, s.StatementId, import.Entries, import.Matched, import.OpenItems,
                ledgerBalance.Euros, s.ClosingBalance?.Euros, reconciled, lines);
        }, ct);
    }

    private async Task<StatementLineResult> DebitAsync(StatementImport import, StatementEntry entry, StatementTransaction tx,
        Account nostro, Account outgoing, Account suspense, string user, CancellationToken ct)
    {
        var payment = tx.EndToEndId is null ? null
            : await db.Payments.SingleOrDefaultAsync(p => p.EndToEndId == tx.EndToEndId && p.Status == PaymentStatus.Submitted, ct);
        if (payment is null || payment.AmountCents != tx.Amount.Cents)
            return await OpenItemAsync(import, entry, tx,
                payment is null ? "Debit without a matching submitted transfer" : $"Amount differs from the transfer ({new Money(payment.AmountCents).ToGerman()})",
                nostro, suspense, user, ct);

        // Settled: the money really left the partner bank account
        await ledger.PostAsync(JournalKind.SepaSettled, $"Settled: {payment.CreditorName}",
            JournalRules.DebitCredit(outgoing.Id, nostro.Id, tx.Amount), user, payment.Id, entry.Reference, entry.BookingDate, ct);
        payment.Status = PaymentStatus.Booked;
        payment.BookedAt = time.GetUtcNow();
        payment.Version++;
        return new StatementLineResult(entry.Reference, "Debit", tx.Amount.Euros, "Transfer settled", tx.EndToEndId, payment.Id, null);
    }

    private async Task<StatementLineResult> CreditAsync(StatementImport import, StatementEntry entry, StatementTransaction tx,
        Account nostro, Account outgoing, Account suspense, string user, CancellationToken ct)
    {
        // A transfer we sent, coming back
        var returned = tx.EndToEndId is null ? null
            : await db.Payments.SingleOrDefaultAsync(p => p.EndToEndId == tx.EndToEndId && (p.Status == PaymentStatus.Booked || p.Status == PaymentStatus.Submitted), ct);
        if (returned is { CreditorAccountId: null })
        {
            if (returned.AmountCents != tx.Amount.Cents)
                return await OpenItemAsync(import, entry, tx, "Return amount differs from the original transfer", nostro, suspense, user, ct);
            // Not yet settled: it never left, take it back from "in transit". Settled: it came back into the bank account
            var from = returned.Status == PaymentStatus.Submitted ? outgoing : nostro;
            await ledger.PostAsync(JournalKind.SepaReturned, $"Returned: {ReturnReasons.Describe(tx.ReturnReason)}",
                JournalRules.DebitCredit(from.Id, returned.DebtorAccountId, tx.Amount), user, returned.Id, entry.Reference, entry.BookingDate, ct);
            returned.Status = PaymentStatus.Returned;
            returned.ReturnReason = ReturnReasons.Describe(tx.ReturnReason);
            returned.Version++;
            await audit.AppendAsync(user, "payment.returned", returned.Id.ToString(), new { returned.EndToEndId, reason = returned.ReturnReason }, ct);
            return new StatementLineResult(entry.Reference, "Credit", tx.Amount.Euros, $"Transfer returned ({tx.ReturnReason ?? "no reason"})", tx.EndToEndId, returned.Id, null);
        }

        // Incoming money for a customer: the IBAN the payer sent it to identifies the account
        if (tx.OwnIban is null)
            return await OpenItemAsync(import, entry, tx, "Incoming payment without the beneficiary IBAN", nostro, suspense, user, ct);
        var customer = await db.Accounts.SingleOrDefaultAsync(a => a.Iban == tx.OwnIban && a.Kind == AccountKind.Customer, ct);
        if (customer is null)
            return await OpenItemAsync(import, entry, tx, $"No customer account with IBAN {Iban.Parse(tx.OwnIban).Masked}", nostro, suspense, user, ct);
        if (customer.Status == AccountStatus.Closed)
            return await OpenItemAsync(import, entry, tx, "Customer account is closed: send the money back", nostro, suspense, user, ct);
        // Incoming payments are screened too: money from a sanctioned party must be frozen, not credited
        if (sanctions.Screen(tx.CounterpartyName) is { } hit)
        {
            await audit.AppendAsync(user, "aml.sanctions_match", customer.Id.ToString(),
                new { direction = "incoming", payer = tx.CounterpartyName, matched = hit.MatchedName, score = hit.Score, list = hit.Party.Id }, ct);
            return await OpenItemAsync(import, entry, tx, $"Payer resembles sanctions list entry \"{hit.MatchedName}\": held for Compliance", nostro, suspense, user, ct);
        }

        await ledger.PostAsync(JournalKind.SepaIncoming, $"Incoming from {tx.CounterpartyName ?? "unknown"}",
            JournalRules.DebitCredit(nostro.Id, customer.Id, tx.Amount), user, null, entry.Reference, entry.BookingDate, ct);
        return new StatementLineResult(entry.Reference, "Credit", tx.Amount.Euros, "Credited to customer", tx.EndToEndId, null, Iban.Parse(customer.Iban!).Formatted);
    }

    /// <summary>
    /// The bank booked it, so the ledger has to as well (otherwise the nostro would no longer match the bank),
    /// but to the suspense account, until a person decides where it belongs.
    /// </summary>
    private async Task<StatementLineResult> OpenItemAsync(StatementImport import, StatementEntry entry, StatementTransaction tx, string reason,
        Account nostro, Account suspense, string user, CancellationToken ct)
    {
        var postings = entry.IsCredit
            ? JournalRules.DebitCredit(nostro.Id, suspense.Id, tx.Amount)
            : JournalRules.DebitCredit(suspense.Id, nostro.Id, tx.Amount);
        await ledger.PostAsync(JournalKind.Unallocated, $"Unallocated: {reason}", postings, user, null, entry.Reference, entry.BookingDate, ct);
        db.OpenItems.Add(new OpenItem
        {
            StatementImportId = import.Id, Reference = entry.Reference, AmountCents = tx.Amount.Cents, IsCredit = entry.IsCredit,
            BookingDate = entry.BookingDate, Reason = reason, CounterpartyName = tx.CounterpartyName, CounterpartyIban = tx.CounterpartyIban,
            EndToEndId = tx.EndToEndId, RemittanceInformation = tx.RemittanceInformation,
        });
        return new StatementLineResult(entry.Reference, entry.IsCredit ? "Credit" : "Debit", tx.Amount.Euros, $"Open item: {reason}", tx.EndToEndId, null, null);
    }
}
