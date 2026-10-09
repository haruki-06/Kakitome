using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Kakitome.Storage.Persistence;

/// <summary>
/// App state and derived indexes. Library content is never stored only here: every <c>Recording*</c> /
/// <c>Artifact*</c> / <c>LibraryIssue*</c> row can be rebuilt from the Library files.
/// </summary>
public sealed class KakitomeDbContext(DbContextOptions<KakitomeDbContext> options) : DbContext(options)
{
    public DbSet<RecordingRow> Recordings => Set<RecordingRow>();

    public DbSet<RecordingTagRow> RecordingTags => Set<RecordingTagRow>();

    public DbSet<ArtifactRow> Artifacts => Set<ArtifactRow>();

    public DbSet<LibraryIssueRow> LibraryIssues => Set<LibraryIssueRow>();

    /// <summary>Durable jobs (app state, not derived; survives restarts, cleared by Factory Reset).</summary>
    public DbSet<JobRow> Jobs => Set<JobRow>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // SQLite cannot order/compare DateTimeOffset stored as text; store UTC ticks + offset in one integer.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<RecordingRow>(e =>
        {
            e.ToTable("Recordings");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasMaxLength(36);
            e.HasIndex(r => r.Folder).IsUnique();
            e.HasIndex(r => r.Project);
            e.HasIndex(r => r.CreatedAtUtcTicks);
            e.HasMany(r => r.Tags).WithOne().HasForeignKey(t => t.RecordingId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RecordingTagRow>(e =>
        {
            e.ToTable("RecordingTags");
            e.HasKey(t => new { t.RecordingId, t.Tag });
            e.HasIndex(t => t.Tag);
        });

        modelBuilder.Entity<ArtifactRow>(e =>
        {
            // No FK: stamps are recorded as files are written, which can precede the recording row.
            e.ToTable("Artifacts");
            e.HasKey(a => new { a.RecordingId, a.FileName });
        });

        modelBuilder.Entity<LibraryIssueRow>(e =>
        {
            e.ToTable("LibraryIssues");
            e.HasKey(i => i.Id);
        });

        modelBuilder.Entity<JobRow>(e =>
        {
            e.ToTable("Jobs");
            e.HasKey(j => j.Id);
            e.Property(j => j.Kind).HasMaxLength(64);
            e.HasIndex(j => j.State);
            e.HasIndex(j => j.DependsOn);
            e.HasIndex(j => j.RecordingId);
        });
    }
}

public sealed class RecordingRow
{
    public required string Id { get; set; }

    /// <summary>Library-relative folder, '/'-separated.</summary>
    public required string Folder { get; set; }

    public required string Title { get; set; }

    public required string Project { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Sort key (UTC ticks of <see cref="CreatedAt"/>).</summary>
    public long CreatedAtUtcTicks { get; set; }

    public DateTimeOffset? RecordedAt { get; set; }

    public double? DurationSeconds { get; set; }

    public int SourceType { get; set; }

    public int? CaptureStatus { get; set; }

    public string? Language { get; set; }

    public bool HasAudio { get; set; }

    public bool HasTranscript { get; set; }

    public bool HasSummary { get; set; }

    public bool HasExternalChanges { get; set; }

    public List<RecordingTagRow> Tags { get; set; } = [];
}

public sealed class RecordingTagRow
{
    public required string RecordingId { get; set; }

    public required string Tag { get; set; }

    /// <summary>Position in metadata.json (tags keep their user-given order).</summary>
    public int Position { get; set; }
}

public sealed class ArtifactRow
{
    public required string RecordingId { get; set; }

    public required string FileName { get; set; }

    public long Length { get; set; }

    public long LastWriteUtcTicks { get; set; }

    public string? Sha256 { get; set; }
}

public sealed class LibraryIssueRow
{
    public long Id { get; set; }

    public int Kind { get; set; }

    public required string Folder { get; set; }

    public string? FileName { get; set; }

    public required string Message { get; set; }

    public DateTimeOffset DetectedAt { get; set; }
}

public sealed class JobRow
{
    public Guid Id { get; set; }

    public required string Kind { get; set; }

    public string? RecordingId { get; set; }

    public int State { get; set; }

    public int ResourceClass { get; set; }

    public int Priority { get; set; }

    public Guid? DependsOn { get; set; }

    public Guid? PipelineId { get; set; }

    public string? Payload { get; set; }

    public string? Checkpoint { get; set; }

    public int Attempts { get; set; }

    public int MaxAttempts { get; set; }

    public long CreatedAtUtcTicks { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public DateTimeOffset? NotBefore { get; set; }

    public double? Progress { get; set; }

    public string? ProgressText { get; set; }

    public int WaitReason { get; set; }

    public string? LastError { get; set; }

    public string? Engine { get; set; }
}

/// <summary>Used only by <c>dotnet ef migrations</c>.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<KakitomeDbContext>
{
    public KakitomeDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<KakitomeDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
