using System.Security.Cryptography;
using System.Text;

namespace Kontor.Domain.Audit;

/// <summary>What is hashed for one audit record. The previous record's hash is part of it, which links the records into a chain.</summary>
public sealed record AuditFields(long Sequence, DateTimeOffset At, string Actor, string Action, string Subject, string Details);

/// <summary>
/// A tamper-evident audit trail: every record stores the SHA-256 hash of its own content plus the previous record's hash.
/// Changing, deleting or re-ordering any record breaks every hash after it, so <see cref="Verify"/> finds the first place
/// where someone edited the table directly. (It proves integrity, not completeness at the end: an attacker with write access
/// could cut off the newest records. Exporting the latest hash regularly, e.g. to the SIEM, covers that.)
/// </summary>
public static class AuditChain
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    public static string Hash(string previousHash, AuditFields f)
    {
        // Length-prefixed fields: "ab"+"c" and "a"+"bc" must not produce the same input
        var sb = new StringBuilder();
        foreach (var part in new[] { previousHash, f.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     f.At.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture), f.Actor, f.Action, f.Subject, f.Details })
            sb.Append(part.Length).Append(':').Append(part).Append('|');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public sealed record VerificationResult(bool Intact, long RecordsChecked, long? FirstBrokenSequence, string? Problem, string LastHash);

    public static VerificationResult Verify(IEnumerable<(AuditFields Fields, string PreviousHash, string Hash)> records)
    {
        var previous = GenesisHash;
        long expected = 1, count = 0;
        foreach (var (fields, storedPrevious, storedHash) in records)
        {
            count++;
            if (fields.Sequence != expected)
                return new(false, count, fields.Sequence, $"Expected record {expected}, found {fields.Sequence}: records were deleted or re-ordered", previous);
            if (storedPrevious != previous)
                return new(false, count, fields.Sequence, "Link to the previous record is broken", previous);
            if (Hash(previous, fields) != storedHash)
                return new(false, count, fields.Sequence, "Record content does not match its hash: it was changed after it was written", previous);
            previous = storedHash;
            expected++;
        }
        return new(true, count, null, null, previous);
    }
}
