using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Friday.Core.Configuration;
using Friday.Core.Entities;
using Friday.Core.Interfaces;
using Friday.Core.Models;
using Friday.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FRIDAY.Services;

public class FridayOrchestrator : IFridayOrchestrator
{
    private static readonly HashSet<string> ConfirmKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "yes", "y", "yes please", "confirm", "confirmed", "yes confirm", "yes, confirm",
        "sure", "do it", "go ahead", "proceed", "delete it", "yes delete it", "yes, delete it",
        "please delete it", "confirm deletion"
    };

    private static readonly HashSet<string> CancelKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "no", "n", "nope", "no thanks", "no thank you", "cancel", "cancel it", "no cancel", "no, cancel",
        "abort", "stop", "nevermind", "never mind", "don't", "do not", "keep it"
    };

    private readonly FridayDbContext _db;
    private readonly IGroqLlmClient _groq;
    private readonly IMcpClient _mcpClient;
    private readonly IAuditLogger _audit;
    private readonly GroqOptions _groqOptions;
    private readonly IHostEnvironment _env;
    private readonly McpToolRelevanceSelector _toolSelector;
    private readonly ILogger<FridayOrchestrator> _logger;

    public FridayOrchestrator(
        FridayDbContext db,
        IGroqLlmClient groq,
        IMcpClient mcpClient,
        IAuditLogger audit,
        IOptions<GroqOptions> groqOptions,
        IHostEnvironment env,
        ILogger<FridayOrchestrator> logger,
        McpToolRelevanceSelector? toolSelector = null)
    {
        _db = db;
        _groq = groq;
        _mcpClient = mcpClient;
        _audit = audit;
        _groqOptions = groqOptions.Value;
        _env = env;
        _logger = logger;
        _toolSelector = toolSelector ?? new McpToolRelevanceSelector();
    }

    public async Task<FridayChatResponse> ProcessMessageAsync(FridayChatRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var correlationId = Guid.NewGuid().ToString("N")[..12];
        var inputMode = string.Equals(request.InputMode, "voice", StringComparison.OrdinalIgnoreCase) ? "voice" : "text";
        var userMessage = (request.Message ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(userMessage))
        {
            const string emptyMsg = "Please enter a message or question for Friday.";
            return new FridayChatResponse
            {
                Message = emptyMsg,
                SpokenSummary = emptyMsg,
                InputMode = inputMode,
                CorrelationId = correlationId,
                IsError = true
            };
        }

        if (userMessage.Length > 4000)
        {
            userMessage = userMessage[..4000];
        }

        // Convert browser timezone offset (JS getTimezoneOffset is minutes WEST of UTC; we pass minutes EAST or handle cleanly)
        // Note: Our frontend sends clientTimezoneOffsetMinutes as localOffsetEastMinutes = -new Date().getTimezoneOffset()
        var userOffset = TimeSpan.FromMinutes(Math.Clamp(request.ClientTimezoneOffsetMinutes, -840, 840));
        var stage = "SESSION_INIT";
        string? lastToolName = null;
        ChatSession? session = null;

        try
        {
            session = await GetOrCreateSessionAsync(request.SessionId, userMessage, ct);

            // Save user message immediately
            var userRecord = new ChatMessageRecord
            {
                ChatSessionId = session.Id,
                Role = "user",
                Content = userMessage,
                CreatedAt = DateTime.UtcNow
            };
            _db.ChatMessages.Add(userRecord);
            session.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            // 1. Check if there is an active server-side PendingConfirmation for destructive operations
            stage = "PENDING_CONFIRMATION_CHECK";
            var activePending = await _db.PendingConfirmations
                .Where(p => p.ChatSessionId == session.Id && !p.IsResolved && p.ExpiresAt > DateTime.UtcNow)
                .OrderByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (activePending is not null)
            {
                var normalizedInput = userMessage.Trim().TrimEnd('.', '!', '?');
                if (ConfirmKeywords.Contains(normalizedInput))
                {
                    stage = "EXECUTE_CONFIRMED_ACTION";
                    lastToolName = activePending.ToolName;
                    return await ExecuteConfirmedDestructiveActionAsync(session, activePending, userOffset, correlationId, inputMode, request.DebugMode, sw, ct);
                }

                if (CancelKeywords.Contains(normalizedInput))
                {
                    activePending.IsResolved = true;
                    var cancelMsg = $"Understood — I cancelled the deletion and kept {activePending.TargetSummary} intact.";
                    await SaveAssistantMessageAsync(session, cancelMsg, null, null, null, new List<string> { "Cancelled pending deletion" }, ct);
                    sw.Stop();
                    await _audit.LogRequestAsync(correlationId, "CHAT_CONFIRM_CANCELLED", true, sw.ElapsedMilliseconds, $"Mode={inputMode}; Tool={activePending.ToolName}", ct);
                    return new FridayChatResponse
                    {
                        SessionId = session.Id,
                        SessionTitle = session.Title,
                        Message = cancelMsg,
                        SpokenSummary = SpeechResponseCleaner.CleanForSpeech(cancelMsg),
                        InputMode = inputMode,
                        ActivitySummary = new List<string> { "Cancelled pending deletion" },
                        CorrelationId = correlationId
                    };
                }

                // User moved on to a different request; expire old confirmation so it never fires unexpectedly later
                activePending.IsResolved = true;
                await _db.SaveChangesAsync(ct);
            }

            // 2. Build minimal, privacy-conscious conversation & profile context
            stage = "MCP_LIST_TOOLS";
            var allAvailableTools = await _mcpClient.ListToolsAsync(ct);
            var existingTurnContext = ParseTurnContext(session.LastContextJson);
            var selectedTools = _toolSelector.SelectRelevantTools(userMessage, existingTurnContext, allAvailableTools);

            stage = "BUILD_PROMPT_AND_HISTORY";
            var systemPrompt = await BuildSystemPromptAsync(session, existingTurnContext, userOffset, ct);
            var historyMessages = await BuildConversationHistoryAsync(session.Id, userRecord.Id, systemPrompt, userMessage, ct);

            var activitySummary = new List<string>();
            var debugTools = new List<string>();
            TableResult? latestTable = null;
            ChartConfiguration? latestChart = null;
            ReportArtifactInfo? latestReport = null;
            PendingConfirmationDto? pendingConfirmation = null;
            var touchedModules = new List<string>();
            var capturedEntities = new List<RecentEntityRef>();
            string? capturedSearchQuery = null;
            bool anyToolFailed = false;
            bool anyAmbiguousResult = false;
            bool usedExpandedToolset = false;

            int maxRounds = Math.Clamp(_groqOptions.MaxToolRounds, 1, 8);
            string? finalAssistantText = null;

            for (int round = 0; round < maxRounds; round++)
            {
                stage = $"GROQ_COMPLETION_ROUND_{round}";
                var completion = await _groq.CompleteAsync(historyMessages, selectedTools, ct);
                if (!completion.Success)
                {
                    sw.Stop();
                    var auditOutcome = string.IsNullOrWhiteSpace(completion.DiagnosticDetail)
                        ? completion.ErrorCode
                        : $"{completion.ErrorCode}: {completion.DiagnosticDetail}";

                    await _audit.LogRequestAsync(correlationId, "CHAT_LLM_FAILURE", false, sw.ElapsedMilliseconds, auditOutcome, ct);

                    if (_env.IsDevelopment())
                    {
                        _logger.LogError(
                            "Friday chat aborted at Groq completion | CorrelationId={CorrelationId} FailingService=GroqLlmClient Stage={Stage} Model={Model} ErrorCode={ErrorCode} Detail={Detail}",
                            correlationId, stage, _groq.ConfiguredModel, completion.ErrorCode, completion.DiagnosticDetail ?? "none");
                    }

                    var errText = completion.IsMissingApiKey
                        ? "Friday is temporarily unable to process that request. Groq API is not configured yet — set `Groq:ApiKey` using .NET User Secrets or the `Groq__ApiKey` environment variable."
                        : "Friday is temporarily unable to process that request.";

                    await SaveAssistantMessageAsync(session, errText, null, null, null, null, ct);

                    return new FridayChatResponse
                    {
                        SessionId = session.Id,
                        SessionTitle = session.Title,
                        Message = errText,
                        SpokenSummary = SpeechResponseCleaner.CleanForSpeech(errText, isError: true),
                        InputMode = inputMode,
                        CorrelationId = correlationId,
                        IsError = true
                    };
                }

                if (completion.ToolCalls.Count == 0)
                {
                    finalAssistantText = completion.Content;
                    break;
                }

                // Record assistant tool_calls turn
                historyMessages.Add(new LlmChatMessage
                {
                    Role = "assistant",
                    Content = completion.Content,
                    ToolCalls = completion.ToolCalls
                });

                bool roundHadFailure = false;
                bool roundHadAmbiguity = false;

                foreach (var tc in completion.ToolCalls)
                {
                    stage = "MCP_TOOL_EXECUTION";
                    lastToolName = tc.Name;

                    JsonElement argsElement;
                    try
                    {
                        using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(tc.ArgumentsJson) ? "{}" : tc.ArgumentsJson);
                        argsElement = parsed.RootElement.Clone();
                    }
                    catch
                    {
                        using var empty = JsonDocument.Parse("{}");
                        argsElement = empty.RootElement.Clone();
                    }

                    if (tc.Name == "universal_search" && McpToolInputHelper.TryGetString(argsElement, "query", out var sq))
                    {
                        capturedSearchQuery = sq;
                    }

                    var toolResult = await _mcpClient.CallToolAsync(tc.Name, argsElement, new McpToolCallContext
                    {
                        SessionId = session.Id,
                        CorrelationId = correlationId,
                        UserUtcOffset = userOffset,
                        ConfirmedDestructiveExecution = false
                    }, ct);

                    debugTools.Add(tc.Name);
                    if (!string.IsNullOrWhiteSpace(toolResult.ModuleName) && !touchedModules.Contains(toolResult.ModuleName))
                    {
                        touchedModules.Add(toolResult.ModuleName);
                    }

                    if (!string.IsNullOrWhiteSpace(toolResult.HumanSummary) && !activitySummary.Contains(toolResult.HumanSummary))
                    {
                        activitySummary.Add(toolResult.HumanSummary);
                    }

                    if (!toolResult.Success)
                    {
                        anyToolFailed = true;
                        roundHadFailure = true;
                        if (_env.IsDevelopment())
                        {
                            _logger.LogWarning(
                                "MCP tool returned failure | CorrelationId={CorrelationId} Stage={Stage} Tool={Tool} Error={Error}",
                                correlationId, stage, tc.Name, toolResult.ErrorMessage ?? "unknown");
                        }
                    }

                    if (toolResult.IsAmbiguous)
                    {
                        anyAmbiguousResult = true;
                        roundHadAmbiguity = true;
                    }

                    if (toolResult.SuggestedTable is not null) latestTable = toolResult.SuggestedTable;
                    if (toolResult.SuggestedChart is not null) latestChart = toolResult.SuggestedChart;
                    if (toolResult.GeneratedReport is not null) latestReport = toolResult.GeneratedReport;

                    if (toolResult.RequiresConfirmation && toolResult.Confirmation is not null)
                    {
                        pendingConfirmation = toolResult.Confirmation;
                    }

                    if (toolResult.Success && !toolResult.IsAmbiguous && !toolResult.RequiresConfirmation)
                    {
                        CaptureEntitiesFromToolResult(tc.Name, toolResult.ModuleName, toolResult.Data, capturedEntities);
                    }

                    var serializedToolPayload = JsonSerializer.Serialize(new
                    {
                        success = toolResult.Success,
                        ambiguous = toolResult.IsAmbiguous,
                        requires_confirmation = toolResult.RequiresConfirmation,
                        error = toolResult.ErrorMessage,
                        data = toolResult.Data
                    });

                    historyMessages.Add(new LlmChatMessage
                    {
                        Role = "tool",
                        ToolCallId = tc.Id,
                        Name = tc.Name,
                        Content = serializedToolPayload
                    });
                }

                // Deterministic safety stop: if a destructive tool queued confirmation, immediately ask the user!
                if (pendingConfirmation is not null)
                {
                    finalAssistantText = $"{pendingConfirmation.PromptMessage} Reply **Yes** to confirm or **Cancel** to keep it.";
                    break;
                }

                // Stop tool-calling immediately when an action returned ambiguous matches so Friday clarifies safely
                if (roundHadAmbiguity)
                {
                    break;
                }

                // If a tool failed (e.g., unknown tool or missing lookup), expand to full toolset once before retrying
                if (roundHadFailure && !usedExpandedToolset)
                {
                    selectedTools = allAvailableTools;
                    usedExpandedToolset = true;
                }

                // If Round succeeded with complete terminal tools (no discovery drill-down or failure recovery needed),
                // break directly into synthesis to keep token usage lean on Groq's 8,000 TPM tier.
                if (!roundHadFailure && completion.ToolCalls.All(tc => IsTerminalTool(tc.Name)))
                {
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(finalAssistantText))
            {
                stage = "GROQ_FINAL_SYNTHESIS";
                var synthesisMessages = new List<LlmChatMessage>(historyMessages)
                {
                    new()
                    {
                        Role = "system",
                        Content = "Tool calling is now complete. Do NOT attempt any further tool calls. Summarize the answer clearly in plain Markdown using only the retrieved data above."
                    }
                };
                var synthesis = await _groq.CompleteAsync(synthesisMessages, Array.Empty<McpToolDescriptor>(), ct);
                if (!synthesis.Success && _env.IsDevelopment())
                {
                    _logger.LogError(
                        "Groq final synthesis failed | CorrelationId={CorrelationId} FailingService=GroqLlmClient Stage={Stage} ErrorCode={ErrorCode} Detail={Detail}",
                        correlationId, stage, synthesis.ErrorCode, synthesis.DiagnosticDetail ?? "none");
                }
                finalAssistantText = synthesis.Success && !string.IsNullOrWhiteSpace(synthesis.Content)
                    ? synthesis.Content
                    : (anyAmbiguousResult
                        ? "Multiple matching items were found. Could you specify which one you mean?"
                        : (anyToolFailed ? "I couldn't retrieve or update that record right now." : "I've processed your request."));
            }

            // Decide conservatively when to attach Table & Chart so simple answers stay clean
            stage = "RESPONSE_PERSISTENCE";
            bool userAskedForChart = UserRequestedChart(userMessage);
            bool userAskedForListOrTable = UserRequestedTableOrList(userMessage);

            var chartToReturn = DecideChartOutput(userMessage, userAskedForChart, latestChart);
            var tableToReturn = DecideTableOutput(userMessage, userAskedForListOrTable, userAskedForChart, latestTable, pendingConfirmation);

            // Update non-sensitive conversational entity & visualization context for follow-up turns
            await UpdateSessionTurnContextAsync(
                session,
                existingTurnContext,
                touchedModules,
                debugTools,
                capturedEntities,
                capturedSearchQuery,
                tableToReturn ?? latestTable,
                chartToReturn ?? latestChart,
                ct);

            await SaveAssistantMessageAsync(session, finalAssistantText, tableToReturn, chartToReturn, latestReport, activitySummary, ct);

            sw.Stop();
            await _audit.LogRequestAsync(correlationId, "CHAT_MESSAGE", true, sw.ElapsedMilliseconds, $"Mode={inputMode}; ToolsCalled={debugTools.Count}", ct);

            var spokenSummary = SpeechResponseCleaner.CleanForSpeech(
                finalAssistantText,
                tableToReturn,
                chartToReturn,
                pendingConfirmation,
                isError: false);

            return new FridayChatResponse
            {
                SessionId = session.Id,
                SessionTitle = session.Title,
                Message = finalAssistantText,
                SpokenSummary = spokenSummary,
                InputMode = inputMode,
                Table = tableToReturn,
                Chart = chartToReturn,
                Report = latestReport,
                PendingConfirmation = pendingConfirmation,
                ActivitySummary = activitySummary,
                DebugToolsCalled = request.DebugMode ? debugTools : null,
                CorrelationId = correlationId,
                TimestampUtc = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            if (_env.IsDevelopment())
            {
                _logger.LogError(
                    ex,
                    "Unhandled exception in FridayOrchestrator | CorrelationId={CorrelationId} FailingService=FridayOrchestrator Stage={Stage} McpTool={ToolName} ExceptionType={ExceptionType}",
                    correlationId, stage, lastToolName ?? "none", ex.GetType().Name);
            }
            else
            {
                _logger.LogError(
                    "Unhandled exception in FridayOrchestrator | CorrelationId={CorrelationId} Stage={Stage} ExceptionType={ExceptionType}",
                    correlationId, stage, ex.GetType().Name);
            }

            await _audit.LogRequestAsync(correlationId, "CHAT_UNHANDLED_ERROR", false, sw.ElapsedMilliseconds, $"Mode={inputMode}; {stage}: {ex.GetType().Name}", ct);

            const string safeMsg = "Friday is temporarily unable to process that request.";
            if (session is not null)
            {
                try
                {
                    await SaveAssistantMessageAsync(session, safeMsg, null, null, null, null, ct);
                }
                catch
                {
                    // Avoid masking original failure if DB write fails
                }
            }

            return new FridayChatResponse
            {
                SessionId = session?.Id ?? Guid.Empty,
                SessionTitle = session?.Title ?? "Conversation",
                Message = safeMsg,
                SpokenSummary = safeMsg,
                InputMode = inputMode,
                CorrelationId = correlationId,
                IsError = true,
                TimestampUtc = DateTime.UtcNow
            };
        }
    }

    public async Task<bool> ClearSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await _db.ChatSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return false;

        _db.ChatSessions.Remove(session);
        await _db.SaveChangesAsync(ct);
        await _audit.LogRequestAsync(Guid.NewGuid().ToString("N")[..12], "CHAT_SESSION_DELETED", true, 0, "Deleted single conversation", ct);
        return true;
    }

    public async Task<int> ClearAllSessionsAsync(CancellationToken ct = default)
    {
        await _db.PendingConfirmations.ExecuteDeleteAsync(ct);
        await _db.ChatMessages.ExecuteDeleteAsync(ct);
        int deleted = await _db.ChatSessions.ExecuteDeleteAsync(ct);
        await _audit.LogRequestAsync(Guid.NewGuid().ToString("N")[..12], "CHAT_HISTORY_CLEARED", true, 0, $"Cleared {deleted} conversation(s)", ct);
        return deleted;
    }

    private async Task<FridayChatResponse> ExecuteConfirmedDestructiveActionAsync(
        ChatSession session,
        PendingConfirmation pending,
        TimeSpan userOffset,
        string correlationId,
        string inputMode,
        bool debugMode,
        Stopwatch sw,
        CancellationToken ct)
    {
        pending.IsResolved = true;
        await _db.SaveChangesAsync(ct);

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(pending.ArgumentsJson) ? "{}" : pending.ArgumentsJson);
        var result = await _mcpClient.CallToolAsync(pending.ToolName, doc.RootElement.Clone(), new McpToolCallContext
        {
            SessionId = session.Id,
            CorrelationId = correlationId,
            UserUtcOffset = userOffset,
            ConfirmedDestructiveExecution = true
        }, ct);

        var reply = result.Success
            ? $"Confirmed — I've deleted {pending.TargetSummary}."
            : (result.ErrorMessage ?? "I couldn't complete that deletion right now.");

        var summary = new List<string> { result.HumanSummary };
        await SaveAssistantMessageAsync(session, reply, null, null, null, summary, ct);

        sw.Stop();
        await _audit.LogRequestAsync(correlationId, "CHAT_CONFIRM_EXECUTED", result.Success, sw.ElapsedMilliseconds, $"Mode={inputMode}; Tool={pending.ToolName}", ct);

        return new FridayChatResponse
        {
            SessionId = session.Id,
            SessionTitle = session.Title,
            Message = reply,
            SpokenSummary = SpeechResponseCleaner.CleanForSpeech(reply, isError: !result.Success),
            InputMode = inputMode,
            ActivitySummary = summary,
            DebugToolsCalled = debugMode ? new List<string> { pending.ToolName } : null,
            CorrelationId = correlationId,
            IsError = !result.Success
        };
    }

    private async Task<ChatSession> GetOrCreateSessionAsync(Guid? sessionId, string firstMessage, CancellationToken ct)
    {
        if (sessionId.HasValue && sessionId.Value != Guid.Empty)
        {
            var existing = await _db.ChatSessions.FirstOrDefaultAsync(s => s.Id == sessionId.Value, ct);
            if (existing is not null)
            {
                if (existing.Title == "New Conversation" && !string.IsNullOrWhiteSpace(firstMessage))
                {
                    existing.Title = BuildSessionTitle(firstMessage);
                }
                return existing;
            }
        }

        var created = new ChatSession
        {
            Id = Guid.NewGuid(),
            Title = BuildSessionTitle(firstMessage),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.ChatSessions.Add(created);
        await _db.SaveChangesAsync(ct);
        return created;
    }

    private static string BuildSessionTitle(string message)
    {
        var clean = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= 48 ? clean : clean[..48].TrimEnd() + "...";
    }

    private async Task<string> BuildSystemPromptAsync(
        ChatSession session,
        ConversationTurnContext? turnContext,
        TimeSpan userOffset,
        CancellationToken ct)
    {
        var localNow = DateTime.UtcNow + userOffset;
        var profile = await _db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine("You are Friday, an intelligent, trustworthy, concise Personal AI Life Assistant.");
        sb.AppendLine($"Local date/time: {localNow:yyyy-MM-dd HH:mm} ({localNow:dddd}), UTC{FormatOffset(userOffset)}.");
        sb.AppendLine();
        sb.AppendLine("OPERATING PRINCIPLES:");
        sb.AppendLine("1. REAL DATA ONLY: Always inspect stored SQLite/file data via tools first; never invent tasks, events, notes, projects, finances, health metrics, or counts.");
        sb.AppendLine("2. EMPTY RESULTS: If zero matching records exist, say clearly what you checked and offer a helpful next action (e.g. creating one).");
        sb.AppendLine("3. MULTI-MODULE REASONING: For broad questions ('What should I focus on?', 'Summarize my week', 'Do I have enough time?'), combine data across tasks, overdue items, schedule conflicts, and active projects before answering.");
        sb.AppendLine("4. TRANSPARENT PRIORITIZATION: Explain priority rankings clearly (overdue weight, due date urgency, priority level, linked project impact).");
        sb.AppendLine("5. CONVERSATIONAL MEMORY: Resolve pronouns and ordinals ('that', 'it', 'first one', 'that task/project') using the recent entities below, passing exact record IDs or exact titles to tools.");
        sb.AppendLine("6. AMBIGUITY & DESTRUCTIVE SAFETY: If a tool returns ambiguous candidates, list the choices with IDs and ask which one. When asked to delete, remove, or erase a record, ALWAYS invoke the appropriate delete tool immediately (never ask in plain text without calling the tool; the backend confirmation gatekeeper intercepts deletions safely).");
        sb.AppendLine("7. NATURAL CONCISE TONE: Be clear, helpful, and direct without filler ('Certainly!'). Never expose internal tool names, schema keys, or stack details.");
        sb.AppendLine("8. TABLES, CHARTS & REMINDERS: Keep summaries punchy when structured tables/charts are shown. Use `create_event` (reminder block) or task `due_date` updates when asked for reminders.");
        sb.AppendLine("9. HEALTH & FINANCE CARE: Never claim medical diagnoses from wellness logs; never present personal expense stats as regulated financial advice.");

        if (profile is not null)
        {
            sb.AppendLine();
            sb.Append("USER PROFILE: ");
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(profile.PreferredName)) parts.Add($"PreferredName={profile.PreferredName}");
            else if (!string.IsNullOrWhiteSpace(profile.FullName)) parts.Add($"Name={profile.FullName}");
            if (!string.IsNullOrWhiteSpace(profile.Occupation)) parts.Add($"Occupation={profile.Occupation}");
            if (!string.IsNullOrWhiteSpace(profile.Goals)) parts.Add($"Goals={profile.Goals}");
            if (!string.IsNullOrWhiteSpace(profile.Preferences)) parts.Add($"Preferences={profile.Preferences}");
            parts.Add($"Currency={profile.CurrencyCode}");
            sb.AppendLine(string.Join(", ", parts));
        }

        if (turnContext is not null)
        {
            sb.AppendLine();
            sb.AppendLine("RECENT CONVERSATION ENTITIES (resolve 'it', 'that', 'first one', follow-ups):");
            if (turnContext.LastTask is not null)
                sb.AppendLine($"- LastTask: #{turnContext.LastTask.Id} '{turnContext.LastTask.Title}' ({turnContext.LastTask.Meta})");
            if (turnContext.LastProject is not null)
                sb.AppendLine($"- LastProject: #{turnContext.LastProject.Id} '{turnContext.LastProject.Title}' ({turnContext.LastProject.Meta})");
            if (turnContext.LastEvent is not null)
                sb.AppendLine($"- LastEvent: #{turnContext.LastEvent.Id} '{turnContext.LastEvent.Title}' ({turnContext.LastEvent.Meta})");
            if (turnContext.LastNote is not null)
                sb.AppendLine($"- LastNote: #{turnContext.LastNote.Id} '{turnContext.LastNote.Title}' ({turnContext.LastNote.Meta})");
            if (turnContext.RecentItems.Count > 0)
            {
                sb.AppendLine("- RecentListedItems: " + string.Join("; ",
                    turnContext.RecentItems.Take(8).Select((r, idx) => $"{idx + 1}. [{r.Type}] #{r.Id} '{r.Title}'{(string.IsNullOrWhiteSpace(r.Meta) ? "" : $" ({r.Meta})")}")));
            }
            if (turnContext.LastTable is not null)
                sb.AppendLine($"- LastTable: '{turnContext.LastTable.Title}' ({turnContext.LastTable.Rows.Count} row(s), columns: {string.Join(", ", turnContext.LastTable.Columns)})");
            if (turnContext.LastChart is not null)
                sb.AppendLine($"- LastChart: {turnContext.LastChart.ChartType} '{turnContext.LastChart.Title}'");
        }

        return sb.ToString();
    }

    private async Task<List<LlmChatMessage>> BuildConversationHistoryAsync(
        Guid sessionId,
        long currentUserMessageId,
        string systemPrompt,
        string currentUserMessage,
        CancellationToken ct)
    {
        var list = new List<LlmChatMessage>
        {
            new() { Role = "system", Content = systemPrompt }
        };

        var priorMessages = await _db.ChatMessages.AsNoTracking()
            .Where(m => m.ChatSessionId == sessionId && m.Id < currentUserMessageId)
            .OrderByDescending(m => m.Id)
            .Take(8)
            .ToListAsync(ct);

        priorMessages.Reverse();
        foreach (var pm in priorMessages)
        {
            var content = pm.Content ?? string.Empty;
            if (content.Length > 450)
            {
                content = content[..450] + "...";
            }

            list.Add(new LlmChatMessage
            {
                Role = pm.Role == "assistant" ? "assistant" : "user",
                Content = content
            });
        }

        list.Add(new LlmChatMessage
        {
            Role = "user",
            Content = currentUserMessage
        });

        return list;
    }

    private async Task UpdateSessionTurnContextAsync(
        ChatSession session,
        ConversationTurnContext? previousContext,
        List<string> modules,
        List<string> tools,
        List<RecentEntityRef> capturedEntities,
        string? searchQuery,
        TableResult? table,
        ChartConfiguration? chart,
        CancellationToken ct)
    {
        // Do not persist restricted health details in LastContextJson
        bool includesHealth = modules.Any(m => string.Equals(m, "health", StringComparison.OrdinalIgnoreCase));

        var next = new ConversationTurnContext
        {
            RecentModules = modules.Count > 0 ? modules.Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList() : (previousContext?.RecentModules ?? new()),
            RecentTools = tools.Count > 0 ? tools.Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList() : (previousContext?.RecentTools ?? new()),
            LastSearchQuery = !string.IsNullOrWhiteSpace(searchQuery) ? searchQuery : previousContext?.LastSearchQuery,
            LastTask = capturedEntities.LastOrDefault(e => e.Type == "Task") ?? previousContext?.LastTask,
            LastProject = capturedEntities.LastOrDefault(e => e.Type == "Project") ?? previousContext?.LastProject,
            LastEvent = capturedEntities.LastOrDefault(e => e.Type == "Event") ?? previousContext?.LastEvent,
            LastNote = capturedEntities.LastOrDefault(e => e.Type == "Note") ?? previousContext?.LastNote,
            RecentItems = capturedEntities.Count > 0
                ? capturedEntities.Take(10).ToList()
                : (previousContext?.RecentItems ?? new()),
            LastTable = (!includesHealth && table is not null)
                ? new TableResult
                {
                    Title = table.Title,
                    Columns = table.Columns.Take(6).ToList(),
                    Rows = table.Rows.Take(10).Select(r => r.Take(6).ToList()).ToList(),
                    FooterSummary = table.FooterSummary,
                    SourceDescription = table.SourceDescription
                }
                : previousContext?.LastTable,
            LastChart = (!includesHealth && chart is not null)
                ? new ChartConfiguration
                {
                    ChartType = chart.ChartType,
                    Title = chart.Title,
                    Labels = chart.Labels.Take(12).ToList()
                }
                : previousContext?.LastChart
        };

        session.LastContextJson = JsonSerializer.Serialize(next);
        session.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private async Task SaveAssistantMessageAsync(
        ChatSession session,
        string content,
        TableResult? table,
        ChartConfiguration? chart,
        ReportArtifactInfo? report,
        List<string>? activitySummary,
        CancellationToken ct)
    {
        _db.ChatMessages.Add(new ChatMessageRecord
        {
            ChatSessionId = session.Id,
            Role = "assistant",
            Content = content,
            TableJson = table is not null ? JsonSerializer.Serialize(table) : null,
            ChartJson = chart is not null ? JsonSerializer.Serialize(chart) : null,
            ReportJson = report is not null ? JsonSerializer.Serialize(report) : null,
            ActivitySummaryJson = activitySummary is { Count: > 0 } ? JsonSerializer.Serialize(activitySummary) : null,
            CreatedAt = DateTime.UtcNow
        });
        session.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private static bool UserRequestedChart(string msg)
    {
        var lower = msg.ToLowerInvariant();
        return lower.Contains("chart") || lower.Contains("graph") || lower.Contains("plot")
            || lower.Contains("visualiz") || lower.Contains("visualis") || lower.Contains("pie")
            || lower.Contains("doughnut") || lower.Contains("trend") || lower.Contains("by category")
            || lower.Contains("by priority") || lower.Contains("by status") || lower.Contains("distribution")
            || lower.Contains("breakdown") || lower.Contains("for each day") || lower.Contains("compare");
    }

    private static bool UserRequestedTableOrList(string msg)
    {
        var lower = msg.ToLowerInvariant();
        if (lower.StartsWith("how many ") || lower.StartsWith("how much "))
            return false;

        return lower.Contains("show") || lower.Contains("list") || lower.Contains("find")
            || lower.Contains("search") || lower.Contains("overdue") || lower.Contains("upcoming")
            || lower.Contains("completed tasks") || lower.Contains("expenses") || lower.Contains("only show")
            || lower.Contains("table") || lower.Contains("schedule") || lower.Contains("appointments")
            || lower.Contains("focus") || lower.Contains("prioritize") || lower.Contains("summarize my week");
    }

    private static ChartConfiguration? DecideChartOutput(string msg, bool userAskedForChart, ChartConfiguration? candidate)
    {
        if (candidate is null || candidate.Datasets.Count == 0) return null;
        if (userAskedForChart) return candidate;

        var lower = msg.ToLowerInvariant();
        if (lower.Contains("analyze") || lower.Contains("productive") || lower.Contains("productivity")
            || lower.Contains("spending") || lower.Contains("sleep") || lower.Contains("weekly summary")
            || lower.Contains("summarize my week") || lower.Contains("compare"))
        {
            return candidate;
        }

        return null;
    }

    private static TableResult? DecideTableOutput(
        string msg,
        bool userAskedForListOrTable,
        bool userAskedForChartOnly,
        TableResult? candidate,
        PendingConfirmationDto? pendingConfirmation)
    {
        if (pendingConfirmation is not null || candidate is null || candidate.Rows.Count == 0)
            return null;

        var lower = msg.ToLowerInvariant();
        if (userAskedForChartOnly && (lower.Contains("make that a chart") || lower.Contains("as a chart") || lower.Contains("into a chart")))
            return null;

        if ((lower.StartsWith("how many ") || lower.StartsWith("how much ")) && !lower.Contains("list") && !lower.Contains("show") && !lower.Contains("table"))
            return null;

        if (userAskedForListOrTable || candidate.Rows.Count >= 2)
            return candidate;

        return null;
    }

    private static bool IsTerminalTool(string toolName)
    {
        // Discovery/search tools that often lead to a follow-up drill-down or action (e.g. read_document, get_project_details, update/delete)
        return toolName is not ("universal_search" or "list_files" or "search_files" or "get_projects" or "get_tasks" or "get_events" or "search_notes" or "get_expenses" or "get_health_records");
    }

    private static ConversationTurnContext? ParseTurnContext(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<ConversationTurnContext>(json);
        }
        catch
        {
            return null;
        }
    }

    private static void CaptureEntitiesFromToolResult(
        string toolName,
        string? moduleName,
        object? data,
        List<RecentEntityRef> captured)
    {
        if (data is null || string.Equals(moduleName, "health", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            var json = JsonSerializer.Serialize(data);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                var fallbackType = moduleName switch
                {
                    "tasks" => "Task",
                    "projects" => "Project",
                    "schedule" => "Event",
                    "notes" => "Note",
                    _ => null
                };
                if (fallbackType is not null)
                {
                    AddArrayEntities(root, fallbackType, captured);
                }
                return;
            }

            if (root.ValueKind != JsonValueKind.Object)
                return;

            // Direct single-item result (create_task, update_task, complete_task, create_project, etc.)
            if (TryReadEntity(root, InferEntityType(toolName, moduleName), out var direct))
            {
                AddOrReplaceEntity(captured, direct);
            }

            // Common container arrays
            TryAddContainerArray(root, "recommended_focus_order", "Task", captured);
            TryAddContainerArray(root, "tasks", "Task", captured);
            TryAddContainerArray(root, "overdue_tasks", "Task", captured);
            TryAddContainerArray(root, "today_tasks", "Task", captured);
            TryAddContainerArray(root, "completed_tasks", "Task", captured);
            TryAddContainerArray(root, "projects", "Project", captured);
            TryAddContainerArray(root, "active_projects", "Project", captured);
            TryAddContainerArray(root, "events", "Event", captured);
            TryAddContainerArray(root, "today_schedule", "Event", captured);
            TryAddContainerArray(root, "notes", "Note", captured);

            if (root.TryGetProperty("project", out var nestedProj) && TryReadEntity(nestedProj, "Project", out var projEntity))
            {
                AddOrReplaceEntity(captured, projEntity);
            }
        }
        catch
        {
            // Ignore metadata extraction issues; never fail a turn over context caching
        }
    }

    private static string InferEntityType(string toolName, string? moduleName)
    {
        if (toolName.Contains("task", StringComparison.OrdinalIgnoreCase) || moduleName == "tasks") return "Task";
        if (toolName.Contains("project", StringComparison.OrdinalIgnoreCase) || moduleName == "projects") return "Project";
        if (toolName.Contains("event", StringComparison.OrdinalIgnoreCase) || moduleName == "schedule") return "Event";
        if (toolName.Contains("note", StringComparison.OrdinalIgnoreCase) || moduleName == "notes") return "Note";
        return "Item";
    }

    private static void TryAddContainerArray(JsonElement root, string propName, string typeName, List<RecentEntityRef> captured)
    {
        if (root.TryGetProperty(propName, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            AddArrayEntities(arr, typeName, captured);
        }
    }

    private static void AddArrayEntities(JsonElement arr, string typeName, List<RecentEntityRef> captured)
    {
        int added = 0;
        foreach (var el in arr.EnumerateArray())
        {
            if (added >= 6 || captured.Count >= 10) break;
            if (TryReadEntity(el, typeName, out var entity))
            {
                AddOrReplaceEntity(captured, entity);
                added++;
            }
        }
    }

    private static bool TryReadEntity(JsonElement el, string typeName, out RecentEntityRef entity)
    {
        entity = new RecentEntityRef();
        if (el.ValueKind != JsonValueKind.Object) return false;

        int id = 0;
        if (el.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var idLower)) id = idLower;
        else if (el.TryGetProperty("Id", out var idPropUpper) && idPropUpper.TryGetInt32(out var idUpper)) id = idUpper;

        string? title = null;
        if (el.TryGetProperty("title", out var t1) && t1.ValueKind == JsonValueKind.String) title = t1.GetString();
        else if (el.TryGetProperty("Title", out var t2) && t2.ValueKind == JsonValueKind.String) title = t2.GetString();
        else if (el.TryGetProperty("name", out var n1) && n1.ValueKind == JsonValueKind.String) title = n1.GetString();
        else if (el.TryGetProperty("Name", out var n2) && n2.ValueKind == JsonValueKind.String) title = n2.GetString();

        if (id <= 0 || string.IsNullOrWhiteSpace(title))
            return false;

        var metaParts = new List<string>();
        if (el.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(st.GetString()))
            metaParts.Add(st.GetString()!);
        if (el.TryGetProperty("priority", out var pr) && pr.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(pr.GetString()))
            metaParts.Add(pr.GetString()!);
        if (el.TryGetProperty("due_date", out var d1) && d1.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(d1.GetString()))
            metaParts.Add($"due {d1.GetString()}");
        else if (el.TryGetProperty("due", out var d2) && d2.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(d2.GetString()))
            metaParts.Add($"due {d2.GetString()}");
        else if (el.TryGetProperty("start", out var s1) && s1.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(s1.GetString()))
            metaParts.Add($"at {s1.GetString()}");

        entity = new RecentEntityRef
        {
            Type = typeName,
            Id = id,
            Title = title,
            Meta = string.Join(", ", metaParts)
        };
        return true;
    }

    private static void AddOrReplaceEntity(List<RecentEntityRef> list, RecentEntityRef entity)
    {
        list.RemoveAll(e => e.Type == entity.Type && e.Id == entity.Id);
        if (list.Count < 10)
        {
            list.Add(entity);
        }
    }

    private static class McpToolInputHelper
    {
        public static bool TryGetString(JsonElement el, string name, out string value)
        {
            value = string.Empty;
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String)
            {
                value = p.GetString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(value);
            }
            return false;
        }
    }

    private static string FormatOffset(TimeSpan offset)
    {
        var sign = offset >= TimeSpan.Zero ? "+" : "-";
        var abs = offset.Duration();
        return $"{sign}{abs.Hours:00}:{abs.Minutes:00}";
    }
}

