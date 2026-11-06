using Kontor.Api.Data;
using Kontor.Domain.Banking;
using Kontor.Domain.Ledger;
using Kontor.Domain.Sanctions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kontor.Api.Services;

public sealed record PostingLine(long EntryId, DateTimeOffset BookedAt, DateOnly ValueDate, string Kind, string Description, string? Reference, decimal Amount, decimal BalanceAfter);

public sealed record AccountStatementDto(AccountDto Account, IReadOnlyList<PostingLine> Lines);

public sealed record SideTotal(string Side, decimal Total, IReadOnlyList<AccountDto> Accounts);

public sealed record BalanceSheetDto(
    IReadOnlyList<SideTotal> Sides, decimal Assets, decimal LiabilitiesAndEquity, bool Balanced,
    IReadOnlyList<string> Problems, long JournalEntries);

public sealed class AccountService(KontorDb db, UnitOfWork uow, AuditTrail audit, TimeProvider time, SanctionsList sanctions, IOptions<KontorOptions> options)
{
    /// <summary>
    /// Opens a customer account. The holder is screened against the sanctions list first (KYC): a clear hit is refused,
    /// a possible match opens the account frozen until Compliance has looked at it.
    /// </summary>
    public async Task<AccountDto> OpenAsync(OpenAccountRequest request, string user, CancellationToken ct)
    {
        var holder = (request.Holder ?? "").Trim();
        if (holder.Length is 0 or > SepaText.MaxNameLength || SepaText.Clean(holder, SepaText.MaxNameLength).Length == 0)
            throw ApiProblem.Invalid("Holder name is required (at most 70 characters).", new() { ["field"] = "holder" });
        var overdraft = request.OverdraftLimit ?? 0;
        if (overdraft is < 0 or > 10_000 || !Money.TryFromEuros(overdraft, out var overdraftLimit))
            throw ApiProblem.Invalid("Overdraft limit must be between 0 and 10,000 EUR.", new() { ["field"] = "overdraftLimit" });

        var hit = sanctions.Screen(holder);
        string? refused = null;
        var result = await uow.RunAsync(async () =>
        {
            if (hit is { Score: >= SanctionsList.HitThreshold })
            {
                await audit.AppendAsync(user, "aml.sanctions_match", "account-opening", new { holder, matched = hit.MatchedName, score = hit.Score, list = hit.Party.Id, action = "refused" }, ct);
                refused = $"\"{holder}\" matches the sanctions list entry \"{hit.MatchedName}\" ({hit.Party.Id}).";
                return null;
            }

            var count = await db.Accounts.CountAsync(a => a.Kind == AccountKind.Customer, ct);
            var now = time.GetUtcNow();
            var account = new Account
            {
                Id = Guid.CreateVersion7(now),
                Kind = AccountKind.Customer,
                Side = AccountSide.Liability,
                Iban = Iban.CreateGerman(options.Value.BankCode, (1_000_000_001L + count).ToString(System.Globalization.CultureInfo.InvariantCulture)).Value,
                Holder = holder,
                Status = hit is null ? AccountStatus.Active : AccountStatus.Frozen,
                OverdraftLimitCents = overdraftLimit.Cents,
                CreatedAt = now,
            };
            db.Accounts.Add(account);
            await audit.AppendAsync(user, "account.opened", account.Id.ToString(), new
            {
                iban = Iban.Parse(account.Iban).Masked, status = account.Status.ToString(),
                possibleSanctionsMatch = hit is null ? null : new { matched = hit.MatchedName, score = hit.Score },
            }, ct);
            return AccountDto.From(account);
        }, ct);
        return result ?? throw new ApiProblem(422, "kyc-sanctions-hit", "Account cannot be opened: the holder is on the sanctions list", refused);
    }

