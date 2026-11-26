using System.Text.Json;
using Friday.Core.Entities;
using Friday.Core.Enums;
using Friday.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Friday.Infrastructure.Services;

/// <summary>
/// Provides privacy controls (full JSON export, health data purge) and OPTIONAL, explicitly marked,
/// one-click removable demo seed data for testing (never loaded automatically).
/// </summary>
public class PrivacyAndDataManagementService
{
    private readonly FridayDbContext _db;

    public PrivacyAndDataManagementService(FridayDbContext db)
    {
        _db = db;
    }

    public async Task<bool> HasDemoDataAsync(CancellationToken ct = default)
    {
        return await _db.Tasks.AnyAsync(t => t.IsDemoData, ct)
            || await _db.Projects.AnyAsync(p => p.IsDemoData, ct)
            || await _db.ScheduleEvents.AnyAsync(e => e.IsDemoData, ct)
            || await _db.Notes.AnyAsync(n => n.IsDemoData, ct)
            || await _db.Expenses.AnyAsync(x => x.IsDemoData, ct);
    }

    public async Task SeedMarkedDemoDataAsync(CancellationToken ct = default)
    {
        if (await HasDemoDataAsync(ct))
        {
            return;
        }

        var now = DateTime.UtcNow;

        var demoProject = new Project
        {
            Name = "[DEMO] Friday MCP Architecture Evaluation",
            Description = "Sample removable demo project for testing Friday's cross-module reasoning and charts.",
            Status = ProjectStatus.Active,
            Priority = PriorityLevel.High,
            StartDate = now.AddDays(-10),
            TargetDate = now.AddDays(14),
            Notes = "Clearly marked demo data — can be removed anytime in Profile & Privacy.",
            IsDemoData = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.Projects.Add(demoProject);
        await _db.SaveChangesAsync(ct);

        _db.Tasks.AddRange(
            new TaskItem
            {
                Title = "[DEMO] Review MCP tool safety classifications",
                Description = "Verify Read, Write, and Destructive tool boundaries.",
                Status = TaskItemStatus.Pending,
                Priority = PriorityLevel.High,
                DueDate = now.AddHours(4),
                Category = "Engineering",
                ProjectId = demoProject.Id,
                IsDemoData = true,
                CreatedAt = now.AddDays(-2),
                UpdatedAt = now
            },
            new TaskItem
            {
                Title = "[DEMO] Configure Groq User Secrets locally",
                Description = "Set Groq:ApiKey via dotnet user-secrets.",
                Status = TaskItemStatus.Completed,
                Priority = PriorityLevel.Medium,
                DueDate = now.AddDays(-1),
                CompletedAt = now.AddDays(-1),
                Category = "Setup",
                ProjectId = demoProject.Id,
                IsDemoData = true,
                CreatedAt = now.AddDays(-3),
                UpdatedAt = now.AddDays(-1)
            },
            new TaskItem
            {
                Title = "[DEMO] Prepare weekly demo summary",
                Description = "Overdue sample task to demonstrate Friday prioritization.",
                Status = TaskItemStatus.InProgress,
                Priority = PriorityLevel.Critical,
                DueDate = now.AddDays(-1),
                Category = "Reporting",
                ProjectId = demoProject.Id,
                IsDemoData = true,
                CreatedAt = now.AddDays(-4),
                UpdatedAt = now
            });

        _db.ScheduleEvents.Add(new ScheduleEvent
        {
            Title = "[DEMO] Architecture Check-in",
            Description = "Sample calendar event for testing schedule queries.",
            StartDateTime = now.AddHours(3),
            EndDateTime = now.AddHours(4),
            Location = "Workspace Room A",
            Category = "Meeting",
            Reminder = "15 minutes before",
            IsDemoData = true,
            CreatedAt = now,
            UpdatedAt = now
        });

        _db.Notes.Add(new Note
        {
            Title = "[DEMO] MCP Design Notes",
            Content = "Model Context Protocol (MCP) separates Groq reasoning from direct SQLite access using strongly validated Read, Write, and Destructive tools.",
            Category = "Architecture",
            Tags = "mcp,architecture,demo",
            ProjectId = demoProject.Id,
            IsDemoData = true,
            CreatedAt = now,
            UpdatedAt = now
        });

        _db.Expenses.AddRange(
            new Expense
            {
                Amount = 18.50m,
                Category = "Transportation",
                Description = "[DEMO] Commute pass",
                Date = now.AddDays(-2),
                PaymentMethod = "Card",
                IsDemoData = true,
                CreatedAt = now.AddDays(-2)
            },
            new Expense
            {
                Amount = 42.00m,
                Category = "Books & Learning",
                Description = "[DEMO] Technical reference guide",
                Date = now.AddDays(-1),
                PaymentMethod = "Card",
                IsDemoData = true,
                CreatedAt = now.AddDays(-1)
            });

        await _db.SaveChangesAsync(ct);
    }

    public async Task<int> RemoveAllDemoDataAsync(CancellationToken ct = default)
    {
        int removed = 0;
        removed += await _db.Tasks.Where(x => x.IsDemoData).ExecuteDeleteAsync(ct);
        removed += await _db.Notes.Where(x => x.IsDemoData).ExecuteDeleteAsync(ct);
        removed += await _db.ScheduleEvents.Where(x => x.IsDemoData).ExecuteDeleteAsync(ct);
        removed += await _db.Expenses.Where(x => x.IsDemoData).ExecuteDeleteAsync(ct);
        removed += await _db.HealthRecords.Where(x => x.IsDemoData).ExecuteDeleteAsync(ct);
        removed += await _db.Projects.Where(x => x.IsDemoData).ExecuteDeleteAsync(ct);
        return removed;
    }

    public async Task<int> DeleteAllHealthRecordsAsync(CancellationToken ct = default)
    {
        return await _db.HealthRecords.ExecuteDeleteAsync(ct);
    }

    public async Task<string> ExportAllPersonalDataJsonAsync(CancellationToken ct = default)
    {
        var payload = new
        {
            ExportedAtUtc = DateTime.UtcNow,
            Profile = await _db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(ct),
            Projects = await _db.Projects.AsNoTracking().ToListAsync(ct),
            Tasks = await _db.Tasks.AsNoTracking().ToListAsync(ct),
            ScheduleEvents = await _db.ScheduleEvents.AsNoTracking().ToListAsync(ct),
            Notes = await _db.Notes.AsNoTracking().ToListAsync(ct),
            Expenses = await _db.Expenses.AsNoTracking().ToListAsync(ct),
            Budgets = await _db.MonthlyBudgets.AsNoTracking().ToListAsync(ct),
            HealthRecords = await _db.HealthRecords.AsNoTracking().ToListAsync(ct)
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }
}

