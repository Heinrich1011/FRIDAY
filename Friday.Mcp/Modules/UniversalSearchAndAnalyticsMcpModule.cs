using System.Text.Json;
using Friday.Core.Enums;
using Friday.Core.Interfaces;
using Friday.Core.Models;
using Friday.Infrastructure.Data;
using Friday.Mcp.Protocol;
using Microsoft.EntityFrameworkCore;

namespace Friday.Mcp.Modules;

public class UniversalSearchAndAnalyticsMcpModule : IMcpToolModule
{
    private readonly FridayDbContext _db;
    private readonly FileMcpModule _fileModule;
    private readonly IReportGeneratorService _reportGenerator;

    public UniversalSearchAndAnalyticsMcpModule(
        FridayDbContext db,
        FileMcpModule fileModule,
        IReportGeneratorService reportGenerator)
    {
        _db = db;
        _fileModule = fileModule;
        _reportGenerator = reportGenerator;
    }

    public string ModuleName => "analytics";

    public IReadOnlyList<McpToolDescriptor> GetTools() => new List<McpToolDescriptor>
    {
        new()
        {
            Name = "universal_search",
            ModuleName = ModuleName,
            HumanActionDescription = "Searched across your tasks, notes, projects, schedule, and documents",
            Description = "Performs a universal keyword search across Tasks, Notes, Projects, Schedule Events, and approved Documents.",
            Safety = McpToolSafety.Read,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["query"] = McpSchemaHelper.Prop("string", "Keyword or topic to search across all modules")
            }, required: new[] { "query" })
        },
        new()
        {
            Name = "get_daily_focus_context",
            ModuleName = ModuleName,
            HumanActionDescription = "Analyzed today's tasks, overdue deadlines, schedule, and active projects",
            Description = "Daily focus & prioritization: overdue tasks, today's tasks, ranked priority scores with reasons, upcoming deadlines, schedule events, and active projects",
            Safety = McpToolSafety.Read,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>())
        },
        new()
        {
            Name = "get_weekly_summary",
            ModuleName = ModuleName,
            HumanActionDescription = "Compiled your weekly summary",
            Description = "Summarizes the week across completed tasks, pending & overdue tasks, scheduled events, next 7 days deadlines, active projects, and optional weekly spending.",
            Safety = McpToolSafety.Read,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["timeframe"] = McpSchemaHelper.Prop("string", "Week window to summarize", new[] { "this_week", "last_week" }),
                ["include_chart"] = McpSchemaHelper.Prop("boolean", "Set true to include daily completion bar chart")
            })
        },
        new()
        {
            Name = "get_productivity_analysis",
            ModuleName = ModuleName,
            HumanActionDescription = "Analyzed your productivity metrics",
            Description = "Computes real cross-module productivity metrics (completed tasks, overdue tasks, scheduled events, project progress, and daily completion chart) for this_week or this_month.",
            Safety = McpToolSafety.Read,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["timeframe"] = McpSchemaHelper.Prop("string", "Period to analyze", new[] { "this_week", "last_week", "this_month" }),
                ["include_chart"] = McpSchemaHelper.Prop("boolean", "Set true to include a completion trend chart")
            })
        },
        new()
        {
            Name = "generate_report",
            ModuleName = ModuleName,
            HumanActionDescription = "Generated downloadable PDF and DOCX reports",
            Description = "Generates downloadable PDF and DOCX reports from verified SQLite records for tasks, expenses, projects, or productivity.",
            Safety = McpToolSafety.Write,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["report_type"] = McpSchemaHelper.Prop("string", "Type of report to generate", new[] { "productivity", "tasks", "expenses", "projects" }),
                ["timeframe"] = McpSchemaHelper.Prop("string", "Time period for the report", new[] { "today", "this_week", "this_month", "all" }),
                ["custom_title"] = McpSchemaHelper.Prop("string", "Optional custom report heading")
            }, required: new[] { "report_type" })
        },
        new()
        {
            Name = "build_chart_from_data",
            ModuleName = ModuleName,
            HumanActionDescription = "Created a chart from your records",
            Description = "Builds a strongly typed chart configuration using verified numbers already retrieved from MCP tools (useful when the user says 'Make that a chart' or asks for a specific chart type).",
            Safety = McpToolSafety.Read,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["chart_type"] = McpSchemaHelper.Prop("string", "Chart type", new[] { "bar", "column", "pie", "doughnut", "line", "area", "groupedBar", "stackedBar" }),
                ["title"] = McpSchemaHelper.Prop("string", "Chart title"),
                ["labels_csv"] = McpSchemaHelper.Prop("string", "Comma-separated labels matching exact retrieved categories/dates"),
                ["values_csv"] = McpSchemaHelper.Prop("string", "Comma-separated numeric values matching exact retrieved numbers"),
                ["series_label"] = McpSchemaHelper.Prop("string", "Dataset series label"),
                ["x_axis_label"] = McpSchemaHelper.Prop("string", "Optional X-axis title"),
                ["y_axis_label"] = McpSchemaHelper.Prop("string", "Optional Y-axis title"),
                ["source_description"] = McpSchemaHelper.Prop("string", "Data source description")
            }, required: new[] { "chart_type", "title", "labels_csv", "values_csv" })
        }
    };

    public Task<string?> DescribeTargetForDestructiveActionAsync(string toolName, JsonElement arguments, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public async Task<McpToolCallResult> ExecuteToolAsync(string toolName, JsonElement arguments, McpToolCallContext context, CancellationToken ct = default)
    {
        switch (toolName)
        {
            case "universal_search":
            {
                var q = McpSchemaHelper.GetRequiredString(arguments, "query", 200).Trim();
                var lower = q.ToLowerInvariant();

                var tasks = await _db.Tasks.AsNoTracking()
                    .Where(t => t.Title.ToLower().Contains(lower)
                        || (t.Description != null && t.Description.ToLower().Contains(lower))
                        || (t.Notes != null && t.Notes.ToLower().Contains(lower))
                        || (t.Category != null && t.Category.ToLower().Contains(lower)))
                    .OrderByDescending(t => t.UpdatedAt)
                    .Take(25)
                    .ToListAsync(ct);

                var notes = await _db.Notes.AsNoTracking()
                    .Where(n => n.Title.ToLower().Contains(lower)
                        || n.Content.ToLower().Contains(lower)
                        || (n.Tags != null && n.Tags.ToLower().Contains(lower))
                        || (n.Category != null && n.Category.ToLower().Contains(lower)))
                    .OrderByDescending(n => n.UpdatedAt)
                    .Take(25)
                    .ToListAsync(ct);

                var projects = await _db.Projects.AsNoTracking()
                    .Where(p => p.Name.ToLower().Contains(lower)
                        || (p.Description != null && p.Description.ToLower().Contains(lower))
                        || (p.Notes != null && p.Notes.ToLower().Contains(lower)))
                    .OrderByDescending(p => p.Priority)
                    .ThenByDescending(p => p.UpdatedAt)
                    .Take(15)
                    .ToListAsync(ct);

                var events = await _db.ScheduleEvents.AsNoTracking()
                    .Where(e => e.Title.ToLower().Contains(lower)
                        || (e.Description != null && e.Description.ToLower().Contains(lower))
                        || (e.Location != null && e.Location.ToLower().Contains(lower)))
                    .OrderByDescending(e => e.StartDateTime)
                    .Take(20)
                    .ToListAsync(ct);

                var docs = await _fileModule.SearchApprovedFilesAsync(q, context.UserUtcOffset, ct);

                static int ScoreHit(string title, string? tagsOrCategory, string? body, DateTime updatedUtc, string needle)
                {
                    int score = 0;
                    var t = (title ?? string.Empty).Trim().ToLowerInvariant();
                    if (t == needle) score += 100;
                    else if (t.StartsWith(needle, StringComparison.Ordinal)) score += 70;
                    else if (t.Contains(needle, StringComparison.Ordinal)) score += 45;

                    if (!string.IsNullOrWhiteSpace(tagsOrCategory) && tagsOrCategory.ToLowerInvariant().Contains(needle, StringComparison.Ordinal))
                        score += 25;
                    if (!string.IsNullOrWhiteSpace(body) && body.ToLowerInvariant().Contains(needle, StringComparison.Ordinal))
                        score += 15;
                    if (updatedUtc >= DateTime.UtcNow.AddDays(-7))
                        score += 5;
                    return score;
                }

                var ranked = new List<(int Score, string Source, string Title, string Detail, string Date, int? Id)>();
                foreach (var p in projects)
                    ranked.Add((ScoreHit(p.Name, null, $"{p.Description} {p.Notes}", p.UpdatedAt, lower), "Project", p.Name, $"{p.Status} ({p.Priority})", McpSchemaHelper.FormatLocal(p.TargetDate, context.UserUtcOffset, "yyyy-MM-dd"), p.Id));
                foreach (var n in notes)
                    ranked.Add((ScoreHit(n.Title, $"{n.Category} {n.Tags}", n.Content, n.UpdatedAt, lower), "Note", n.Title, n.Category ?? (n.Tags ?? "Note"), McpSchemaHelper.FormatLocal(n.UpdatedAt, context.UserUtcOffset), n.Id));
                foreach (var t in tasks)
                    ranked.Add((ScoreHit(t.Title, t.Category, $"{t.Description} {t.Notes}", t.UpdatedAt, lower), "Task", t.Title, $"{t.Status} ({t.Priority})", McpSchemaHelper.FormatLocal(t.DueDate, context.UserUtcOffset), t.Id));
                foreach (var e in events)
                    ranked.Add((ScoreHit(e.Title, e.Category, $"{e.Description} {e.Location}", e.UpdatedAt, lower), "Schedule", e.Title, e.Location ?? "Event", McpSchemaHelper.FormatLocal(e.StartDateTime, context.UserUtcOffset), e.Id));
                foreach (var d in docs)
                {
                    var docEl = JsonSerializer.SerializeToElement(d);
                    string? docName = TryGetJsonStr(docEl, "file_name") ?? TryGetJsonStr(docEl, "name") ?? TryGetJsonStr(docEl, "FileName") ?? "Document";
                    string? docPath = TryGetJsonStr(docEl, "relative_path") ?? TryGetJsonStr(docEl, "path") ?? TryGetJsonStr(docEl, "RelativePath") ?? "Approved File";
                    string? docSnippet = TryGetJsonStr(docEl, "snippet") ?? TryGetJsonStr(docEl, "Snippet") ?? TryGetJsonStr(docEl, "preview");
                    ranked.Add((ScoreHit(docName, null, docSnippet, DateTime.UtcNow, lower), "File", docName, docPath, McpSchemaHelper.FormatLocal(DateTime.UtcNow, context.UserUtcOffset), null));
                }

                static string? TryGetJsonStr(JsonElement el, string prop)
                    => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String
                        ? p.GetString()
                        : null;

                var orderedHits = ranked
                    .OrderByDescending(r => r.Score)
                    .ThenBy(r => r.Source)
                    .Take(30)
                    .ToList();

                var rows = orderedHits
                    .Select(r => new List<string> { r.Source, r.Title, r.Detail, r.Date })
                    .ToList();

                int totalHits = orderedHits.Count;
                var table = rows.Count > 0 ? new TableResult
                {
                    Title = $"Universal Search Results for '{q}'",
                    Columns = new List<string> { "Source", "Title / Name", "Status / Category", "Date" },
                    Rows = rows,
                    FooterSummary = $"{totalHits} ranked match(es) across workspace modules",
                    SourceDescription = "SQLite & Approved Files"
                } : null;

                return McpToolCallResult.Ok(toolName, ModuleName, $"Searched all modules for '{q}' ({totalHits} match(es))", new
                {
                    query = q,
                    total_matches = totalHits,
                    ranked_results = orderedHits.Select(r => new { id = r.Id, source = r.Source, title = r.Title, detail = r.Detail, date = r.Date, relevance_score = r.Score }),
                    projects = projects.Select(p => new { p.Id, p.Name, status = p.Status.ToString(), priority = p.Priority.ToString() }),
                    tasks = tasks.Select(t => new { t.Id, t.Title, status = t.Status.ToString(), priority = t.Priority.ToString(), due = McpSchemaHelper.FormatLocal(t.DueDate, context.UserUtcOffset) }),
                    notes = notes.Select(n => new { n.Id, n.Title, n.Category, n.Tags, preview = n.Content.Length <= 220 ? n.Content : n.Content[..220] + "..." }),
                    events = events.Select(e => new { e.Id, e.Title, start = McpSchemaHelper.FormatLocal(e.StartDateTime, context.UserUtcOffset), e.Location }),
                    documents = docs
                }, table);
            }

            case "get_daily_focus_context":
            {
                var nowUtc = DateTime.UtcNow;
                var (todayStartUtc, tomorrowStartUtc, _) = McpSchemaHelper.GetTimeframeBoundsUtc("today", context.UserUtcOffset);
                var threeDaysOutUtc = tomorrowStartUtc.AddDays(3);
                var sevenDaysOutUtc = tomorrowStartUtc.AddDays(7);

                var openTasks = await _db.Tasks.AsNoTracking().Include(t => t.Project)
                    .Where(t => t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled)
                    .OrderByDescending(t => t.Priority)
                    .ThenBy(t => t.DueDate ?? DateTime.MaxValue)
                    .ToListAsync(ct);

                var overdue = openTasks.Where(t => t.DueDate.HasValue && t.DueDate.Value < todayStartUtc).ToList();
                var dueToday = openTasks.Where(t => t.DueDate.HasValue && t.DueDate.Value >= todayStartUtc && t.DueDate.Value < tomorrowStartUtc).ToList();
                var dueSoon = openTasks.Where(t => t.DueDate.HasValue && t.DueDate.Value >= tomorrowStartUtc && t.DueDate.Value < threeDaysOutUtc).ToList();
                var highPriorityUnscheduled = openTasks.Where(t => !t.DueDate.HasValue && (t.Priority == PriorityLevel.Critical || t.Priority == PriorityLevel.High)).Take(5).ToList();

                var scoredTasks = openTasks.Select(t =>
                {
                    int score = t.Priority switch
                    {
                        PriorityLevel.Critical => 50,
                        PriorityLevel.High => 35,
                        PriorityLevel.Medium => 20,
                        _ => 10
                    };
                    var reasons = new List<string> { $"{t.Priority} priority (+{score})" };
                    string urgency = "No due date";

                    if (t.DueDate.HasValue)
                    {
                        if (t.DueDate.Value < todayStartUtc)
                        {
                            var daysOverdue = Math.Max(1, (int)Math.Ceiling((todayStartUtc - t.DueDate.Value).TotalDays));
                            var bonus = 45 + Math.Min(15, daysOverdue * 3);
                            score += bonus;
                            urgency = $"Overdue by {daysOverdue}d";
                            reasons.Add($"{urgency} (+{bonus})");
                        }
                        else if (t.DueDate.Value < tomorrowStartUtc)
                        {
                            score += 35;
                            urgency = "Due today";
                            reasons.Add("Due today (+35)");
                        }
                        else if (t.DueDate.Value < threeDaysOutUtc)
                        {
                            score += 20;
                            urgency = "Due within 3 days";
                            reasons.Add("Due within 3 days (+20)");
                        }
                        else if (t.DueDate.Value < sevenDaysOutUtc)
                        {
                            score += 10;
                            urgency = "Due this week";
                            reasons.Add("Due within 7 days (+10)");
                        }
                        else
                        {
                            urgency = McpSchemaHelper.FormatLocal(t.DueDate, context.UserUtcOffset, "MMM dd");
                        }
                    }

                    if (t.Project is not null && t.Project.Status == ProjectStatus.Active &&
                        (t.Project.Priority == PriorityLevel.Critical || t.Project.Priority == PriorityLevel.High))
                    {
                        score += 10;
                        reasons.Add($"Active {t.Project.Priority}-priority project '{t.Project.Name}' (+10)");
                    }

                    return new
                    {
                        id = t.Id,
                        title = t.Title,
                        priority = t.Priority.ToString(),
                        status = t.Status.ToString(),
                        due = McpSchemaHelper.FormatLocal(t.DueDate, context.UserUtcOffset),
                        urgency,
                        project = t.Project?.Name,
                        priority_score = score,
                        score_reasons = reasons
                    };
                })
                .OrderByDescending(x => x.priority_score)
                .ThenBy(x => x.id)
                .ToList();

                var todayEvents = await _db.ScheduleEvents.AsNoTracking()
                    .Where(e => e.StartDateTime >= todayStartUtc && e.StartDateTime < tomorrowStartUtc)
                    .OrderBy(e => e.StartDateTime)
                    .ToListAsync(ct);

                var upcomingEvents = await _db.ScheduleEvents.AsNoTracking()
                    .Where(e => e.StartDateTime >= tomorrowStartUtc && e.StartDateTime < sevenDaysOutUtc)
                    .OrderBy(e => e.StartDateTime)
                    .Take(10)
                    .ToListAsync(ct);

                var activeProjects = await _db.Projects.AsNoTracking().Include(p => p.Tasks)
                    .Where(p => p.Status == ProjectStatus.Active)
                    .OrderByDescending(p => p.Priority)
                    .Take(6)
                    .ToListAsync(ct);

                var busyMinutesToday = todayEvents.Sum(e => e.EndDateTime.HasValue
                    ? Math.Max(0, (int)(e.EndDateTime.Value - e.StartDateTime).TotalMinutes)
                    : 60);

                var rankedTable = scoredTasks.Count > 0
                    ? new TableResult
                    {
                        Title = "Prioritized Focus Queue",
                        Columns = new List<string> { "#", "Task", "Priority", "Urgency", "Score", "Why" },
                        Rows = scoredTasks.Take(8).Select((t, idx) => new List<string>
                        {
                            (idx + 1).ToString(),
                            t.title,
                            t.priority,
                            t.urgency,
                            t.priority_score.ToString(),
                            string.Join("; ", t.score_reasons)
                        }).ToList(),
                        FooterSummary = $"Ranked {scoredTasks.Count} open task(s) | {todayEvents.Count} scheduled event(s) today",
                        SourceDescription = "SQLite Tasks, Schedule & Projects"
                    }
                    : null;

                return McpToolCallResult.Ok(toolName, ModuleName, "Checked today's tasks, overdue items, schedule, and active projects", new
                {
                    open_task_count = scoredTasks.Count,
                    priority_scoring_formula = "Stored Priority (Critical=50, High=35, Medium=20, Low=10) + Urgency (Overdue=45+, Today=35, Next 3d=20, Next 7d=10) + High/Critical Active Project Boost (+10)",
                    recommended_focus_order = scoredTasks.Take(8).ToList(),
                    overdue_tasks = overdue.Select(t => new { t.Id, t.Title, priority = t.Priority.ToString(), due = McpSchemaHelper.FormatLocal(t.DueDate, context.UserUtcOffset), project = t.Project?.Name }),
                    today_tasks = dueToday.Select(t => new { t.Id, t.Title, priority = t.Priority.ToString(), due = McpSchemaHelper.FormatLocal(t.DueDate, context.UserUtcOffset), project = t.Project?.Name }),
                    upcoming_deadlines_next_3_days = dueSoon.Select(t => new { t.Id, t.Title, priority = t.Priority.ToString(), due = McpSchemaHelper.FormatLocal(t.DueDate, context.UserUtcOffset), project = t.Project?.Name }),
                    high_priority_backlog = highPriorityUnscheduled.Select(t => new { t.Id, t.Title, priority = t.Priority.ToString(), project = t.Project?.Name }),
                    other_open_backlog = scoredTasks.Skip(0).Take(5).ToList(),
                    today_schedule = todayEvents.Select(e => new { e.Id, e.Title, start = McpSchemaHelper.FormatLocal(e.StartDateTime, context.UserUtcOffset), end = McpSchemaHelper.FormatLocal(e.EndDateTime, context.UserUtcOffset), e.Location }),
                    today_scheduled_minutes = busyMinutesToday,
                    upcoming_events_next_7_days = upcomingEvents.Select(e => new { e.Id, e.Title, start = McpSchemaHelper.FormatLocal(e.StartDateTime, context.UserUtcOffset), end = McpSchemaHelper.FormatLocal(e.EndDateTime, context.UserUtcOffset), e.Location }),
                    active_projects = activeProjects.Select(p => new
                    {
                        p.Id,
                        p.Name,
                        priority = p.Priority.ToString(),
                        target_date = McpSchemaHelper.FormatLocal(p.TargetDate, context.UserUtcOffset, "yyyy-MM-dd"),
                        completed_tasks = p.Tasks.Count(t => t.Status == TaskItemStatus.Completed),
                        total_tasks = p.Tasks.Count
                    })
                }, rankedTable);
            }

            case "get_weekly_summary":
            {
                var tf = McpSchemaHelper.GetOptionalString(arguments, "timeframe") ?? "this_week";
                var includeChart = McpSchemaHelper.GetOptionalBool(arguments, "include_chart") ?? true;
                var (startUtc, endUtc, label) = McpSchemaHelper.GetTimeframeBoundsUtc(tf, context.UserUtcOffset);
                var nowUtc = DateTime.UtcNow;

                var completed = await _db.Tasks.AsNoTracking().Include(t => t.Project)
                    .Where(t => t.Status == TaskItemStatus.Completed && t.CompletedAt.HasValue && t.CompletedAt.Value >= startUtc && t.CompletedAt.Value < endUtc)
                    .OrderByDescending(t => t.CompletedAt)
                    .ToListAsync(ct);

                var openTasks = await _db.Tasks.AsNoTracking().Include(t => t.Project)
                    .Where(t => t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled)
                    .OrderByDescending(t => t.Priority)
                    .ThenBy(t => t.DueDate ?? DateTime.MaxValue)
                    .ToListAsync(ct);

                var pendingInPeriod = openTasks.Where(t => !t.DueDate.HasValue || (t.DueDate.Value >= startUtc && t.DueDate.Value < endUtc)).ToList();
                var overdue = openTasks.Where(t => t.DueDate.HasValue && t.DueDate.Value < nowUtc).ToList();

                var weekEvents = await _db.ScheduleEvents.AsNoTracking()
                    .Where(e => e.StartDateTime >= startUtc && e.StartDateTime < endUtc)
                    .OrderBy(e => e.StartDateTime)
                    .ToListAsync(ct);

                var next7DaysEvents = await _db.ScheduleEvents.AsNoTracking()
                    .Where(e => e.StartDateTime >= nowUtc && e.StartDateTime < nowUtc.AddDays(7))
                    .OrderBy(e => e.StartDateTime)
                    .ToListAsync(ct);

                var activeProjects = await _db.Projects.AsNoTracking().Include(p => p.Tasks)
                    .Where(p => p.Status == ProjectStatus.Active)
                    .OrderByDescending(p => p.Priority)
                    .ToListAsync(ct);

                var profile = await _db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(ct);
                bool financeAllowed = profile?.AllowFinanceContextInAi ?? true;
                decimal? weeklySpent = null;
                int? weeklyExpenseCount = null;
                var currency = profile?.CurrencyCode ?? "PHP";

                if (financeAllowed)
                {
                    var expenses = await _db.Expenses.AsNoTracking()
                        .Where(e => e.Date >= startUtc && e.Date < endUtc)
                        .ToListAsync(ct);
                    weeklySpent = expenses.Sum(e => e.Amount);
                    weeklyExpenseCount = expenses.Count;
                }

                var summaryRows = new List<List<string>>
                {
                    new() { "Completed Tasks", completed.Count.ToString(), completed.Count > 0 ? string.Join(", ", completed.Take(3).Select(t => t.Title)) : "None recorded" },
                    new() { "Open / Pending Tasks", openTasks.Count.ToString(), openTasks.Count > 0 ? string.Join(", ", openTasks.Take(3).Select(t => t.Title)) : "All caught up" },
                    new() { "Overdue Tasks", overdue.Count.ToString(), overdue.Count > 0 ? string.Join(", ", overdue.Take(3).Select(t => t.Title)) : "None" },
                    new() { $"Schedule ({label})", weekEvents.Count.ToString(), weekEvents.Count > 0 ? string.Join(", ", weekEvents.Take(3).Select(e => e.Title)) : "No events recorded" },
                    new() { "Active Projects", activeProjects.Count.ToString(), activeProjects.Count > 0 ? string.Join(", ", activeProjects.Take(3).Select(p => $"{p.Name} ({p.Tasks.Count(t => t.Status == TaskItemStatus.Completed)}/{p.Tasks.Count})")) : "None" }
                };

                if (financeAllowed)
                {
                    summaryRows.Add(new List<string> { $"Spending ({label})", $"{currency} {weeklySpent:N2}", $"{weeklyExpenseCount} transaction(s)" });
                }

                var table = new TableResult
                {
                    Title = $"Weekly Summary ({label})",
                    Columns = new List<string> { "Area", "Metric", "Highlights" },
                    Rows = summaryRows,
                    FooterSummary = $"Completed: {completed.Count} | Open: {openTasks.Count} | Overdue: {overdue.Count} | Next 7d Events: {next7DaysEvents.Count}",
                    SourceDescription = "SQLite Multi-Module Weekly Summary"
                };

                ChartConfiguration? chart = null;
                if (includeChart && completed.Count > 0)
                {
                    var daily = completed
                        .GroupBy(t => (t.CompletedAt!.Value + context.UserUtcOffset).Date)
                        .OrderBy(g => g.Key)
                        .ToList();

                    chart = new ChartConfiguration
                    {
                        ChartType = daily.Count >= 2 ? "line" : "bar",
                        Title = $"Weekly Task Completions ({label})",
                        Labels = daily.Select(d => d.Key.ToString("ddd MMM dd")).ToList(),
                        Datasets = new List<ChartDataset>
                        {
                            new()
                            {
                                Label = "Completed Tasks",
                                Data = daily.Select(d => (double)d.Count()).ToList(),
                                BackgroundColor = new List<string> { "#10b981" },
                                BorderColor = "#059669"
                            }
                        },
                        XAxisLabel = "Day",
                        YAxisLabel = "Completed",
                        Legend = false,
                        SourceDescription = $"SQLite Tasks ({label})"
                    };
                }

                return McpToolCallResult.Ok(toolName, ModuleName, $"Compiled weekly summary for {label}", new
                {
                    timeframe = label,
                    completed_count = completed.Count,
                    completed_tasks = completed.Take(15).Select(t => new { t.Id, t.Title, priority = t.Priority.ToString(), project = t.Project?.Name, completed_at = McpSchemaHelper.FormatLocal(t.CompletedAt, context.UserUtcOffset) }),
                    open_tasks_count = openTasks.Count,
                    pending_tasks_in_period = pendingInPeriod.Take(15).Select(t => new { t.Id, t.Title, priority = t.Priority.ToString(), due = McpSchemaHelper.FormatLocal(t.DueDate, context.UserUtcOffset), project = t.Project?.Name }),
                    overdue_count = overdue.Count,
                    overdue_tasks = overdue.Take(10).Select(t => new { t.Id, t.Title, priority = t.Priority.ToString(), due = McpSchemaHelper.FormatLocal(t.DueDate, context.UserUtcOffset) }),
                    schedule_events_count = weekEvents.Count,
                    schedule_events = weekEvents.Select(e => new { e.Id, e.Title, start = McpSchemaHelper.FormatLocal(e.StartDateTime, context.UserUtcOffset), e.Location }),
                    upcoming_events_next_7_days = next7DaysEvents.Take(10).Select(e => new { e.Id, e.Title, start = McpSchemaHelper.FormatLocal(e.StartDateTime, context.UserUtcOffset), e.Location }),
                    active_projects = activeProjects.Select(p => new
                    {
                        p.Id,
                        p.Name,
                        priority = p.Priority.ToString(),
                        completed_tasks = p.Tasks.Count(t => t.Status == TaskItemStatus.Completed),
                        total_tasks = p.Tasks.Count,
                        target_date = McpSchemaHelper.FormatLocal(p.TargetDate, context.UserUtcOffset, "yyyy-MM-dd")
                    }),
                    weekly_finance = financeAllowed
                        ? new { included = true, currency, total_spent = weeklySpent, transaction_count = weeklyExpenseCount }
                        : new { included = false, currency, total_spent = (decimal?)null, transaction_count = (int?)null }
                }, table, chart);
            }

            case "get_productivity_analysis":
            {
                var tf = McpSchemaHelper.GetOptionalString(arguments, "timeframe") ?? "this_week";
                var (startUtc, endUtc, label) = McpSchemaHelper.GetTimeframeBoundsUtc(tf, context.UserUtcOffset);

                var completed = await _db.Tasks.AsNoTracking().Include(t => t.Project)
                    .Where(t => t.Status == TaskItemStatus.Completed && t.CompletedAt.HasValue && t.CompletedAt.Value >= startUtc && t.CompletedAt.Value < endUtc)
                    .ToListAsync(ct);

                var createdCount = await _db.Tasks.AsNoTracking()
                    .CountAsync(t => t.CreatedAt >= startUtc && t.CreatedAt < endUtc, ct);

                var overdueOpenCount = await _db.Tasks.AsNoTracking()
                    .CountAsync(t => t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled && t.DueDate.HasValue && t.DueDate.Value < DateTime.UtcNow, ct);

                var eventsCount = await _db.ScheduleEvents.AsNoTracking()
                    .CountAsync(e => e.StartDateTime >= startUtc && e.StartDateTime < endUtc, ct);

                var activeProjects = await _db.Projects.AsNoTracking().Include(p => p.Tasks)
                    .Where(p => p.Status == ProjectStatus.Active)
                    .ToListAsync(ct);

                ChartConfiguration? chart = null;
                if ((McpSchemaHelper.GetOptionalBool(arguments, "include_chart") ?? true) && completed.Count > 0)
                {
                    var daily = completed
                        .GroupBy(t => (t.CompletedAt!.Value + context.UserUtcOffset).Date)
                        .OrderBy(g => g.Key)
                        .ToList();

                    chart = new ChartConfiguration
                    {
                        ChartType = "bar",
                        Title = $"Tasks Completed Per Day ({label})",
                        Labels = daily.Select(d => d.Key.ToString("ddd MMM dd")).ToList(),
                        Datasets = new List<ChartDataset>
                        {
                            new()
                            {
                                Label = "Completed Tasks",
                                Data = daily.Select(d => (double)d.Count()).ToList(),
                                BackgroundColor = new List<string> { "#10b981" }
                            }
                        },
                        XAxisLabel = "Day",
                        YAxisLabel = "Completed",
                        Legend = false,
                        SourceDescription = $"SQLite Tasks ({label})"
                    };
                }

                return McpToolCallResult.Ok(toolName, ModuleName, $"Analyzed productivity for {label}", new
                {
                    timeframe = label,
                    completed_tasks_count = completed.Count,
                    tasks_created_in_period = createdCount,
                    currently_overdue_tasks = overdueOpenCount,
                    scheduled_events_in_period = eventsCount,
                    active_projects_count = activeProjects.Count,
                    completed_by_priority = Enum.GetValues<PriorityLevel>()
                        .ToDictionary(p => p.ToString(), p => completed.Count(t => t.Priority == p)),
                    active_project_progress = activeProjects.Select(p => new
                    {
                        p.Name,
                        completed_tasks = p.Tasks.Count(t => t.Status == TaskItemStatus.Completed),
                        total_tasks = p.Tasks.Count
                    }).ToList()
                }, chart: chart);
            }

            case "generate_report":
            {
                var reportType = McpSchemaHelper.GetRequiredString(arguments, "report_type", 60);
                var timeframe = McpSchemaHelper.GetOptionalString(arguments, "timeframe", 60) ?? "this_month";
                var customTitle = McpSchemaHelper.GetOptionalString(arguments, "custom_title", 150);

                var artifact = await _reportGenerator.GenerateReportAsync(new ReportGenerationRequest
                {
                    ReportType = reportType,
                    Timeframe = timeframe,
                    CustomTitle = customTitle,
                    UserUtcOffset = context.UserUtcOffset
                }, ct);

                return McpToolCallResult.Ok(
                    toolName,
                    ModuleName,
                    $"Generated PDF and DOCX report '{artifact.Title}'",
                    new
                    {
                        report_id = artifact.ReportId,
                        title = artifact.Title,
                        summary = artifact.Summary,
                        pdf_url = artifact.PdfDownloadUrl,
                        docx_url = artifact.DocxDownloadUrl
                    },
                    report: artifact);
            }

            case "build_chart_from_data":
            {
                var chartType = McpSchemaHelper.GetRequiredString(arguments, "chart_type", 40);
                var title = McpSchemaHelper.GetRequiredString(arguments, "title", 180);
                var labelsCsv = McpSchemaHelper.GetRequiredString(arguments, "labels_csv", 2000);
                var valuesCsv = McpSchemaHelper.GetRequiredString(arguments, "values_csv", 2000);

                var labels = labelsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                var values = valuesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(v => double.TryParse(v, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : 0)
                    .ToList();

                if (labels.Count == 0 || labels.Count != values.Count)
                {
                    return McpToolCallResult.Fail(toolName, ModuleName, "labels_csv and values_csv must be non-empty and have matching counts.");
                }

                var chart = new ChartConfiguration
                {
                    ChartType = chartType,
                    Title = title,
                    Labels = labels,
                    Datasets = new List<ChartDataset>
                    {
                        new()
                        {
                            Label = McpSchemaHelper.GetOptionalString(arguments, "series_label", 80) ?? title,
                            Data = values,
                            BackgroundColor = new List<string> { "#6366f1", "#06b6d4", "#10b981", "#f59e0b", "#ec4899", "#8b5cf6", "#14b8a6" },
                            BorderColor = "#6366f1",
                            Fill = chartType is "area"
                        }
                    },
                    XAxisLabel = McpSchemaHelper.GetOptionalString(arguments, "x_axis_label", 80),
                    YAxisLabel = McpSchemaHelper.GetOptionalString(arguments, "y_axis_label", 80),
                    Legend = chartType is "pie" or "doughnut" or "groupedBar" or "stackedBar",
                    SourceDescription = McpSchemaHelper.GetOptionalString(arguments, "source_description", 150) ?? "Verified session records"
                };

                return McpToolCallResult.Ok(toolName, ModuleName, $"Prepared {chartType} chart '{title}'", new
                {
                    chart_created = true,
                    chart_type = chartType,
                    points = labels.Count
                }, chart: chart);
            }

            default:
                return McpToolCallResult.Fail(toolName, ModuleName, $"Unknown analytics tool '{toolName}'.");
        }
    }
}

