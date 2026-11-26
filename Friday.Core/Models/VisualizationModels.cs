using System.Text.Json;
using Friday.Core.Enums;

namespace Friday.Core.Models;

public class ChartConfiguration
{
    public string ChartType { get; set; } = "bar";
    public string Title { get; set; } = string.Empty;
    public List<string> Labels { get; set; } = new();
    public List<ChartDataset> Datasets { get; set; } = new();
    public string? XAxisLabel { get; set; }
    public string? YAxisLabel { get; set; }
    public bool Legend { get; set; } = true;
    public string SourceDescription { get; set; } = string.Empty;
}

public class ChartDataset
{
    public string Label { get; set; } = string.Empty;
    public List<double> Data { get; set; } = new();
    public List<ScatterPoint>? ScatterPoints { get; set; }
    public List<string>? BackgroundColor { get; set; }
    public string? BorderColor { get; set; }
    public bool Fill { get; set; }
}

public class ScatterPoint
{
    public double X { get; set; }
    public double Y { get; set; }
    public string? Label { get; set; }
}

public class TableResult
{
    public string Title { get; set; } = string.Empty;
    public List<string> Columns { get; set; } = new();
    public List<List<string>> Rows { get; set; } = new();
    public string? FooterSummary { get; set; }
    public string? SourceDescription { get; set; }
}

public class ReportArtifactInfo
{
    public string ReportId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string PdfDownloadUrl { get; set; } = string.Empty;
    public string DocxDownloadUrl { get; set; } = string.Empty;
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
}

public class PendingConfirmationDto
{
    public Guid ConfirmationId { get; set; }
    public string ToolName { get; set; } = string.Empty;
    public string TargetSummary { get; set; } = string.Empty;
    public string PromptMessage { get; set; } = string.Empty;
}

public class RecentEntityRef
{
    public string Type { get; set; } = string.Empty; // task, project, event, note, expense
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Meta { get; set; }
}

public class ConversationTurnContext
{
    public List<string> RecentModules { get; set; } = new();
    public List<string> RecentTools { get; set; } = new();
    public string? LastSubjectSummary { get; set; }
    public RecentEntityRef? LastTask { get; set; }
    public RecentEntityRef? LastProject { get; set; }
    public RecentEntityRef? LastEvent { get; set; }
    public RecentEntityRef? LastNote { get; set; }
    public List<RecentEntityRef> RecentItems { get; set; } = new();
    public string? LastSearchQuery { get; set; }
    public TableResult? LastTable { get; set; }
    public ChartConfiguration? LastChart { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class FridayChatRequest
{
    public Guid? SessionId { get; set; }
    public string Message { get; set; } = string.Empty;
    public int ClientTimezoneOffsetMinutes { get; set; }
    public bool DebugMode { get; set; }
    public string InputMode { get; set; } = "text"; // "text" | "voice"
}

public class FridayChatResponse
{
    public Guid SessionId { get; set; }
    public string SessionTitle { get; set; } = "Conversation";
    public string Message { get; set; } = string.Empty;
    public string? SpokenSummary { get; set; }
    public string InputMode { get; set; } = "text";
    public TableResult? Table { get; set; }
    public ChartConfiguration? Chart { get; set; }
    public ReportArtifactInfo? Report { get; set; }
    public PendingConfirmationDto? PendingConfirmation { get; set; }
    public List<string> ActivitySummary { get; set; } = new();
    public List<string>? DebugToolsCalled { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public bool IsError { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}

public class McpToolDescriptor
{
    public string Name { get; set; } = string.Empty;
    public string ModuleName { get; set; } = string.Empty;
    public string HumanActionDescription { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public McpToolSafety Safety { get; set; } = McpToolSafety.Read;
    public DataSensitivityLevel Sensitivity { get; set; } = DataSensitivityLevel.Standard;
    public JsonElement InputSchema { get; set; }
}

public class McpToolCallContext
{
    public Guid SessionId { get; set; }
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public TimeSpan UserUtcOffset { get; set; } = TimeSpan.Zero;
    public bool ConfirmedDestructiveExecution { get; set; }
}

public class McpToolCallResult
{
    public bool Success { get; set; }
    public bool RequiresConfirmation { get; set; }
    public bool IsAmbiguous { get; set; }
    public PendingConfirmationDto? Confirmation { get; set; }
    public string ToolName { get; set; } = string.Empty;
    public string ModuleName { get; set; } = string.Empty;
    public string HumanSummary { get; set; } = string.Empty;
    public object? Data { get; set; }
    public TableResult? SuggestedTable { get; set; }
    public ChartConfiguration? SuggestedChart { get; set; }
    public ReportArtifactInfo? GeneratedReport { get; set; }
    public string? ErrorMessage { get; set; }

    public static McpToolCallResult Ok(
        string toolName,
        string moduleName,
        string humanSummary,
        object? data,
        TableResult? table = null,
        ChartConfiguration? chart = null,
        ReportArtifactInfo? report = null) => new()
        {
            Success = true,
            ToolName = toolName,
            ModuleName = moduleName,
            HumanSummary = humanSummary,
            Data = data,
            SuggestedTable = table,
            SuggestedChart = chart,
            GeneratedReport = report
        };

    public static McpToolCallResult Fail(string toolName, string moduleName, string errorMessage) => new()
    {
        Success = false,
        ToolName = toolName,
        ModuleName = moduleName,
        ErrorMessage = errorMessage,
        HumanSummary = errorMessage
    };

    public static McpToolCallResult Ambiguous(string toolName, string moduleName, string promptMessage, object matches) => new()
    {
        Success = false,
        IsAmbiguous = true,
        ToolName = toolName,
        ModuleName = moduleName,
        ErrorMessage = promptMessage,
        HumanSummary = "Multiple matching records found; requested clarification",
        Data = new
        {
            ambiguous = true,
            message = promptMessage,
            matches
        }
    };
}

public class DashboardViewModel
{
    public string GreetingName { get; set; } = string.Empty;
    public int TodayTasksCount { get; set; }
    public int CompletedTasksThisWeekCount { get; set; }
    public int OverdueTasksCount { get; set; }
    public int UpcomingEventsCount { get; set; }
    public int ActiveProjectsCount { get; set; }
    public decimal MonthlyExpensesTotal { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    public bool IsGroqConfigured { get; set; }
    public int RegisteredMcpToolsCount { get; set; }

    public List<DashboardTaskSummary> TodayAndOverdueTasks { get; set; } = new();
    public List<DashboardEventSummary> UpcomingEvents { get; set; } = new();
    public List<DashboardProjectSummary> ActiveProjects { get; set; } = new();

    public ChartConfiguration TasksCompletedOverTimeChart { get; set; } = new();
    public ChartConfiguration TasksByPriorityChart { get; set; } = new();
    public ChartConfiguration ExpensesByCategoryChart { get; set; } = new();
    public ChartConfiguration ProjectStatusChart { get; set; } = new();
}

public class DashboardTaskSummary
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime? DueDateUtc { get; set; }
    public bool IsOverdue { get; set; }
    public string? ProjectName { get; set; }
}

public class DashboardEventSummary
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTime StartDateTimeUtc { get; set; }
    public DateTime? EndDateTimeUtc { get; set; }
    public string? Location { get; set; }
    public string? Category { get; set; }
}

public class DashboardProjectSummary
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty;
    public int TotalTasks { get; set; }
    public int CompletedTasks { get; set; }
    public DateTime? TargetDateUtc { get; set; }
}

