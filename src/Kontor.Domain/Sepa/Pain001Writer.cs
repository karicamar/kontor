using System.Globalization;
using System.Text;
using System.Xml;
using Kontor.Domain.Banking;

namespace Kontor.Domain.Sepa;

public sealed record SepaAccount(string Name, Iban Iban, Bic? Bic);

public sealed record CreditTransfer(
    string EndToEndId,
    Money Amount,
    string CreditorName,
    Iban CreditorIban,
    Bic? CreditorBic,
    string? UltimateDebtor,
    string? RemittanceInformation);

/// <summary>One payment file: all transfers debited from <see cref="Debtor"/> on <see cref="ExecutionDate"/>.</summary>
public sealed record CreditTransferBatch(
    string MessageId,
    DateTimeOffset CreatedAt,
    SepaAccount Debtor,
    DateOnly ExecutionDate,
    IReadOnlyList<CreditTransfer> Transfers);

/// <summary>
/// Writes a SEPA credit transfer initiation, ISO 20022 <c>pain.001.001.09</c> (the version the German banking industry's
/// DFÜ agreement, Annex 3, has required since November 2023). The platform's account at its partner bank is the debtor;
/// the customer on whose behalf a transfer is made is the <c>UltmtDbtr</c>, the way payment platforms on top of a
/// partner bank send their customers' transfers.
/// </summary>
public static class Pain001Writer
{
    public const string Namespace = "urn:iso:std:iso:20022:tech:xsd:pain.001.001.09";

    public static string Write(CreditTransferBatch batch)
    {
        Validate(batch);
        var total = batch.Transfers.Aggregate(Money.Zero, (s, t) => s + t.Amount);
        var count = batch.Transfers.Count.ToString(CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using (var w = XmlWriter.Create(new Utf8StringWriter(sb), settings))
        {
            w.WriteStartDocument();
            w.WriteStartElement("Document", Namespace);
            w.WriteStartElement("CstmrCdtTrfInitn");

            w.WriteStartElement("GrpHdr");
            w.WriteElementString("MsgId", batch.MessageId);
            w.WriteElementString("CreDtTm", batch.CreatedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            w.WriteElementString("NbOfTxs", count);
            w.WriteElementString("CtrlSum", total.ToXml());
            w.WriteStartElement("InitgPty");
            w.WriteElementString("Nm", SepaText.Clean(batch.Debtor.Name, SepaText.MaxNameLength));
            w.WriteEndElement();
            w.WriteEndElement(); // GrpHdr

            w.WriteStartElement("PmtInf");
            w.WriteElementString("PmtInfId", batch.MessageId);
            w.WriteElementString("PmtMtd", "TRF");
            w.WriteElementString("BtchBookg", "false"); // one statement entry per transfer, so camt.053 can be matched by EndToEndId
            w.WriteElementString("NbOfTxs", count);
            w.WriteElementString("CtrlSum", total.ToXml());
            w.WriteStartElement("PmtTpInf");
            w.WriteStartElement("SvcLvl");
            w.WriteElementString("Cd", "SEPA");
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteStartElement("ReqdExctnDt");
            w.WriteElementString("Dt", batch.ExecutionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            w.WriteEndElement();
            w.WriteStartElement("Dbtr");
            w.WriteElementString("Nm", SepaText.Clean(batch.Debtor.Name, SepaText.MaxNameLength));
            w.WriteEndElement();
            WriteAccount(w, "DbtrAcct", batch.Debtor.Iban);
            WriteAgent(w, "DbtrAgt", batch.Debtor.Bic, required: true);
            w.WriteElementString("ChrgBr", "SLEV");

            foreach (var t in batch.Transfers)
            {
                w.WriteStartElement("CdtTrfTxInf");
                w.WriteStartElement("PmtId");
                w.WriteElementString("EndToEndId", t.EndToEndId);
                w.WriteEndElement();
                w.WriteStartElement("Amt");
                w.WriteStartElement("InstdAmt");
                w.WriteAttributeString("Ccy", "EUR");
                w.WriteString(t.Amount.ToXml());
                w.WriteEndElement();
                w.WriteEndElement();
                if (!string.IsNullOrWhiteSpace(t.UltimateDebtor))
                {
                    w.WriteStartElement("UltmtDbtr");
                    w.WriteElementString("Nm", SepaText.Clean(t.UltimateDebtor, SepaText.MaxNameLength));
                    w.WriteEndElement();
                }
                WriteAgent(w, "CdtrAgt", t.CreditorBic, required: false);
                w.WriteStartElement("Cdtr");
                w.WriteElementString("Nm", SepaText.Clean(t.CreditorName, SepaText.MaxNameLength));
                w.WriteEndElement();
                WriteAccount(w, "CdtrAcct", t.CreditorIban);
                var remittance = SepaText.Clean(t.RemittanceInformation, SepaText.MaxRemittanceLength);
                if (remittance.Length > 0)
                {
                    w.WriteStartElement("RmtInf");
                    w.WriteElementString("Ustrd", remittance);
                    w.WriteEndElement();
                }
                w.WriteEndElement(); // CdtTrfTxInf
            }

            w.WriteEndElement(); // PmtInf
            w.WriteEndElement(); // CstmrCdtTrfInitn
            w.WriteEndElement(); // Document
        }
        return sb.ToString();
    }

    private static void WriteAccount(XmlWriter w, string element, Iban iban)
    {
        w.WriteStartElement(element);
        w.WriteStartElement("Id");
        w.WriteElementString("IBAN", iban.Value);
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void WriteAgent(XmlWriter w, string element, Bic? bic, bool required)
    {
        if (bic is null && !required) return;
        w.WriteStartElement(element);
        w.WriteStartElement("FinInstnId");
        if (bic is not null)
            w.WriteElementString("BICFI", bic.Value);
        else
        {
            // IBAN-only: the debtor agent is still a mandatory element, "NOTPROVIDED" is the agreed placeholder
            w.WriteStartElement("Othr");
            w.WriteElementString("Id", "NOTPROVIDED");
            w.WriteEndElement();
        }
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void Validate(CreditTransferBatch batch)
    {
        if (batch.Transfers.Count == 0) throw new SepaException("A payment file needs at least one transfer.");
        if (!SepaText.IsValidId(batch.MessageId)) throw new SepaException($"Invalid message id \"{batch.MessageId}\".");
        if (!batch.Debtor.Iban.IsSepa) throw new SepaException("Debtor account is outside SEPA.");
        foreach (var t in batch.Transfers)
        {
            if (!SepaText.IsValidId(t.EndToEndId)) throw new SepaException($"Invalid EndToEndId \"{t.EndToEndId}\".");
            if (!t.Amount.IsPositive || t.Amount > Money.SepaMaximum) throw new SepaException($"{t.EndToEndId}: amount out of range.");
            if (!t.CreditorIban.IsSepa) throw new SepaException($"{t.EndToEndId}: {t.CreditorIban.CountryCode} is not reachable by SEPA.");
            if (SepaText.Clean(t.CreditorName, SepaText.MaxNameLength).Length == 0) throw new SepaException($"{t.EndToEndId}: creditor name is empty.");
        }
        if (batch.Transfers.GroupBy(t => t.EndToEndId).Any(g => g.Count() > 1))
            throw new SepaException("EndToEndIds must be unique within a file.");
    }

    private sealed class Utf8StringWriter(StringBuilder sb) : StringWriter(sb, CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }
}

public sealed class SepaException(string message) : Exception(message);
