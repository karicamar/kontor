using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Kontor.Api.Data;

public class KontorDb(DbContextOptions<KontorDb> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<PostingRow> Postings => Set<PostingRow>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<SepaBatch> Batches => Set<SepaBatch>();
    public DbSet<StatementImport> StatementImports => Set<StatementImport>();
    public DbSet<OpenItem> OpenItems => Set<OpenItem>();
    public DbSet<AuditRecord> Audit => Set<AuditRecord>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // SQLite has no DateTimeOffset type: store it as a sortable number so ORDER BY and range filters work in SQL
        builder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        builder.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Account>(e =>
        {
            e.HasIndex(a => a.Iban).IsUnique();
            e.HasIndex(a => a.Code).IsUnique();
            e.Property(a => a.Version).IsConcurrencyToken();
            e.Property(a => a.Holder).HasMaxLength(140);
            e.Property(a => a.Iban).HasMaxLength(34);
            e.Property(a => a.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(a => a.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(a => a.Side).HasConversion<string>().HasMaxLength(16);
            e.Ignore(a => a.AvailableCents);
        });
        b.Entity<JournalEntry>(e =>
        {
            e.HasMany(j => j.Postings).WithOne().HasForeignKey(p => p.JournalEntryId);
            e.HasIndex(j => j.PaymentId);
            e.Property(j => j.Kind).HasConversion<string>().HasMaxLength(32);
        });
        b.Entity<PostingRow>(e =>
        {
            e.HasIndex(p => p.AccountId);
            e.HasOne<Account>().WithMany().HasForeignKey(p => p.AccountId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Payment>(e =>
        {
            e.HasIndex(p => p.EndToEndId).IsUnique();
            // The same key from the same user always means the same payment, even when two requests race
            e.HasIndex(p => new { p.CreatedBy, p.IdempotencyKey }).IsUnique();
            e.HasIndex(p => new { p.Status, p.ExecutionDate });
            e.HasIndex(p => new { p.DebtorAccountId, p.CreatedAt });
            e.Property(p => p.Version).IsConcurrencyToken();
            e.Property(p => p.Status).HasConversion<string>().HasMaxLength(32);
            e.HasOne<Account>().WithMany().HasForeignKey(p => p.DebtorAccountId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<SepaBatch>().HasIndex(x => x.MessageId).IsUnique();
        b.Entity<StatementImport>().HasIndex(x => x.StatementId).IsUnique();
        b.Entity<AuditRecord>().HasKey(a => a.Sequence);
        b.Entity<AuditRecord>().Property(a => a.Sequence).ValueGeneratedNever();
    }
}
