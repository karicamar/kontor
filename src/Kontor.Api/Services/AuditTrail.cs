using System.Text.Json;
using Kontor.Api.Data;
using Kontor.Domain.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kontor.Api.Services;

/// <summary>
/// Appends hash-chained records to the audit table inside the caller's transaction, so a booking and its audit record are
/// saved together or not at all. After the commit the records are also written to an optional JSON-lines file for
/// the SIEM.
/// </summary>
public sealed class AuditTrail
{
    // Relaxed escaping keeps quotes and umlauts readable in the details; the dashboard inserts them as text, never as HTML
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions FileJson = new(JsonSerializerDefaults.Web) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly Lock FileLock = new();

    private readonly KontorDb _db;
    private readonly TimeProvider _time;
    private readonly string? _jsonlPath;
    private readonly List<AuditRecord> _pending = [];
    private (long Sequence, string Hash)? _last;

    public AuditTrail(KontorDb db, TimeProvider time, IOptions<KontorOptions> options)
    {
        _db = db;
        _time = time;
        _jsonlPath = options.Value.AuditJsonlPath;
    }

    public async Task AppendAsync(string actor, string action, string subject, object details, CancellationToken ct = default)
    {
        if (_last is null)
        {
            var last = await _db.Audit.OrderByDescending(a => a.Sequence).Select(a => new { a.Sequence, a.Hash }).FirstOrDefaultAsync(ct);
            _last = last is null ? (0, AuditChain.GenesisHash) : (last.Sequence, last.Hash);
        }

        // Whole milliseconds: the stored timestamp must hash exactly like the one in memory
        var now = _time.GetUtcNow();
        var at = new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
        var fields = new AuditFields(_last.Value.Sequence + 1, at, actor, action, subject, JsonSerializer.Serialize(details, Json));
        var hash = AuditChain.Hash(_last.Value.Hash, fields);
        var record = new AuditRecord
        {
            Sequence = fields.Sequence, At = fields.At, Actor = actor, Action = action, Subject = subject,
            Details = fields.Details, PreviousHash = _last.Value.Hash, Hash = hash,
        };
        _db.Audit.Add(record);
        _pending.Add(record);
        _last = (fields.Sequence, hash);
    }

    /// <summary>Forget the cached chain head; called when a new transaction starts.</summary>
    public void Reset()
    {
        _pending.Clear();
        _last = null;
    }

    public static AuditChain.VerificationResult Verify(IEnumerable<AuditRecord> records) =>
        AuditChain.Verify(records.Select(r => (new AuditFields(r.Sequence, r.At, r.Actor, r.Action, r.Subject, r.Details), r.PreviousHash, r.Hash)));

    /// <summary>Writes the records of the committed transaction to the JSON-lines file (called after commit).</summary>
    public void Flush()
    {
        if (_pending.Count == 0) return;
        if (_jsonlPath is not null)
        {
            var lines = _pending.Select(r => JsonSerializer.Serialize(new
            {
                timestamp = r.At.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture),
                source = "kontor",
                sequence = r.Sequence,
                actor = r.Actor,
                action = r.Action,
                subject = r.Subject,
                details = JsonDocument.Parse(r.Details).RootElement,
                hash = r.Hash,
            }, FileJson));
            lock (FileLock)
                File.AppendAllLines(_jsonlPath, lines);
        }
        _pending.Clear();
    }
}
