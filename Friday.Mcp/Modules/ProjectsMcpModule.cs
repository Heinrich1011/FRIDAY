using System.Text.Json;
using Friday.Core.Entities;
using Friday.Core.Enums;
using Friday.Core.Interfaces;
using Friday.Core.Models;
using Friday.Infrastructure.Data;
using Friday.Mcp.Protocol;
using Microsoft.EntityFrameworkCore;

namespace Friday.Mcp.Modules;

public class ProjectsMcpModule : IMcpToolModule
{
    private readonly FridayDbContext _db;

    public ProjectsMcpModule(FridayDbContext db)
    {
        _db = db;
    }

    public string ModuleName => "projects";

    public IReadOnlyList<McpToolDescriptor> GetTools() => new List<McpToolDescriptor>
    {
        new()
        {
            Name = "create_project",
            ModuleName = ModuleName,
            HumanActionDescription = "Created a project",
            Description = "Creates a new project with status, priority, optional start/target dates, description, and notes.",
            Safety = McpToolSafety.Write,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["name"] = McpSchemaHelper.Prop("string", "Project name"),
                ["description"] = McpSchemaHelper.Prop("string", "Project description"),
                ["status"] = McpSchemaHelper.Prop("string", "Project status", new[] { "Planning", "Active", "OnHold", "Completed", "Archived" }),
                ["priority"] = McpSchemaHelper.Prop("string", "Priority level", new[] { "Low", "Medium", "High", "Critical" }),
                ["start_date"] = McpSchemaHelper.Prop("string", "Start date YYYY-MM-DD"),
                ["target_date"] = McpSchemaHelper.Prop("string", "Target completion date YYYY-MM-DD"),
                ["notes"] = McpSchemaHelper.Prop("string", "Project notes")
            }, required: new[] { "name" })
        },
        new()
        {
            Name = "get_projects",
            ModuleName = ModuleName,
            HumanActionDescription = "Checked your projects",
            Description = "Retrieves projects filtered by status, priority, or keyword, including real task completion statistics.",
            Safety = McpToolSafety.Read,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["status"] = McpSchemaHelper.Prop("string", "Filter by status", new[] { "Planning", "Active", "OnHold", "Completed", "Archived", "All" }),
                ["priority"] = McpSchemaHelper.Prop("string", "Filter by priority", new[] { "Low", "Medium", "High", "Critical" }),
                ["search"] = McpSchemaHelper.Prop("string", "Keyword search"),
                ["include_chart"] = McpSchemaHelper.Prop("boolean", "Set true to include a project progress chart")
            })
        },
        new()
        {
            Name = "get_project_details",
            ModuleName = ModuleName,
            HumanActionDescription = "Inspected project details and tasks",
            Description = "Retrieves detailed progress, tasks, and notes belonging to a specific project by id or name_match.",
            Safety = McpToolSafety.Read,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["id"] = McpSchemaHelper.Prop("integer", "Project ID if known"),
                ["name_match"] = McpSchemaHelper.Prop("string", "Project name or partial name")
            })
        },
        new()
        {
            Name = "update_project",
            ModuleName = ModuleName,
            HumanActionDescription = "Updated a project",
            Description = "Updates a project's status, priority, dates, description, or notes.",
            Safety = McpToolSafety.Write,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["id"] = McpSchemaHelper.Prop("integer", "Project ID if known"),
                ["name_match"] = McpSchemaHelper.Prop("string", "Existing project name to locate"),
                ["new_name"] = McpSchemaHelper.Prop("string", "Updated project name"),
                ["description"] = McpSchemaHelper.Prop("string", "Updated description"),
                ["status"] = McpSchemaHelper.Prop("string", "Updated status", new[] { "Planning", "Active", "OnHold", "Completed", "Archived" }),
                ["priority"] = McpSchemaHelper.Prop("string", "Updated priority", new[] { "Low", "Medium", "High", "Critical" }),
                ["target_date"] = McpSchemaHelper.Prop("string", "Updated target date YYYY-MM-DD"),
                ["notes"] = McpSchemaHelper.Prop("string", "Updated notes")
            })
        },
        new()
        {
            Name = "delete_project",
            ModuleName = ModuleName,
            HumanActionDescription = "Deleted a project",
            Description = "Deletes a project by id or name_match (connected tasks are unlinked, not deleted). Requires user confirmation.",
            Safety = McpToolSafety.Destructive,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["id"] = McpSchemaHelper.Prop("integer", "Project ID if known"),
                ["name_match"] = McpSchemaHelper.Prop("string", "Project name to delete")
            })
        }
    };

    public async Task<string?> DescribeTargetForDestructiveActionAsync(string toolName, JsonElement arguments, CancellationToken ct = default)
    {
        if (toolName != "delete_project") return null;
        var lookup = await FindProjectAmbiguityAwareAsync(arguments, ct);
        if (lookup.IsAmbiguous)
        {
            return $"AMBIGUOUS:{string.Join(" | ", lookup.Matches.Select(p => $"#{p.Id} {p.Name} ({p.Status})"))}";
        }
        return lookup.Match is null ? null : $"project '{lookup.Match.Name}' (ID #{lookup.Match.Id}, Status: {lookup.Match.Status})";
    }

    public async Task<McpToolCallResult> ExecuteToolAsync(string toolName, JsonElement arguments, McpToolCallContext context, CancellationToken ct = default)
    {
        switch (toolName)
        {
            case "create_project":
            {
                var name = McpSchemaHelper.GetRequiredString(arguments, "name", 200);
                var status = Enum.TryParse<ProjectStatus>(McpSchemaHelper.GetOptionalString(arguments, "status"), true, out var st) ? st : ProjectStatus.Active;
                var priority = Enum.TryParse<PriorityLevel>(McpSchemaHelper.GetOptionalString(arguments, "priority"), true, out var pr) ? pr : PriorityLevel.Medium;

                var now = DateTime.UtcNow;
                var proj = new Project
                {
                    Name = name,
                    Description = McpSchemaHelper.GetOptionalString(arguments, "description", 2000),
                    Status = status,
                    Priority = priority,
                    StartDate = McpSchemaHelper.ParseFlexibleDateTimeToUtc(McpSchemaHelper.GetOptionalString(arguments, "start_date"), context.UserUtcOffset),
                    TargetDate = McpSchemaHelper.ParseFlexibleDateTimeToUtc(McpSchemaHelper.GetOptionalString(arguments, "target_date"), context.UserUtcOffset, true),
                    Notes = McpSchemaHelper.GetOptionalString(arguments, "notes", 4000),
                    CreatedAt = now,
                    UpdatedAt = now
                };

                _db.Projects.Add(proj);
                await _db.SaveChangesAsync(ct);

                return McpToolCallResult.Ok(toolName, ModuleName, $"Created project '{proj.Name}'", new
                {
                    id = proj.Id,
                    name = proj.Name,
                    status = proj.Status.ToString(),
                    priority = proj.Priority.ToString(),
                    target_date = McpSchemaHelper.FormatLocal(proj.TargetDate, context.UserUtcOffset, "yyyy-MM-dd")
                });
            }

            case "get_projects":
            {
                var query = _db.Projects.AsNoTracking().Include(p => p.Tasks).AsQueryable();

                var statusStr = McpSchemaHelper.GetOptionalString(arguments, "status");
                if (!string.IsNullOrWhiteSpace(statusStr) && !string.Equals(statusStr, "All", StringComparison.OrdinalIgnoreCase)
                    && Enum.TryParse<ProjectStatus>(statusStr, true, out var st))
                {
                    query = query.Where(p => p.Status == st);
                }

                if (Enum.TryParse<PriorityLevel>(McpSchemaHelper.GetOptionalString(arguments, "priority"), true, out var pr))
                {
                    query = query.Where(p => p.Priority == pr);
                }

                if (McpSchemaHelper.GetOptionalString(arguments, "search", 200) is { } kw)
                {
                    var lower = kw.ToLower();
                    query = query.Where(p => p.Name.ToLower().Contains(lower)
                        || (p.Description != null && p.Description.ToLower().Contains(lower)));
                }

                var projects = await query.OrderByDescending(p => p.Priority).ThenBy(p => p.TargetDate ?? DateTime.MaxValue).ToListAsync(ct);

                var table = projects.Count > 0 ? new TableResult
                {
                    Title = "Projects",
                    Columns = new List<string> { "ID", "Project", "Status", "Priority", "Tasks Done", "Completion", "Target Date" },
                    Rows = projects.Select(p =>
                    {
                        int total = p.Tasks.Count;
                        int done = p.Tasks.Count(t => t.Status == TaskItemStatus.Completed);
                        int pct = total > 0 ? (int)Math.Round(done * 100.0 / total) : 0;
                        return new List<string>
                        {
                            $"#{p.Id}",
                            p.Name,
                            p.Status.ToString(),
                            p.Priority.ToString(),
                            $"{done}/{total}",
                            total > 0 ? $"{pct}%" : "No tasks",
                            McpSchemaHelper.FormatLocal(p.TargetDate, context.UserUtcOffset, "yyyy-MM-dd")
                        };
                    }).ToList(),
                    FooterSummary = $"Total: {projects.Count} project(s)",
                    SourceDescription = "SQLite Projects & Tasks"
                } : null;

                ChartConfiguration? chart = null;
                if (McpSchemaHelper.GetOptionalBool(arguments, "include_chart") == true && projects.Count > 0)
                {
                    chart = new ChartConfiguration
                    {
                        ChartType = "groupedBar",
                        Title = "Project Task Completion",
                        Labels = projects.Select(p => p.Name).ToList(),
                        Datasets = new List<ChartDataset>
                        {
                            new()
                            {
                                Label = "Completed Tasks",
                                Data = projects.Select(p => (double)p.Tasks.Count(t => t.Status == TaskItemStatus.Completed)).ToList(),
                                BackgroundColor = new List<string> { "#10b981" }
                            },
                            new()
                            {
                                Label = "Remaining Tasks",
                                Data = projects.Select(p => (double)p.Tasks.Count(t => t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled)).ToList(),
                                BackgroundColor = new List<string> { "#6366f1" }
                            }
                        },
                        XAxisLabel = "Project",
                        YAxisLabel = "Tasks",
                        Legend = true,
                        SourceDescription = "SQLite Projects & connected Tasks"
                    };
                }

                return McpToolCallResult.Ok(toolName, ModuleName, $"Retrieved {projects.Count} project(s)", new
                {
                    count = projects.Count,
                    projects = projects.Select(p =>
                    {
                        int total = p.Tasks.Count;
                        int done = p.Tasks.Count(t => t.Status == TaskItemStatus.Completed);
                        int unfinished = p.Tasks.Count(t => t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled);
                        int overdue = p.Tasks.Count(t => t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled && t.DueDate.HasValue && t.DueDate.Value < DateTime.UtcNow);
                        return new
                        {
                            id = p.Id,
                            name = p.Name,
                            description = p.Description,
                            status = p.Status.ToString(),
                            priority = p.Priority.ToString(),
                            total_tasks = total,
                            completed_tasks = done,
                            unfinished_tasks = unfinished,
                            overdue_tasks = overdue,
                            completion_percent = total > 0 ? Math.Round(done * 100.0 / total, 1) : 0,
                            target_date = McpSchemaHelper.FormatLocal(p.TargetDate, context.UserUtcOffset, "yyyy-MM-dd")
                        };
                    }).ToList()
                }, table, chart);
            }

            case "get_project_details":
            {
                var lookup = await FindProjectWithDetailsAmbiguityAwareAsync(arguments, ct);
                if (lookup.IsAmbiguous)
                {
                    return McpToolCallResult.Ambiguous(
                        toolName,
                        ModuleName,
                        "Multiple projects match that name. Ask the user which project they mean.",
                        lookup.Matches.Select(p => new { id = p.Id, name = p.Name, status = p.Status.ToString(), priority = p.Priority.ToString() }).ToList());
                }

                var proj = lookup.Match;
                if (proj is null)
                {
                    return McpToolCallResult.Ok(toolName, ModuleName, "Project not found", new { exists = false, message = "No matching project found." });
                }

                int total = proj.Tasks.Count;
                int done = proj.Tasks.Count(t => t.Status == TaskItemStatus.Completed);
                int pending = proj.Tasks.Count(t => t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled);
                int overdueCount = proj.Tasks.Count(t => t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled && t.DueDate.HasValue && t.DueDate.Value < DateTime.UtcNow);

                var taskTable = proj.Tasks.Count > 0 ? new TableResult
                {
                    Title = $"Tasks in '{proj.Name}'",
                    Columns = new List<string> { "ID", "Task", "Status", "Priority", "Due Date" },
                    Rows = proj.Tasks.OrderBy(t => t.Status == TaskItemStatus.Completed ? 1 : 0).ThenByDescending(t => t.Priority)
                        .Select(t => new List<string>
                        {
                            $"#{t.Id}",
                            t.Title,
                            t.Status.ToString(),
                            t.Priority.ToString(),
                            McpSchemaHelper.FormatLocal(t.DueDate, context.UserUtcOffset)
                        }).ToList(),
                    FooterSummary = $"{done} of {total} task(s) completed ({pending} unfinished, {overdueCount} overdue)",
                    SourceDescription = $"SQLite Project #{proj.Id}"
                } : null;

                return McpToolCallResult.Ok(toolName, ModuleName, $"Retrieved details for project '{proj.Name}'", new
                {
                    exists = true,
                    id = proj.Id,
                    name = proj.Name,
                    description = proj.Description,
                    status = proj.Status.ToString(),
                    priority = proj.Priority.ToString(),
                    start_date = McpSchemaHelper.FormatLocal(proj.StartDate, context.UserUtcOffset, "yyyy-MM-dd"),
                    target_date = McpSchemaHelper.FormatLocal(proj.TargetDate, context.UserUtcOffset, "yyyy-MM-dd"),
                    notes = proj.Notes,
                    total_tasks = total,
                    completed_tasks = done,
                    unfinished_tasks = pending,
                    overdue_tasks = overdueCount,
                    completion_percent = total > 0 ? Math.Round(done * 100.0 / total, 1) : 0,
                    tasks = proj.Tasks.Select(t => new
                    {
                        id = t.Id,
                        title = t.Title,
                        status = t.Status.ToString(),
                        priority = t.Priority.ToString(),
                        due_date = McpSchemaHelper.FormatLocal(t.DueDate, context.UserUtcOffset)
                    }).ToList(),
                    linked_notes_count = proj.LinkedNotes.Count
                }, taskTable);
            }

            case "update_project":
            {
                var lookup = await FindProjectAmbiguityAwareAsync(arguments, ct);
                if (lookup.IsAmbiguous)
                {
                    return McpToolCallResult.Ambiguous(
                        toolName,
                        ModuleName,
                        "Multiple projects match that name. Ask the user which project to update.",
                        lookup.Matches.Select(p => new { id = p.Id, name = p.Name, status = p.Status.ToString() }).ToList());
                }

                var proj = lookup.Match;
                if (proj is null)
                {
                    return McpToolCallResult.Fail(toolName, ModuleName, "No matching project was found to update.");
                }

                if (McpSchemaHelper.GetOptionalString(arguments, "new_name", 200) is { } nn) proj.Name = nn;
                if (McpSchemaHelper.GetOptionalString(arguments, "description", 2000) is { } desc) proj.Description = desc;
                if (Enum.TryParse<ProjectStatus>(McpSchemaHelper.GetOptionalString(arguments, "status"), true, out var st)) proj.Status = st;
                if (Enum.TryParse<PriorityLevel>(McpSchemaHelper.GetOptionalString(arguments, "priority"), true, out var pr)) proj.Priority = pr;
                if (McpSchemaHelper.GetOptionalString(arguments, "target_date") is { } td)
                    proj.TargetDate = McpSchemaHelper.ParseFlexibleDateTimeToUtc(td, context.UserUtcOffset, true);
                if (McpSchemaHelper.GetOptionalString(arguments, "notes", 4000) is { } notes) proj.Notes = notes;

                proj.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);

                return McpToolCallResult.Ok(toolName, ModuleName, $"Updated project '{proj.Name}'", new
                {
                    id = proj.Id,
                    name = proj.Name,
                    status = proj.Status.ToString(),
                    priority = proj.Priority.ToString()
                });
            }

            case "delete_project":
            {
                var lookup = await FindProjectAmbiguityAwareAsync(arguments, ct);
                if (lookup.IsAmbiguous)
                {
                    return McpToolCallResult.Ambiguous(
                        toolName,
                        ModuleName,
                        "Multiple projects match that name. Ask the user which project to delete.",
                        lookup.Matches.Select(p => new { id = p.Id, name = p.Name, status = p.Status.ToString() }).ToList());
                }

                var proj = lookup.Match;
                if (proj is null)
                {
                    return McpToolCallResult.Fail(toolName, ModuleName, "No matching project was found to delete.");
                }

                var name = proj.Name;
                var id = proj.Id;
                _db.Projects.Remove(proj);
                await _db.SaveChangesAsync(ct);

                return McpToolCallResult.Ok(toolName, ModuleName, $"Deleted project '{name}'", new
                {
                    deleted = true,
                    id,
                    name
                });
            }

            default:
                return McpToolCallResult.Fail(toolName, ModuleName, $"Unknown project tool '{toolName}'.");
        }
    }

    private async Task<(Project? Match, List<Project> Matches, bool IsAmbiguous)> FindProjectAmbiguityAwareAsync(JsonElement arguments, CancellationToken ct)
    {
        if (McpSchemaHelper.GetOptionalInt(arguments, "id") is { } id)
        {
            var byId = await _db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct);
            return (byId, byId is null ? new List<Project>() : new List<Project> { byId }, false);
        }

        if (McpSchemaHelper.GetOptionalString(arguments, "name_match", 200) is { } match)
        {
            var lower = match.ToLowerInvariant();
            var list = await _db.Projects.Where(p => p.Name.ToLower().Contains(lower)).ToListAsync(ct);
            if (list.Count <= 1) return (list.FirstOrDefault(), list, false);

            var exact = list.Where(p => string.Equals(p.Name, match, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return (exact[0], exact, false);

            return (null, list, true);
        }

        return (null, new List<Project>(), false);
    }

    private async Task<(Project? Match, List<Project> Matches, bool IsAmbiguous)> FindProjectWithDetailsAmbiguityAwareAsync(JsonElement arguments, CancellationToken ct)
    {
        if (McpSchemaHelper.GetOptionalInt(arguments, "id") is { } id)
        {
            var byId = await _db.Projects.AsNoTracking().Include(p => p.Tasks).Include(p => p.LinkedNotes).FirstOrDefaultAsync(p => p.Id == id, ct);
            return (byId, byId is null ? new List<Project>() : new List<Project> { byId }, false);
        }

        if (McpSchemaHelper.GetOptionalString(arguments, "name_match", 200) is { } match)
        {
            var lower = match.ToLowerInvariant();
            var list = await _db.Projects.AsNoTracking().Include(p => p.Tasks).Include(p => p.LinkedNotes)
                .Where(p => p.Name.ToLower().Contains(lower)).ToListAsync(ct);
            if (list.Count <= 1) return (list.FirstOrDefault(), list, false);

            var exact = list.Where(p => string.Equals(p.Name, match, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return (exact[0], exact, false);

            return (null, list, true);
        }

        return (null, new List<Project>(), false);
    }
}

