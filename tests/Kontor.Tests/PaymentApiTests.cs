using System.Net;
using System.Net.Http.Json;
using Kontor.Api.Services;
using Kontor.Domain.Banking;

namespace Kontor.Tests;

public class AccessTests
{
    [Fact]
    public async Task Api_requires_a_token()
    {
        await using var app = new KontorApp();
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().GetAsync("/api/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.CreateClient().GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Roles_are_enforced()
    {
        await using var app = new KontorApp();
        var account = await app.AccountWith("Anna Schmidt", 100);
        var auditor = await app.As("ali.auditor", Roles.Auditor);
        var request = new CreatePaymentRequest(account.Id, "Stadtwerke", DemoIban(1), 10);
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.Pay(request)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await app.Clerk()).GetAsync("/api/audit/verify")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await app.Clerk()).Post("/api/sepa/batches")).StatusCode);
    }

    [Fact]
    public async Task A_token_signed_with_another_key_is_rejected()
    {
        await using var app = new KontorApp();
        var forged = Kontor.Api.Services.DevTools.IssueToken(
            new AuthOptions { SigningKey = "attacker-key-attacker-key-attacker-key-00" }, new DevTokenRequest("mallory", [Roles.Approver]), TimeProvider.System);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", forged);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/payments")).StatusCode);
    }

    [Fact]
    public async Task Responses_carry_security_headers_and_are_not_cached()
    {
        await using var app = new KontorApp();
        var response = await (await app.Clerk()).GetAsync("/api/accounts");
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    internal static string DemoIban(int n) => Iban.CreateGerman("09111100", (5_550_000_000L + n).ToString(System.Globalization.CultureInfo.InvariantCulture)).Value;
}

public class AccountApiTests
{
    [Fact]
    public async Task Opens_accounts_with_a_valid_german_iban()
    {
        await using var app = new KontorApp();
        var a = await app.AccountWith("Anna Schmidt", 0);
        var b = await app.AccountWith("Lukas Becker", 0);
        var iban = Iban.Parse(a.Iban!);
        Assert.Equal("09999900", iban.GermanBankCode);
        Assert.NotEqual(a.Iban, b.Iban);
        Assert.Equal("Active", a.Status);
    }

    [Fact]
    public async Task Sanctioned_holder_cannot_open_an_account_and_the_attempt_is_audited()
    {
        await using var app = new KontorApp();
        var problem = await (await (await app.Clerk()).PostAsJsonAsync("/api/accounts", new OpenAccountRequest("Viktor Exempelsson"))).Problem(422);
        Assert.Contains("sanctions", problem.GetProperty("title").GetString());

        var audit = await (await (await app.Operations()).GetAsync("/api/audit")).Content.ReadAsStringAsync();
        Assert.Contains("aml.sanctions_match", audit);
    }

    [Fact]
    public async Task Possible_match_opens_the_account_frozen()
    {
        await using var app = new KontorApp();
        var account = await (await (await app.Clerk()).PostAsJsonAsync("/api/accounts", new OpenAccountRequest("Borris Mustarow"))).Read<AccountDto>();
        Assert.Equal("Frozen", account.Status);

        var unfrozen = await (await (await app.Compliance()).Post($"/api/accounts/{account.Id}/unfreeze", new DecisionRequest("Different date of birth, checked passport"))).Read<AccountDto>();
        Assert.Equal("Active", unfrozen.Status);
    }

    [Fact]
    public async Task Frozen_account_cannot_send_money()
    {
        await using var app = new KontorApp();
        var account = await app.AccountWith("Anna Schmidt", 500);
        await (await (await app.Compliance()).Post($"/api/accounts/{account.Id}/freeze", new DecisionRequest("Suspected account takeover"))).Read<AccountDto>();
        var problem = await (await (await app.Clerk()).Pay(new(account.Id, "Stadtwerke", AccessTests.DemoIban(1), 10))).Problem(422);
        Assert.Contains("frozen", problem.GetProperty("title").GetString());
    }
}

