using System.Text.Json;
using Friday.Core.Entities;
using Friday.Core.Enums;
using Friday.Core.Interfaces;
using Friday.Core.Models;
using Friday.Infrastructure.Data;
using Friday.Mcp.Protocol;
using Microsoft.EntityFrameworkCore;

namespace Friday.Mcp.Modules;

public class TaskMcpModule : IMcpToolModule
{
    private readonly FridayDbContext _db;

    public TaskMcpModule(FridayDbContext db)
    {
        _db = db;
    }

    public string ModuleName => "tasks";

    public IReadOnlyList<McpToolDescriptor> GetTools() => new List<McpToolDescriptor>
    {
        new()
        {
            Name = "create_task",
            ModuleName = ModuleName,
            HumanActionDescription = "Created a new task",
            Description = "Creates a new task with optional priority, due date, category, notes, and project association.",
            Safety = McpToolSafety.Write,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["title"] = McpSchemaHelper.Prop("string", "Title of the task"),
                ["description"] = McpSchemaHelper.Prop("string", "Detailed task description"),
                ["priority"] = McpSchemaHelper.Prop("string", "Task priority", new[] { "Low", "Medium", "High", "Critical" }),
                ["status"] = McpSchemaHelper.Prop("string", "Initial status", new[] { "Pending", "InProgress", "Completed" }),
                ["due_date"] = McpSchemaHelper.Prop("string", "Due date/time in ISO format YYYY-MM-DD or YYYY-MM-DDTHH:mm, or 'today'/'tomorrow'"),
                ["category"] = McpSchemaHelper.Prop("string", "Optional task category (e.g., Work, OJT, Study, Personal)"),
                ["notes"] = McpSchemaHelper.Prop("string", "Additional task notes"),
                ["project_name"] = McpSchemaHelper.Prop("string", "Optional name of an existing project to connect this task to"),
                ["project_id"] = McpSchemaHelper.Prop("integer", "Optional ID of an existing project")
            }, required: new[] { "title" })
        },
        new()
        {
            Name = "get_tasks",
            ModuleName = ModuleName,
            HumanActionDescription = "Retrieved your tasks",
            Description = "Retrieves tasks filtered by status, priority, category, project, search keyword, or timeframe.",
            Safety = McpToolSafety.Read,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["status"] = McpSchemaHelper.Prop("string", "Filter by status", new[] { "Pending", "InProgress", "Completed", "Cancelled", "Open", "All" }),
                ["priority"] = McpSchemaHelper.Prop("string", "Filter by priority", new[] { "Low", "Medium", "High", "Critical" }),
                ["category"] = McpSchemaHelper.Prop("string", "Filter by category"),
                ["project_name"] = McpSchemaHelper.Prop("string", "Filter by project name"),
                ["search"] = McpSchemaHelper.Prop("string", "Keyword search in title, description, or notes"),
                ["timeframe"] = McpSchemaHelper.Prop("string", "Optional due/created timeframe", new[] { "today", "tomorrow", "this_week", "this_month", "all" }),
                ["include_chart"] = McpSchemaHelper.Prop("boolean", "Set true if the user asked to visualize or chart these tasks"),
                ["limit"] = McpSchemaHelper.Prop("integer", "Max records to return (default 50)")
            })
        },
        new()
        {
            Name = "get_today_tasks",
            ModuleName = ModuleName,
            HumanActionDescription = "Checked your tasks for today",
            Description = "Retrieves open tasks due today (and optionally overdue tasks) in the user's local timezone.",
            Safety = McpToolSafety.Read,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["include_overdue"] = McpSchemaHelper.Prop("boolean", "Include overdue tasks alongside today's tasks (default true)")
            })
        },
        new()
        {
            Name = "get_completed_tasks",
            ModuleName = ModuleName,
            HumanActionDescription = "Checked your completed tasks",
            Description = "Retrieves completed tasks within a given timeframe (today, this_week, last_week, this_month, all) and provides completion counts.",
            Safety = McpToolSafety.Read,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["timeframe"] = McpSchemaHelper.Prop("string", "Time period for completed tasks", new[] { "today", "this_week", "last_week", "this_month", "last_month", "all" }),
                ["include_chart"] = McpSchemaHelper.Prop("boolean", "Set true if a chart of completed tasks over time or by category is useful"),
                ["chart_type"] = McpSchemaHelper.Prop("string", "Preferred chart type when charting daily completions", new[] { "line", "bar" })
            })
        },
        new()
        {
            Name = "get_overdue_tasks",
            ModuleName = ModuleName,
            HumanActionDescription = "Checked your overdue tasks",
            Description = "Retrieves all uncompleted tasks whose due date has passed.",
            Safety = McpToolSafety.Read,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>())
        },
        new()
        {
            Name = "get_task_statistics",
            ModuleName = ModuleName,
            HumanActionDescription = "Analyzed task statistics",
            Description = "Aggregates task counts by status, priority, category, completion rate, most common category, and overdue tasks, optionally returning a chart.",
            Safety = McpToolSafety.Read,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["timeframe"] = McpSchemaHelper.Prop("string", "Time window based on task creation or due date", new[] { "this_week", "this_month", "all" }),
                ["include_chart"] = McpSchemaHelper.Prop("boolean", "Generate a visual breakdown chart (default true)"),
                ["chart_dimension"] = McpSchemaHelper.Prop("string", "Dimension to chart", new[] { "priority", "status", "category" })
            })
        },
        new()
        {
            Name = "update_task",
            ModuleName = ModuleName,
            HumanActionDescription = "Updated a task",
            Description = "Updates an existing task matched by id or title_match.",
            Safety = McpToolSafety.Write,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["id"] = McpSchemaHelper.Prop("integer", "Task ID if known"),
                ["title_match"] = McpSchemaHelper.Prop("string", "Existing task title or partial title to locate the task"),
                ["new_title"] = McpSchemaHelper.Prop("string", "New title for the task"),
                ["description"] = McpSchemaHelper.Prop("string", "Updated description"),
                ["priority"] = McpSchemaHelper.Prop("string", "Updated priority", new[] { "Low", "Medium", "High", "Critical" }),
                ["status"] = McpSchemaHelper.Prop("string", "Updated status", new[] { "Pending", "InProgress", "Completed", "Cancelled" }),
                ["due_date"] = McpSchemaHelper.Prop("string", "Updated due date/time"),
                ["category"] = McpSchemaHelper.Prop("string", "Updated category"),
                ["notes"] = McpSchemaHelper.Prop("string", "Updated notes"),
                ["project_name"] = McpSchemaHelper.Prop("string", "Project name to associate with")
            })
        },
        new()
        {
            Name = "complete_task",
            ModuleName = ModuleName,
            HumanActionDescription = "Marked task as completed",
            Description = "Marks an existing task as Completed by id or title_match.",
            Safety = McpToolSafety.Write,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["id"] = McpSchemaHelper.Prop("integer", "Task ID if known"),
                ["title_match"] = McpSchemaHelper.Prop("string", "Title or partial title of the task to mark completed")
            })
        },
        new()
        {
            Name = "delete_task",
            ModuleName = ModuleName,
            HumanActionDescription = "Deleted a task",
            Description = "Permanently deletes a task by id or title_match. Requires user confirmation.",
            Safety = McpToolSafety.Destructive,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["id"] = McpSchemaHelper.Prop("integer", "Task ID if known"),
                ["title_match"] = McpSchemaHelper.Prop("string", "Title or partial title of the task to delete")
            })
        }
    };

    public async Task<string?> DescribeTargetForDestructiveActionAsync(string toolName, JsonElement arguments, CancellationToken ct = default)
    {
        if (toolName != "delete_task") return null;
        var (task, isAmbiguous, candidates) = await FindTaskAmbiguityAwareAsync(arguments, preferOpenOnly: false, ct);
        if (isAmbiguous)
        {
            return "AMBIGUOUS:" + string.Join("; ", candidates.Take(5).Select(t => $"#{t.Id} '{t.Title}' ({t.Status}, {t.Priority})"));
        }
        return task is null ? null : $"task '{task.Title}' (ID #{task.Id}, Status: {task.Status})";
    }

    public async Task<McpToolCallResult> ExecuteToolAsync(string toolName, JsonElement arguments, McpToolCallContext context, CancellationToken ct = default)
    {
        switch (toolName)
        {
            case "create_task":
            {
                var title = McpSchemaHelper.GetRequiredString(arguments, "title", 250);
                var priority = ParsePriority(McpSchemaHelper.GetOptionalString(arguments, "priority")) ?? PriorityLevel.Medium;
                var status = ParseStatus(McpSchemaHelper.GetOptionalString(arguments, "status")) ?? TaskItemStatus.Pending;
                var dueUtc = McpSchemaHelper.ParseFlexibleDateTimeToUtc(
                    McpSchemaHelper.GetOptionalString(arguments, "due_date"),
                    context.UserUtcOffset,
                    endOfDayIfDateOnly: true);

                int? projectId = McpSchemaHelper.GetOptionalInt(arguments, "project_id");
                if (!projectId.HasValue && McpSchemaHelper.GetOptionalString(arguments, "project_name", 200) is { } projName)
                {
                    var proj = await _db.Projects.FirstOrDefaultAsync(p => p.Name.ToLower().Contains(projName.ToLower()), ct);
                    projectId = proj?.Id;
                }

                var now = DateTime.UtcNow;
                var item = new TaskItem
                {
                    Title = title,
                    Description = McpSchemaHelper.GetOptionalString(arguments, "description", 2000),
                    Priority = priority,
                    Status = status,
                    DueDate = dueUtc,
                    CompletedAt = status == TaskItemStatus.Completed ? now : null,
                    Category = McpSchemaHelper.GetOptionalString(arguments, "category", 100),
                    Notes = McpSchemaHelper.GetOptionalString(arguments, "notes", 2000),
                    ProjectId = projectId,
                    CreatedAt = now,
                    UpdatedAt = now
                };

                _db.Tasks.Add(item);
                await _db.SaveChangesAsync(ct);

                return McpToolCallResult.Ok(toolName, ModuleName, $"Added task '{item.Title}'", new
                {
                    id = item.Id,
                    title = item.Title,
                    status = item.Status.ToString(),
                    priority = item.Priority.ToString(),
                    due_date = McpSchemaHelper.FormatLocal(item.DueDate, context.UserUtcOffset),
                    category = item.Category,
                    project_id = item.ProjectId
                });
            }

            case "get_tasks":
            {
                var query = _db.Tasks.AsNoTracking().Include(t => t.Project).AsQueryable();

                var statusStr = McpSchemaHelper.GetOptionalString(arguments, "status");
                if (string.Equals(statusStr, "Open", StringComparison.OrdinalIgnoreCase))
                {
                    query = query.Where(t => t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled);
                }
                else if (!string.IsNullOrWhiteSpace(statusStr) && !string.Equals(statusStr, "All", StringComparison.OrdinalIgnoreCase) && ParseStatus(statusStr) is { } st)
                {
                    query = query.Where(t => t.Status == st);
                }

                if (ParsePriority(McpSchemaHelper.GetOptionalString(arguments, "priority")) is { } pr)
                {
                    query = query.Where(t => t.Priority == pr);
                }

                if (McpSchemaHelper.GetOptionalString(arguments, "category", 100) is { } cat)
                {
                    query = query.Where(t => t.Category != null && t.Category.ToLower().Contains(cat.ToLower()));
                }

                if (McpSchemaHelper.GetOptionalString(arguments, "project_name", 200) is { } pName)
                {
                    query = query.Where(t => t.Project != null && t.Project.Name.ToLower().Contains(pName.ToLower()));
                }

                if (McpSchemaHelper.GetOptionalString(arguments, "search", 200) is { } kw)
                {
                    var lower = kw.ToLower();
                    query = query.Where(t => t.Title.ToLower().Contains(lower)
                        || (t.Description != null && t.Description.ToLower().Contains(lower))
                        || (t.Notes != null && t.Notes.ToLower().Contains(lower)));
                }

                if (McpSchemaHelper.GetOptionalString(arguments, "timeframe") is { } tf && tf != "all")
                {
                    var (startUtc, endUtc, _) = McpSchemaHelper.GetTimeframeBoundsUtc(tf, context.UserUtcOffset);
                    query = query.Where(t => t.DueDate.HasValue && t.DueDate.Value >= startUtc && t.DueDate.Value < endUtc);
                }

                int limit = McpSchemaHelper.ClampLimit(McpSchemaHelper.GetOptionalInt(arguments, "limit"));
                var items = await query
                    .OrderBy(t => t.Status == TaskItemStatus.Completed ? 1 : 0)
                    .ThenByDescending(t => t.Priority)
                    .ThenBy(t => t.DueDate ?? DateTime.MaxValue)
                    .Take(limit)
                    .ToListAsync(ct);

                var table = BuildTaskTable("Tasks", items, context.UserUtcOffset);
                ChartConfiguration? chart = null;
                if (McpSchemaHelper.GetOptionalBool(arguments, "include_chart") == true && items.Count > 0)
                {
                    var byPriority = Enum.GetValues<PriorityLevel>()
                        .Select(p => new { Priority = p.ToString(), Count = (double)items.Count(x => x.Priority == p) })
                        .ToList();
                    chart = new ChartConfiguration
                    {
                        ChartType = "bar",
                        Title = "Tasks by Priority",
                        Labels = byPriority.Select(x => x.Priority).ToList(),
                        Datasets = new List<ChartDataset>
                        {
                            new()
                            {
                                Label = "Tasks",
                                Data = byPriority.Select(x => x.Count).ToList(),
                                BackgroundColor = new List<string> { "#10b981", "#3b82f6", "#f59e0b", "#ef4444" }
                            }
                        },
                        XAxisLabel = "Priority",
                        YAxisLabel = "Count",
                        Legend = false,
                        SourceDescription = $"SQLite Tasks ({items.Count} matching record(s))"
                    };
                }

                return McpToolCallResult.Ok(
                    toolName,
                    ModuleName,
                    $"Retrieved {items.Count} task(s)",
                    MapTasks(items, context.UserUtcOffset),
                    items.Count > 0 ? table : null,
                    chart);
            }

            case "get_today_tasks":
            {
                bool includeOverdue = McpSchemaHelper.GetOptionalBool(arguments, "include_overdue") ?? true;
                var (todayStartUtc, tomorrowStartUtc, _) = McpSchemaHelper.GetTimeframeBoundsUtc("today", context.UserUtcOffset);

                var query = _db.Tasks.AsNoTracking().Include(t => t.Project)
                    .Where(t => t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled);

                if (includeOverdue)
                {
                    query = query.Where(t => t.DueDate.HasValue && t.DueDate.Value < tomorrowStartUtc);
                }
                else
                {
                    query = query.Where(t => t.DueDate.HasValue && t.DueDate.Value >= todayStartUtc && t.DueDate.Value < tomorrowStartUtc);
                }

                var items = await query
                    .OrderByDescending(t => t.Priority)
                    .ThenBy(t => t.DueDate)
                    .ToListAsync(ct);

                var table = items.Count > 0 ? BuildTaskTable("Today's & Overdue Tasks", items, context.UserUtcOffset) : null;
                return McpToolCallResult.Ok(toolName, ModuleName, $"Checked today's tasks ({items.Count} found)", new
                {
                    count = items.Count,
                    overdue_count = items.Count(t => t.DueDate.HasValue && t.DueDate.Value < todayStartUtc),
                    due_today_count = items.Count(t => t.DueDate.HasValue && t.DueDate.Value >= todayStartUtc && t.DueDate.Value < tomorrowStartUtc),
                    tasks = MapTasks(items, context.UserUtcOffset)
                }, table);
            }

            case "get_completed_tasks":
            {
                var tf = McpSchemaHelper.GetOptionalString(arguments, "timeframe") ?? "this_week";
                var (startUtc, endUtc, label) = McpSchemaHelper.GetTimeframeBoundsUtc(tf, context.UserUtcOffset);

                var query = _db.Tasks.AsNoTracking().Include(t => t.Project)
                    .Where(t => t.Status == TaskItemStatus.Completed);

                if (tf != "all")
                {
                    query = query.Where(t => (t.CompletedAt.HasValue && t.CompletedAt.Value >= startUtc && t.CompletedAt.Value < endUtc)
                                          || (!t.CompletedAt.HasValue && t.UpdatedAt >= startUtc && t.UpdatedAt < endUtc));
                }

                var items = await query.OrderByDescending(t => t.CompletedAt ?? t.UpdatedAt).ToListAsync(ct);
                var table = items.Count > 0 ? BuildTaskTable($"Completed Tasks ({label})", items, context.UserUtcOffset) : null;

                ChartConfiguration? chart = null;
                if (McpSchemaHelper.GetOptionalBool(arguments, "include_chart") == true && items.Count > 0)
                {
                    var groupedByDay = items
                        .GroupBy(t => ((t.CompletedAt ?? t.UpdatedAt) + context.UserUtcOffset).Date)
                        .OrderBy(g => g.Key)
                        .ToList();

                    var requestedChartType = McpSchemaHelper.GetOptionalString(arguments, "chart_type")?.ToLowerInvariant();
                    var resolvedChartType = requestedChartType is "line" or "bar"
                        ? requestedChartType
                        : (groupedByDay.Count >= 2 ? "line" : "bar");

                    chart = new ChartConfiguration
                    {
                        ChartType = resolvedChartType,
                        Title = $"Completed Tasks ({label})",
                        Labels = groupedByDay.Select(g => g.Key.ToString("MMM dd")).ToList(),
                        Datasets = new List<ChartDataset>
                        {
                            new()
                            {
                                Label = "Completed Tasks",
                                Data = groupedByDay.Select(g => (double)g.Count()).ToList(),
                                BackgroundColor = new List<string> { "#10b981" },
                                BorderColor = "#059669"
                            }
                        },
                        XAxisLabel = "Date",
                        YAxisLabel = "Completed",
                        Legend = false,
                        SourceDescription = $"SQLite Tasks ({label})"
                    };
                }

                return McpToolCallResult.Ok(toolName, ModuleName, $"Retrieved {items.Count} completed task(s) for {label}", new
                {
                    timeframe = label,
                    count = items.Count,
                    tasks = MapTasks(items, context.UserUtcOffset)
                }, table, chart);
            }

            case "get_overdue_tasks":
            {
                var nowUtc = DateTime.UtcNow;
                var items = await _db.Tasks.AsNoTracking().Include(t => t.Project)
                    .Where(t => t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled && t.DueDate.HasValue && t.DueDate.Value < nowUtc)
                    .OrderByDescending(t => t.Priority)
                    .ThenBy(t => t.DueDate)
                    .ToListAsync(ct);

                var table = items.Count > 0 ? BuildTaskTable("Overdue Tasks", items, context.UserUtcOffset) : null;
                return McpToolCallResult.Ok(toolName, ModuleName, $"Found {items.Count} overdue task(s)", new
                {
                    count = items.Count,
                    tasks = MapTasks(items, context.UserUtcOffset)
                }, table);
            }

            case "get_task_statistics":
            {
                var tf = McpSchemaHelper.GetOptionalString(arguments, "timeframe") ?? "all";
                var includeChart = McpSchemaHelper.GetOptionalBool(arguments, "include_chart") ?? true;
                var dimension = McpSchemaHelper.GetOptionalString(arguments, "chart_dimension")?.ToLowerInvariant() ?? "priority";

                var query = _db.Tasks.AsNoTracking().AsQueryable();
                string label = "All Time";
                if (tf != "all")
                {
                    var (startUtc, endUtc, tfLabel) = McpSchemaHelper.GetTimeframeBoundsUtc(tf, context.UserUtcOffset);
                    label = tfLabel;
                    query = query.Where(t =>
                        (t.DueDate.HasValue && t.DueDate.Value >= startUtc && t.DueDate.Value < endUtc) ||
                        (t.CompletedAt.HasValue && t.CompletedAt.Value >= startUtc && t.CompletedAt.Value < endUtc) ||
                        (t.CreatedAt >= startUtc && t.CreatedAt < endUtc));
                }

                var items = await query.ToListAsync(ct);
                var nowUtc = DateTime.UtcNow;
                var total = items.Count;
                var completed = items.Count(t => t.Status == TaskItemStatus.Completed);
                var overdue = items.Count(t => t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled && t.DueDate.HasValue && t.DueDate.Value < nowUtc);
                var open = items.Count(t => t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled);

                var byPriority = Enum.GetValues<PriorityLevel>()
                    .Select(p => new { Name = p.ToString(), Count = items.Count(x => x.Priority == p) })
                    .ToList();
                var byStatus = Enum.GetValues<TaskItemStatus>()
                    .Select(s => new { Name = s.ToString(), Count = items.Count(x => x.Status == s) })
                    .ToList();
                var byCategory = items
                    .GroupBy(t => string.IsNullOrWhiteSpace(t.Category) ? "General" : t.Category.Trim())
                    .Select(g => new { Name = g.Key, Count = g.Count() })
                    .OrderByDescending(g => g.Count)
                    .ToList();

                var topCategory = byCategory.FirstOrDefault();

                var table = total > 0
                    ? new TableResult
                    {
                        Title = $"Task Breakdown ({label})",
                        Columns = new List<string> { "Metric / Group", "Count" },
                        Rows = (dimension == "category"
                                ? byCategory.Select(x => new List<string> { x.Name, x.Count.ToString() })
                                : dimension == "status"
                                    ? byStatus.Select(x => new List<string> { x.Name, x.Count.ToString() })
                                    : byPriority.Select(x => new List<string> { x.Name, x.Count.ToString() }))
                            .ToList(),
                        FooterSummary = $"Total: {total} | Completed: {completed} | Open: {open} | Overdue: {overdue}",
                        SourceDescription = "SQLite Tasks"
                    }
                    : null;

                ChartConfiguration? chart = null;
                if (includeChart && total > 0)
                {
                    var chartGroups = dimension switch
                    {
                        "status" => byStatus.Select(x => (x.Name, (double)x.Count)).ToList(),
                        "category" => byCategory.Select(x => (x.Name, (double)x.Count)).ToList(),
                        _ => byPriority.Select(x => (x.Name, (double)x.Count)).ToList()
                    };

                    chart = new ChartConfiguration
                    {
                        ChartType = "bar",
                        Title = $"Tasks by {char.ToUpperInvariant(dimension[0]) + dimension[1..]} ({label})",
                        Labels = chartGroups.Select(g => g.Item1).ToList(),
                        Datasets = new List<ChartDataset>
                        {
                            new()
                            {
                                Label = "Tasks",
                                Data = chartGroups.Select(g => g.Item2).ToList(),
                                BackgroundColor = new List<string> { "#10b981", "#3b82f6", "#f59e0b", "#ef4444", "#8b5cf6" }
                            }
                        },
                        XAxisLabel = char.ToUpperInvariant(dimension[0]) + dimension[1..],
                        YAxisLabel = "Tasks",
                        Legend = false,
                        SourceDescription = $"SQLite Tasks ({total} total)"
                    };
                }

                return McpToolCallResult.Ok(toolName, ModuleName, $"Analyzed {total} task(s) ({label})", new
                {
                    timeframe = label,
                    total_tasks = total,
                    completed_tasks = completed,
                    open_tasks = open,
                    overdue_tasks = overdue,
                    completion_rate_percent = total > 0 ? Math.Round(completed * 100.0 / total, 1) : 0.0,
                    most_common_category = topCategory?.Name ?? "None",
                    by_priority = byPriority,
                    by_status = byStatus,
                    by_category = byCategory
                }, table, chart);
            }

            case "update_task":
            {
                var (task, isAmbiguous, candidates) = await FindTaskAmbiguityAwareAsync(arguments, preferOpenOnly: false, ct);
                if (isAmbiguous)
                {
                    return McpToolCallResult.Ambiguous(
                        toolName,
                        ModuleName,
                        $"Multiple tasks match that request: {FormatTaskCandidates(candidates, context.UserUtcOffset)}. Ask the user which task ID or exact task they want to update.",
                        new { candidates = MapTasks(candidates, context.UserUtcOffset) });
                }
                if (task is null)
                {
                    return McpToolCallResult.Fail(toolName, ModuleName, "No matching task was found to update.");
                }

                if (McpSchemaHelper.GetOptionalString(arguments, "new_title", 250) is { } nt) task.Title = nt;
                if (McpSchemaHelper.GetOptionalString(arguments, "description", 2000) is { } desc) task.Description = desc;
                if (ParsePriority(McpSchemaHelper.GetOptionalString(arguments, "priority")) is { } pr) task.Priority = pr;
                if (ParseStatus(McpSchemaHelper.GetOptionalString(arguments, "status")) is { } st)
                {
                    task.Status = st;
                    if (st == TaskItemStatus.Completed && !task.CompletedAt.HasValue)
                        task.CompletedAt = DateTime.UtcNow;
                }
                if (McpSchemaHelper.GetOptionalString(arguments, "due_date") is { } dueStr)
                {
                    task.DueDate = McpSchemaHelper.ParseFlexibleDateTimeToUtc(dueStr, context.UserUtcOffset, endOfDayIfDateOnly: true);
                }
                if (McpSchemaHelper.GetOptionalString(arguments, "category", 100) is { } cat) task.Category = cat;
                if (McpSchemaHelper.GetOptionalString(arguments, "notes", 2000) is { } notes) task.Notes = notes;
                if (McpSchemaHelper.GetOptionalString(arguments, "project_name", 200) is { } projName)
                {
                    var proj = await _db.Projects.FirstOrDefaultAsync(p => p.Name.ToLower().Contains(projName.ToLower()), ct);
                    if (proj is not null) task.ProjectId = proj.Id;
                }

                task.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);

                return McpToolCallResult.Ok(toolName, ModuleName, $"Updated task '{task.Title}'", new
                {
                    id = task.Id,
                    title = task.Title,
                    status = task.Status.ToString(),
                    priority = task.Priority.ToString(),
                    due_date = McpSchemaHelper.FormatLocal(task.DueDate, context.UserUtcOffset)
                });
            }

            case "complete_task":
            {
                var (task, isAmbiguous, candidates) = await FindTaskAmbiguityAwareAsync(arguments, preferOpenOnly: true, ct);
                if (isAmbiguous)
                {
                    return McpToolCallResult.Ambiguous(
                        toolName,
                        ModuleName,
                        $"Multiple open tasks match that description: {FormatTaskCandidates(candidates, context.UserUtcOffset)}. Ask the user which task to mark completed.",
                        new { candidates = MapTasks(candidates, context.UserUtcOffset) });
                }
                if (task is null)
                {
                    return McpToolCallResult.Fail(toolName, ModuleName, "No matching task was found to mark completed.");
                }

                task.Status = TaskItemStatus.Completed;
                task.CompletedAt = DateTime.UtcNow;
                task.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);

                return McpToolCallResult.Ok(toolName, ModuleName, $"Marked '{task.Title}' as completed", new
                {
                    id = task.Id,
                    title = task.Title,
                    status = task.Status.ToString(),
                    completed_at = McpSchemaHelper.FormatLocal(task.CompletedAt, context.UserUtcOffset)
                });
            }

            case "delete_task":
            {
                var (task, isAmbiguous, candidates) = await FindTaskAmbiguityAwareAsync(arguments, preferOpenOnly: false, ct);
                if (isAmbiguous)
                {
                    return McpToolCallResult.Ambiguous(
                        toolName,
                        ModuleName,
                        $"Multiple tasks match that deletion request: {FormatTaskCandidates(candidates, context.UserUtcOffset)}. Ask the user which specific task ID or item to delete.",
                        new { candidates = MapTasks(candidates, context.UserUtcOffset) });
                }
                if (task is null)
                {
                    return McpToolCallResult.Fail(toolName, ModuleName, "No matching task was found to delete.");
                }

                var deletedTitle = task.Title;
                var deletedId = task.Id;
                _db.Tasks.Remove(task);
                await _db.SaveChangesAsync(ct);

                return McpToolCallResult.Ok(toolName, ModuleName, $"Deleted task '{deletedTitle}'", new
                {
                    deleted = true,
                    id = deletedId,
                    title = deletedTitle
                });
            }

            default:
                return McpToolCallResult.Fail(toolName, ModuleName, $"Unknown task tool '{toolName}'.");
        }
    }

    private async Task<(TaskItem? Task, bool IsAmbiguous, List<TaskItem> Candidates)> FindTaskAmbiguityAwareAsync(
        JsonElement arguments,
        bool preferOpenOnly,
        CancellationToken ct)
    {
        if (McpSchemaHelper.GetOptionalInt(arguments, "id") is { } id)
        {
            var byId = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == id, ct);
            return (byId, false, byId is null ? new List<TaskItem>() : new List<TaskItem> { byId });
        }

        if (McpSchemaHelper.GetOptionalString(arguments, "title_match", 250) is { } match)
        {
            var trimmed = match.Trim();
            var lower = trimmed.ToLowerInvariant();
            var matches = await _db.Tasks
                .Where(t => t.Title.ToLower().Contains(lower))
                .OrderBy(t => t.Status == TaskItemStatus.Completed || t.Status == TaskItemStatus.Cancelled ? 1 : 0)
                .ThenByDescending(t => t.UpdatedAt)
                .Take(12)
                .ToListAsync(ct);

            if (matches.Count == 0)
                return (null, false, matches);

            if (preferOpenOnly && matches.Count > 1)
            {
                var openMatches = matches
                    .Where(t => t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled)
                    .ToList();
                if (openMatches.Count == 1)
                    return (openMatches[0], false, openMatches);
                if (openMatches.Count > 1)
                    matches = openMatches;
            }

            if (matches.Count == 1)
                return (matches[0], false, matches);

            var exact = matches
                .Where(t => string.Equals(t.Title.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (exact.Count == 1)
                return (exact[0], false, exact);
            if (exact.Count > 1)
                return (null, true, exact);

            return (null, true, matches);
        }

        return (null, false, new List<TaskItem>());
    }

    private static string FormatTaskCandidates(List<TaskItem> candidates, TimeSpan offset)
        => string.Join(", ", candidates.Take(5).Select(t =>
            $"#{t.Id} \"{t.Title}\" ({t.Status}, {t.Priority}{(t.DueDate.HasValue ? $", due {McpSchemaHelper.FormatLocal(t.DueDate, offset)}" : "")})"));

    private static PriorityLevel? ParsePriority(string? value)
        => Enum.TryParse<PriorityLevel>(value, ignoreCase: true, out var p) ? p : null;

    private static TaskItemStatus? ParseStatus(string? value)
        => Enum.TryParse<TaskItemStatus>(value, ignoreCase: true, out var s) ? s : null;

    private static object MapTasks(List<TaskItem> items, TimeSpan offset)
    {
        var nowUtc = DateTime.UtcNow;
        return items.Select(t => new
        {
            id = t.Id,
            title = t.Title,
            description = t.Description,
            status = t.Status.ToString(),
            priority = t.Priority.ToString(),
            due_date = McpSchemaHelper.FormatLocal(t.DueDate, offset),
            is_overdue = t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled && t.DueDate.HasValue && t.DueDate.Value < nowUtc,
            completed_at = McpSchemaHelper.FormatLocal(t.CompletedAt, offset),
            category = t.Category,
            project = t.Project?.Name
        }).ToList();
    }

    private static TableResult BuildTaskTable(string title, List<TaskItem> items, TimeSpan offset)
    {
        return new TableResult
        {
            Title = title,
            Columns = new List<string> { "ID", "Title", "Priority", "Status", "Due Date", "Category", "Project" },
            Rows = items.Select(t => new List<string>
            {
                $"#{t.Id}",
                t.Title,
                t.Priority.ToString(),
                t.Status.ToString(),
                McpSchemaHelper.FormatLocal(t.DueDate, offset),
                t.Category ?? "-",
                t.Project?.Name ?? "-"
            }).ToList(),
            FooterSummary = $"Total: {items.Count} task(s)",
            SourceDescription = "SQLite Tasks"
        };
    }
}

