using System.Text;
using System.Text.Json;
using Friday.Core.Configuration;
using Friday.Core.Enums;
using Friday.Core.Interfaces;
using Friday.Core.Models;
using Friday.Infrastructure.Data;
using Friday.Infrastructure.Services;
using Friday.Mcp.Client;
using FRIDAY.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Friday.Tests;

public class FridayIntegrationTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _approvedDocsDir;
    private readonly string _outsideSecretFile;
    private readonly ServiceProvider _provider;
    private readonly ScriptedGroqClient _fakeGroq;

    public FridayIntegrationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"friday_tests_{Guid.NewGuid():N}");
        _approvedDocsDir = Path.Combine(_tempRoot, "ApprovedDocs");
        Directory.CreateDirectory(_approvedDocsDir);

        File.WriteAllText(Path.Combine(_approvedDocsDir, "mcp-notes.md"), "# MCP Project Overview\nFriday uses Model Context Protocol cleanly.");
        _outsideSecretFile = Path.Combine(_tempRoot, "outside-secret.txt");
        File.WriteAllText(_outsideSecretFile, "TOP_SECRET_OUTSIDE_SANDBOX");

        var dbPath = Path.Combine(_tempRoot, "friday_test.db");

        _fakeGroq = new ScriptedGroqClient();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDbContext<FridayDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
        services.Configure<GroqOptions>(o =>
        {
            o.ApiKey = "test-only-in-memory";
            o.Model = "llama-3.3-70b-versatile";
        });
        services.Configure<FileMcpOptions>(o =>
        {
            o.AllowedDirectories = new List<string> { _approvedDocsDir };
        });
        services.Configure<List<ExternalMcpServerOptions>>(_ => { });

        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(_tempRoot));
        services.AddScoped<IGroqLlmClient>(_ => _fakeGroq);
        services.AddScoped<IAuditLogger, AuditLoggerService>();
        services.AddScoped<IReportGeneratorService>(sp =>
            new ReportGeneratorService(sp.GetRequiredService<FridayDbContext>(), _tempRoot));
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<PrivacyAndDataManagementService>();
        services.AddFridayMcpModules();
        services.AddSingleton<McpToolRelevanceSelector>();
        services.AddScoped<IFridayOrchestrator, FridayOrchestrator>();

        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FridayDbContext>();
        db.Database.Migrate();
    }

    [Fact]
    public async Task SqliteMigrations_CreateSchemaCleanly_WithZeroFictionalData()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FridayDbContext>();

        Assert.Equal(0, await db.UserProfiles.CountAsync());
        Assert.Equal(0, await db.Tasks.CountAsync());
        Assert.Equal(0, await db.Projects.CountAsync());
        Assert.Equal(0, await db.ScheduleEvents.CountAsync());
        Assert.Equal(0, await db.Notes.CountAsync());
        Assert.Equal(0, await db.Expenses.CountAsync());
        Assert.Equal(0, await db.HealthRecords.CountAsync());
    }

    [Fact]
    public async Task McpRegistry_DiscoversAllModules_AndClassifiesSafetyCorrectly()
    {
        using var scope = _provider.CreateScope();
        var mcp = scope.ServiceProvider.GetRequiredService<IMcpClient>();
        var tools = await mcp.ListToolsAsync();

        Assert.True(tools.Count >= 40, $"Expected >= 40 MCP tools across 9 modules, found {tools.Count}");

        var deleteTask = Assert.Single(tools, t => t.Name == "delete_task");
        Assert.Equal(McpToolSafety.Destructive, deleteTask.Safety);

        var createTask = Assert.Single(tools, t => t.Name == "create_task");
        Assert.Equal(McpToolSafety.Write, createTask.Safety);

        var getToday = Assert.Single(tools, t => t.Name == "get_today_tasks");
        Assert.Equal(McpToolSafety.Read, getToday.Safety);
    }

    [Fact]
    public async Task DestructiveTool_EnforcesServerSideConfirmation_BeforeDeletingRecord()
    {
        using var scope = _provider.CreateScope();
        var mcp = scope.ServiceProvider.GetRequiredService<IMcpClient>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IFridayOrchestrator>();
        var db = scope.ServiceProvider.GetRequiredService<FridayDbContext>();

        // Create a real task via MCP
        using var createArgs = JsonDocument.Parse("""{"title":"Old Test Task","priority":"Low"}""");
        var created = await mcp.CallToolAsync("create_task", createArgs.RootElement, new McpToolCallContext());
        Assert.True(created.Success);
        Assert.Equal(1, await db.Tasks.CountAsync());

        // Script Groq to call delete_task when user says "Delete my Old Test Task"
        _fakeGroq.EnqueueToolCall("delete_task", """{"title_match":"Old Test Task"}""");

        var firstTurn = await orchestrator.ProcessMessageAsync(new FridayChatRequest
        {
            Message = "Delete my Old Test Task"
        });

        // Must NOT have deleted yet! Must return PendingConfirmation!
        Assert.NotNull(firstTurn.PendingConfirmation);
        Assert.Contains("Are you sure you want me to delete", firstTurn.Message);
        Assert.Equal(1, await db.Tasks.CountAsync());

        // Now user confirms with "Yes"
        var confirmTurn = await orchestrator.ProcessMessageAsync(new FridayChatRequest
        {
            SessionId = firstTurn.SessionId,
            Message = "Yes"
        });

        Assert.False(confirmTurn.IsError);
        Assert.Contains("deleted", confirmTurn.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await db.Tasks.CountAsync());
    }

    [Fact]
    public async Task FileMcp_AllowsApprovedFiles_AndBlocksPathTraversal()
    {
        using var scope = _provider.CreateScope();
        var mcp = scope.ServiceProvider.GetRequiredService<IMcpClient>();

        // 1. Read approved file
        using var okArgs = JsonDocument.Parse("""{"file_path":"mcp-notes.md"}""");
        var okRes = await mcp.CallToolAsync("read_document", okArgs.RootElement, new McpToolCallContext());
        Assert.True(okRes.Success);

        // 2. Attempt relative path traversal ../outside-secret.txt
        using var traversalArgs = JsonDocument.Parse("""{"file_path":"../outside-secret.txt"}""");
        var blockedTraversal = await mcp.CallToolAsync("read_document", traversalArgs.RootElement, new McpToolCallContext());
        Assert.False(blockedTraversal.Success);
        Assert.Contains("Access denied", blockedTraversal.ErrorMessage);

        // 3. Attempt absolute path outside approved roots
        var escapedAbs = JsonSerializer.Serialize(new { file_path = _outsideSecretFile });
        using var absDoc = JsonDocument.Parse(escapedAbs);
        var blockedAbs = await mcp.CallToolAsync("read_document", absDoc.RootElement, new McpToolCallContext());
        Assert.False(blockedAbs.Success);
        Assert.Contains("Access denied", blockedAbs.ErrorMessage);
    }

    [Fact]
    public async Task CrossModuleFlow_ExpensesAndReports_ProduceVerifiedChartTableAndPdfDocx()
    {
        using var scope = _provider.CreateScope();
        var mcp = scope.ServiceProvider.GetRequiredService<IMcpClient>();
        var reports = scope.ServiceProvider.GetRequiredService<IReportGeneratorService>();

        using var exp1 = JsonDocument.Parse("""{"amount":25.50,"category":"Transportation","description":"Train fare","date":"today"}""");
        using var exp2 = JsonDocument.Parse("""{"amount":60.00,"category":"Food","description":"Groceries","date":"today"}""");
        Assert.True((await mcp.CallToolAsync("add_expense", exp1.RootElement, new McpToolCallContext())).Success);
        Assert.True((await mcp.CallToolAsync("add_expense", exp2.RootElement, new McpToolCallContext())).Success);

        using var byCatArgs = JsonDocument.Parse("""{"timeframe":"this_month","chart_type":"doughnut"}""");
        var catRes = await mcp.CallToolAsync("get_expenses_by_category", byCatArgs.RootElement, new McpToolCallContext());

        Assert.True(catRes.Success);
        Assert.NotNull(catRes.SuggestedTable);
        Assert.Equal(2, catRes.SuggestedTable!.Rows.Count);
        Assert.NotNull(catRes.SuggestedChart);
        Assert.Equal("doughnut", catRes.SuggestedChart!.ChartType);

        // Generate real PDF + DOCX reports
        using var repArgs = JsonDocument.Parse("""{"report_type":"expenses","timeframe":"this_month"}""");
        var repRes = await mcp.CallToolAsync("generate_report", repArgs.RootElement, new McpToolCallContext());
        Assert.True(repRes.Success);
        Assert.NotNull(repRes.GeneratedReport);

        var pdfFile = await reports.GetReportFileAsync(repRes.GeneratedReport!.ReportId, "pdf");
        var docxFile = await reports.GetReportFileAsync(repRes.GeneratedReport!.ReportId, "docx");

        Assert.NotNull(pdfFile);
        Assert.True(pdfFile!.Bytes.Length > 200);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdfFile.Bytes, 0, 4));

        Assert.NotNull(docxFile);
        Assert.True(docxFile!.Bytes.Length > 200);
        Assert.Equal("PK", Encoding.ASCII.GetString(docxFile.Bytes, 0, 2));
    }

    [Fact]
    public async Task ChatHistoryManagement_ClearSessionAndClearAll_DeletesOnlyChatRecords()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FridayDbContext>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IFridayOrchestrator>();

        var s1 = new Friday.Core.Entities.ChatSession { Title = "Chat 1" };
        var s2 = new Friday.Core.Entities.ChatSession { Title = "Chat 2" };
        db.ChatSessions.AddRange(s1, s2);
        db.ChatMessages.AddRange(
            new Friday.Core.Entities.ChatMessageRecord { ChatSessionId = s1.Id, Role = "user", Content = "Hello 1" },
            new Friday.Core.Entities.ChatMessageRecord { ChatSessionId = s2.Id, Role = "user", Content = "Hello 2" });
        db.Tasks.Add(new Friday.Core.Entities.TaskItem { Title = "Keep this task" });
        await db.SaveChangesAsync();

        Assert.True(await orchestrator.ClearSessionAsync(s1.Id));
        Assert.Equal(1, await db.ChatSessions.CountAsync());
        Assert.Equal(1, await db.ChatMessages.CountAsync());

        int clearedRest = await orchestrator.ClearAllSessionsAsync();
        Assert.Equal(1, clearedRest);
        Assert.Equal(0, await db.ChatSessions.CountAsync());
        Assert.Equal(0, await db.ChatMessages.CountAsync());
        Assert.Equal(1, await db.Tasks.CountAsync());
    }

    [Fact]
    public async Task GroqFailure_ReturnsSafeFriendlyErrorMessage()
    {
        using var scope = _provider.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IFridayOrchestrator>();

        _fakeGroq.EnqueueFailure();
        var res = await orchestrator.ProcessMessageAsync(new FridayChatRequest
        {
            Message = "What do I need to do today?"
        });

        Assert.True(res.IsError);
        Assert.Equal("Friday is temporarily unable to process that request.", res.Message);
    }

    [Fact]
    public async Task McpToolRelevanceSelector_FiltersSchemasPerIntent_AndFollowUpContext()
    {
        using var scope = _provider.CreateScope();
        var mcp = scope.ServiceProvider.GetRequiredService<IMcpClient>();
        var selector = scope.ServiceProvider.GetRequiredService<McpToolRelevanceSelector>();
        var allTools = await mcp.ListToolsAsync();

        Assert.True(allTools.Count >= 52, $"Expected >= 52 MCP tools after intelligence upgrade, found {allTools.Count}");

        var financeTools = selector.SelectRelevantTools("How much did I spend this month?", null, allTools);
        Assert.True(financeTools.Count < 20, $"Expected compact finance tool subset, got {financeTools.Count}");
        Assert.Contains(financeTools, t => t.Name == "get_monthly_expenses");
        Assert.Contains(financeTools, t => t.Name == "get_spending_statistics");

        var focusTools = selector.SelectRelevantTools("What should I focus on today?", null, allTools);
        Assert.True(focusTools.Count < 30, $"Expected pruned focus tool subset, got {focusTools.Count}");
        Assert.Contains(focusTools, t => t.Name == "get_daily_focus_context");
        Assert.Contains(focusTools, t => t.Name == "get_today_tasks");
        Assert.Contains(focusTools, t => t.Name == "get_today_events");
        Assert.Contains(focusTools, t => t.Name == "get_projects");
        Assert.DoesNotContain(focusTools, t => t.Safety == McpToolSafety.Destructive);

        var weeklyTools = selector.SelectRelevantTools("Summarize my week.", null, allTools);
        Assert.Contains(weeklyTools, t => t.Name == "get_weekly_summary");

        var followUpContext = new ConversationTurnContext
        {
            RecentModules = new List<string> { "tasks" },
            LastTask = new RecentEntityRef { Type = "Task", Id = 7, Title = "Prepare Demo" }
        };
        var followUpTools = selector.SelectRelevantTools("Rename that one to Demo v2", followUpContext, allTools);
        Assert.Contains(followUpTools, t => t.Name == "update_task");
    }

    [Fact]
    public async Task AmbiguousTargets_PreventSilentUpdateOrDeleteConfirmation()
    {
        using var scope = _provider.CreateScope();
        var mcp = scope.ServiceProvider.GetRequiredService<IMcpClient>();
        var db = scope.ServiceProvider.GetRequiredService<FridayDbContext>();

        using var t1 = JsonDocument.Parse("""{"title":"Weekly Report Draft","priority":"High"}""");
        using var t2 = JsonDocument.Parse("""{"title":"Weekly Report Final","priority":"Critical"}""");
        Assert.True((await mcp.CallToolAsync("create_task", t1.RootElement, new McpToolCallContext())).Success);
        Assert.True((await mcp.CallToolAsync("create_task", t2.RootElement, new McpToolCallContext())).Success);

        using var updateArgs = JsonDocument.Parse("""{"title_match":"Weekly Report","new_title":"Changed"}""");
        var updateRes = await mcp.CallToolAsync("update_task", updateArgs.RootElement, new McpToolCallContext());
        Assert.False(updateRes.Success);
        Assert.True(updateRes.IsAmbiguous);

        var session = new Friday.Core.Entities.ChatSession { Title = "Ambiguity Test" };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync();

        using var delArgs = JsonDocument.Parse("""{"title_match":"Weekly Report"}""");
        var delRes = await mcp.CallToolAsync("delete_task", delArgs.RootElement, new McpToolCallContext { SessionId = session.Id });
        Assert.True(delRes.IsAmbiguous);
        Assert.False(delRes.RequiresConfirmation);
        Assert.Equal(0, await db.PendingConfirmations.CountAsync());

        // Specific ID must queue explicit confirmation
        var specificId = await db.Tasks.Where(x => x.Title == "Weekly Report Draft").Select(x => x.Id).FirstAsync();
        using var delByIdArgs = JsonDocument.Parse($$"""{"id":{{specificId}}}""");
        var delByIdRes = await mcp.CallToolAsync("delete_task", delByIdArgs.RootElement, new McpToolCallContext { SessionId = session.Id });
        Assert.True(delByIdRes.RequiresConfirmation);
        Assert.Equal(1, await db.PendingConfirmations.CountAsync());
    }

    [Fact]
    public async Task UpgradedIntelligenceTools_ReturnScoredFocusWeeklySummaryTaskStatsAndMonthlyFinance()
    {
        using var scope = _provider.CreateScope();
        var mcp = scope.ServiceProvider.GetRequiredService<IMcpClient>();

        using var pArgs = JsonDocument.Parse("""{"name":"FRIDAY Core","priority":"High"}""");
        Assert.True((await mcp.CallToolAsync("create_project", pArgs.RootElement, new McpToolCallContext())).Success);

        using var tDone = JsonDocument.Parse("""{"title":"Audit DB","priority":"Medium","category":"Engineering","project_name":"FRIDAY Core"}""");
        Assert.True((await mcp.CallToolAsync("create_task", tDone.RootElement, new McpToolCallContext())).Success);
        using var completeArgs = JsonDocument.Parse("""{"title_match":"Audit DB"}""");
        Assert.True((await mcp.CallToolAsync("complete_task", completeArgs.RootElement, new McpToolCallContext())).Success);

        var yesterday = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");
        using var tOverdue = JsonDocument.Parse($$"""{"title":"Ship Upgrade","priority":"Critical","category":"Engineering","due_date":"{{yesterday}}","project_name":"FRIDAY Core"}""");
        Assert.True((await mcp.CallToolAsync("create_task", tOverdue.RootElement, new McpToolCallContext())).Success);

        using var exp1 = JsonDocument.Parse("""{"amount":500,"category":"Food","description":"Lunch","date":"today"}""");
        Assert.True((await mcp.CallToolAsync("add_expense", exp1.RootElement, new McpToolCallContext())).Success);

        var focusRes = await mcp.CallToolAsync("get_daily_focus_context", JsonDocument.Parse("{}").RootElement, new McpToolCallContext());
        Assert.True(focusRes.Success);
        var focusJson = JsonSerializer.Serialize(focusRes.Data);
        Assert.Contains("priority_score", focusJson);
        Assert.Contains("Ship Upgrade", focusJson);

        var weekRes = await mcp.CallToolAsync("get_weekly_summary", JsonDocument.Parse("""{"timeframe":"this_week"}""").RootElement, new McpToolCallContext());
        Assert.True(weekRes.Success);
        Assert.NotNull(weekRes.SuggestedTable);
        var weekJson = JsonSerializer.Serialize(weekRes.Data);
        Assert.Contains("\"completed_count\":1", weekJson);
        Assert.Contains("FRIDAY Core", weekJson);

        var statsRes = await mcp.CallToolAsync("get_task_statistics", JsonDocument.Parse("""{"timeframe":"all","include_chart":true}""").RootElement, new McpToolCallContext());
        Assert.True(statsRes.Success);
        Assert.NotNull(statsRes.SuggestedChart);
        Assert.Equal("bar", statsRes.SuggestedChart!.ChartType);

        var searchRes = await mcp.CallToolAsync("universal_search", JsonDocument.Parse("""{"query":"FRIDAY Core"}""").RootElement, new McpToolCallContext());
        Assert.True(searchRes.Success);
        Assert.Contains("ranked_results", JsonSerializer.Serialize(searchRes.Data));

        var financeCompareRes = await mcp.CallToolAsync("get_monthly_expenses", JsonDocument.Parse("""{"month_scope":"compare_months"}""").RootElement, new McpToolCallContext());
        Assert.True(financeCompareRes.Success);
        Assert.Contains("highest_spending_month", JsonSerializer.Serialize(financeCompareRes.Data));
    }

    [Fact]
    public async Task OrchestratorTurnContext_PersistsEntityMemoryAcrossTurns()
    {
        using var scope = _provider.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IFridayOrchestrator>();
        var db = scope.ServiceProvider.GetRequiredService<FridayDbContext>();

        _fakeGroq.EnqueueToolCall("create_task", """{"title":"Context Memory Task","priority":"High"}""");
        var resp = await orchestrator.ProcessMessageAsync(new FridayChatRequest
        {
            Message = "Add a task called Context Memory Task"
        });

        Assert.False(resp.IsError);
        var session = await db.ChatSessions.AsNoTracking().FirstAsync(s => s.Id == resp.SessionId);
        Assert.False(string.IsNullOrWhiteSpace(session.LastContextJson));

        var turnCtx = JsonSerializer.Deserialize<ConversationTurnContext>(session.LastContextJson!);
        Assert.NotNull(turnCtx);
        Assert.NotNull(turnCtx!.LastTask);
        Assert.Equal("Context Memory Task", turnCtx.LastTask!.Title);
        Assert.Contains("tasks", turnCtx.RecentModules);
    }

    [Fact]
    public void SpeechResponseCleaner_StripsMarkdownTablesJsonAndInternalDiagnostics()
    {
        var raw = """
            ### **Today's Tasks** (ID #7, Status: Pending)
            Checked via `get_today_tasks` with CorrelationId abc123 and gsk_secretTokenValue999.
            {"debug":"internal"}

            | Task | Status |
            |------|--------|
            | Weekly Report | Pending |
            | Study MCP | Completed |

            Download [PDF Report](https://localhost:7235/api/reports/rep1?format=pdf).
            """;

        var table = new TableResult
        {
            Title = "Today's Tasks",
            Columns = new List<string> { "Task", "Status" },
            Rows = new List<List<string>>
            {
                new() { "Weekly Report", "Pending" },
                new() { "Study MCP", "Completed" }
            }
        };

        var spoken = SpeechResponseCleaner.CleanForSpeech(raw, table: table);

        Assert.DoesNotContain("|", spoken);
        Assert.DoesNotContain("**", spoken);
        Assert.DoesNotContain("###", spoken);
        Assert.DoesNotContain("get_today_tasks", spoken);
        Assert.DoesNotContain("CorrelationId", spoken);
        Assert.DoesNotContain("gsk_secretTokenValue999", spoken);
        Assert.DoesNotContain("ID #7", spoken);
        Assert.DoesNotContain("{\"debug\"", spoken);
        Assert.Contains("Weekly Report", spoken);
        Assert.Contains("Pending", spoken);
        Assert.Contains("Study MCP", spoken);
    }

    [Fact]
    public async Task VoiceMode_ReusesSameOrchestratorPipelineSessionAndConfirmationSafety()
    {
        using var scope = _provider.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IFridayOrchestrator>();
        var db = scope.ServiceProvider.GetRequiredService<FridayDbContext>();

        // 1. Start session in Text Mode creating a task
        _fakeGroq.EnqueueToolCall("create_task", """{"title":"Study MCP","priority":"High"}""");
        var turn1 = await orchestrator.ProcessMessageAsync(new FridayChatRequest
        {
            Message = "Add Study MCP as a task tomorrow.",
            InputMode = "text"
        });
        Assert.False(turn1.IsError);
        Assert.Equal("text", turn1.InputMode);
        Assert.Equal(1, await db.Tasks.CountAsync());

        // 2. Continue same conversation in Voice Mode
        _fakeGroq.EnqueueToolCall("complete_task", """{"title_match":"Study MCP"}""");
        var turn2 = await orchestrator.ProcessMessageAsync(new FridayChatRequest
        {
            SessionId = turn1.SessionId,
            Message = "Mark Study MCP as completed.",
            InputMode = "voice"
        });
        Assert.False(turn2.IsError);
        Assert.Equal(turn1.SessionId, turn2.SessionId);
        Assert.Equal("voice", turn2.InputMode);
        Assert.False(string.IsNullOrWhiteSpace(turn2.SpokenSummary));

        // 3. Delete in Voice Mode MUST enforce confirmation gatekeeper
        _fakeGroq.EnqueueToolCall("delete_task", """{"title_match":"Study MCP"}""");
        var turn3 = await orchestrator.ProcessMessageAsync(new FridayChatRequest
        {
            SessionId = turn1.SessionId,
            Message = "Delete Study MCP.",
            InputMode = "voice"
        });
        Assert.NotNull(turn3.PendingConfirmation);
        Assert.Contains("Say Yes to confirm", turn3.SpokenSummary);
        Assert.Equal(1, await db.Tasks.CountAsync());

        // 4. Confirm verbally ("Yes confirm") in same session
        var turn4 = await orchestrator.ProcessMessageAsync(new FridayChatRequest
        {
            SessionId = turn1.SessionId,
            Message = "Yes confirm",
            InputMode = "voice"
        });
        Assert.False(turn4.IsError);
        Assert.Equal("voice", turn4.InputMode);
        Assert.Equal(0, await db.Tasks.CountAsync());
    }

    public void Dispose()
    {
        _provider.Dispose();
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Ignore temp file cleanup locks
        }
    }

    private sealed class ScriptedGroqClient : IGroqLlmClient
    {
        private readonly Queue<LlmCompletionResult> _queue = new();

        public bool IsConfigured => true;
        public string ConfiguredModel => "test-scripted-model";

        public void EnqueueToolCall(string toolName, string argsJson)
        {
            _queue.Enqueue(new LlmCompletionResult
            {
                Success = true,
                ToolCalls = new List<LlmToolCall>
                {
                    new() { Id = "call_1", Name = toolName, ArgumentsJson = argsJson }
                }
            });
        }

        public void EnqueueFailure()
        {
            _queue.Enqueue(new LlmCompletionResult
            {
                Success = false,
                ErrorCode = "HTTP_503"
            });
        }

        public Task<LlmCompletionResult> CompleteAsync(
            IReadOnlyList<LlmChatMessage> messages,
            IReadOnlyList<McpToolDescriptor> availableTools,
            CancellationToken ct = default)
        {
            if (_queue.Count > 0)
                return Task.FromResult(_queue.Dequeue());

            return Task.FromResult(new LlmCompletionResult
            {
                Success = true,
                Content = "Processed verified records."
            });
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string contentRootPath) => ContentRootPath = contentRootPath;
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Friday.Tests";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

