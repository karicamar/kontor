using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Kontor.Api.Data;
using Kontor.Api.Services;
using Kontor.Domain.Sepa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kontor.Tests;

public class SepaCycleTests
{
    private static CreatePaymentRequest To(AccountDto from, decimal amount, int payee, string name) =>
        new(from.Id, name, AccessTests.DemoIban(payee), amount, $"Rechnung {payee}");

    [Fact]
    public async Task Full_cycle_payment_file_statement_return_and_reconciliation()
    {
        await using var app = new KontorApp();
        var anna = await app.AccountWith("Anna Schmidt", 3_000);
        var lukas = await app.AccountWith("Lukas Becker", 1_000);
        var clerk = await app.Clerk();
        var rent = await (await clerk.Pay(To(anna, 1_150, 1, "Hausverwaltung Römerberg GmbH"))).Read<PaymentDto>();
        var power = await (await clerk.Pay(To(lukas, 84.50m, 2, "Stadtwerke Beispielstadt"))).Read<PaymentDto>();
        var ops = await app.Operations();

        // 1. The payment file
        var batch = await (await ops.Post("/api/sepa/batches")).Read<BatchDto>();
        Assert.Equal(2, batch.Transactions);
        Assert.Equal(1_234.50m, batch.Total);
        var file = await ops.GetAsync($"/api/sepa/batches/{batch.Id}/pain001");
        Assert.Equal("application/xml", file.Content.Headers.ContentType?.MediaType);
        var xml = await file.Content.ReadAsStringAsync();
        Schema.AssertValid(xml);
        XNamespace ns = Pain001Writer.Namespace;
        Assert.Equal([rent.EndToEndId, power.EndToEndId], XDocument.Parse(xml).Descendants(ns + "EndToEndId").Select(e => e.Value));

        // Customers are debited when the file leaves; the money is "in transit" until the bank confirms it
        Assert.Equal(1_850m, (await app.Account(anna.Id)).Balance);
        Assert.Equal(0m, (await app.Account(anna.Id)).Held);
        Assert.Equal("Submitted", (await (await clerk.GetAsync($"/api/payments/{rent.Id}")).Read<PaymentDto>()).Status);
        await (await ops.Post("/api/sepa/batches")).Problem(422); // nothing left to send

        // 2. The bank statement: rent settled, power bill returned (account closed), salary for Anna, money for an unknown IBAN
        var result = await app.ImportStatement(new SimulateStatementRequest(
            ReturnEndToEndIds: [power.EndToEndId], ReturnReason: "AC04",
            Incoming:
            [
                new SimulatedIncoming(anna.Iban!, "Arbeitgeber Beispiel AG", "DE89370400440532013000", 2_840m, "Gehalt November"),
                new SimulatedIncoming(Kontor.Domain.Banking.Iban.CreateGerman("09999900", "9999999999").Value, "Unbekannt", "DE89370400440532013000", 75m, "?"),
            ]));

        Assert.True(result.Reconciled, $"ledger {result.NostroBalanceInLedger} vs bank {result.ClosingBalanceOnStatement}");
        Assert.Equal(5, result.Entries); // 2 debits, 1 return, 2 incoming
        Assert.Equal(1, result.OpenItems);
        Assert.Equal("Booked", (await (await clerk.GetAsync($"/api/payments/{rent.Id}")).Read<PaymentDto>()).Status);
        var returned = await (await clerk.GetAsync($"/api/payments/{power.Id}")).Read<PaymentDto>();
        Assert.Equal("Returned", returned.Status);
        Assert.Equal("AC04: Account closed", returned.ReturnReason);
        Assert.Equal(1_000m, (await app.Account(lukas.Id)).Balance); // got the money back
        Assert.Equal(1_850m + 2_840m, (await app.Account(anna.Id)).Balance);

        var openItems = await ops.GetFromJsonAsync<JsonElement>("/api/reconciliation/open-items");
        Assert.Contains("No customer account", openItems[0].GetProperty("reason").GetString());

        // 3. The books still balance, and every number can be traced back to its postings
        var sheet = await (await ops.GetAsync("/api/ledger/balance-sheet")).Read<BalanceSheetDto>();
        Assert.True(sheet.Balanced, string.Join("; ", sheet.Problems));
        var transit = sheet.Sides.SelectMany(s => s.Accounts).Single(a => a.Code == InternalAccounts.SepaOutgoing);
        Assert.Equal(0m, transit.Balance); // everything sent was settled or returned
        var suspense = sheet.Sides.SelectMany(s => s.Accounts).Single(a => a.Code == InternalAccounts.Suspense);
        Assert.Equal(75m, suspense.Balance);

        var statement = await (await ops.GetAsync($"/api/accounts/{lukas.Id}/statement")).Read<AccountStatementDto>();
        Assert.Equal(["SepaIncoming", "SepaSubmitted", "SepaReturned"], statement.Lines.Select(l => l.Kind));
        Assert.Equal(1_000m, statement.Lines[^1].BalanceAfter);
    }

