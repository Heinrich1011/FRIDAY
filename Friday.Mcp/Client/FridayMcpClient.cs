using System.Diagnostics;
using System.Text.Json;
using Friday.Core.Configuration;
using Friday.Core.Entities;
using Friday.Core.Enums;
using Friday.Core.Interfaces;
using Friday.Core.Models;
using Friday.Infrastructure.Data;
using Friday.Mcp.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Friday.Mcp.Client;

/// <summary>
/// Central MCP Client and Safety Gatekeeper.
/// Discovers tools from all modular in-process MCP modules AND any configured external stdio MCP servers,
/// validates arguments, enforces READ / WRITE / DESTRUCTIVE confirmation boundaries, and records privacy-safe telemetry.
/// </summary>
public class FridayMcpClient : IMcpClient
{
    private readonly IEnumerable<IMcpToolModule> _modules;
    private readonly FridayDbContext _db;
    private readonly IAuditLogger _audit;
    private readonly ILogger<FridayMcpClient> _logger;
    private readonly List<ExternalMcpServerOptions> _externalServers;

    public FridayMcpClient(
        IEnumerable<IMcpToolModule> modules,
        FridayDbContext db,
        IAuditLogger audit,
        IOptions<List<ExternalMcpServerOptions>> externalServers,
        ILogger<FridayMcpClient> logger)
    {
        _modules = modules;
        _db = db;
        _audit = audit;
        _logger = logger;
        _externalServers = externalServers.Value ?? new List<ExternalMcpServerOptions>();
    }

