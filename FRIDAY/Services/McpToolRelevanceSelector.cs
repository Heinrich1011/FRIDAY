using System.Text.RegularExpressions;
using Friday.Core.Enums;
using Friday.Core.Models;

namespace FRIDAY.Services;

/// <summary>
/// Selects a focused, context-aware subset of registered MCP tools for each turn
/// to improve Groq reasoning precision and respect Groq's 8,000 TPM budget.
/// All tools remain registered in IMcpClient; this only scopes what schemas are sent per LLM request.
/// </summary>
public class McpToolRelevanceSelector
{
    private static readonly string[] CoreStarterTools =
    {
        "get_daily_focus_context",
        "get_weekly_summary",
        "universal_search",
        "get_profile",
        "get_tasks",
        "get_today_tasks",
        "create_task",
        "update_task",
        "get_today_events",
        "create_event",
        "get_projects",
        "search_notes",
        "get_expenses",
        "get_health_summary"
    };

    private static readonly string[] FocusTaskSubset =
    {
        "get_daily_focus_context",
        "get_today_tasks",
        "get_overdue_tasks",
        "get_tasks",
        "update_task",
        "complete_task",
        "get_today_events",
        "get_upcoming_events",
        "get_projects",
        "get_project_details",
        "get_profile"
    };

