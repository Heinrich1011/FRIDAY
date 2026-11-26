using System.Text.Json;
using Friday.Core.Configuration;
using Friday.Core.Interfaces;
using Friday.Core.Models;
using Friday.Infrastructure.Data;
using Friday.Infrastructure.Services;
using Friday.Mcp.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Standalone MCP JSON-RPC 2.0 Stdio Server for Friday's modular MCP tools.
// Allows external MCP clients to discover and invoke Friday's safe SQLite/File tools over stdio.
var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(opts => opts.LogToStandardErrorThreshold = LogLevel.Trace);

var dbPath = Environment.GetEnvironmentVariable("FRIDAY_DB_PATH")
    ?? Path.Combine(AppContext.BaseDirectory, "friday.db");

builder.Services.AddDbContext<FridayDbContext>(opts => opts.UseSqlite($"Data Source={dbPath}"));
builder.Services.Configure<FileMcpOptions>(builder.Configuration.GetSection(FileMcpOptions.SectionName));
builder.Services.Configure<List<ExternalMcpServerOptions>>(builder.Configuration.GetSection(ExternalMcpServerOptions.SectionName));
builder.Services.AddScoped<IAuditLogger, AuditLoggerService>();
builder.Services.AddScoped<IGroqLlmClient, NullGroqClient>();
builder.Services.AddScoped<IReportGeneratorService>(sp =>
    new ReportGeneratorService(sp.GetRequiredService<FridayDbContext>(), AppContext.BaseDirectory));
builder.Services.AddFridayMcpModules();

using var host = builder.Build();

// Standard MCP JSON-RPC 2.0 loop over stdin/stdout
using var stdin = Console.OpenStandardInput();
using var reader = new StreamReader(stdin);

while (await reader.ReadLineAsync() is { } line)
{
    if (string.IsNullOrWhiteSpace(line)) continue;

    try
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
        var hasId = root.TryGetProperty("id", out var idElem);

        using var scope = host.Services.CreateScope();
        var mcpClient = scope.ServiceProvider.GetRequiredService<IMcpClient>();

        if (method == "initialize" && hasId)
        {
            WriteJsonRpcResult(idElem, new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { tools = new { } },
                serverInfo = new { name = "friday-mcp-server", version = "1.0.0" }
            });
        }
        else if (method == "tools/list" && hasId)
        {
            var tools = await mcpClient.ListToolsAsync();
            WriteJsonRpcResult(idElem, new
            {
                tools = tools.Select(t => new
                {
                    name = t.Name,
                    description = $"[{t.Safety}] {t.Description}",
                    inputSchema = t.InputSchema
                })
            });
        }
        else if (method == "tools/call" && hasId)
        {
            var paramsEl = root.GetProperty("params");
            var toolName = paramsEl.GetProperty("name").GetString() ?? "";
            var argsEl = paramsEl.TryGetProperty("arguments", out var a) ? a : JsonDocument.Parse("{}").RootElement;

            var callRes = await mcpClient.CallToolAsync(toolName, argsEl, new McpToolCallContext
            {
                SessionId = Guid.Empty,
                CorrelationId = Guid.NewGuid().ToString("N")[..12]
            });

            var payloadJson = JsonSerializer.Serialize(callRes);
            WriteJsonRpcResult(idElem, new
            {
                content = new[]
                {
                    new { type = "text", text = payloadJson }
                },
                isError = !callRes.Success
            });
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[Friday.Mcp.Server] Frame error: {ex.GetType().Name}");
    }
}

static void WriteJsonRpcResult(JsonElement id, object result)
{
    var envelope = new
    {
        jsonrpc = "2.0",
        id,
        result
    };
    Console.Out.WriteLine(JsonSerializer.Serialize(envelope));
    Console.Out.Flush();
}

file sealed class NullGroqClient : IGroqLlmClient
{
    public bool IsConfigured => false;
    public string ConfiguredModel => "none";
    public Task<LlmCompletionResult> CompleteAsync(IReadOnlyList<LlmChatMessage> messages, IReadOnlyList<McpToolDescriptor> availableTools, CancellationToken ct = default)
        => Task.FromResult(new LlmCompletionResult { Success = false, IsMissingApiKey = true });
}
