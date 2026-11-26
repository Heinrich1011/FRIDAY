using System.Text.Json;
using Friday.Core.Enums;
using Friday.Core.Models;

namespace Friday.Core.Interfaces;

public interface IMcpToolModule
{
    string ModuleName { get; }
    IReadOnlyList<McpToolDescriptor> GetTools();
    Task<string?> DescribeTargetForDestructiveActionAsync(string toolName, JsonElement arguments, CancellationToken ct = default);
    Task<McpToolCallResult> ExecuteToolAsync(string toolName, JsonElement arguments, McpToolCallContext context, CancellationToken ct = default);
}

public interface IMcpClient
{
    Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken ct = default);
    Task<McpToolCallResult> CallToolAsync(string toolName, JsonElement arguments, McpToolCallContext context, CancellationToken ct = default);
}

public class LlmChatMessage
{
    public string Role { get; set; } = "user"; // system, user, assistant, tool
    public string? Content { get; set; }
    public string? ToolCallId { get; set; }
    public string? Name { get; set; }
    public List<LlmToolCall>? ToolCalls { get; set; }
}

public class LlmToolCall
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ArgumentsJson { get; set; } = "{}";
}

public class LlmCompletionResult
{
    public bool Success { get; set; }
    public bool IsMissingApiKey { get; set; }
    public string? Content { get; set; }
    public List<LlmToolCall> ToolCalls { get; set; } = new();
    public string? ErrorCode { get; set; }
    public string? DiagnosticDetail { get; set; }
}

public interface IGroqLlmClient
{
    bool IsConfigured { get; }
    string ConfiguredModel { get; }
    Task<LlmCompletionResult> CompleteAsync(
        IReadOnlyList<LlmChatMessage> messages,
        IReadOnlyList<McpToolDescriptor> availableTools,
        CancellationToken ct = default);
}

public interface IFridayOrchestrator
{
    Task<FridayChatResponse> ProcessMessageAsync(FridayChatRequest request, CancellationToken ct = default);
    Task<bool> ClearSessionAsync(Guid sessionId, CancellationToken ct = default);
    Task<int> ClearAllSessionsAsync(CancellationToken ct = default);
}

public interface IDashboardService
{
    Task<DashboardViewModel> GetDashboardAsync(TimeSpan userUtcOffset, CancellationToken ct = default);
}

public class ReportGenerationRequest
{
    public string ReportType { get; set; } = "productivity"; // tasks, expenses, productivity, projects, health
    public string Timeframe { get; set; } = "this_month"; // today, this_week, this_month, all
    public string? CustomTitle { get; set; }
    public TimeSpan UserUtcOffset { get; set; } = TimeSpan.Zero;
}

public class StoredReportFile
{
    public byte[] Bytes { get; set; } = Array.Empty<byte>();
    public string ContentType { get; set; } = "application/octet-stream";
    public string FileName { get; set; } = "report.bin";
}

public interface IReportGeneratorService
{
    Task<ReportArtifactInfo> GenerateReportAsync(ReportGenerationRequest request, CancellationToken ct = default);
    Task<StoredReportFile?> GetReportFileAsync(string reportId, string format, CancellationToken ct = default);
}

public interface IAuditLogger
{
    Task LogToolExecutionAsync(
        string correlationId,
        string moduleName,
        string toolName,
        McpToolSafety safety,
        bool success,
        long durationMs,
        string? nonSensitiveOutcomeSummary = null,
        CancellationToken ct = default);

    Task LogRequestAsync(
        string correlationId,
        string requestType,
        bool success,
        long durationMs,
        string? nonSensitiveSummary = null,
        CancellationToken ct = default);
}

