using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Kontor.Domain.Banking;
using Kontor.Domain.Sepa;

namespace Kontor.Tests;

/// <summary>Validation against the official ISO 20022 schemas (downloaded from iso20022.org, see Schemas/README.md).</summary>
public static class Schema
{
    private static readonly Lazy<XmlSchemaSet> Set = new(() =>
    {
        var set = new XmlSchemaSet();
        foreach (var file in new[] { "pain.001.001.09.xsd", "camt.053.001.08.xsd" })
            set.Add(null, Path.Combine(AppContext.BaseDirectory, "Schemas", file));
        set.Compile();
        return set;
    });

    public static IReadOnlyList<string> Errors(string xml)
    {
        var errors = new List<string>();
        var doc = XDocument.Parse(xml);
        doc.Validate(Set.Value, (_, e) => errors.Add($"{e.Severity}: {e.Message}"));
        return errors;
    }

    public static void AssertValid(string xml)
    {
        var errors = Errors(xml);
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }
}

public class Pain001Tests
{
    private static readonly SepaAccount Platform = new("Kontor Payments (Demo)", Iban.CreateGerman("09888800", "12345678"), null);

    internal static CreditTransfer Transfer(string id, decimal amount, string name = "Hausverwaltung Römerberg GmbH", string? bic = null, string? text = "Miete November") =>
        new(id, Money.FromEuros(amount), name, Iban.Parse("DE89370400440532013000"), bic is null ? null : Bic.TryParse(bic, out var b) ? b : null, "Anna Schmidt", text);

    private static string Write(params CreditTransfer[] transfers) =>
        Pain001Writer.Write(new CreditTransferBatch("KON20261110090000ABC", new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero), Platform, new DateOnly(2026, 11, 10), transfers));

    private static XNamespace Ns => Pain001Writer.Namespace;

    [Fact]
    public void Output_is_valid_against_the_iso_schema()
    {
        Schema.AssertValid(Write(Transfer("E2E-1", 1150m), Transfer("E2E-2", 84.5m, "Stadtwerke", "COBADEFFXXX", null)));
    }

    [Fact]
    public void Control_sums_and_counts_match_the_transfers()
    {
        var doc = XDocument.Parse(Write(Transfer("E2E-1", 1150m), Transfer("E2E-2", 84.5m), Transfer("E2E-3", 0.01m)));
        var header = doc.Descendants(Ns + "GrpHdr").Single();
        Assert.Equal("3", header.Element(Ns + "NbOfTxs")!.Value);
        Assert.Equal("1234.51", header.Element(Ns + "CtrlSum")!.Value);
        Assert.Equal("1234.51", doc.Descendants(Ns + "PmtInf").Single().Element(Ns + "CtrlSum")!.Value);
        Assert.Equal(["1150.00", "84.50", "0.01"], doc.Descendants(Ns + "InstdAmt").Select(a => a.Value));
    }

    [Fact]
    public void Names_are_converted_to_the_sepa_character_set()
    {
        var doc = XDocument.Parse(Write(Transfer("E2E-1", 10m, "Bäckerei Müller & Söhne", text: "Rechnung Nr. 4711 – Brötchen")));
        Assert.Equal("Baeckerei Mueller + Soehne", doc.Descendants(Ns + "Cdtr").Single().Element(Ns + "Nm")!.Value);
        Assert.Equal("Rechnung Nr. 4711 Broetchen", doc.Descendants(Ns + "Ustrd").Single().Value);
    }

    [Fact]
    public void Customer_is_the_ultimate_debtor_and_the_agent_is_notprovided_without_bic()
    {
        var doc = XDocument.Parse(Write(Transfer("E2E-1", 10m)));
        Assert.Equal("Anna Schmidt", doc.Descendants(Ns + "UltmtDbtr").Single().Element(Ns + "Nm")!.Value);
        Assert.Equal("NOTPROVIDED", doc.Descendants(Ns + "DbtrAgt").Single().Descendants(Ns + "Id").Single().Value);
        Assert.Empty(doc.Descendants(Ns + "CdtrAgt")); // IBAN-only
        Assert.Equal("SLEV", doc.Descendants(Ns + "ChrgBr").Single().Value);
    }

    [Fact]
    public void Invalid_batches_are_refused()
    {
        Assert.Throws<SepaException>(() => Write());
        Assert.Throws<SepaException>(() => Write(Transfer("E2E-1", 10m), Transfer("E2E-1", 20m)));
        Assert.Throws<SepaException>(() => Write(Transfer("E2E/1//x", 10m)));
        var turkish = Transfer("E2E-1", 10m) with { CreditorIban = Iban.Parse("TR330006100519786457841326") };
        Assert.Contains("not reachable by SEPA", Assert.Throws<SepaException>(() => Write(turkish)).Message);
    }
}

public class Camt053Tests
{
    private static readonly Iban Nostro = Iban.CreateGerman("09888800", "12345678");

    private static string Statement(Money opening, params StatementLine[] lines) =>
        Camt053Writer.Write("MSG1", "STMT-1", new DateTimeOffset(2026, 11, 10, 18, 0, 0, TimeSpan.Zero), Nostro, opening, new DateOnly(2026, 11, 10), lines);

