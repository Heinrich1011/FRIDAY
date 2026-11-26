namespace Friday.Core.Enums;

public enum TaskItemStatus
{
    Pending = 0,
    InProgress = 1,
    Completed = 2,
    Cancelled = 3
}

public enum PriorityLevel
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3
}

public enum ProjectStatus
{
    Planning = 0,
    Active = 1,
    OnHold = 2,
    Completed = 3,
    Archived = 4
}

public enum McpToolSafety
{
    Read = 0,
    Write = 1,
    Destructive = 2
}

public enum DataSensitivityLevel
{
    Standard = 0,
    Personal = 1,
    Financial = 2,
    RestrictedHealth = 3
}

