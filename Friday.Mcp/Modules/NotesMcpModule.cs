using System.Text.Json;
using Friday.Core.Entities;
using Friday.Core.Enums;
using Friday.Core.Interfaces;
using Friday.Core.Models;
using Friday.Infrastructure.Data;
using Friday.Mcp.Protocol;
using Microsoft.EntityFrameworkCore;

namespace Friday.Mcp.Modules;

public class NotesMcpModule : IMcpToolModule
{
    private readonly FridayDbContext _db;

    public NotesMcpModule(FridayDbContext db)
    {
        _db = db;
    }

    public string ModuleName => "notes";

    public IReadOnlyList<McpToolDescriptor> GetTools() => new List<McpToolDescriptor>
    {
        new()
        {
            Name = "create_note",
            ModuleName = ModuleName,
            HumanActionDescription = "Created a note",
            Description = "Creates a new personal note with title, content, optional category, tags, and optional project association.",
            Safety = McpToolSafety.Write,
            Sensitivity = DataSensitivityLevel.Personal,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["title"] = McpSchemaHelper.Prop("string", "Note title"),
                ["content"] = McpSchemaHelper.Prop("string", "Note body content"),
                ["category"] = McpSchemaHelper.Prop("string", "Optional category"),
                ["tags"] = McpSchemaHelper.Prop("string", "Comma-separated tags"),
                ["project_name"] = McpSchemaHelper.Prop("string", "Optional project name to link this note to")
            }, required: new[] { "title", "content" })
        },
        new()
        {
            Name = "search_notes",
            ModuleName = ModuleName,
            HumanActionDescription = "Searched your notes",
            Description = "Searches notes by keyword query, category, tag, or project. Returns matching notes.",
            Safety = McpToolSafety.Read,
            Sensitivity = DataSensitivityLevel.Personal,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["query"] = McpSchemaHelper.Prop("string", "Search keyword across title, content, and tags"),
                ["category"] = McpSchemaHelper.Prop("string", "Optional category filter"),
                ["tag"] = McpSchemaHelper.Prop("string", "Optional tag filter"),
                ["project_name"] = McpSchemaHelper.Prop("string", "Optional project name filter"),
                ["include_full_content"] = McpSchemaHelper.Prop("boolean", "Set true when summarizing or reading full note contents")
            })
        },
        new()
        {
            Name = "get_note",
            ModuleName = ModuleName,
            HumanActionDescription = "Read a note",
            Description = "Retrieves the full content of a specific note by id or title_match.",
            Safety = McpToolSafety.Read,
            Sensitivity = DataSensitivityLevel.Personal,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["id"] = McpSchemaHelper.Prop("integer", "Note ID if known"),
                ["title_match"] = McpSchemaHelper.Prop("string", "Title or partial title of the note")
            })
        },
        new()
        {
            Name = "update_note",
            ModuleName = ModuleName,
            HumanActionDescription = "Updated a note",
            Description = "Updates a note's title, content (or appends content), category, or tags.",
            Safety = McpToolSafety.Write,
            Sensitivity = DataSensitivityLevel.Personal,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["id"] = McpSchemaHelper.Prop("integer", "Note ID if known"),
                ["title_match"] = McpSchemaHelper.Prop("string", "Existing note title to match"),
                ["new_title"] = McpSchemaHelper.Prop("string", "Updated title"),
                ["content"] = McpSchemaHelper.Prop("string", "Replacement full content"),
                ["append_content"] = McpSchemaHelper.Prop("string", "Text to append to the end of existing content"),
                ["category"] = McpSchemaHelper.Prop("string", "Updated category"),
                ["tags"] = McpSchemaHelper.Prop("string", "Updated comma-separated tags")
            })
        },
        new()
        {
            Name = "delete_note",
            ModuleName = ModuleName,
            HumanActionDescription = "Deleted a note",
            Description = "Deletes a note by id or title_match. Requires user confirmation.",
            Safety = McpToolSafety.Destructive,
            Sensitivity = DataSensitivityLevel.Personal,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["id"] = McpSchemaHelper.Prop("integer", "Note ID if known"),
                ["title_match"] = McpSchemaHelper.Prop("string", "Title or partial title of the note to delete")
            })
        }
    };

    public async Task<string?> DescribeTargetForDestructiveActionAsync(string toolName, JsonElement arguments, CancellationToken ct = default)
    {
        if (toolName != "delete_note") return null;
        var lookup = await FindNoteAmbiguityAwareAsync(arguments, ct);
        if (lookup.IsAmbiguous)
        {
            return $"AMBIGUOUS:{string.Join(" | ", lookup.Matches.Select(n => $"#{n.Id} {n.Title}"))}";
        }
        return lookup.Match is null ? null : $"note '{lookup.Match.Title}' (ID #{lookup.Match.Id})";
    }

    public async Task<McpToolCallResult> ExecuteToolAsync(string toolName, JsonElement arguments, McpToolCallContext context, CancellationToken ct = default)
    {
        switch (toolName)
        {
            case "create_note":
            {
                var title = McpSchemaHelper.GetRequiredString(arguments, "title", 250);
                var content = McpSchemaHelper.GetRequiredString(arguments, "content", 20000);
                int? projectId = null;
                if (McpSchemaHelper.GetOptionalString(arguments, "project_name", 200) is { } projName)
                {
                    var proj = await _db.Projects.FirstOrDefaultAsync(p => p.Name.ToLower().Contains(projName.ToLower()), ct);
                    projectId = proj?.Id;
                }

                var now = DateTime.UtcNow;
                var note = new Note
                {
                    Title = title,
                    Content = content,
                    Category = McpSchemaHelper.GetOptionalString(arguments, "category", 100),
                    Tags = McpSchemaHelper.GetOptionalString(arguments, "tags", 500),
                    ProjectId = projectId,
                    CreatedAt = now,
                    UpdatedAt = now
                };

                _db.Notes.Add(note);
                await _db.SaveChangesAsync(ct);

                return McpToolCallResult.Ok(toolName, ModuleName, $"Created note '{note.Title}'", new
                {
                    id = note.Id,
                    title = note.Title,
                    category = note.Category,
                    tags = note.Tags,
                    created_at = McpSchemaHelper.FormatLocal(note.CreatedAt, context.UserUtcOffset)
                });
            }

            case "search_notes":
            {
                var query = _db.Notes.AsNoTracking().Include(n => n.Project).AsQueryable();

                if (McpSchemaHelper.GetOptionalString(arguments, "query", 200) is { } q)
                {
                    var lower = q.ToLower();
                    query = query.Where(n => n.Title.ToLower().Contains(lower)
                        || n.Content.ToLower().Contains(lower)
                        || (n.Tags != null && n.Tags.ToLower().Contains(lower)));
                }

                if (McpSchemaHelper.GetOptionalString(arguments, "category", 100) is { } cat)
                {
                    query = query.Where(n => n.Category != null && n.Category.ToLower().Contains(cat.ToLower()));
                }

                if (McpSchemaHelper.GetOptionalString(arguments, "tag", 100) is { } tag)
                {
                    query = query.Where(n => n.Tags != null && n.Tags.ToLower().Contains(tag.ToLower()));
                }

                if (McpSchemaHelper.GetOptionalString(arguments, "project_name", 200) is { } pName)
                {
                    query = query.Where(n => n.Project != null && n.Project.Name.ToLower().Contains(pName.ToLower()));
                }

                bool fullContent = McpSchemaHelper.GetOptionalBool(arguments, "include_full_content") ?? true;
                var notes = await query.OrderByDescending(n => n.UpdatedAt).Take(40).ToListAsync(ct);

                var table = notes.Count > 0 ? new TableResult
                {
                    Title = "Notes",
                    Columns = new List<string> { "ID", "Title", "Category", "Tags", "Updated", "Preview" },
                    Rows = notes.Select(n => new List<string>
                    {
                        $"#{n.Id}",
                        n.Title,
                        n.Category ?? "-",
                        n.Tags ?? "-",
                        McpSchemaHelper.FormatLocal(n.UpdatedAt, context.UserUtcOffset),
                        n.Content.Length <= 80 ? n.Content : n.Content[..80] + "..."
                    }).ToList(),
                    FooterSummary = $"Total: {notes.Count} note(s)",
                    SourceDescription = "SQLite Notes"
                } : null;

                return McpToolCallResult.Ok(toolName, ModuleName, $"Searched notes ({notes.Count} match(es))", new
                {
                    count = notes.Count,
                    notes = notes.Select(n => new
                    {
                        id = n.Id,
                        title = n.Title,
                        category = n.Category,
                        tags = n.Tags,
                        project = n.Project?.Name,
                        content = fullContent ? (n.Content.Length <= 1500 ? n.Content : n.Content[..1500] + "...") : (n.Content.Length <= 220 ? n.Content : n.Content[..220] + "..."),
                        updated_at = McpSchemaHelper.FormatLocal(n.UpdatedAt, context.UserUtcOffset)
                    }).ToList()
                }, table);
            }

            case "get_note":
            {
                var lookup = await FindNoteAmbiguityAwareAsync(arguments, ct);
                if (lookup.IsAmbiguous)
                {
                    return McpToolCallResult.Ambiguous(
                        toolName,
                        ModuleName,
                        "Multiple notes match that title. Ask the user which note they mean.",
                        lookup.Matches.Select(n => new { id = n.Id, title = n.Title, category = n.Category }).ToList());
                }

                var note = lookup.Match;
                if (note is null)
                {
                    return McpToolCallResult.Ok(toolName, ModuleName, "Note not found", new { exists = false, message = "No matching note found." });
                }

                return McpToolCallResult.Ok(toolName, ModuleName, $"Retrieved note '{note.Title}'", new
                {
                    exists = true,
                    id = note.Id,
                    title = note.Title,
                    content = note.Content,
                    category = note.Category,
                    tags = note.Tags,
                    updated_at = McpSchemaHelper.FormatLocal(note.UpdatedAt, context.UserUtcOffset)
                });
            }

            case "update_note":
            {
                var lookup = await FindNoteAmbiguityAwareAsync(arguments, ct);
                if (lookup.IsAmbiguous)
                {
                    return McpToolCallResult.Ambiguous(
                        toolName,
                        ModuleName,
                        "Multiple notes match that title. Ask the user which note to update.",
                        lookup.Matches.Select(n => new { id = n.Id, title = n.Title, category = n.Category }).ToList());
                }

                var note = lookup.Match;
                if (note is null)
                {
                    return McpToolCallResult.Fail(toolName, ModuleName, "No matching note was found to update.");
                }

                if (McpSchemaHelper.GetOptionalString(arguments, "new_title", 250) is { } nt) note.Title = nt;
                if (McpSchemaHelper.GetOptionalString(arguments, "content", 20000) is { } c) note.Content = c;
                if (McpSchemaHelper.GetOptionalString(arguments, "append_content", 10000) is { } app)
                {
                    note.Content = string.IsNullOrEmpty(note.Content) ? app : $"{note.Content}\n{app}";
                }
                if (McpSchemaHelper.GetOptionalString(arguments, "category", 100) is { } cat) note.Category = cat;
                if (McpSchemaHelper.GetOptionalString(arguments, "tags", 500) is { } tags) note.Tags = tags;

                note.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);

                return McpToolCallResult.Ok(toolName, ModuleName, $"Updated note '{note.Title}'", new
                {
                    id = note.Id,
                    title = note.Title,
                    updated_at = McpSchemaHelper.FormatLocal(note.UpdatedAt, context.UserUtcOffset)
                });
            }

            case "delete_note":
            {
                var lookup = await FindNoteAmbiguityAwareAsync(arguments, ct);
                if (lookup.IsAmbiguous)
                {
                    return McpToolCallResult.Ambiguous(
                        toolName,
                        ModuleName,
                        "Multiple notes match that title. Ask the user which note to delete.",
                        lookup.Matches.Select(n => new { id = n.Id, title = n.Title, category = n.Category }).ToList());
                }

                var note = lookup.Match;
                if (note is null)
                {
                    return McpToolCallResult.Fail(toolName, ModuleName, "No matching note was found to delete.");
                }

                var title = note.Title;
                var id = note.Id;
                _db.Notes.Remove(note);
                await _db.SaveChangesAsync(ct);

                return McpToolCallResult.Ok(toolName, ModuleName, $"Deleted note '{title}'", new
                {
                    deleted = true,
                    id,
                    title
                });
            }

            default:
                return McpToolCallResult.Fail(toolName, ModuleName, $"Unknown notes tool '{toolName}'.");
        }
    }

    private async Task<(Note? Match, List<Note> Matches, bool IsAmbiguous)> FindNoteAmbiguityAwareAsync(JsonElement arguments, CancellationToken ct)
    {
        if (McpSchemaHelper.GetOptionalInt(arguments, "id") is { } id)
        {
            var byId = await _db.Notes.FirstOrDefaultAsync(n => n.Id == id, ct);
            return (byId, byId is null ? new List<Note>() : new List<Note> { byId }, false);
        }

        if (McpSchemaHelper.GetOptionalString(arguments, "title_match", 250) is { } match)
        {
            var lower = match.ToLowerInvariant();
            var list = await _db.Notes.Where(n => n.Title.ToLower().Contains(lower))
                .OrderByDescending(n => n.UpdatedAt)
                .Take(10)
                .ToListAsync(ct);
            if (list.Count <= 1) return (list.FirstOrDefault(), list, false);

            var exact = list.Where(n => string.Equals(n.Title, match, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return (exact[0], exact, false);

            return (null, list, true);
        }

        return (null, new List<Note>(), false);
    }
}

