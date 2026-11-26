using System.ComponentModel.DataAnnotations;
using Friday.Core.Enums;

namespace Friday.Core.Entities;

public class UserProfile
{
    public int Id { get; set; }

    [MaxLength(150)]
    public string? FullName { get; set; }

    [MaxLength(80)]
    public string? PreferredName { get; set; }

    public DateOnly? BirthDate { get; set; }

    [MaxLength(150)]
    public string? Location { get; set; }

    [MaxLength(150)]
    public string? Occupation { get; set; }

    [MaxLength(250)]
    public string? Education { get; set; }

    [MaxLength(1000)]
    public string? Skills { get; set; }

    [MaxLength(1000)]
    public string? Interests { get; set; }

    [MaxLength(2000)]
    public string? Goals { get; set; }

    [MaxLength(2000)]
    public string? Preferences { get; set; }

    [MaxLength(100)]
    public string? TimeZoneId { get; set; }

    [MaxLength(10)]
    public string CurrencyCode { get; set; } = "USD";

    public bool AllowFinanceContextInAi { get; set; } = true;

    public bool AllowHealthContextInAi { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class Project
{
    public int Id { get; set; }

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Description { get; set; }

    public ProjectStatus Status { get; set; } = ProjectStatus.Active;

    public PriorityLevel Priority { get; set; } = PriorityLevel.Medium;

    public DateTime? StartDate { get; set; }

    public DateTime? TargetDate { get; set; }

    [MaxLength(4000)]
    public string? Notes { get; set; }

    public bool IsDemoData { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<TaskItem> Tasks { get; set; } = new List<TaskItem>();

    public ICollection<Note> LinkedNotes { get; set; } = new List<Note>();
}

public class TaskItem
{
    public int Id { get; set; }

    [Required, MaxLength(250)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Description { get; set; }

    public TaskItemStatus Status { get; set; } = TaskItemStatus.Pending;

    public PriorityLevel Priority { get; set; } = PriorityLevel.Medium;

    public DateTime? DueDate { get; set; }

    public DateTime? CompletedAt { get; set; }

    [MaxLength(100)]
    public string? Category { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }

    public int? ProjectId { get; set; }

    public Project? Project { get; set; }

    public bool IsDemoData { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class ScheduleEvent
{
    public int Id { get; set; }

    [Required, MaxLength(250)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Description { get; set; }

    public DateTime StartDateTime { get; set; }

    public DateTime? EndDateTime { get; set; }

    [MaxLength(250)]
    public string? Location { get; set; }

    [MaxLength(100)]
    public string? Category { get; set; }

    [MaxLength(150)]
    public string? Reminder { get; set; }

    public bool IsAllDay { get; set; }

    public bool IsDemoData { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class Note
{
    public int Id { get; set; }

    [Required, MaxLength(250)]
    public string Title { get; set; } = string.Empty;

    [Required, MaxLength(20000)]
    public string Content { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? Category { get; set; }

    [MaxLength(500)]
    public string? Tags { get; set; }

    public int? ProjectId { get; set; }

    public Project? Project { get; set; }

    public bool IsDemoData { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class Expense
{
    public int Id { get; set; }

    [Range(0.01, 100000000)]
    public decimal Amount { get; set; }

    [Required, MaxLength(100)]
    public string Category { get; set; } = "General";

    [MaxLength(500)]
    public string? Description { get; set; }

    public DateTime Date { get; set; } = DateTime.UtcNow;

    [MaxLength(80)]
    public string? PaymentMethod { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    public bool IsDemoData { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class MonthlyBudget
{
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string Category { get; set; } = "Overall";

    [Range(0.01, 100000000)]
    public decimal MonthlyLimit { get; set; }

    public int Year { get; set; }

    public int Month { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class HealthRecord
{
    public int Id { get; set; }

    public DateTime Date { get; set; } = DateTime.UtcNow;

    [Range(1, 500)]
    public double? WeightKg { get; set; }

    [MaxLength(30)]
    public string? BloodPressure { get; set; }

    [Range(20, 260)]
    public int? HeartRateBpm { get; set; }

    [Range(0, 24)]
    public double? SleepHours { get; set; }

    [MaxLength(500)]
    public string? Exercise { get; set; }

    [Range(0, 1440)]
    public int? ExerciseMinutes { get; set; }

    [Range(0, 30)]
    public double? WaterIntakeLiters { get; set; }

    [MaxLength(1000)]
    public string? Symptoms { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }

    public bool IsDemoData { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class ChatSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(200)]
    public string Title { get; set; } = "New Conversation";

    /// <summary>
    /// Stores non-sensitive structured context metadata (e.g., last module queried, filters, table/chart summary)
    /// to power follow-up questions like "Make that a chart" or "Only show transportation".
    /// </summary>
    public string? LastContextJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<ChatMessageRecord> Messages { get; set; } = new List<ChatMessageRecord>();

    public ICollection<PendingConfirmation> PendingConfirmations { get; set; } = new List<PendingConfirmation>();
}

public class ChatMessageRecord
{
    public long Id { get; set; }

    public Guid ChatSessionId { get; set; }

    public ChatSession? ChatSession { get; set; }

    [Required, MaxLength(30)]
    public string Role { get; set; } = "user";

    [Required]
    public string Content { get; set; } = string.Empty;

    public string? TableJson { get; set; }

    public string? ChartJson { get; set; }

    public string? ReportJson { get; set; }

    public string? ActivitySummaryJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class PendingConfirmation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChatSessionId { get; set; }

    public ChatSession? ChatSession { get; set; }

    [Required, MaxLength(120)]
    public string ToolName { get; set; } = string.Empty;

    [Required]
    public string ArgumentsJson { get; set; } = "{}";

    [Required, MaxLength(500)]
    public string TargetSummary { get; set; } = string.Empty;

    public bool IsResolved { get; set; }

    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddMinutes(15);

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class AuditLog
{
    public long Id { get; set; }

    [Required, MaxLength(64)]
    public string CorrelationId { get; set; } = string.Empty;

    [Required, MaxLength(80)]
    public string RequestType { get; set; } = string.Empty;

    [Required, MaxLength(80)]
    public string ModuleName { get; set; } = string.Empty;

    [MaxLength(120)]
    public string? ToolName { get; set; }

    [Required, MaxLength(30)]
    public string SafetyLevel { get; set; } = "Read";

    public bool Success { get; set; }

    public long DurationMs { get; set; }

    [MaxLength(300)]
    public string? OutcomeSummary { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

