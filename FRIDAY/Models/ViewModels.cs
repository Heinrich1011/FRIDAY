using Friday.Core.Entities;
using Friday.Core.Models;

namespace FRIDAY.Models;

public class ChatWorkspaceViewModel
{
    public Guid? ActiveSessionId { get; set; }
    public string ActiveSessionTitle { get; set; } = "Friday Assistant";
    public string UserGreetingName { get; set; } = string.Empty;
    public bool IsGroqConfigured { get; set; }
    public string ConfiguredModel { get; set; } = string.Empty;
    public int McpToolCount { get; set; }
    public List<SessionSummaryItem> RecentSessions { get; set; } = new();
    public List<HydratedChatMessage> Messages { get; set; } = new();
}

public class SessionSummaryItem
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; }
}

public class HydratedChatMessage
{
    public long Id { get; set; }
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
    public TableResult? Table { get; set; }
    public ChartConfiguration? Chart { get; set; }
    public ReportArtifactInfo? Report { get; set; }
    public List<string> ActivitySummary { get; set; } = new();
    public DateTime CreatedAtUtc { get; set; }
}

public class ProfileSettingsViewModel
{
    public UserProfile Profile { get; set; } = new();
    public bool HasDemoData { get; set; }
    public bool IsGroqConfigured { get; set; }
    public string ConfiguredModel { get; set; } = string.Empty;
    public List<string> ApprovedDirectories { get; set; } = new();
    public string? StatusMessage { get; set; }
}

public class WorkspaceIndexViewModel
{
    public List<TaskItem> Tasks { get; set; } = new();
    public List<Project> Projects { get; set; } = new();
    public List<ScheduleEvent> Events { get; set; } = new();
    public List<Note> Notes { get; set; } = new();
    public List<Expense> Expenses { get; set; } = new();
    public List<MonthlyBudget> Budgets { get; set; } = new();
    public List<HealthRecord> HealthRecords { get; set; } = new();
    public string CurrencyCode { get; set; } = "USD";
    public string ActiveTab { get; set; } = "tasks";
    public string? StatusMessage { get; set; }
}

