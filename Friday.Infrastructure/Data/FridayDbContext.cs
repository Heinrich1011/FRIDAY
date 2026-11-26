using Friday.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Friday.Infrastructure.Data;

public class FridayDbContext : DbContext
{
    public FridayDbContext(DbContextOptions<FridayDbContext> options) : base(options)
    {
    }

    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<ScheduleEvent> ScheduleEvents => Set<ScheduleEvent>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<MonthlyBudget> MonthlyBudgets => Set<MonthlyBudget>();
    public DbSet<HealthRecord> HealthRecords => Set<HealthRecord>();
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<ChatMessageRecord> ChatMessages => Set<ChatMessageRecord>();
    public DbSet<PendingConfirmation> PendingConfirmations => Set<PendingConfirmation>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        var utcConverter = new ValueConverter<DateTime, DateTime>(
            v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

        var nullableUtcConverter = new ValueConverter<DateTime?, DateTime?>(
            v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v.Value : v.Value.ToUniversalTime()) : v,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime))
                {
                    property.SetValueConverter(utcConverter);
                }
                else if (property.ClrType == typeof(DateTime?))
                {
                    property.SetValueConverter(nullableUtcConverter);
                }
            }
        }

        modelBuilder.Entity<Project>(e =>
        {
            e.HasIndex(p => p.Name);
            e.HasIndex(p => p.Status);
            e.HasIndex(p => p.Priority);
            e.HasIndex(p => p.TargetDate);
        });

        modelBuilder.Entity<TaskItem>(e =>
        {
            e.HasOne(t => t.Project)
                .WithMany(p => p.Tasks)
                .HasForeignKey(t => t.ProjectId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(t => t.Title);
            e.HasIndex(t => t.Status);
            e.HasIndex(t => t.Priority);
            e.HasIndex(t => t.DueDate);
            e.HasIndex(t => t.CompletedAt);
            e.HasIndex(t => t.Category);
        });

        modelBuilder.Entity<ScheduleEvent>(e =>
        {
            e.HasIndex(s => s.StartDateTime);
            e.HasIndex(s => s.Title);
            e.HasIndex(s => s.Category);
        });

        modelBuilder.Entity<Note>(e =>
        {
            e.HasOne(n => n.Project)
                .WithMany(p => p.LinkedNotes)
                .HasForeignKey(n => n.ProjectId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(n => n.Title);
            e.HasIndex(n => n.Category);
            e.HasIndex(n => n.UpdatedAt);
        });

        modelBuilder.Entity<Expense>(e =>
        {
            e.Property(x => x.Amount).HasColumnType(" decimal(18,2) ");
            e.HasIndex(x => x.Date);
            e.HasIndex(x => x.Category);
        });

        modelBuilder.Entity<MonthlyBudget>(e =>
        {
            e.Property(x => x.MonthlyLimit).HasColumnType(" decimal(18,2) ");
            e.HasIndex(x => new { x.Category, x.Year, x.Month }).IsUnique();
        });

        modelBuilder.Entity<HealthRecord>(e =>
        {
            e.HasIndex(h => h.Date);
        });

        modelBuilder.Entity<ChatSession>(e =>
        {
            e.HasIndex(s => s.UpdatedAt);
        });

        modelBuilder.Entity<ChatMessageRecord>(e =>
        {
            e.HasOne(m => m.ChatSession)
                .WithMany(s => s.Messages)
                .HasForeignKey(m => m.ChatSessionId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(m => new { m.ChatSessionId, m.CreatedAt });
        });

        modelBuilder.Entity<PendingConfirmation>(e =>
        {
            e.HasOne(c => c.ChatSession)
                .WithMany(s => s.PendingConfirmations)
                .HasForeignKey(c => c.ChatSessionId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(c => new { c.ChatSessionId, c.IsResolved });
        });

        modelBuilder.Entity<AuditLog>(e =>
        {
            e.HasIndex(a => a.CreatedAt);
            e.HasIndex(a => a.CorrelationId);
        });
    }
}

