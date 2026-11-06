using System.Security.Claims;
using Kontor.Api.Data;
using Kontor.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kontor.Api.Endpoints;

public static class KontorEndpoints
{
    public const string MaxStatementBytesKey = "Kontor:MaxStatementBytes";

    public static void MapKontor(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/me", (ClaimsPrincipal user) => new { user = user.Id(), roles = user.FindAll("role").Select(c => c.Value) });

        // Accounts
        var accounts = api.MapGroup("/accounts").WithTags("Accounts");
        accounts.MapGet("/", async (KontorDb db, string? kind, CancellationToken ct) =>
            (await db.Accounts.AsNoTracking()
                .Where(a => kind == null || a.Kind == (kind == "internal" ? AccountKind.Internal : AccountKind.Customer))
                .OrderBy(a => a.Kind).ThenBy(a => a.Holder).ToListAsync(ct)).Select(AccountDto.From));
        accounts.MapGet("/{id:guid}", async (Guid id, KontorDb db, CancellationToken ct) =>
            await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct) is { } a ? Results.Ok(AccountDto.From(a)) : throw ApiProblem.NotFound("Account"));
        accounts.MapGet("/{id:guid}/statement", (Guid id, AccountService s, CancellationToken ct) => s.StatementAsync(id, ct));
        accounts.MapPost("/", async (OpenAccountRequest r, AccountService s, ClaimsPrincipal u, CancellationToken ct) =>
        {
            var a = await s.OpenAsync(r, u.Id(), ct);
            return Results.Created($"/api/accounts/{a.Id}", a);
        }).RequireAuthorization(Roles.Clerk);
        accounts.MapPost("/{id:guid}/freeze", (Guid id, DecisionRequest r, AccountService s, ClaimsPrincipal u, CancellationToken ct) =>
            s.SetFrozenAsync(id, true, r.Reason, u.Id(), ct)).RequireAuthorization(Roles.Compliance);
        accounts.MapPost("/{id:guid}/unfreeze", (Guid id, DecisionRequest r, AccountService s, ClaimsPrincipal u, CancellationToken ct) =>
            s.SetFrozenAsync(id, false, r.Reason, u.Id(), ct)).RequireAuthorization(Roles.Compliance);