    [Fact]
    public async Task The_same_statement_cannot_be_booked_twice()
    {
        await using var app = new KontorApp();
        var ops = await app.Operations();
        var xml = await (await ops.PostAsJsonAsync("/dev/bank/statement", new SimulateStatementRequest())).Content.ReadAsStringAsync();
        (await ops.PostAsync("/api/sepa/statements", new StringContent(xml, Encoding.UTF8, "application/xml"))).EnsureSuccessStatusCode();
        await (await ops.PostAsync("/api/sepa/statements", new StringContent(xml, Encoding.UTF8, "application/xml"))).Problem(409);
    }

    [Fact]
    public async Task Statement_for_another_account_or_with_wrong_balances_is_refused()
    {
        await using var app = new KontorApp();
        var ops = await app.Operations();
        var other = Camt053Writer.Write("M", "S-OTHER", DateTimeOffset.UtcNow, Kontor.Domain.Banking.Iban.Parse("DE89370400440532013000"),
            Kontor.Domain.Banking.Money.Zero, new DateOnly(2026, 11, 10), []);
        var wrongAccount = await (await ops.PostAsync("/api/sepa/statements", new StringContent(other, Encoding.UTF8, "application/xml"))).Problem(422);
        Assert.Contains("wrong-account", wrongAccount.GetProperty("type").GetString());

        var xml = await (await ops.PostAsJsonAsync("/dev/bank/statement", new SimulateStatementRequest(Incoming: [new SimulatedIncoming(AccessTests.DemoIban(1), "X", "DE89370400440532013000", 10m, null)]))).Content.ReadAsStringAsync();
        // An entry is missing (a truncated file): the closing balance no longer follows from the entries
        var tampered = System.Text.RegularExpressions.Regex.Replace(xml, "<Ntry>.*?</Ntry>", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        var inconsistent = await (await ops.PostAsync("/api/sepa/statements", new StringContent(tampered, Encoding.UTF8, "application/xml"))).Problem(422);
        Assert.Contains("statement-inconsistent", inconsistent.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Incoming_money_from_a_sanctioned_payer_is_held_not_credited()
    {
        await using var app = new KontorApp();
        var anna = await app.AccountWith("Anna Schmidt", 0);
        var result = await app.ImportStatement(new SimulateStatementRequest(Incoming:
            [new SimulatedIncoming(anna.Iban!, "Nordwind Shell Shipping", "DE89370400440532013000", 9_000m, "Darlehen")]));
        Assert.Equal(1, result.OpenItems);
        Assert.Contains("sanctions", result.Lines.Single().Outcome);
        Assert.Equal(0m, (await app.Account(anna.Id)).Balance);
        Assert.True(result.Reconciled); // booked to suspense, so the ledger still matches the bank
    }

    [Fact]
    public async Task Oversized_statement_upload_is_refused()
    {
        await using var app = new KontorApp();
        var ops = await app.Operations();
        var huge = new string(' ', 6 * 1024 * 1024);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await ops.PostAsync("/api/sepa/statements", new StringContent(huge, Encoding.UTF8, "application/xml"))).StatusCode);
    }
}

public class ConcurrencyTests
{
    [Fact]
    public async Task Parallel_payments_cannot_overdraw_an_account()
    {
        await using var app = new KontorApp();
        var account = await app.AccountWith("Anna Schmidt", 1_000);
        var clerk = await app.Clerk();

        // 20 requests of 100 € at the same moment against 1,000 €: exactly 10 may succeed
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            clerk.Pay(new CreatePaymentRequest(account.Id, "Stadtwerke", AccessTests.DemoIban(1), 100))));

