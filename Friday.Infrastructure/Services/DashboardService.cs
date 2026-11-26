using Friday.Core.Enums;
using Friday.Core.Interfaces;
using Friday.Core.Models;
using Friday.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Friday.Infrastructure.Services;

public class DashboardService : IDashboardService
{
    private readonly FridayDbContext _db;
    private readonly IGroqLlmClient _groq;
    private readonly IMcpClient _mcpClient;

    public DashboardService(FridayDbContext db, IGroqLlmClient groq, IMcpClient mcpClient)
    {
        _db = db;
        _groq = groq;
        _mcpClient = mcpClient;
    }

    public async Task<DashboardViewModel> GetDashboardAsync(TimeSpan userUtcOffset, CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        var userLocalNow = nowUtc + userUtcOffset;
        var localTodayStart = userLocalNow.Date;
        var todayStartUtc = localTodayStart - userUtcOffset;
        var tomorrowStartUtc = todayStartUtc.AddDays(1);

        int diffToMonday = (7 + (userLocalNow.DayOfWeek - DayOfWeek.Monday)) % 7;
        var weekStartUtc = localTodayStart.AddDays(-diffToMonday) - userUtcOffset;
        var monthStartUtc = new DateTime(userLocalNow.Year, userLocalNow.Month, 1) - userUtcOffset;
        var nextMonthStartUtc = new DateTime(userLocalNow.Year, userLocalNow.Month, 1).AddMonths(1) - userUtcOffset;

        var profile = await _db.UserProfiles.AsNoTracking().OrderBy(p => p.Id).FirstOrDefaultAsync(ct);
        var greetingName = !string.IsNullOrWhiteSpace(profile?.PreferredName)
            ? profile.PreferredName
            : (!string.IsNullOrWhiteSpace(profile?.FullName) ? profile.FullName : string.Empty);

        var openTasks = await _db.Tasks
            .AsNoTracking()
            .Include(t => t.Project)
            .Where(t => t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled)
            .OrderBy(t => t.DueDate ?? DateTime.MaxValue)
            .ThenByDescending(t => t.Priority)
            .ToListAsync(ct);

        var todayTasks = openTasks.Where(t => t.DueDate.HasValue && t.DueDate.Value >= todayStartUtc && t.DueDate.Value < tomorrowStartUtc).ToList();
        var overdueTasks = openTasks.Where(t => t.DueDate.HasValue && t.DueDate.Value < todayStartUtc).ToList();

        var completedThisWeek = await _db.Tasks
            .AsNoTracking()
            .CountAsync(t => t.Status == TaskItemStatus.Completed && t.CompletedAt.HasValue && t.CompletedAt.Value >= weekStartUtc, ct);

        var upcomingEvents = await _db.ScheduleEvents
            .AsNoTracking()
            .Where(e => e.StartDateTime >= todayStartUtc && e.StartDateTime <= todayStartUtc.AddDays(14))
            .OrderBy(e => e.StartDateTime)
            .Take(8)
            .ToListAsync(ct);

        var allProjects = await _db.Projects
            .AsNoTracking()
            .Include(p => p.Tasks)
            .OrderByDescending(p => p.Priority)
            .ThenBy(p => p.TargetDate ?? DateTime.MaxValue)
            .ToListAsync(ct);

        var activeProjects = allProjects.Where(p => p.Status == ProjectStatus.Active || p.Status == ProjectStatus.Planning).ToList();

        var monthExpenses = await _db.Expenses
            .AsNoTracking()
            .Where(x => x.Date >= monthStartUtc && x.Date < nextMonthStartUtc)
            .ToListAsync(ct);

        // Chart 1: Tasks Completed Over Last 7 Days
        var sevenDaysAgoLocal = localTodayStart.AddDays(-6);
        var sevenDaysAgoUtc = sevenDaysAgoLocal - userUtcOffset;
        var recentCompleted = await _db.Tasks
            .AsNoTracking()
            .Where(t => t.Status == TaskItemStatus.Completed && t.CompletedAt.HasValue && t.CompletedAt.Value >= sevenDaysAgoUtc)
            .Select(t => t.CompletedAt!.Value)
            .ToListAsync(ct);

        var trendLabels = new List<string>();
        var trendData = new List<double>();
        for (int i = 0; i < 7; i++)
        {
            var dayLocal = sevenDaysAgoLocal.AddDays(i);
            trendLabels.Add(dayLocal.ToString("MMM dd"));
            var dayStartUtc = dayLocal - userUtcOffset;
            var dayEndUtc = dayStartUtc.AddDays(1);
            trendData.Add(recentCompleted.Count(c => c >= dayStartUtc && c < dayEndUtc));
        }

        // Chart 2: Open Tasks by Priority
        var priorityOrder = new[] { PriorityLevel.Critical, PriorityLevel.High, PriorityLevel.Medium, PriorityLevel.Low };
        var priorityLabels = priorityOrder.Select(p => p.ToString()).ToList();
        var priorityCounts = priorityOrder.Select(p => (double)openTasks.Count(t => t.Priority == p)).ToList();

        // Chart 3: Monthly Expenses by Category
        var expenseGroups = monthExpenses
            .GroupBy(e => string.IsNullOrWhiteSpace(e.Category) ? "Uncategorized" : e.Category.Trim())
            .Select(g => new { Category = g.Key, Total = (double)g.Sum(x => x.Amount) })
            .OrderByDescending(g => g.Total)
            .ToList();

        // Chart 4: Projects by Status
        var statusGroups = Enum.GetValues<ProjectStatus>()
            .Select(s => new { Status = s.ToString(), Count = (double)allProjects.Count(p => p.Status == s) })
            .Where(x => x.Count > 0)
            .ToList();

        var tools = await _mcpClient.ListToolsAsync(ct);

        return new DashboardViewModel
        {
            GreetingName = greetingName,
            TodayTasksCount = todayTasks.Count,
            CompletedTasksThisWeekCount = completedThisWeek,
            OverdueTasksCount = overdueTasks.Count,
            UpcomingEventsCount = upcomingEvents.Count,
            ActiveProjectsCount = activeProjects.Count,
            MonthlyExpensesTotal = monthExpenses.Sum(x => x.Amount),
            CurrencyCode = profile?.CurrencyCode ?? "USD",
            IsGroqConfigured = _groq.IsConfigured,
            RegisteredMcpToolsCount = tools.Count,

            TodayAndOverdueTasks = overdueTasks.Concat(todayTasks)
                .DistinctBy(t => t.Id)
                .Take(8)
                .Select(t => new DashboardTaskSummary
                {
                    Id = t.Id,
                    Title = t.Title,
                    Priority = t.Priority.ToString(),
                    Status = t.Status.ToString(),
                    DueDateUtc = t.DueDate,
                    IsOverdue = t.DueDate.HasValue && t.DueDate.Value < todayStartUtc,
                    ProjectName = t.Project?.Name
                })
                .ToList(),

            UpcomingEvents = upcomingEvents.Select(e => new DashboardEventSummary
            {
                Id = e.Id,
                Title = e.Title,
                StartDateTimeUtc = e.StartDateTime,
                EndDateTimeUtc = e.EndDateTime,
                Location = e.Location,
                Category = e.Category
            }).ToList(),

            ActiveProjects = activeProjects.Take(6).Select(p => new DashboardProjectSummary
            {
                Id = p.Id,
                Name = p.Name,
                Status = p.Status.ToString(),
                Priority = p.Priority.ToString(),
                TotalTasks = p.Tasks.Count,
                CompletedTasks = p.Tasks.Count(t => t.Status == TaskItemStatus.Completed),
                TargetDateUtc = p.TargetDate
            }).ToList(),

            TasksCompletedOverTimeChart = new ChartConfiguration
            {
                ChartType = "area",
                Title = "Tasks Completed (Last 7 Days)",
                Labels = trendLabels,
                Datasets = new List<ChartDataset>
                {
                    new()
                    {
                        Label = "Completed Tasks",
                        Data = trendData,
                        BorderColor = "#38bdf8",
                        Fill = true
                    }
                },
                XAxisLabel = "Date",
                YAxisLabel = "Tasks",
                Legend = false,
                SourceDescription = "SQLite Tasks table (last 7 days)"
            },

            TasksByPriorityChart = new ChartConfiguration
            {
                ChartType = "bar",
                Title = "Open Tasks by Priority",
                Labels = priorityLabels,
                Datasets = new List<ChartDataset>
                {
                    new()
                    {
                        Label = "Open Tasks",
                        Data = priorityCounts,
                        BackgroundColor = new List<string> { "#ef4444", "#f59e0b", "#3b82f6", "#10b981" }
                    }
                },
                XAxisLabel = "Priority",
                YAxisLabel = "Count",
                Legend = false,
                SourceDescription = "SQLite Tasks table (active items)"
            },

            ExpensesByCategoryChart = new ChartConfiguration
            {
                ChartType = "doughnut",
                Title = "Expenses by Category (This Month)",
                Labels = expenseGroups.Select(g => g.Category).ToList(),
                Datasets = new List<ChartDataset>
                {
                    new()
                    {
                        Label = "Amount",
                        Data = expenseGroups.Select(g => g.Total).ToList(),
                        BackgroundColor = new List<string> { "#6366f1", "#06b6d4", "#10b981", "#f59e0b", "#ec4899", "#8b5cf6", "#14b8a6" }
                    }
                },
                Legend = true,
                SourceDescription = "SQLite Expenses table (current month)"
            },

            ProjectStatusChart = new ChartConfiguration
            {
                ChartType = "bar",
                Title = "Projects by Status",
                Labels = statusGroups.Select(g => g.Status).ToList(),
                Datasets = new List<ChartDataset>
                {
                    new()
                    {
                        Label = "Projects",
                        Data = statusGroups.Select(g => g.Count).ToList(),
                        BackgroundColor = new List<string> { "#38bdf8", "#818cf8", "#fbbf24", "#34d399", "#94a3b8" }
                    }
                },
                XAxisLabel = "Status",
                YAxisLabel = "Projects",
                Legend = false,
                SourceDescription = "SQLite Projects table"
            }
        };
    }
}