        // Payments
        var payments = api.MapGroup("/payments").WithTags("Payments");
        payments.MapGet("/", async (KontorDb db, string? status, int? limit, CancellationToken ct) =>
        {
            PaymentStatus? filter = status is null ? null
                : Enum.TryParse<PaymentStatus>(status, ignoreCase: true, out var s) ? s : throw ApiProblem.Invalid($"Unknown status \"{status}\".");
            var list = await db.Payments.AsNoTracking()
                .Where(p => filter == null || p.Status == filter)
                .OrderByDescending(p => p.CreatedAt).Take(Math.Clamp(limit ?? 100, 1, 500)).ToListAsync(ct);
            return list.Select(PaymentDto.From);
        });
        payments.MapGet("/{id:guid}", async (Guid id, KontorDb db, CancellationToken ct) =>
            await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct) is { } p ? Results.Ok(PaymentDto.From(p)) : throw ApiProblem.NotFound("Payment"));
        payments.MapPost("/", async (CreatePaymentRequest r, [FromHeader(Name = "Idempotency-Key")] string? key, PaymentService s, ClaimsPrincipal u, HttpContext http, CancellationToken ct) =>
        {
            var outcome = await s.CreateAsync(r, key, u.Id(), ct);
            if (outcome.Replayed)
            {
                http.Response.Headers["Idempotent-Replayed"] = "true";
                return Results.Ok(outcome.Payment);
            }
            return Results.Created($"/api/payments/{outcome.Payment.Id}", outcome.Payment);
        }).RequireAuthorization(Roles.Clerk);
        payments.MapPost("/{id:guid}/approve", (Guid id, PaymentService s, ClaimsPrincipal u, CancellationToken ct) =>
            s.ApproveAsync(id, u.Id(), ct)).RequireAuthorization(Roles.Approver);
        payments.MapPost("/{id:guid}/reject", (Guid id, DecisionRequest r, PaymentService s, ClaimsPrincipal u, CancellationToken ct) =>
            s.RejectAsync(id, r.Reason, u.Id(), u.IsInRole(Roles.Compliance), ct)).RequireAuthorization(Policies.ApproverOrCompliance);
        payments.MapPost("/{id:guid}/release", (Guid id, DecisionRequest r, PaymentService s, ClaimsPrincipal u, CancellationToken ct) =>
            s.ReleaseAsync(id, r.Reason, u.Id(), ct)).RequireAuthorization(Roles.Compliance);

        // SEPA files and bank statements
        var sepa = api.MapGroup("/sepa").WithTags("SEPA").RequireAuthorization(Roles.Operations);
        sepa.MapGet("/batches", async (KontorDb db, CancellationToken ct) =>
            (await db.Batches.AsNoTracking().OrderByDescending(b => b.CreatedAt).ToListAsync(ct)).Select(BatchDto.From));
        sepa.MapPost("/batches", async (DateOnly? upTo, SepaService s, ClaimsPrincipal u, CancellationToken ct) =>
        {
            var b = await s.CreateBatchAsync(upTo, u.Id(), ct);
            return Results.Created($"/api/sepa/batches/{b.Id}", b);
        });
        sepa.MapGet("/batches/{id:guid}/pain001", async (Guid id, KontorDb db, CancellationToken ct) =>
            await db.Batches.AsNoTracking().SingleOrDefaultAsync(b => b.Id == id, ct) is { } b
                ? Results.File(System.Text.Encoding.UTF8.GetBytes(b.Xml), "application/xml", $"{b.MessageId}.pain.001.xml")
                : throw ApiProblem.NotFound("Batch"));
        sepa.MapPost("/statements", async (HttpRequest request, SepaService s, ClaimsPrincipal u, IConfiguration config, CancellationToken ct) =>
        {
            var max = config.GetValue<long?>(MaxStatementBytesKey) ?? 5 * 1024 * 1024;
            if (request.ContentLength > max) throw new ApiProblem(413, "too-large", "Statement file is too large");
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, ct);
            if (buffer.Length > max) throw new ApiProblem(413, "too-large", "Statement file is too large");
            buffer.Position = 0;
            return await s.ImportStatementAsync(buffer, u.Id(), ct);
        }).Accepts<string>("application/xml");

        api.MapGet("/reconciliation/open-items", async (KontorDb db, bool? resolved, CancellationToken ct) =>
            await db.OpenItems.AsNoTracking().Where(o => o.Resolved == (resolved ?? false)).OrderBy(o => o.Id)
                .Select(o => new { o.Id, o.Reference, amount = o.AmountCents / 100m, direction = o.IsCredit ? "Credit" : "Debit", o.BookingDate, o.Reason, o.CounterpartyName, o.CounterpartyIban, o.EndToEndId, o.RemittanceInformation })
                .ToListAsync(ct)).WithTags("SEPA").RequireAuthorization(Roles.Operations);

        // Control functions
        api.MapGet("/ledger/balance-sheet", (AccountService s, CancellationToken ct) => s.BalanceSheetAsync(ct))
            .WithTags("Ledger").RequireAuthorization(Policies.OperationsOrAuditor);

        var audit = api.MapGroup("/audit").WithTags("Audit").RequireAuthorization(Roles.Auditor);
        audit.MapGet("/", async (KontorDb db, long? after, int? limit, CancellationToken ct) =>
            await db.Audit.AsNoTracking().Where(a => a.Sequence > (after ?? 0)).OrderBy(a => a.Sequence).Take(Math.Clamp(limit ?? 200, 1, 1000)).ToListAsync(ct));
        audit.MapGet("/verify", async (KontorDb db, CancellationToken ct) =>
            AuditTrail.Verify(await db.Audit.AsNoTracking().OrderBy(a => a.Sequence).ToListAsync(ct)));
    }

    public static void MapDevTools(this WebApplication app)
    {
        var dev = app.MapGroup("/dev").WithTags("Development only").AllowAnonymous();
        dev.MapPost("/token", (DevTokenRequest r, IOptions<AuthOptions> auth, TimeProvider time) =>
        {
            if (string.IsNullOrWhiteSpace(r.User) || r.User.Length > 64) throw ApiProblem.Invalid("User is required.");
            return Results.Ok(new { token = DevTools.IssueToken(auth.Value, r, time) });
        });
        dev.MapPost("/bank/statement", async (SimulateStatementRequest? r, KontorDb db, IOptions<KontorOptions> o, TimeProvider time, CancellationToken ct) =>
            Results.Text(await DevTools.SimulateStatementAsync(db, o.Value, time, r ?? new SimulateStatementRequest(), ct), "application/xml"));
    }
}

public static class Policies
{
    public const string ApproverOrCompliance = "approver-or-compliance";
    public const string OperationsOrAuditor = "operations-or-auditor";
}