    public IReadOnlyList<McpToolDescriptor> SelectRelevantTools(
        string userMessage,
        ConversationTurnContext? turnContext,
        IReadOnlyList<McpToolDescriptor> allTools,
        bool includeAllFallback = false)
    {
        if (includeAllFallback || allTools.Count <= 16)
        {
            return allTools;
        }

        var lower = (userMessage ?? string.Empty).Trim().ToLowerInvariant();
        var modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var explicitTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool wantsDelete = ContainsAnyWord(lower, "delete", "remove", "erase", "drop", "trash", "discard", "clear");
        bool wantsModify = wantsDelete || ContainsAnyWord(lower, "update", "rename", "edit", "change", "modify", "move", "reschedule", "mark", "complete", "finish", "done", "add", "create", "new", "log", "record", "set", "track");
        bool wantsChart = ContainsAnyWord(lower, "chart", "graph", "plot", "visual", "visualize", "visualise", "pie", "doughnut", "bar", "line", "trend", "compare", "comparison");
        bool wantsReport = ContainsAnyWord(lower, "report", "pdf", "docx", "export");
        bool wantsFollowUp = ContainsAnyWord(lower, "it", "that", "those", "this", "them", "previous", "last", "first", "second", "third", "instead", "only")
            || lower.Contains("first one") || lower.Contains("second one") || lower.Contains("that one") || lower.Contains("make that");

        // 1. Direct intent detection per module
        if (ContainsAnyWord(lower, "task", "tasks", "todo", "to-do", "checklist", "overdue", "backlog", "complete", "completed", "pending", "deadline", "deadlines")
            || lower.Contains("finish today") || lower.Contains("due today") || lower.Contains("need to do"))
        {
            modules.Add("tasks");
        }

        if (ContainsAnyWord(lower, "schedule", "scheduled", "calendar", "event", "events", "appointment", "appointments", "meeting", "meetings", "agenda", "remind", "reminder", "reminders", "free", "busy", "conflict", "overlap", "reschedule"))
        {
            modules.Add("schedule");
            if (ContainsAnyWord(lower, "remind", "reminder", "reminders"))
            {
                modules.Add("tasks");
            }
        }

        if (ContainsAnyWord(lower, "project", "projects", "milestone", "milestones", "roadmap", "deliverable", "deliverables", "workspace", "sprint")
            || lower.Contains("how am i doing") || lower.Contains("how is my"))
        {
            modules.Add("projects");
            modules.Add("tasks");
            if (ContainsAnyWord(lower, "time", "week", "schedule", "busy", "enough", "finish"))
            {
                modules.Add("schedule");
            }
        }

        if (ContainsAnyWord(lower, "note", "notes", "memo", "memos", "jot", "jotted", "journal", "idea", "ideas"))
        {
            modules.Add("notes");
        }

        if (ContainsAnyWord(lower, "expense", "expenses", "spend", "spent", "spending", "budget", "budgets", "money", "cost", "costs", "peso", "php", "usd", "dollar", "dollars", "transaction", "transactions", "paid", "purchase", "purchased"))
        {
            modules.Add("finance");
        }

        if (ContainsAnyWord(lower, "health", "sleep", "slept", "insomnia", "workout", "workouts", "exercise", "gym", "water", "hydration", "weight", "calories", "steps", "wellness", "fatigue", "tired", "rested", "blood", "heart", "metric", "metrics"))
        {
            modules.Add("health");
        }

        if (ContainsAnyWord(lower, "file", "files", "document", "documents", "doc", "docs", "folder", "folders", "markdown", "attachment", "approved")
            || (wantsReport && modules.Count > 0))
        {
            modules.Add("files");
        }

        if (ContainsAnyWord(lower, "profile", "occupation", "job", "goal", "goals", "currency", "timezone", "preference", "preferences")
            || lower.Contains("my name") || lower.Contains("who am i") || lower.Contains("about me"))
        {
            modules.Add("profile");
        }

        // 2. Cross-module / analytics intents
        bool isFocusIntent = ContainsAnyWord(lower, "focus", "prioritize", "priorities", "priority")
            || lower.Contains("what should i do") || lower.Contains("finish first") || lower.Contains("do first")
            || lower.Contains("what do i need to finish") || lower.Contains("enough time") || lower.Contains("what's on my plate");

        if (isFocusIntent)
        {
            foreach (var name in FocusTaskSubset) explicitTools.Add(name);
            modules.Add("tasks");
            modules.Add("analytics");
            if (lower.Contains("focus") || lower.Contains("today") || lower.Contains("schedule") || lower.Contains("enough time"))
            {
                modules.Add("schedule");
                modules.Add("projects");
            }
        }

        bool isWeeklyIntent = lower.Contains("summarize my week") || lower.Contains("weekly summary")
            || lower.Contains("this week") && ContainsAnyWord(lower, "summarize", "summary", "accomplish", "productive", "productivity", "overview", "recap")
            || ContainsAnyWord(lower, "productivity", "productive", "accomplished", "accomplish");

        if (isWeeklyIntent)
        {
            modules.Add("analytics");
            modules.Add("tasks");
            modules.Add("projects");
            if (lower.Contains("week") || lower.Contains("summarize") || lower.Contains("recap"))
            {
                modules.Add("schedule");
            }
        }

        bool isSearchIntent = ContainsAnyWord(lower, "search", "find", "lookup", "locate")
            || lower.Contains("everything about") || lower.Contains("everything related");
        if (isSearchIntent)
        {
            explicitTools.Add("universal_search");
            modules.Add("analytics");
            if (modules.Count <= 1)
            {
                modules.Add("notes");
                modules.Add("files");
                modules.Add("tasks");
                modules.Add("projects");
            }
        }

        if (wantsChart || wantsReport)
        {
            modules.Add("analytics");
            if (wantsChart) explicitTools.Add("build_chart_from_data");
            if (wantsReport) explicitTools.Add("generate_report");
        }

        // 3. Conversational context carry-over for follow-up turns
        if (turnContext is not null && (wantsFollowUp || modules.Count == 0))
        {
            foreach (var recentMod in turnContext.RecentModules)
            {
                if (!string.IsNullOrWhiteSpace(recentMod))
                {
                    modules.Add(recentMod);
                }
            }

            if (turnContext.LastTask is not null && (wantsFollowUp || ContainsAnyWord(lower, "rename", "complete", "mark", "finish", "first", "delete")))
            {
                modules.Add("tasks");
            }
            if (turnContext.LastProject is not null && wantsFollowUp)
            {
                modules.Add("projects");
            }
            if (turnContext.LastEvent is not null && wantsFollowUp)
            {
                modules.Add("schedule");
            }
            if (turnContext.LastNote is not null && wantsFollowUp)
            {
                modules.Add("notes");
            }
            foreach (var item in turnContext.RecentItems)
            {
                switch (item.Type.ToLowerInvariant())
                {
                    case "task": modules.Add("tasks"); break;
                    case "project": modules.Add("projects"); break;
                    case "event": modules.Add("schedule"); break;
                    case "note": modules.Add("notes"); break;
                }
            }
        }

        // 4. General or delete-target starter fallback if no clear module signal matched
        if (modules.Count == 0 && explicitTools.Count == 0)
        {
            if (wantsDelete)
            {
                var deleteOrLookup = allTools
                    .Where(t => t.Safety == McpToolSafety.Destructive || t.Name == "universal_search")
                    .ToList();
                return deleteOrLookup.Count > 0 ? deleteOrLookup : allTools;
            }

            var starter = allTools
                .Where(t => CoreStarterTools.Contains(t.Name, StringComparer.OrdinalIgnoreCase))
                .ToList();
            return starter.Count > 0 ? starter : allTools;
        }

        // 5. Assemble matched set while trimming unnecessary destructive/noise schemas on multi-module read queries
        var selected = new List<McpToolDescriptor>();
        foreach (var tool in allTools)
        {
            if (explicitTools.Contains(tool.Name))
            {
                selected.Add(tool);
                continue;
            }

            if (!modules.Contains(tool.ModuleName))
            {
                continue;
            }

            // When 3+ modules are active and the user is NOT deleting anything, omit delete_* schemas to save tokens & prevent accidents
            if (!wantsDelete && modules.Count >= 3 && tool.Safety == McpToolSafety.Destructive)
            {
                continue;
            }

            // When in a broad focus/search/summary multi-module query without write verbs, omit rarely-needed create/update schemas in secondary modules
            if (!wantsModify && modules.Count >= 3 && tool.Safety == McpToolSafety.Write && tool.Name != "generate_report")
            {
                continue;
            }

            // Always allow chart helper when charting is mentioned
            if (tool.ModuleName == "analytics" && !wantsReport && tool.Name == "generate_report" && modules.Count >= 2)
            {
                continue;
            }

            selected.Add(tool);
        }

        return selected.Count > 0 ? selected : allTools;
    }

    private static bool ContainsAnyWord(string lowerText, params string[] keywords)
    {
        foreach (var kw in keywords)
        {
            if (Regex.IsMatch(lowerText, $@"\b{Regex.Escape(kw)}\b", RegexOptions.CultureInvariant))
                return true;
        }
        return false;
    }
}
