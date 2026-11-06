using Kontor.Api.Services;
using Kontor.Domain.Banking;
using Kontor.Domain.Ledger;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kontor.Api.Data;

public static class DatabaseSetup
{
    /// <summary>Creates the schema and the internal accounts; in Development optionally a set of demo customers and payments.</summary>
    public static async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<KontorDb>();
        await db.Database.EnsureCreatedAsync();

        var time = sp.GetRequiredService<TimeProvider>();
        foreach (var (code, name, side) in InternalAccounts.All)
            if (!await db.Accounts.AnyAsync(a => a.Code == code))
                db.Accounts.Add(new Account
                {
                    Id = Guid.CreateVersion7(time.GetUtcNow()), Kind = AccountKind.Internal, Code = code, Holder = name, Side = side,
                    Iban = code == InternalAccounts.Nostro ? sp.GetRequiredService<IOptions<KontorOptions>>().Value.NostroIban : null,
                    Status = AccountStatus.Active, CreatedAt = time.GetUtcNow(),
                });
        await db.SaveChangesAsync();

        if (sp.GetRequiredService<IOptions<KontorOptions>>().Value.SeedDemoData && !await db.Accounts.AnyAsync(a => a.Kind == AccountKind.Customer))
            await DemoData.SeedAsync(sp);
    }
}

/// <summary>
/// Demo customers and payments covering every state, so the dashboard has something to show on first start.
/// All names and IBANs are invented (the bank codes start with 0, which German banks do not use).
/// </summary>
public static class DemoData
{
    public const string ExternalBankCode = "09111100";

    public static readonly (string Holder, decimal Opening)[] Customers =
    [
        ("Anna Schmidt", 3_250.00m),
        ("Mehmet Yilmaz", 18_900.00m),
        ("Bäckerei Müller GmbH", 42_500.00m),
        ("Lukas Becker", 1_120.40m),
        ("Sofia Rossi", 15_000.00m),
    ];

    public static Iban External(int n) => Iban.CreateGerman(ExternalBankCode, (5_550_000_000L + n).ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static async Task SeedAsync(IServiceProvider sp)
    {
        var accounts = sp.GetRequiredService<AccountService>();
        var payments = sp.GetRequiredService<PaymentService>();
        var ledger = sp.GetRequiredService<LedgerService>();
        var uow = sp.GetRequiredService<UnitOfWork>();

        var ids = new List<AccountDto>();
        foreach (var (holder, _) in Customers)
            ids.Add(await accounts.OpenAsync(new OpenAccountRequest(holder), "demo.onboarding", default));

        // Opening balances: the customers' money arrived at the partner bank before go-live
        await uow.RunAsync(async () =>
        {
            var nostro = await ledger.InternalAsync(InternalAccounts.Nostro);
            for (var i = 0; i < Customers.Length; i++)
                await ledger.PostAsync(JournalKind.Opening, "Opening balance", JournalRules.DebitCredit(nostro.Id, ids[i].Id, Money.FromEuros(Customers[i].Opening)), "demo.migration");
        });

        var anna = ids[0].Id; var mehmet = ids[1].Id; var baeckerei = ids[2].Id; var lukas = ids[3].Id; var sofia = ids[4].Id;
        async Task Pay(string user, Guid from, string name, string iban, decimal amount, string text) =>
            await payments.CreateAsync(new CreatePaymentRequest(from, name, iban, amount, text), Guid.NewGuid().ToString(), user, default);

        await Pay("clara.clerk", anna, "Hausverwaltung Roemerberg GmbH", External(1).Value, 1_150.00m, "Miete November 2026");
        await Pay("clara.clerk", lukas, "Stadtwerke Beispielstadt", External(2).Value, 84.50m, "Abschlag Strom 11/2026");
        await Pay("clara.clerk", lukas, "Anna Schmidt", ids[0].Iban!, 25.00m, "Pizza Freitag");
        await Pay("clara.clerk", baeckerei, "Muehlenwerk Taunus GmbH", External(3).Value, 12_400.00m, "Rechnung 2026-0471 Mehl");
        await Pay("clara.clerk", mehmet, "Golden Lantern Trading", External(4).Value, 7_800.00m, "Consulting fee");
        await Pay("clara.clerk", sofia, "Kunsthandel Weber", External(5).Value, 6_000.00m, "Anzahlung Gemaelde");
        await Pay("clara.clerk", sofia, "Kunsthandel Weber", External(5).Value, 5_500.00m, "Restzahlung Gemaelde");
    }
}
