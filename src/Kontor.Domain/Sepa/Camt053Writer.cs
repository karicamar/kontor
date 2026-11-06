using System.Globalization;
using System.Text;
using System.Xml;
using Kontor.Domain.Banking;

namespace Kontor.Domain.Sepa;

/// <summary>A statement line to write: what a bank reports about one booking on the account.</summary>
public sealed record StatementLine(
    string Reference,
    Money Amount,
    bool IsCredit,
    DateOnly BookingDate,
    string? EndToEndId,
    string? CounterpartyName,
    string? CounterpartyIban,
    string? OwnIban,
    string? RemittanceInformation,
    string? ReturnReason = null);

/// <summary>
/// Writes a <c>camt.053.001.08</c> end-of-day statement. Kontor itself only reads statements; this writer is the
/// "partner bank" in the demo and the tests, so the full cycle (pain.001 out, camt.053 back) can run without a real bank.
/// </summary>
public static class Camt053Writer
{
    public const string Namespace = "urn:iso:std:iso:20022:tech:xsd:camt.053.001.08";

    public static string Write(string messageId, string statementId, DateTimeOffset createdAt, Iban account, Money openingBalance,
        DateOnly date, IReadOnlyList<StatementLine> lines)
    {
        var closing = lines.Aggregate(openingBalance, (s, l) => l.IsCredit ? s + l.Amount : s - l.Amount);
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(new Utf8Writer(sb), new XmlWriterSettings { Indent = true }))
        {
            w.WriteStartDocument();
            w.WriteStartElement("Document", Namespace);
            w.WriteStartElement("BkToCstmrStmt");
            w.WriteStartElement("GrpHdr");
            w.WriteElementString("MsgId", messageId);
            w.WriteElementString("CreDtTm", createdAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            w.WriteEndElement();

            w.WriteStartElement("Stmt");
            w.WriteElementString("Id", statementId);
            w.WriteElementString("CreDtTm", createdAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            w.WriteStartElement("Acct");
            w.WriteStartElement("Id");
            w.WriteElementString("IBAN", account.Value);
            w.WriteEndElement();
            w.WriteElementString("Ccy", "EUR");
            w.WriteEndElement();
            WriteBalance(w, "OPBD", openingBalance, date);
            WriteBalance(w, "CLBD", closing, date);

            foreach (var l in lines)
            {
                w.WriteStartElement("Ntry");
                WriteAmount(w, "Amt", l.Amount);
                w.WriteElementString("CdtDbtInd", l.IsCredit ? "CRDT" : "DBIT");
                w.WriteStartElement("Sts");
                w.WriteElementString("Cd", "BOOK");
                w.WriteEndElement();
                WriteDate(w, "BookgDt", l.BookingDate);
                WriteDate(w, "ValDt", l.BookingDate);
                w.WriteElementString("AcctSvcrRef", l.Reference);
                w.WriteStartElement("BkTxCd");
                w.WriteStartElement("Domn");
                w.WriteElementString("Cd", "PMNT");
                w.WriteStartElement("Fmly");
                w.WriteElementString("Cd", l.IsCredit ? "RCDT" : "ICDT");
                w.WriteElementString("SubFmlyCd", l.ReturnReason is null ? "ESCT" : "RRTN");
                w.WriteEndElement();
                w.WriteEndElement();
                w.WriteEndElement();

                w.WriteStartElement("NtryDtls");
                w.WriteStartElement("TxDtls");
                w.WriteStartElement("Refs");
                w.WriteElementString("EndToEndId", l.EndToEndId ?? "NOTPROVIDED");
                w.WriteEndElement();
                WriteAmount(w, "Amt", l.Amount);
                w.WriteStartElement("RltdPties");
                // For a credit the counterparty paid us (it is the debtor); for a debit we paid it (creditor).
                // The schema fixes the order: Dbtr, DbtrAcct, Cdtr, CdtrAcct
                if (l.IsCredit)
                {
                    WriteParty(w, "Dbtr", l.CounterpartyName);
                    if (l.CounterpartyIban is not null) WriteIban(w, "DbtrAcct", l.CounterpartyIban);
                    if (l.OwnIban is not null) WriteIban(w, "CdtrAcct", l.OwnIban);
                }
                else
                {
                    if (l.OwnIban is not null) WriteIban(w, "DbtrAcct", l.OwnIban);
                    WriteParty(w, "Cdtr", l.CounterpartyName);
                    if (l.CounterpartyIban is not null) WriteIban(w, "CdtrAcct", l.CounterpartyIban);
                }
                w.WriteEndElement(); // RltdPties
                if (l.RemittanceInformation is not null)
                {
                    w.WriteStartElement("RmtInf");
                    w.WriteElementString("Ustrd", l.RemittanceInformation);
                    w.WriteEndElement();
                }
                if (l.ReturnReason is not null)
                {
                    w.WriteStartElement("RtrInf");
                    w.WriteStartElement("Rsn");
                    w.WriteElementString("Cd", l.ReturnReason);
                    w.WriteEndElement();
                    w.WriteEndElement();
                }
                w.WriteEndElement(); // TxDtls
                w.WriteEndElement(); // NtryDtls
                w.WriteEndElement(); // Ntry
            }

            w.WriteEndElement(); // Stmt
            w.WriteEndElement(); // BkToCstmrStmt
            w.WriteEndElement(); // Document
        }
        return sb.ToString();
    }

    private static void WriteBalance(XmlWriter w, string type, Money amount, DateOnly date)
    {
        w.WriteStartElement("Bal");
        w.WriteStartElement("Tp");
        w.WriteStartElement("CdOrPrtry");
        w.WriteElementString("Cd", type);
        w.WriteEndElement();
        w.WriteEndElement();
        WriteAmount(w, "Amt", amount.Cents < 0 ? -amount : amount);
        w.WriteElementString("CdtDbtInd", amount.Cents < 0 ? "DBIT" : "CRDT");
        WriteDate(w, "Dt", date);
        w.WriteEndElement();
    }

    private static void WriteAmount(XmlWriter w, string element, Money amount)
    {
        w.WriteStartElement(element);
        w.WriteAttributeString("Ccy", "EUR");
        w.WriteString(amount.ToXml());
        w.WriteEndElement();
    }

    private static void WriteDate(XmlWriter w, string element, DateOnly date)
    {
        w.WriteStartElement(element);
        w.WriteElementString("Dt", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        w.WriteEndElement();
    }

    private static void WriteParty(XmlWriter w, string element, string? name)
    {
        if (name is null) return;
        w.WriteStartElement(element);
        w.WriteStartElement("Pty");
        w.WriteElementString("Nm", name);
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void WriteIban(XmlWriter w, string element, string iban)
    {
        w.WriteStartElement(element);
        w.WriteStartElement("Id");
        w.WriteElementString("IBAN", iban);
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private sealed class Utf8Writer(StringBuilder sb) : StringWriter(sb, CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