    private static readonly StatementLine Settled = new("BANK1", Money.FromEuros(1150m), false, new DateOnly(2026, 11, 10), "E2E-1", "Hausverwaltung", "DE89370400440532013000", null, "Miete");
    private static readonly StatementLine Returned = new("BANK2", Money.FromEuros(84.5m), true, new DateOnly(2026, 11, 10), "E2E-2", "Stadtwerke", "DE89370400440532013000", null, "RUECKUEBERWEISUNG", "AC04");
    private static readonly StatementLine Incoming = new("BANK3", Money.FromEuros(2840m), true, new DateOnly(2026, 11, 10), null, "Arbeitgeber AG", "DE89370400440532013000", "DE02099999001000000001", "Gehalt November");

    [Fact]
    public void Writer_output_is_valid_against_the_iso_schema() =>
        Schema.AssertValid(Statement(Money.FromEuros(10_000m), Settled, Returned, Incoming));

    [Fact]
    public void Reads_entries_parties_and_return_reasons()
    {
        var s = Camt053Reader.Read(Statement(Money.FromEuros(10_000m), Settled, Returned, Incoming)).Single();
        Assert.Equal("STMT-1", s.StatementId);
        Assert.Equal(Nostro.Value, s.AccountIban);
        Assert.Equal(Money.FromEuros(10_000m), s.OpeningBalance);
        Assert.Equal(Money.FromEuros(10_000m - 1150m + 84.5m + 2840m), s.ClosingBalance);
        Assert.True(s.BalancesAgree);

        var debit = s.Entries[0];
        Assert.False(debit.IsCredit);
        Assert.Equal("E2E-1", debit.Transactions.Single().EndToEndId);
        Assert.Equal("Hausverwaltung", debit.Transactions.Single().CounterpartyName);

        Assert.Equal("AC04", s.Entries[1].Transactions.Single().ReturnReason);

        var credit = s.Entries[2].Transactions.Single();
        Assert.Null(credit.EndToEndId); // NOTPROVIDED
        Assert.Equal("Arbeitgeber AG", credit.CounterpartyName);
        Assert.Equal("DE02099999001000000001", credit.OwnIban);
        Assert.Equal("Gehalt November", credit.RemittanceInformation);
    }

    [Fact]
    public void Reads_the_older_camt_053_001_02_layout()
    {
        // v02: <Sts> holds the code directly and parties have <Nm> without the <Pty> wrapper. Pending entries are skipped.
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Document xmlns="urn:iso:std:iso:20022:tech:xsd:camt.053.001.02">
              <BkToCstmrStmt>
                <GrpHdr><MsgId>OLD1</MsgId><CreDtTm>2026-11-10T18:00:00</CreDtTm></GrpHdr>
                <Stmt>
                  <Id>OLD-STMT</Id>
                  <Acct><Id><IBAN>DE89370400440532013000</IBAN></Id></Acct>
                  <Ntry>
                    <Amt Ccy="EUR">250.00</Amt><CdtDbtInd>CRDT</CdtDbtInd><Sts>BOOK</Sts>
                    <BookgDt><Dt>2026-11-10</Dt></BookgDt>
                    <NtryDtls>
                      <TxDtls><Refs><EndToEndId>A</EndToEndId></Refs><Amt Ccy="EUR">100.00</Amt>
                        <RltdPties><Dbtr><Nm>Erika Mustermann</Nm></Dbtr></RltdPties></TxDtls>
                      <TxDtls><Refs><EndToEndId>B</EndToEndId></Refs><Amt Ccy="EUR">150.00</Amt></TxDtls>
                    </NtryDtls>
                  </Ntry>
                  <Ntry><Amt Ccy="EUR">5.00</Amt><CdtDbtInd>DBIT</CdtDbtInd><Sts>PDNG</Sts><BookgDt><Dt>2026-11-10</Dt></BookgDt></Ntry>
                </Stmt>
              </BkToCstmrStmt>
            </Document>
            """;
        var entry = Camt053Reader.Read(xml).Single().Entries.Single();
        Assert.Equal(2, entry.Transactions.Count);
        Assert.Equal("Erika Mustermann", entry.Transactions[0].CounterpartyName);
        Assert.Equal(Money.FromEuros(150m), entry.Transactions[1].Amount);
    }

    [Fact]
    public void Detects_a_statement_whose_balances_do_not_add_up()
    {
        var xml = Statement(Money.FromEuros(1000m), Settled).Replace("<Amt Ccy=\"EUR\">1150.00</Amt>", "<Amt Ccy=\"EUR\">1100.00</Amt>", StringComparison.Ordinal);
        Assert.False(Camt053Reader.Read(xml).Single().BalancesAgree);
    }

    [Fact]
    public void Rejects_foreign_currency_dtds_and_other_documents()
    {
        Assert.Throws<SepaException>(() => Camt053Reader.Read(Statement(Money.Zero, Incoming).Replace("Ccy=\"EUR\"", "Ccy=\"USD\"", StringComparison.Ordinal)));
        Assert.Throws<SepaException>(() => Camt053Reader.Read("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><Document>&e;</Document>"));
        Assert.Throws<SepaException>(() => Camt053Reader.Read("<Document xmlns=\"urn:iso:std:iso:20022:tech:xsd:pain.001.001.09\"><CstmrCdtTrfInitn/></Document>"));
        Assert.Throws<SepaException>(() => Camt053Reader.Read("not xml"));
    }

    [Fact]
    public void Return_reasons_are_described()
    {
        Assert.Equal("AC04: Account closed", ReturnReasons.Describe("AC04"));
        Assert.Equal("ZZ99: unknown reason code", ReturnReasons.Describe("ZZ99"));
    }
}