    public Task<AccountDto> SetFrozenAsync(Guid id, bool frozen, string? reason, string user, CancellationToken ct) =>
        uow.RunAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(reason)) throw ApiProblem.Invalid("A reason is required.");
            var a = await db.Accounts.SingleOrDefaultAsync(x => x.Id == id && x.Kind == AccountKind.Customer, ct) ?? throw ApiProblem.NotFound("Account");
            if (a.Status == AccountStatus.Closed) throw ApiProblem.Conflict("invalid-state", "Account is closed");
            a.Status = frozen ? AccountStatus.Frozen : AccountStatus.Active;
            a.Version++;
            await audit.AppendAsync(user, frozen ? "account.frozen" : "account.unfrozen", a.Id.ToString(), new { reason = reason.Trim() }, ct);
            return AccountDto.From(a);
        }, ct);

    public async Task<AccountStatementDto> StatementAsync(Guid id, CancellationToken ct)
    {
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct) ?? throw ApiProblem.NotFound("Account");
        var rows = await (from p in db.Postings.AsNoTracking()
                          join e in db.JournalEntries.AsNoTracking() on p.JournalEntryId equals e.Id
                          where p.AccountId == id
                          orderby e.Id
                          select new { e.Id, e.BookedAt, e.ValueDate, e.Kind, e.Description, e.Reference, p.AmountCents }).ToListAsync(ct);
        long running = 0;
        var lines = rows.Select(r =>
        {
            var effect = JournalRules.BalanceEffect(account.Side, new Money(r.AmountCents)).Cents;
            running += effect;
            return new PostingLine(r.Id, r.BookedAt, r.ValueDate, r.Kind.ToString(), r.Description, r.Reference, effect / 100m, running / 100m);
        }).ToList();
        return new AccountStatementDto(AccountDto.From(account), lines);
    }

    /// <summary>
    /// The books check themselves: assets must equal liabilities plus equity, every journal entry must balance, and every
    /// cached account balance must equal the sum of its postings. Any difference is listed as a problem.
    /// </summary>
    public async Task<BalanceSheetDto> BalanceSheetAsync(CancellationToken ct)
    {
        var accounts = await db.Accounts.AsNoTracking().OrderBy(a => a.Kind).ThenBy(a => a.Holder).ToListAsync(ct);
        var sums = await db.Postings.AsNoTracking().GroupBy(p => p.AccountId)
            .Select(g => new { AccountId = g.Key, Sum = g.Sum(p => p.AmountCents) }).ToDictionaryAsync(x => x.AccountId, x => x.Sum, ct);
        var unbalanced = await db.Postings.AsNoTracking().GroupBy(p => p.JournalEntryId)
            .Where(g => g.Sum(p => p.AmountCents) != 0).Select(g => g.Key).ToListAsync(ct);

        var problems = new List<string>();
        foreach (var a in accounts)
        {
            var expected = JournalRules.BalanceEffect(a.Side, new Money(sums.GetValueOrDefault(a.Id))).Cents;
            if (expected != a.BalanceCents)
                problems.Add($"{a.Code ?? a.Iban}: stored balance {a.BalanceCents / 100m:0.00} differs from its postings {expected / 100m:0.00}");
        }
        problems.AddRange(unbalanced.Select(id => $"Journal entry {id} does not balance"));

        var sides = accounts.GroupBy(a => a.Side)
            .Select(g => new SideTotal(g.Key.ToString(), g.Sum(a => a.BalanceCents) / 100m, g.Select(AccountDto.From).ToList())).ToList();
        var assets = accounts.Where(a => a.Side == AccountSide.Asset).Sum(a => a.BalanceCents) / 100m;
        var other = accounts.Where(a => a.Side != AccountSide.Asset).Sum(a => a.BalanceCents) / 100m;
        if (assets != other) problems.Add($"Assets {assets:0.00} do not equal liabilities and equity {other:0.00}");

        return new BalanceSheetDto(sides, assets, other, problems.Count == 0, problems, await db.JournalEntries.CountAsync(ct));
    }
}
