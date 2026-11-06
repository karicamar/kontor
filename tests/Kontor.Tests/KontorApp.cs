using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kontor.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Kontor.Tests;

/// <summary>
/// The real API in memory, with its own database and a clock the test controls (Tuesday 10 Nov 2026, 09:00 UTC).
/// The database is a throwaway SQLite file, or a fresh PostgreSQL database when KONTOR_TEST_POSTGRES holds a connection
/// string (CI runs the whole suite both ways).
/// </summary>
public sealed class KontorApp : WebApplicationFactory<Program>
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly string? Postgres = Environment.GetEnvironmentVariable("KONTOR_TEST_POSTGRES");
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"kontor-test-{Guid.NewGuid():N}.db");
    private readonly string _pgDatabase = $"kontor_test_{Guid.NewGuid():N}";

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
    public string AuditFile { get; } = Path.Combine(Path.GetTempPath(), $"kontor-audit-{Guid.NewGuid():N}.jsonl");
    public string? HighRiskCountries { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        if (Postgres is { Length: > 0 })
        {
            builder.UseSetting("Database:Provider", "postgres");
            builder.UseSetting("ConnectionStrings:Default", $"{Postgres};Database={_pgDatabase}");
        }
        else
            builder.UseSetting("ConnectionStrings:Default", $"Data Source={_db}");
        builder.UseSetting("Auth:SigningKey", "test-signing-key-0123456789abcdef-0123456789");
        builder.UseSetting("Kontor:EnableDevTools", "true");
        builder.UseSetting("Kontor:AuditJsonlPath", AuditFile);
        if (HighRiskCountries is not null) builder.UseSetting("Kontor:Risk:HighRiskCountries:0", HighRiskCountries);
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Time);
        });
    }

    /// <summary>A client acting as <paramref name="user"/> with the given roles (token from the dev endpoint).</summary>
    public async Task<HttpClient> As(string user, params string[] roles)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/dev/token", new DevTokenRequest(user, roles));
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public Task<HttpClient> Clerk(string user = "clara.clerk") => As(user, Roles.Clerk);
    public Task<HttpClient> Approver(string user = "anton.approver") => As(user, Roles.Approver);
    public Task<HttpClient> Compliance(string user = "cem.compliance") => As(user, Roles.Compliance);
    public Task<HttpClient> Operations(string user = "olga.ops") => As(user, Roles.Operations, Roles.Auditor);

    /// <summary>Opens an account and pays money into it the way it really arrives: as a credit on the bank statement.</summary>
    public async Task<AccountDto> AccountWith(string holder, decimal balance, decimal overdraft = 0)
    {
        var clerk = await Clerk();
        var account = await (await clerk.PostAsJsonAsync("/api/accounts", new OpenAccountRequest(holder, overdraft))).Read<AccountDto>();
        if (balance > 0)
            await ImportStatement(new SimulateStatementRequest(Incoming: [new SimulatedIncoming(account.Iban!, "Einzahlung", "DE89370400440532013000", balance, "Startguthaben")]));
        return await (await clerk.GetAsync($"/api/accounts/{account.Id}")).Read<AccountDto>();
    }

    public async Task<StatementImportResult> ImportStatement(SimulateStatementRequest request)
    {
        var ops = await Operations();
        var xml = await (await ops.PostAsJsonAsync("/dev/bank/statement", request)).Content.ReadAsStringAsync();
        var response = await ops.PostAsync("/api/sepa/statements", new StringContent(xml, Encoding.UTF8, "application/xml"));
        return await response.Read<StatementImportResult>();
    }

    public async Task<AccountDto> Account(Guid id) => await (await (await Operations()).GetAsync($"/api/accounts/{id}")).Read<AccountDto>();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, AuditFile })
            try { File.Delete(f); } catch (IOException) { /* best effort */ }
    }
}

public static class HttpExtensions
{
    public static async Task<T> Read<T>(this HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, KontorApp.Json)!;
    }

    public static async Task<JsonElement> Problem(this HttpResponseMessage response, int status)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True((int)response.StatusCode == status, $"Expected {status}, got {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    public static Task<HttpResponseMessage> Pay(this HttpClient client, CreatePaymentRequest request, string? key = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/payments") { Content = JsonContent.Create(request, options: KontorApp.Json) };
        message.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return client.SendAsync(message);
    }

    public static Task<HttpResponseMessage> Post(this HttpClient client, string url, object? body = null) =>
        client.PostAsJsonAsync(url, body ?? new { }, KontorApp.Json);
}
