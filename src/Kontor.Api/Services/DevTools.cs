using System.Security.Claims;
using System.Text;
using Kontor.Api.Data;
using Kontor.Domain.Banking;
using Kontor.Domain.Sepa;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Kontor.Api.Services;

public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>OpenID Connect authority (Entra ID, Keycloak, ...). When set, tokens are validated against its keys.</summary>
    public string? Authority { get; set; }
    public string Audience { get; set; } = "kontor-api";
    public string Issuer { get; set; } = "kontor-dev";
    /// <summary>Development only: HMAC key for locally issued tokens (at least 32 characters).</summary>
    public string? SigningKey { get; set; }

    public SymmetricSecurityKey? Key => SigningKey is { Length: >= 32 } k ? new SymmetricSecurityKey(Encoding.UTF8.GetBytes(k)) : null;
}

public sealed record DevTokenRequest(string User, string[] Roles);

public sealed record SimulatedIncoming(string ToIban, string FromName, string FromIban, decimal Amount, string? Text);

public sealed record SimulateStatementRequest(string[]? ReturnEndToEndIds = null, string? ReturnReason = null, SimulatedIncoming[]? Incoming = null);

/// <summary>
/// Local stand-ins for what a real deployment gets from outside: an identity provider (tokens) and the partner bank
/// (statements). Mapped only in Development or when Kontor:EnableDevTools is set.
/// </summary>
public static class DevTools
{
    public static string IssueToken(AuthOptions auth, DevTokenRequest request, TimeProvider time)
    {
        var key = auth.Key ?? throw new InvalidOperationException("Auth:SigningKey is not configured.");
        var claims = new List<Claim> { new("sub", request.User), new("name", request.User) };
        claims.AddRange(request.Roles.Where(r => Roles.All.Contains(r)).Select(r => new Claim("role", r)));
        var now = time.GetUtcNow().UtcDateTime;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = auth.Issuer, Audience = auth.Audience, Subject = new ClaimsIdentity(claims),
            IssuedAt = now, NotBefore = now, Expires = now.AddHours(8),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        });
    }

    /// <summary>
    /// What the partner bank would send after executing the latest SEPA file: every submitted transfer settled (or returned,
    /// if asked), plus any incoming payments. The opening balance is the nostro balance in the ledger, as if both sides agreed until now.
    /// </summary>
    public static async Task<string> SimulateStatementAsync(KontorDb db, KontorOptions options, TimeProvider time, SimulateStatementRequest request, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var nostro = await db.Accounts.AsNoTracking().SingleAsync(a => a.Code == InternalAccounts.Nostro, ct);
        var submitted = await db.Payments.AsNoTracking().Where(p => p.Status == PaymentStatus.Submitted).OrderBy(p => p.CreatedAt).ToListAsync(ct);
        var returns = new HashSet<string>(request.ReturnEndToEndIds ?? [], StringComparer.Ordinal);
        var lines = new List<StatementLine>();
        var n = 0;
        string Ref() => $"BANK{today:yyyyMMdd}{++n:0000}";

        foreach (var p in submitted)
        {
            lines.Add(new StatementLine(Ref(), new Money(p.AmountCents), false, today, p.EndToEndId, p.CreditorName, p.CreditorIban, options.NostroIban, p.RemittanceInformation));
            if (returns.Contains(p.EndToEndId))
                lines.Add(new StatementLine(Ref(), new Money(p.AmountCents), true, today, p.EndToEndId, p.CreditorName, p.CreditorIban, options.NostroIban,
                    $"RUECKUEBERWEISUNG {p.RemittanceInformation}", request.ReturnReason ?? "AC04"));
        }
        foreach (var i in request.Incoming ?? [])
            lines.Add(new StatementLine(Ref(), Money.FromEuros(i.Amount), true, today, null, i.FromName, Iban.Parse(i.FromIban).Value, Iban.Parse(i.ToIban).Value, i.Text));

        var count = await db.StatementImports.CountAsync(ct) + 1;
        return Camt053Writer.Write($"BANKMSG{now.UtcDateTime:yyyyMMddHHmmss}", $"STMT-{today:yyyyMMdd}-{count:000}", now,
            Iban.Parse(options.NostroIban), new Money(nostro.BalanceCents), today, lines);
    }
}
