using Friday.Core.Entities;
using Friday.Core.Enums;
using Friday.Infrastructure.Data;
using FRIDAY.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FRIDAY.Controllers;

/// <summary>
/// Secondary manual navigation controller for inspecting and managing records directly.
/// Friday AI Chat (/) remains the primary natural-language interface.
/// </summary>
public class WorkspaceController : Controller
{
    private readonly FridayDbContext _db;

    public WorkspaceController(FridayDbContext db)
    {
        _db = db;
    }

    [HttpGet("/Workspace")]
    public async Task<IActionResult> Index(string tab = "tasks", string? status = null, CancellationToken ct = default)
    {
        var profile = await _db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(ct);
        var vm = new WorkspaceIndexViewModel
        {
            ActiveTab = (tab ?? "tasks").ToLowerInvariant(),
            CurrencyCode = profile?.CurrencyCode ?? "USD",
            StatusMessage = status,
            Tasks = await _db.Tasks.AsNoTracking().Include(t => t.Project)
                .OrderBy(t => t.Status == TaskItemStatus.Completed ? 1 : 0)
                .ThenByDescending(t => t.Priority)
                .ThenBy(t => t.DueDate ?? DateTime.MaxValue)
                .Take(100)
                .ToListAsync(ct),
            Projects = await _db.Projects.AsNoTracking().Include(p => p.Tasks)
                .OrderByDescending(p => p.Priority)
                .ToListAsync(ct),
            Events = await _db.ScheduleEvents.AsNoTracking()
                .OrderBy(e => e.StartDateTime)
                .Take(100)
                .ToListAsync(ct),
            Notes = await _db.Notes.AsNoTracking().Include(n => n.Project)
                .OrderByDescending(n => n.UpdatedAt)
                .Take(100)
                .ToListAsync(ct),
            Expenses = await _db.Expenses.AsNoTracking()
                .OrderByDescending(x => x.Date)
                .Take(100)
                .ToListAsync(ct),
            Budgets = await _db.MonthlyBudgets.AsNoTracking()
                .OrderByDescending(b => b.Year).ThenByDescending(b => b.Month)
                .ToListAsync(ct),
            HealthRecords = await _db.HealthRecords.AsNoTracking()
                .OrderByDescending(h => h.Date)
                .Take(60)
                .ToListAsync(ct)
        };

        return View(vm);
    }

    [HttpPost("/Workspace/CreateTask")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateTask(string title, PriorityLevel priority, DateTime? dueDate, string? category, int? projectId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(title))
        {
            _db.Tasks.Add(new TaskItem
            {
                Title = title.Trim(),
                Priority = priority,
                DueDate = dueDate?.ToUniversalTime(),
                Category = string.IsNullOrWhiteSpace(category) ? null : category.Trim(),
                ProjectId = projectId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(ct);
        }
        return RedirectToAction(nameof(Index), new { tab = "tasks", status = "Task created." });
    }

    [HttpPost("/Workspace/ToggleTask")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleTask(int id, CancellationToken ct)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (task is not null)
        {
            if (task.Status == TaskItemStatus.Completed)
            {
                task.Status = TaskItemStatus.Pending;
                task.CompletedAt = null;
            }
            else
            {
                task.Status = TaskItemStatus.Completed;
                task.CompletedAt = DateTime.UtcNow;
            }
            task.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        return RedirectToAction(nameof(Index), new { tab = "tasks" });
    }

    [HttpPost("/Workspace/DeleteTask")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteTask(int id, CancellationToken ct)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (task is not null)
        {
            _db.Tasks.Remove(task);
            await _db.SaveChangesAsync(ct);
        }
        return RedirectToAction(nameof(Index), new { tab = "tasks", status = "Task deleted." });
    }

    [HttpPost("/Workspace/CreateProject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateProject(string name, string? description, PriorityLevel priority, DateTime? targetDate, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            _db.Projects.Add(new Project
            {
                Name = name.Trim(),
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                Priority = priority,
                Status = ProjectStatus.Active,
                TargetDate = targetDate?.ToUniversalTime(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(ct);
        }
        return RedirectToAction(nameof(Index), new { tab = "projects", status = "Project created." });
    }

    [HttpPost("/Workspace/CreateEvent")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateEvent(string title, DateTime startDateTime, DateTime? endDateTime, string? location, string? category, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(title))
        {
            _db.ScheduleEvents.Add(new ScheduleEvent
            {
                Title = title.Trim(),
                StartDateTime = startDateTime.ToUniversalTime(),
                EndDateTime = endDateTime?.ToUniversalTime(),
                Location = string.IsNullOrWhiteSpace(location) ? null : location.Trim(),
                Category = string.IsNullOrWhiteSpace(category) ? null : category.Trim(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(ct);
        }
        return RedirectToAction(nameof(Index), new { tab = "schedule", status = "Event scheduled." });
    }

    [HttpPost("/Workspace/CreateNote")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateNote(string title, string content, string? category, string? tags, int? projectId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(content))
        {
            _db.Notes.Add(new Note
            {
                Title = title.Trim(),
                Content = content.Trim(),
                Category = string.IsNullOrWhiteSpace(category) ? null : category.Trim(),
                Tags = string.IsNullOrWhiteSpace(tags) ? null : tags.Trim(),
                ProjectId = projectId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(ct);
        }
        return RedirectToAction(nameof(Index), new { tab = "notes", status = "Note saved." });
    }

    [HttpPost("/Workspace/AddExpense")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddExpense(decimal amount, string category, string? description, DateTime? date, string? paymentMethod, CancellationToken ct)
    {
        if (amount > 0 && !string.IsNullOrWhiteSpace(category))
        {
            _db.Expenses.Add(new Expense
            {
                Amount = Math.Round(amount, 2),
                Category = category.Trim(),
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                Date = (date ?? DateTime.Now).ToUniversalTime(),
                PaymentMethod = string.IsNullOrWhiteSpace(paymentMethod) ? null : paymentMethod.Trim(),
                CreatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(ct);
        }
        return RedirectToAction(nameof(Index), new { tab = "finance", status = "Expense recorded." });
    }

    [HttpPost("/Workspace/AddHealthRecord")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddHealthRecord(DateTime? date, double? sleepHours, double? waterIntakeLiters, string? exercise, int? exerciseMinutes, double? weightKg, string? bloodPressure, int? heartRateBpm, string? notes, CancellationToken ct)
    {
        _db.HealthRecords.Add(new HealthRecord
        {
            Date = (date ?? DateTime.Now).ToUniversalTime(),
            SleepHours = sleepHours,
            WaterIntakeLiters = waterIntakeLiters,
            Exercise = string.IsNullOrWhiteSpace(exercise) ? null : exercise.Trim(),
            ExerciseMinutes = exerciseMinutes,
            WeightKg = weightKg,
            BloodPressure = string.IsNullOrWhiteSpace(bloodPressure) ? null : bloodPressure.Trim(),
            HeartRateBpm = heartRateBpm,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Index), new { tab = "health", status = "Wellness entry saved." });
    }
}

