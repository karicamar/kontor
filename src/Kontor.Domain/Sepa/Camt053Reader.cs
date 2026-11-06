using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Kontor.Domain.Banking;

namespace Kontor.Domain.Sepa;

/// <summary>One transaction inside a statement entry (an entry holds several when the bank books a batch as one line).</summary>
public sealed record StatementTransaction(
    Money Amount,
    string? EndToEndId,
    string? CounterpartyName,
    string? CounterpartyIban,
    /// <summary>For credits: the IBAN the payer sent to (a virtual IBAN identifies the customer). For debits: our own account.</summary>
    string? OwnIban,
    string? RemittanceInformation,
    /// <summary>ISO 20022 return reason (AC04 "account closed", ...) when this is a returned transfer.</summary>
    string? ReturnReason);

public sealed record StatementEntry(
    string? Reference,
    Money Amount,
    bool IsCredit,
    bool IsReversal,
    DateOnly BookingDate,
    DateOnly? ValueDate,
    IReadOnlyList<StatementTransaction> Transactions);

public sealed record BankStatement(
    string MessageId,
    string StatementId,
    string? AccountIban,
    Money? OpeningBalance,
    Money? ClosingBalance,
    IReadOnlyList<StatementEntry> Entries)
{
    /// <summary>Opening balance + credits − debits = closing balance. A statement where this fails is incomplete or corrupt.</summary>
    public bool BalancesAgree =>
        OpeningBalance is null || ClosingBalance is null
        || OpeningBalance.Value + Entries.Aggregate(Money.Zero, (s, e) => s + (e.IsCredit ? e.Amount : -e.Amount)) == ClosingBalance.Value;
}

/// <summary>
/// Reads ISO 20022 bank-to-customer statements, <c>camt.053</c> (end-of-day account statement; versions 001.02 to 001.10,
/// the elements used here did not change). Only booked entries are returned. Namespaces are ignored on purpose, so
/// every version parses with the same code.
/// </summary>
public static class Camt053Reader
{
    public static IReadOnlyList<BankStatement> Read(Stream xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }; // no XXE
        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(xml, settings);
            doc = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new SepaException($"Not a valid XML file: {ex.Message}");
        }

        return new Parser(doc.Root?.Name.Namespace ?? XNamespace.None).Parse(doc);
    }

    private sealed class Parser(XNamespace ns)
    {
    public IReadOnlyList<BankStatement> Parse(XDocument doc)
    {
        var root = doc.Root?.Element(L("BkToCstmrStmt"))
            ?? throw new SepaException("Not a camt.053 statement (BkToCstmrStmt is missing).");
        var messageId = Text(root.Element(L("GrpHdr")), "MsgId") ?? throw new SepaException("GrpHdr/MsgId is missing.");

        return root.Elements(L("Stmt")).Select(stmt => new BankStatement(
            messageId,
            Text(stmt, "Id") ?? throw new SepaException("Stmt/Id is missing."),
            Text(stmt.Element(L("Acct"))?.Element(L("Id")), "IBAN"),
            Balance(stmt, "OPBD") ?? Balance(stmt, "PRCD"),
            Balance(stmt, "CLBD"),
            stmt.Elements(L("Ntry")).Where(IsBooked).Select(Entry).ToList())).ToList();
    }

    private StatementEntry Entry(XElement ntry)
    {
        var amount = Amount(ntry.Element(L("Amt"))) ?? throw new SepaException("Ntry/Amt is missing.");
        var credit = Text(ntry, "CdtDbtInd") == "CRDT";
        var details = ntry.Element(L("NtryDtls"))?.Elements(L("TxDtls")).ToList() ?? [];
        var transactions = details.Count == 0
            ? [new StatementTransaction(amount, null, null, null, null, Text(ntry, "AddtlNtryInf"), null)]
            : details.Select(tx => Transaction(tx, credit, details.Count == 1 ? amount : null)).ToList();

        return new StatementEntry(
            Text(ntry, "AcctSvcrRef") ?? Text(ntry, "NtryRef"),
            amount,
            credit,
            Text(ntry, "RvslInd") == "true",
            Date(ntry.Element(L("BookgDt"))) ?? throw new SepaException("Ntry/BookgDt is missing."),
            Date(ntry.Element(L("ValDt"))),
            transactions);
    }

    private StatementTransaction Transaction(XElement tx, bool credit, Money? entryAmount)
    {
        var parties = tx.Element(L("RltdPties"));
        // camt.053.001.08+ wraps the party in <Pty>; older versions put <Nm> directly under <Dbtr>/<Cdtr>
        string? Name(string role) => parties?.Element(L(role)) is { } p ? Text(p.Element(L("Pty")) ?? p, "Nm") : null;
        string? IbanOf(string role) => Text(parties?.Element(L(role))?.Element(L("Id")), "IBAN");

        var endToEnd = Text(tx.Element(L("Refs")), "EndToEndId");
        var remittance = tx.Element(L("RmtInf"))?.Elements(L("Ustrd")).Select(u => u.Value.Trim()).ToList();
        return new StatementTransaction(
            Amount(tx.Element(L("Amt"))) ?? entryAmount ?? throw new SepaException("TxDtls/Amt is missing in a batch entry."),
            endToEnd is null or "NOTPROVIDED" ? null : endToEnd,
            Name(credit ? "Dbtr" : "Cdtr"),
            IbanOf(credit ? "DbtrAcct" : "CdtrAcct"),
            IbanOf(credit ? "CdtrAcct" : "DbtrAcct"),
            remittance is { Count: > 0 } ? string.Join(" ", remittance) : null,
            Text(tx.Element(L("RtrInf"))?.Element(L("Rsn")), "Cd"));
    }

    private bool IsBooked(XElement ntry)
    {
        var status = ntry.Element(L("Sts"));
        // v02-v06: <Sts>BOOK</Sts>, v07+: <Sts><Cd>BOOK</Cd></Sts>
        var code = status?.Element(L("Cd"))?.Value ?? status?.Value;
        return code?.Trim() == "BOOK";
    }

    private Money? Balance(XElement stmt, string type)
    {
        var bal = stmt.Elements(L("Bal")).FirstOrDefault(b => Text(b.Element(L("Tp"))?.Element(L("CdOrPrtry")), "Cd") == type);
        if (bal is null || Amount(bal.Element(L("Amt"))) is not { } amount) return null;
        return Text(bal, "CdtDbtInd") == "DBIT" ? -amount : amount;
    }

    private Money? Amount(XElement? amt)
    {
        if (amt is null) return null;
        if ((string?)amt.Attribute("Ccy") is { } ccy && ccy != "EUR")
            throw new SepaException($"Only EUR is supported, the statement contains {ccy}.");
        if (!decimal.TryParse(amt.Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || !Money.TryFromEuros(value, out var money))
            throw new SepaException($"Invalid amount \"{amt.Value}\".");
        return money;
    }

    private DateOnly? Date(XElement? container)
    {
        var text = Text(container, "Dt") ?? Text(container, "DtTm");
        if (text is null) return null;
        return DateOnly.ParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private string? Text(XElement? parent, string name)
    {
        var value = parent?.Element(L(name))?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    // Elements are looked up in the namespace of the document, whichever camt.053 version it is
    private XName L(string localName) => ns + localName;
    }

    /// <summary>Reads from a string (tests, small files).</summary>
    public static IReadOnlyList<BankStatement> Read(string xml) => Read(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml)));
}
