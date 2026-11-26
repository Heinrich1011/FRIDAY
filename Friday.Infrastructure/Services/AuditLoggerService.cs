using Friday.Core.Entities;
using Friday.Core.Enums;
using Friday.Core.Interfaces;
using Friday.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Friday.Infrastructure.Services;

public class AuditLoggerService : IAuditLogger
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuditLoggerService> _logger;

    public AuditLoggerService(IServiceScopeFactory scopeFactory, ILogger<AuditLoggerService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task LogToolExecutionAsync(
        string correlationId,
        string moduleName,
        string toolName,
        McpToolSafety safety,
        bool success,
        long durationMs,
        string? nonSensitiveOutcomeSummary = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "MCP Tool Execution | CorrelationId={CorrelationId} Module={Module} Tool={Tool} Safety={Safety} Success={Success} DurationMs={DurationMs}",
            correlationId, moduleName, toolName, safety, success, durationMs);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FridayDbContext>();
            db.AuditLogs.Add(new AuditLog
            {
                CorrelationId = Truncate(correlationId, 64),
                RequestType = "MCP_TOOL",
                ModuleName = Truncate(moduleName, 80),
                ToolName = Truncate(toolName, 120),
                SafetyLevel = safety.ToString(),
                Success = success,
                DurationMs = durationMs,
                OutcomeSummary = Truncate(nonSensitiveOutcomeSummary, 300),
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Audit DB persistence skipped for CorrelationId={CorrelationId}: {ErrorType}", correlationId, ex.GetType().Name);
        }
    }

    public async Task LogRequestAsync(
        string correlationId,
        string requestType,
        bool success,
        long durationMs,
        string? nonSensitiveSummary = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Friday Request | CorrelationId={CorrelationId} RequestType={RequestType} Success={Success} DurationMs={DurationMs}",
            correlationId, requestType, success, durationMs);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FridayDbContext>();
            db.AuditLogs.Add(new AuditLog
            {
                CorrelationId = Truncate(correlationId, 64),
                RequestType = Truncate(requestType, 80),
                ModuleName = "FridayOrchestrator",
                SafetyLevel = "Read",
                Success = success,
                DurationMs = durationMs,
                OutcomeSummary = Truncate(nonSensitiveSummary, 300),
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Audit DB persistence skipped for CorrelationId={CorrelationId}: {ErrorType}", correlationId, ex.GetType().Name);
        }
    }

    private static string Truncate(string? value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return value.Length <= maxLen ? value : value[..maxLen];
    }
}

