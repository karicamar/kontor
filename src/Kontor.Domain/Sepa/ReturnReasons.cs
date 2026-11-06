namespace Kontor.Domain.Sepa;

/// <summary>The ISO 20022 / EPC reason codes a receiving bank gives when it sends a SEPA transfer back.</summary>
public static class ReturnReasons
{
    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.Ordinal)
    {
        ["AC01"] = "Account identifier incorrect (invalid IBAN)",
        ["AC04"] = "Account closed",
        ["AC06"] = "Account blocked",
        ["AG01"] = "Transaction forbidden on this type of account",
        ["AG02"] = "Invalid bank operation code",
        ["AM05"] = "Duplicate payment",
        ["BE04"] = "Creditor address missing or incorrect",
        ["CNOR"] = "Creditor bank is not registered for this scheme",
        ["FF01"] = "Invalid file format",
        ["FOCR"] = "Returned after a cancellation request",
        ["MD07"] = "Account holder deceased",
        ["MS02"] = "Not specified, reason given by the customer",
        ["MS03"] = "Not specified, reason given by the bank",
        ["RC01"] = "Bank identifier (BIC) incorrect",
        ["RR01"] = "Regulatory reason: debtor account or identification missing",
        ["RR02"] = "Regulatory reason: debtor name or address missing",
        ["RR03"] = "Regulatory reason: creditor name or address missing",
        ["RR04"] = "Regulatory reason",
    };

    public static string Describe(string? code) =>
        code is null ? "No reason given" : Descriptions.TryGetValue(code, out var text) ? $"{code}: {text}" : $"{code}: unknown reason code";
}