        Assert.Equal(10, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(10, responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity));
        var after = await app.Account(account.Id);
        Assert.Equal(1_000m, after.Held);
        Assert.Equal(0m, after.Available);
    }

    [Fact]
    public async Task Parallel_retries_with_one_key_create_one_payment()
    {
        await using var app = new KontorApp();
        var account = await app.AccountWith("Anna Schmidt", 1_000);
        var clerk = await app.Clerk();
        var request = new CreatePaymentRequest(account.Id, "Stadtwerke", AccessTests.DemoIban(1), 100);
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => clerk.Pay(request, "same-key")));

        Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode));
        var ids = await Task.WhenAll(responses.Select(async r => (await r.Read<PaymentDto>()).Id));
        Assert.Single(ids.Distinct());
        Assert.Equal(100m, (await app.Account(account.Id)).Held);
    }
}

public class AuditTrailTests
{
    [Fact]
    public async Task Every_action_is_in_the_chain_and_the_chain_verifies()
    {
        await using var app = new KontorApp();
        var account = await app.AccountWith("Anna Schmidt", 20_000);
        var payment = await (await (await app.Clerk()).Pay(new(account.Id, "Muehlenwerk", AccessTests.DemoIban(3), 15_000))).Read<PaymentDto>();
        await (await (await app.Approver()).Post($"/api/payments/{payment.Id}/approve")).Read<PaymentDto>();
        var ops = await app.Operations();
        await (await ops.Post("/api/sepa/batches")).Read<BatchDto>();

        var records = await ops.GetFromJsonAsync<JsonElement>("/api/audit");
        var actions = records.EnumerateArray().Select(r => r.GetProperty("action").GetString()).ToList();
        Assert.Equal(["account.opened", "sepa.statement_imported", "payment.created", "payment.approved", "sepa.file_created"], actions);
        Assert.Equal("anton.approver", records[3].GetProperty("actor").GetString());

        var verify = await ops.GetFromJsonAsync<JsonElement>("/api/audit/verify");
        Assert.True(verify.GetProperty("intact").GetBoolean());
        Assert.Equal(5, verify.GetProperty("recordsChecked").GetInt32());

        // The SIEM copy has the same records, one JSON object per line
        var lines = await File.ReadAllLinesAsync(app.AuditFile);
        Assert.Equal(5, lines.Length);
        Assert.Equal("kontor", JsonDocument.Parse(lines[2]).RootElement.GetProperty("source").GetString());
        Assert.EndsWith("Z", JsonDocument.Parse(lines[2]).RootElement.GetProperty("timestamp").GetString());
    }

    [Fact]
    public async Task Editing_the_audit_table_directly_is_detected()
    {
        await using var app = new KontorApp();
        var account = await app.AccountWith("Anna Schmidt", 20_000);
        var payment = await (await (await app.Clerk()).Pay(new(account.Id, "Muehlenwerk", AccessTests.DemoIban(3), 15_000))).Read<PaymentDto>();
        await (await (await app.Approver()).Post($"/api/payments/{payment.Id}/approve")).Read<PaymentDto>();

        // An insider with database access rewrites who approved the payment
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KontorDb>();
            // Quoted identifiers: the same SQL works on SQLite and PostgreSQL
            await db.Database.ExecuteSqlRawAsync("UPDATE \"Audit\" SET \"Actor\" = 'clara.clerk' WHERE \"Action\" = 'payment.approved'");
        }

        var verify = await (await app.Operations()).GetFromJsonAsync<JsonElement>("/api/audit/verify");
        Assert.False(verify.GetProperty("intact").GetBoolean());
        Assert.Equal(4, verify.GetProperty("firstBrokenSequence").GetInt64());
    }

    [Fact]
    public async Task A_failed_request_leaves_no_audit_record()
    {
        await using var app = new KontorApp();
        var account = await app.AccountWith("Anna Schmidt", 10);
        await (await (await app.Clerk()).Pay(new(account.Id, "Muehlenwerk", AccessTests.DemoIban(3), 15_000))).Problem(422);
        var records = await (await app.Operations()).GetFromJsonAsync<JsonElement>("/api/audit");
        Assert.DoesNotContain(records.EnumerateArray(), r => r.GetProperty("action").GetString() == "payment.created");
    }
}

public class DatabaseProviderTests
{
    [Fact]
    public async Task Runs_on_the_configured_database()
    {
        await using var app = new KontorApp();
        using var scope = app.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<KontorDb>().Database.ProviderName;
        var expected = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KONTOR_TEST_POSTGRES")) ? "Sqlite" : "Npgsql";
        Assert.Contains(expected, provider);
    }
}