public class PaymentApiTests
{
    private static CreatePaymentRequest To(AccountDto from, decimal amount, string name = "Stadtwerke Beispielstadt", int payee = 1, DateOnly? date = null) =>
        new(from.Id, name, AccessTests.DemoIban(payee), amount, "Abschlag 11/2026", date);

    [Fact]
    public async Task Small_payment_is_approved_and_the_amount_is_reserved()
    {
        await using var app = new KontorApp();
        var account = await app.AccountWith("Anna Schmidt", 500);
        var response = await (await app.Clerk()).Pay(To(account, 84.50m));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payment = await response.Read<PaymentDto>();

        Assert.Equal("Approved", payment.Status);
        Assert.StartsWith("KON-20261110-", payment.EndToEndId);
        var after = await app.Account(account.Id);
        Assert.Equal(500m, after.Balance);    // not booked yet: it leaves with the next SEPA file
        Assert.Equal(84.50m, after.Held);
        Assert.Equal(415.50m, after.Available);
    }

    [Fact]
    public async Task Retried_request_with_the_same_idempotency_key_returns_the_same_payment()
    {
        await using var app = new KontorApp();
        var account = await app.AccountWith("Anna Schmidt", 500);
        var clerk = await app.Clerk();
        var first = await (await clerk.Pay(To(account, 100), "key-1")).Read<PaymentDto>();
        var retry = await clerk.Pay(To(account, 100), "key-1");

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("true", retry.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(first.Id, (await retry.Read<PaymentDto>()).Id);
        Assert.Equal(100m, (await app.Account(account.Id)).Held); // reserved once, not twice
    }

    [Fact]
    public async Task Same_key_with_a_different_body_is_refused()
    {
        await using var app = new KontorApp();
        var account = await app.AccountWith("Anna Schmidt", 500);
        var clerk = await app.Clerk();
        await (await clerk.Pay(To(account, 100), "key-1")).Read<PaymentDto>();
        var problem = await (await clerk.Pay(To(account, 200), "key-1")).Problem(422);
        Assert.Contains("idempotency-key-reused", problem.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Missing_idempotency_key_is_refused()
    {
        await using var app = new KontorApp();
        var account = await app.AccountWith("Anna Schmidt", 500);
        var response = await (await app.Clerk()).PostAsJsonAsync("/api/payments", To(account, 10), KontorApp.Json);
        Assert.Contains("Idempotency-Key", (await response.Problem(422)).GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData("DE89370400440532013001", "check digits")]
    [InlineData("TR330006100519786457841326", "SEPA")]
    public async Task Invalid_or_non_sepa_iban_is_refused(string iban, string reason)
    {
        await using var app = new KontorApp();
        var account = await app.AccountWith("Anna Schmidt", 500);
        var problem = await (await (await app.Clerk()).Pay(new(account.Id, "Somebody", iban, 10))).Problem(422);
        Assert.Contains(reason, problem.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData(10.005)]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Invalid_amounts_are_refused(double amount)
    {
        await using var app = new KontorApp();
        var account = await app.AccountWith("Anna Schmidt", 500);
        await (await (await app.Clerk()).Pay(To(account, (decimal)amount))).Problem(422);
    }

    [Fact]
    public async Task Insufficient_funds_are_refused_and_the_overdraft_limit_counts()
    {
        await using var app = new KontorApp();
        var account = await app.AccountWith("Anna Schmidt", 100, overdraft: 50);
        var clerk = await app.Clerk();
        await (await clerk.Pay(To(account, 150))).Read<PaymentDto>();
        var problem = await (await clerk.Pay(To(account, 0.01m))).Problem(422);
        Assert.Contains("insufficient-funds", problem.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Execution_dates_follow_the_target2_calendar()
    {
        await using var app = new KontorApp();
        var account = await app.AccountWith("Anna Schmidt", 500);
        var clerk = await app.Clerk();
        var saturday = await (await clerk.Pay(To(account, 10, date: new DateOnly(2026, 11, 14)))).Read<PaymentDto>();
        Assert.Equal(new DateOnly(2026, 11, 16), saturday.ExecutionDate);
        await (await clerk.Pay(To(account, 10, date: new DateOnly(2026, 11, 9)))).Problem(422); // yesterday
    }

    [Fact]
    public async Task Payment_from_the_approval_limit_needs_a_second_person()
    {
        await using var app = new KontorApp();
        var account = await app.AccountWith("Bäckerei Müller GmbH", 50_000);
        var clerk = await app.As("clara.clerk", Roles.Clerk, Roles.Approver); // has both roles, still cannot approve her own
        var payment = await (await clerk.Pay(To(account, 12_400, "Muehlenwerk Taunus GmbH"))).Read<PaymentDto>();
        Assert.Equal("PendingApproval", payment.Status);

        var denied = await (await clerk.Post($"/api/payments/{payment.Id}/approve")).Problem(403);
        Assert.Contains("Four-eyes", denied.GetProperty("title").GetString());

        var approved = await (await (await app.Approver()).Post($"/api/payments/{payment.Id}/approve")).Read<PaymentDto>();
        Assert.Equal("Approved", approved.Status);
        Assert.Equal("anton.approver", approved.ApprovedBy);

        // The denied attempt is in the audit trail: it is a security event
        var audit = await (await (await app.Operations()).GetAsync("/api/audit")).Content.ReadAsStringAsync();
        Assert.Contains("payment.self_approval_denied", audit);
    }

    [Fact]
    public async Task Approving_twice_or_after_rejection_is_a_conflict()
    {
        await using var app = new KontorApp();
        var account = await app.AccountWith("Bäckerei Müller GmbH", 50_000);
        var payment = await (await (await app.Clerk()).Pay(To(account, 20_000))).Read<PaymentDto>();
        var approver = await app.Approver();
        await (await approver.Post($"/api/payments/{payment.Id}/approve")).Read<PaymentDto>();
        await (await approver.Post($"/api/payments/{payment.Id}/approve")).Problem(409);
    }

    [Fact]
    public async Task Rejecting_releases_the_reserved_money()
    {
        await using var app = new KontorApp();
        var account = await app.AccountWith("Bäckerei Müller GmbH", 50_000);
        var payment = await (await (await app.Clerk()).Pay(To(account, 20_000))).Read<PaymentDto>();
        Assert.Equal(30_000m, (await app.Account(account.Id)).Available);

        var approver = await app.Approver();
        await (await approver.Post($"/api/payments/{payment.Id}/reject", new DecisionRequest(null))).Problem(422); // reason required
        var rejected = await (await approver.Post($"/api/payments/{payment.Id}/reject", new DecisionRequest("Invoice already paid"))).Read<PaymentDto>();
        Assert.Equal("Rejected", rejected.Status);
        Assert.Equal(50_000m, (await app.Account(account.Id)).Available);
    }

    [Fact]
    public async Task Sanctioned_payee_is_blocked_and_only_compliance_decides()
    {
        await using var app = new KontorApp();
        var account = await app.AccountWith("Mehmet Yilmaz", 10_000);
        var payment = await (await (await app.Clerk()).Pay(To(account, 800, "Golden Lantern Trading FZE"))).Read<PaymentDto>();
        Assert.Equal("Blocked", payment.Status);
        Assert.Equal("KON-AML-001", payment.RiskFindings[0].RuleId);

        await (await (await app.Approver()).Post($"/api/payments/{payment.Id}/approve")).Problem(409);
        await (await (await app.Approver()).Post($"/api/payments/{payment.Id}/reject", new DecisionRequest("no"))).Problem(403);

        var compliance = await app.As("cem.compliance", Roles.Compliance, Roles.Approver);
        var released = await (await compliance.Post($"/api/payments/{payment.Id}/release", new DecisionRequest("False positive: different company, checked register"))).Read<PaymentDto>();
        Assert.Equal("PendingApproval", released.Status);
        // ...and the compliance officer cannot be the second approver of the same payment
        await (await compliance.Post($"/api/payments/{payment.Id}/approve")).Problem(403);
        Assert.Equal("Approved", (await (await (await app.Approver()).Post($"/api/payments/{payment.Id}/approve")).Read<PaymentDto>()).Status);
    }

    [Fact]
    public async Task Splitting_a_payment_to_stay_below_the_limit_is_caught()
    {
        await using var app = new KontorApp();
        var account = await app.AccountWith("Sofia Rossi", 20_000);
        var clerk = await app.Clerk();
        Assert.Equal("Approved", (await (await clerk.Pay(To(account, 4_900, "Kunsthandel Weber", 5))).Read<PaymentDto>()).Status);
        app.Time.Advance(TimeSpan.FromHours(2));
        var second = await (await clerk.Pay(To(account, 4_900, "Kunsthandel Weber", 5))).Read<PaymentDto>();
        app.Time.Advance(TimeSpan.FromHours(2));
        var third = await (await clerk.Pay(To(account, 4_900, "Kunsthandel Weber", 5))).Read<PaymentDto>();

        Assert.Equal("Approved", second.Status); // 9,800 together: still below the limit
        Assert.Equal("PendingApproval", third.Status);
        Assert.Contains(third.RiskFindings, f => f.RuleId == "KON-AML-003");
    }

    [Fact]
    public async Task High_risk_country_is_configurable()
    {
        await using var app = new KontorApp { HighRiskCountries = "MT" };
        var account = await app.AccountWith("Anna Schmidt", 500);
        var maltese = Iban.Create("MT", "MALT011000012345MTLCAST001S").Value;
        var payment = await (await (await app.Clerk()).Pay(new(account.Id, "Valletta Imports", maltese, 50))).Read<PaymentDto>();
        Assert.Equal("PendingApproval", payment.Status);
        Assert.Contains(payment.RiskFindings, f => f.RuleId == "KON-AML-002");
    }
}

public class OnUsAndPayeeVerificationTests
{
    [Fact]
    public async Task Transfer_between_kontor_accounts_is_booked_immediately()
    {
        await using var app = new KontorApp();
        var anna = await app.AccountWith("Anna Schmidt", 100);
        var lukas = await app.AccountWith("Lukas Becker", 200);
        var payment = await (await (await app.Clerk()).Pay(new(lukas.Id, "Anna Schmidt", anna.Iban!, 25, "Pizza"))).Read<PaymentDto>();

        Assert.Equal("Booked", payment.Status);
        Assert.True(payment.OnUs);
        Assert.Equal("Match", payment.PayeeVerification);
        Assert.Equal(125m, (await app.Account(anna.Id)).Balance);
        var after = await app.Account(lukas.Id);
        Assert.Equal(175m, after.Balance);
        Assert.Equal(0m, after.Held);
    }

    [Fact]
    public async Task Close_match_shows_the_real_name()
    {
        await using var app = new KontorApp();
        var anna = await app.AccountWith("Anna Schmidt", 0);
        var lukas = await app.AccountWith("Lukas Becker", 200);
        var problem = await (await (await app.Clerk()).Pay(new(lukas.Id, "Anna Schmitt", anna.Iban!, 25))).Problem(422);
        Assert.Equal("CloseMatch", problem.GetProperty("verificationOfPayee").GetString());
        Assert.Equal("Anna Schmidt", problem.GetProperty("suggestedName").GetString());
        Assert.Equal(200m, (await app.Account(lukas.Id)).Available); // nothing reserved
    }

    [Fact]
    public async Task No_match_hides_the_name_and_a_confirmed_mismatch_needs_approval()
    {
        await using var app = new KontorApp();
        var anna = await app.AccountWith("Anna Schmidt", 0);
        var lukas = await app.AccountWith("Lukas Becker", 200);
        var clerk = await app.Clerk();
        var problem = await (await clerk.Pay(new(lukas.Id, "Online Shop GmbH", anna.Iban!, 25))).Problem(422);
        Assert.Equal("NoMatch", problem.GetProperty("verificationOfPayee").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, problem.GetProperty("suggestedName").ValueKind);

        var confirmed = await (await clerk.Pay(new(lukas.Id, "Online Shop GmbH", anna.Iban!, 25, ConfirmPayeeMismatch: true))).Read<PaymentDto>();
        Assert.Equal("PendingApproval", confirmed.Status);
        Assert.Contains(confirmed.RiskFindings, f => f.RuleId == "KON-VOP-001");
    }
}