    public async Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken ct = default)
    {
        var list = new List<McpToolDescriptor>();
        foreach (var module in _modules)
        {
            list.AddRange(module.GetTools());
        }

        foreach (var ext in _externalServers.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Command)))
        {
            try
            {
                var transport = new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = ext.Name,
                    Command = ext.Command,
                    Arguments = ext.Arguments
                });
                await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
                var extTools = await client.ListToolsAsync(cancellationToken: ct);
                foreach (var t in extTools)
                {
                    list.Add(new McpToolDescriptor
                    {
                        Name = $"{ext.Name}__{t.Name}",
                        ModuleName = ext.Name,
                        HumanActionDescription = $"Queried external MCP server '{ext.Name}'",
                        Description = t.Description ?? $"External tool {t.Name}",
                        Safety = t.Name.Contains("delete", StringComparison.OrdinalIgnoreCase) || t.Name.Contains("remove", StringComparison.OrdinalIgnoreCase)
                            ? McpToolSafety.Destructive
                            : McpToolSafety.Read,
                        InputSchema = t.JsonSchema
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("External MCP server '{ServerName}' unavailable during tool discovery: {ErrorType}", ext.Name, ex.GetType().Name);
            }
        }

        return list;
    }

    public async Task<McpToolCallResult> CallToolAsync(
        string toolName,
        JsonElement arguments,
        McpToolCallContext context,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(toolName))
        {
            return McpToolCallResult.Fail("unknown", "none", "Tool name is required.");
        }

        // Locate target modular MCP provider
        IMcpToolModule? targetModule = null;
        McpToolDescriptor? descriptor = null;

        foreach (var mod in _modules)
        {
            var match = mod.GetTools().FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                targetModule = mod;
                descriptor = match;
                break;
            }
        }

        if (targetModule is null || descriptor is null)
        {
            // Check external federated MCP server tool format: ServerName__ToolName
            if (toolName.Contains("__", StringComparison.Ordinal))
            {
                return await CallExternalServerToolAsync(toolName, arguments, context, sw, ct);
            }

            await _audit.LogToolExecutionAsync(context.CorrelationId, "unknown", toolName, McpToolSafety.Read, false, sw.ElapsedMilliseconds, "Unknown tool", ct);
            return McpToolCallResult.Fail(toolName, "unknown", "I couldn't retrieve that information right now.");
        }

        try
        {
            // Enforce server-side DESTRUCTIVE confirmation gatekeeper!
            if (descriptor.Safety == McpToolSafety.Destructive && !context.ConfirmedDestructiveExecution)
            {
                var targetSummary = await targetModule.DescribeTargetForDestructiveActionAsync(descriptor.Name, arguments, ct);
                if (string.IsNullOrWhiteSpace(targetSummary))
                {
                    sw.Stop();
                    await _audit.LogToolExecutionAsync(context.CorrelationId, descriptor.ModuleName, descriptor.Name, descriptor.Safety, false, sw.ElapsedMilliseconds, "Target not found for destructive op", ct);
                    return McpToolCallResult.Ok(descriptor.Name, descriptor.ModuleName, "Checked target record (none found)", new
                    {
                        exists = false,
                        message = "I don't have any matching records to delete."
                    });
                }
                if (targetSummary.StartsWith("AMBIGUOUS:", StringComparison.Ordinal))
                {
                    var candidateList = targetSummary["AMBIGUOUS:".Length..].Trim();
                    sw.Stop();
                    await _audit.LogToolExecutionAsync(context.CorrelationId, descriptor.ModuleName, descriptor.Name, descriptor.Safety, true, sw.ElapsedMilliseconds, "Multiple matches found for destructive op", ct);
                    return McpToolCallResult.Ambiguous(
                        descriptor.Name,
                        descriptor.ModuleName,
                        $"Multiple matching records were found ({candidateList}). Ask the user which specific record they want to delete before asking for confirmation.",
                        new
                        {
                            ambiguous = true,
                            candidates = candidateList
                        });
                }

                var pending = new PendingConfirmation
                {
                    Id = Guid.NewGuid(),
                    ChatSessionId = context.SessionId,
                    ToolName = descriptor.Name,
                    ArgumentsJson = arguments.ValueKind == JsonValueKind.Undefined ? "{}" : arguments.GetRawText(),
                    TargetSummary = targetSummary,
                    IsResolved = false,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(15),
                    CreatedAt = DateTime.UtcNow
                };

                _db.PendingConfirmations.Add(pending);
                await _db.SaveChangesAsync(ct);

                var dto = new PendingConfirmationDto
                {
                    ConfirmationId = pending.Id,
                    ToolName = descriptor.Name,
                    TargetSummary = targetSummary,
                    PromptMessage = $"Are you sure you want me to delete {targetSummary}?"
                };

                sw.Stop();
                await _audit.LogToolExecutionAsync(context.CorrelationId, descriptor.ModuleName, descriptor.Name, descriptor.Safety, true, sw.ElapsedMilliseconds, "Queued confirmation prompt", ct);

                return new McpToolCallResult
                {
                    Success = true,
                    RequiresConfirmation = true,
                    Confirmation = dto,
                    ToolName = descriptor.Name,
                    ModuleName = descriptor.ModuleName,
                    HumanSummary = $"Requested confirmation before deleting {targetSummary}",
                    Data = new
                    {
                        confirmation_required = true,
                        target = targetSummary,
                        instruction = $"Ask the user clearly: 'Are you sure you want me to delete {targetSummary}?' Do NOT claim it has already been deleted."
                    }
                };
            }

            var result = await targetModule.ExecuteToolAsync(descriptor.Name, arguments, context, ct);
            sw.Stop();
            await _audit.LogToolExecutionAsync(
                context.CorrelationId,
                descriptor.ModuleName,
                descriptor.Name,
                descriptor.Safety,
                result.Success,
                sw.ElapsedMilliseconds,
                result.Success ? "Completed" : "Tool returned validation/domain failure",
                ct);

            return result;
        }
        catch (ArgumentException argEx)
        {
            sw.Stop();
            await _audit.LogToolExecutionAsync(context.CorrelationId, descriptor.ModuleName, descriptor.Name, descriptor.Safety, false, sw.ElapsedMilliseconds, "Validation error", ct);
            return McpToolCallResult.Fail(descriptor.Name, descriptor.ModuleName, argEx.Message);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "MCP tool '{ToolName}' failed | FailingService=FridayMcpClient Stage=MCP_EXECUTE CorrelationId={CorrelationId} ExceptionType={ExceptionType}", descriptor.Name, context.CorrelationId, ex.GetType().Name);
            await _audit.LogToolExecutionAsync(context.CorrelationId, descriptor.ModuleName, descriptor.Name, descriptor.Safety, false, sw.ElapsedMilliseconds, $"{ex.GetType().Name}: {ex.Message}", ct);
            return McpToolCallResult.Fail(descriptor.Name, descriptor.ModuleName, "I couldn't retrieve that information right now.");
        }
    }

    private async Task<McpToolCallResult> CallExternalServerToolAsync(
        string namespacedToolName,
        JsonElement arguments,
        McpToolCallContext context,
        Stopwatch sw,
        CancellationToken ct)
    {
        var parts = namespacedToolName.Split("__", 2);
        var serverName = parts[0];
        var remoteToolName = parts[1];

        var serverCfg = _externalServers.FirstOrDefault(s => s.Enabled && string.Equals(s.Name, serverName, StringComparison.OrdinalIgnoreCase));
        if (serverCfg is null)
        {
            return McpToolCallResult.Fail(namespacedToolName, serverName, "External MCP server is not enabled.");
        }

        try
        {
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = serverCfg.Name,
                Command = serverCfg.Command,
                Arguments = serverCfg.Arguments
            });

            await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
            var argDict = arguments.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments.GetRawText()) ?? new Dictionary<string, object?>()
                : new Dictionary<string, object?>();

            var callRes = await client.CallToolAsync(remoteToolName, argDict, cancellationToken: ct);
            var text = string.Join("\n", callRes.Content.OfType<TextContentBlock>().Select(b => b.Text));

            sw.Stop();
            await _audit.LogToolExecutionAsync(context.CorrelationId, serverName, remoteToolName, McpToolSafety.Read, true, sw.ElapsedMilliseconds, "External MCP call", ct);
            return McpToolCallResult.Ok(namespacedToolName, serverName, $"Queried {serverName}", new { response = text });
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError("External MCP call '{Tool}' failed: {ErrorType}", namespacedToolName, ex.GetType().Name);
            await _audit.LogToolExecutionAsync(context.CorrelationId, serverName, remoteToolName, McpToolSafety.Read, false, sw.ElapsedMilliseconds, ex.GetType().Name, ct);
            return McpToolCallResult.Fail(namespacedToolName, serverName, "I couldn't retrieve that information right now.");
        }
    }
}

public static class FridayMcpServiceCollectionExtensions
{
    public static IServiceCollection AddFridayMcpModules(this IServiceCollection services)
    {
        services.AddScoped<FileMcpModule>();
        services.AddScoped<IMcpToolModule, ProfileMcpModule>();
        services.AddScoped<IMcpToolModule, TaskMcpModule>();
        services.AddScoped<IMcpToolModule, ScheduleMcpModule>();
        services.AddScoped<IMcpToolModule, NotesMcpModule>();
        services.AddScoped<IMcpToolModule, ProjectsMcpModule>();
        services.AddScoped<IMcpToolModule, FinanceMcpModule>();
        services.AddScoped<IMcpToolModule, HealthMcpModule>();
        services.AddScoped<IMcpToolModule>(sp => sp.GetRequiredService<FileMcpModule>());
        services.AddScoped<IMcpToolModule, UniversalSearchAndAnalyticsMcpModule>();
        services.AddScoped<IMcpClient, FridayMcpClient>();
        return services;
    }
}

