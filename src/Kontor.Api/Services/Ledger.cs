using Kontor.Api.Data;
using Kontor.Domain.Banking;
using Kontor.Domain.Ledger;
using Microsoft.EntityFrameworkCore;

namespace Kontor.Api.Services;

/// <summary>
/// Runs a piece of work as one database transaction: all bookings, status changes and audit records of a request are
/// committed together or not at all.
/// <list type="bullet">
/// <item>SQLite: the transaction takes the write lock up front (BEGIN IMMEDIATE), so writers simply wait for each other.</item>
/// <item>PostgreSQL (read committed): two requests can read the same account. The second UPDATE then finds a newer
/// <c>Version</c> and fails (lost update prevented), or the second insert of the same idempotency key or audit sequence
/// number hits the unique index. Both are expected under load: the work is rolled back and run again on fresh data.</item>
/// </list>
/// </summary>
public sealed class UnitOfWork(KontorDb db, AuditTrail audit, ILogger<UnitOfWork> log)
{
    private const int MaxAttempts = 12;

    public async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            audit.Reset();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                var result = await work();
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                audit.Flush();
                return result;
            }
            catch (DbUpdateException ex) when (attempt < MaxAttempts && IsRaceCondition(ex))
            {
                log.LogInformation("Concurrent update ({Reason}), retrying (attempt {Attempt})", ex.GetType().Name, attempt);
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                // Small random pause so the requests that collided do not collide again in lockstep
                await Task.Delay(Random.Shared.Next(5, 20 * attempt), ct);
            }
            catch
            {
                db.ChangeTracker.Clear();
                throw;
            }
        }
    }

    public Task RunAsync(Func<Task> work, CancellationToken ct = default) =>
        RunAsync(async () => { await work(); return true; }, ct);

    /// <summary>A lost update (concurrency token), a unique-key race (23505) or a serialization failure (40001).</summary>
    private static bool IsRaceCondition(DbUpdateException ex) =>
        ex is DbUpdateConcurrencyException
        || ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation or Npgsql.PostgresErrorCodes.SerializationFailure };
}

public static class InternalAccounts
{
    /// <summary>The platform's account at its partner bank (asset): where the money really is.</summary>
    public const string Nostro = "NOSTRO";
    /// <summary>Transfers sent in a SEPA file but not yet confirmed by the bank statement (liability).</summary>
    public const string SepaOutgoing = "SEPA_OUT";
    /// <summary>Incoming money that could not be assigned to a customer yet (liability: it belongs to someone).</summary>
    public const string Suspense = "SUSPENSE";

    public static readonly (string Code, string Name, AccountSide Side)[] All =
    [
        (Nostro, "Partner bank account (nostro)", AccountSide.Asset),
        (SepaOutgoing, "Outgoing SEPA transfers in transit", AccountSide.Liability),
        (Suspense, "Unallocated incoming payments (suspense)", AccountSide.Liability),
    ];
}

public sealed class LedgerService(KontorDb db, TimeProvider time)
{
    /// <summary>Loaded by code each time (unique index): a retried transaction never works on a stale copy.</summary>
    public async Task<Account> InternalAsync(string code, CancellationToken ct = default) =>
        await db.Accounts.SingleOrDefaultAsync(a => a.Code == code, ct)
        ?? throw new InvalidOperationException($"Internal account {code} is missing; was the database initialized?");

    /// <summary>
    /// Books a journal entry and updates the cached balances. A customer account can never end up with less available
    /// money than zero (balance + overdraft limit − reserved amounts): the database is the last line of defence against
    /// an overdraft, whatever the calling code checked before.
    /// </summary>
    public async Task<JournalEntry> PostAsync(
        JournalKind kind, string description, IReadOnlyList<Posting> postings, string actor,
        Guid? paymentId = null, string? reference = null, DateOnly? valueDate = null, CancellationToken ct = default)
    {
        JournalRules.Validate(postings);
        var ids = postings.Select(p => p.AccountId).ToList();
        var accounts = await db.Accounts.Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);

        foreach (var p in postings)
        {
            if (!accounts.TryGetValue(p.AccountId, out var account))
                throw new LedgerException($"Account {p.AccountId} does not exist.");
            if (account.Kind == AccountKind.Customer && account.Status != AccountStatus.Active && JournalRules.BalanceEffect(account.Side, p.Amount).Cents < 0)
                throw new LedgerException($"Account {account.Iban} is {account.Status.ToString().ToLowerInvariant()}.");

            account.BalanceCents += JournalRules.BalanceEffect(account.Side, p.Amount).Cents;
            account.Version++;
            if (account.Kind == AccountKind.Customer && account.AvailableCents < 0)
                throw new LedgerException($"Insufficient funds on {account.Iban}.");
        }

        var now = time.GetUtcNow();
        var entry = new JournalEntry
        {
            BookedAt = now,
            ValueDate = valueDate ?? DateOnly.FromDateTime(now.UtcDateTime),
            Kind = kind,
            Description = description,
            PaymentId = paymentId,
            Reference = reference,
            CreatedBy = actor,
            Postings = postings.Select(p => new PostingRow { AccountId = p.AccountId, AmountCents = p.Amount.Cents }).ToList(),
        };
        db.JournalEntries.Add(entry);
        return entry;
    }

    /// <summary>Gives back money reserved by a payment (it is booked now, or it will never be).</summary>
    public async Task ReleaseHoldAsync(Guid accountId, long cents, CancellationToken ct = default)
    {
        var account = await db.Accounts.SingleAsync(a => a.Id == accountId, ct);
        if (account.HeldCents < cents) throw new LedgerException($"Account {account.Iban} has less reserved than is being released.");
        account.HeldCents -= cents;
        account.Version++;
    }
}
