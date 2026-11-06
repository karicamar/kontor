using System.Security.Claims;
using Kontor.Domain.Ledger;
using Kontor.Domain.Risk;
using Kontor.Domain.Sepa;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Kontor.Api.Services;

public sealed class KontorOptions
{
    public const string Section = "Kontor";

    /// <summary>The platform, debtor of every outgoing SEPA file.</summary>
    public string PlatformName { get; set; } = "Kontor Payments (Demo)";
    /// <summary>Bank code used to issue customer IBANs. The default is made up (German bank codes never start with 0).</summary>
    public string BankCode { get; set; } = "09999900";
    /// <summary>The platform's own account at its partner bank (debtor account in pain.001, the account in camt.053).</summary>
    public string NostroIban { get; set; } = Kontor.Domain.Banking.Iban.CreateGerman("09888800", "12345678").Value;
    public string? NostroBic { get; set; }
    /// <summary>EU consolidated sanctions list (XML). Empty = the demo list shipped with the project.</summary>
    public string? SanctionsListPath { get; set; }
    /// <summary>Optional JSON-lines copy of the audit trail, for a SIEM (LogSentinel reads it).</summary>
    public string? AuditJsonlPath { get; set; }
    /// <summary>Fill the database with demo customers and payments on first start (Development only).</summary>
    public bool SeedDemoData { get; set; }
    public RiskSettings Risk { get; set; } = new();
}

/// <summary>An expected error with an HTTP status, turned into an RFC 9457 problem response.</summary>
public sealed class ApiProblem(int status, string code, string title, string? detail = null, Dictionary<string, object?>? extensions = null)
    : Exception(detail ?? title)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string Title { get; } = title;
    public string? Detail { get; } = detail;
    public Dictionary<string, object?> Extensions { get; } = extensions ?? [];

    public static ApiProblem NotFound(string what) => new(404, "not-found", $"{what} not found");
    public static ApiProblem Invalid(string detail, Dictionary<string, object?>? ext = null) => new(422, "validation", "The request is not valid", detail, ext);
    public static ApiProblem Conflict(string code, string title, string? detail = null) => new(409, code, title, detail);
    public static ApiProblem Forbidden(string code, string title, string? detail = null) => new(403, code, title, detail);
}

public sealed class ProblemExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        var problem = exception switch
        {
            ApiProblem p => p,
            LedgerException l => new ApiProblem(422, "ledger", "Booking rejected", l.Message),
            SepaException s => new ApiProblem(422, "sepa", "SEPA rules violated", s.Message),
            BadHttpRequestException b => new ApiProblem(b.StatusCode, "bad-request", "Bad request", b.Message),
            _ => null,
        };
        if (problem is null) return false; // unexpected: the default handler returns a generic 500 without details

        http.Response.StatusCode = problem.Status;
        var details = new ProblemDetails
        {
            Status = problem.Status,
            Title = problem.Title,
            Detail = problem.Detail,
            Type = $"https://github.com/karicamar/kontor/blob/main/docs/errors.md#{problem.Code}",
        };
        foreach (var (k, v) in problem.Extensions) details.Extensions[k] = v;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = details, Exception = exception });
    }
}

public static class Roles
{
    public const string Clerk = "clerk";             // enters payments, opens accounts
    public const string Approver = "approver";       // second pair of eyes
    public const string Compliance = "compliance";   // AML: blocked payments, sanctions hits
    public const string Operations = "operations";   // SEPA files, bank statements, reconciliation
    public const string Auditor = "auditor";         // read-only: audit trail and its verification

    public static readonly string[] All = [Clerk, Approver, Compliance, Operations, Auditor];
}

public static class UserExtensions
{
    /// <summary>The user id from the token ("sub"); every action in the audit trail is tied to it.</summary>
    public static string Id(this ClaimsPrincipal user) =>
        user.FindFirstValue("sub") ?? user.Identity?.Name ?? throw new InvalidOperationException("Authenticated user without id.");
}
