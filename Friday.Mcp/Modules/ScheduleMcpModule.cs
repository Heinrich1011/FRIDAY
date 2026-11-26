using System.Text.Json;
using Friday.Core.Entities;
using Friday.Core.Enums;
using Friday.Core.Interfaces;
using Friday.Core.Models;
using Friday.Infrastructure.Data;
using Friday.Mcp.Protocol;
using Microsoft.EntityFrameworkCore;

namespace Friday.Mcp.Modules;

public class ScheduleMcpModule : IMcpToolModule
{
    private readonly FridayDbContext _db;

    public ScheduleMcpModule(FridayDbContext db)
    {
        _db = db;
    }

    public string ModuleName => "schedule";

    public IReadOnlyList<McpToolDescriptor> GetTools() => new List<McpToolDescriptor>
    {
        new()
        {
            Name = "create_event",
            ModuleName = ModuleName,
            HumanActionDescription = "Scheduled a calendar event",
            Description = "Creates a new schedule/calendar event with start time, optional end time, location, category, and reminder.",
            Safety = McpToolSafety.Write,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["title"] = McpSchemaHelper.Prop("string", "Title of the event or appointment"),
                ["start_datetime"] = McpSchemaHelper.Prop("string", "Start date and time in ISO format YYYY-MM-DDTHH:mm"),
                ["end_datetime"] = McpSchemaHelper.Prop("string", "Optional end date and time in ISO format YYYY-MM-DDTHH:mm"),
                ["description"] = McpSchemaHelper.Prop("string", "Event description or agenda"),
                ["location"] = McpSchemaHelper.Prop("string", "Physical location or meeting link"),
                ["category"] = McpSchemaHelper.Prop("string", "Category such as Meeting, Appointment, Class, Personal"),
                ["reminder"] = McpSchemaHelper.Prop("string", "Optional reminder note, e.g., '30 minutes before'"),
                ["is_all_day"] = McpSchemaHelper.Prop("boolean", "Whether this is an all-day event")
            }, required: new[] { "title", "start_datetime" })
        },
        new()
        {
            Name = "get_events",
            ModuleName = ModuleName,
            HumanActionDescription = "Checked your schedule",
            Description = "Retrieves schedule events filtered by timeframe, date range, category, or search term.",
            Safety = McpToolSafety.Read,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["timeframe"] = McpSchemaHelper.Prop("string", "Timeframe to query", new[] { "today", "tomorrow", "this_week", "this_month", "all" }),
                ["start_date"] = McpSchemaHelper.Prop("string", "Optional custom start date YYYY-MM-DD"),
                ["end_date"] = McpSchemaHelper.Prop("string", "Optional custom end date YYYY-MM-DD"),
                ["category"] = McpSchemaHelper.Prop("string", "Optional event category filter"),
                ["search"] = McpSchemaHelper.Prop("string", "Optional keyword search")
            })
        },
        new()
        {
            Name = "get_today_events",
            ModuleName = ModuleName,
            HumanActionDescription = "Checked today's schedule",
            Description = "Retrieves all events scheduled for today in the user's local timezone.",
            Safety = McpToolSafety.Read,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>())
        },
        new()
        {
            Name = "get_upcoming_events",
            ModuleName = ModuleName,
            HumanActionDescription = "Checked upcoming appointments",
            Description = "Retrieves upcoming events over the next N days (default 7 days).",
            Safety = McpToolSafety.Read,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["days"] = McpSchemaHelper.Prop("integer", "Number of days ahead to look (default 7)")
            })
        },
        new()
        {
            Name = "update_event",
            ModuleName = ModuleName,
            HumanActionDescription = "Updated a scheduled event",
            Description = "Updates an existing schedule event matched by id or title_match.",
            Safety = McpToolSafety.Write,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["id"] = McpSchemaHelper.Prop("integer", "Event ID if known"),
                ["title_match"] = McpSchemaHelper.Prop("string", "Existing event title to locate"),
                ["new_title"] = McpSchemaHelper.Prop("string", "Updated title"),
                ["start_datetime"] = McpSchemaHelper.Prop("string", "Updated start date/time"),
                ["end_datetime"] = McpSchemaHelper.Prop("string", "Updated end date/time"),
                ["description"] = McpSchemaHelper.Prop("string", "Updated description"),
                ["location"] = McpSchemaHelper.Prop("string", "Updated location"),
                ["category"] = McpSchemaHelper.Prop("string", "Updated category"),
                ["reminder"] = McpSchemaHelper.Prop("string", "Updated reminder")
            })
        },
        new()
        {
            Name = "delete_event",
            ModuleName = ModuleName,
            HumanActionDescription = "Deleted a scheduled event",
            Description = "Deletes a schedule event by id or title_match. Requires user confirmation.",
            Safety = McpToolSafety.Destructive,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["id"] = McpSchemaHelper.Prop("integer", "Event ID if known"),
                ["title_match"] = McpSchemaHelper.Prop("string", "Title or partial title of the event to delete")
            })
        }
    };

    public async Task<string?> DescribeTargetForDestructiveActionAsync(string toolName, JsonElement arguments, CancellationToken ct = default)
    {
        if (toolName != "delete_event") return null;
        var lookup = await FindEventAmbiguityAwareAsync(arguments, ct);
        if (lookup.IsAmbiguous)
        {
            return $"AMBIGUOUS:{string.Join(" | ", lookup.Matches.Select(e => $"#{e.Id} {e.Title} ({e.StartDateTime:yyyy-MM-dd HH:mm} UTC)"))}";
        }
        return lookup.Match is null ? null : $"event '{lookup.Match.Title}' (ID #{lookup.Match.Id})";
    }

    public async Task<McpToolCallResult> ExecuteToolAsync(string toolName, JsonElement arguments, McpToolCallContext context, CancellationToken ct = default)
    {
        switch (toolName)
        {
            case "create_event":
            {
                var title = McpSchemaHelper.GetRequiredString(arguments, "title", 250);
                var startStr = McpSchemaHelper.GetRequiredString(arguments, "start_datetime", 60);
                var startUtc = McpSchemaHelper.ParseFlexibleDateTimeToUtc(startStr, context.UserUtcOffset)
                    ?? throw new ArgumentException($"Invalid start_datetime '{startStr}'. Use YYYY-MM-DDTHH:mm.");

                var endUtc = McpSchemaHelper.ParseFlexibleDateTimeToUtc(
                    McpSchemaHelper.GetOptionalString(arguments, "end_datetime"),
                    context.UserUtcOffset) ?? startUtc.AddHours(1);

                var now = DateTime.UtcNow;
                var ev = new ScheduleEvent
                {
                    Title = title,
                    StartDateTime = startUtc,
                    EndDateTime = endUtc,
                    Description = McpSchemaHelper.GetOptionalString(arguments, "description", 2000),
                    Location = McpSchemaHelper.GetOptionalString(arguments, "location", 250),
                    Category = McpSchemaHelper.GetOptionalString(arguments, "category", 100),
                    Reminder = McpSchemaHelper.GetOptionalString(arguments, "reminder", 150),
                    IsAllDay = McpSchemaHelper.GetOptionalBool(arguments, "is_all_day") ?? false,
                    CreatedAt = now,
                    UpdatedAt = now
                };

                _db.ScheduleEvents.Add(ev);
                await _db.SaveChangesAsync(ct);

                return McpToolCallResult.Ok(toolName, ModuleName, $"Scheduled '{ev.Title}'", new
                {
                    id = ev.Id,
                    title = ev.Title,
                    start_datetime = McpSchemaHelper.FormatLocal(ev.StartDateTime, context.UserUtcOffset),
                    end_datetime = McpSchemaHelper.FormatLocal(ev.EndDateTime, context.UserUtcOffset),
                    location = ev.Location,
                    category = ev.Category
                });
            }

            case "get_events":
            {
                var query = _db.ScheduleEvents.AsNoTracking().AsQueryable();

                var startCustom = McpSchemaHelper.ParseFlexibleDateTimeToUtc(McpSchemaHelper.GetOptionalString(arguments, "start_date"), context.UserUtcOffset);
                var endCustom = McpSchemaHelper.ParseFlexibleDateTimeToUtc(McpSchemaHelper.GetOptionalString(arguments, "end_date"), context.UserUtcOffset, endOfDayIfDateOnly: true);

                if (startCustom.HasValue) query = query.Where(e => e.StartDateTime >= startCustom.Value);
                if (endCustom.HasValue) query = query.Where(e => e.StartDateTime <= endCustom.Value);

                var tf = McpSchemaHelper.GetOptionalString(arguments, "timeframe");
                if (!startCustom.HasValue && !endCustom.HasValue && !string.IsNullOrWhiteSpace(tf) && tf != "all")
                {
                    var (sUtc, eUtc, _) = McpSchemaHelper.GetTimeframeBoundsUtc(tf, context.UserUtcOffset);
                    query = query.Where(e => e.StartDateTime >= sUtc && e.StartDateTime < eUtc);
                }

                if (McpSchemaHelper.GetOptionalString(arguments, "category", 100) is { } cat)
                {
                    query = query.Where(e => e.Category != null && e.Category.ToLower().Contains(cat.ToLower()));
                }

                if (McpSchemaHelper.GetOptionalString(arguments, "search", 200) is { } kw)
                {
                    var lower = kw.ToLower();
                    query = query.Where(e => e.Title.ToLower().Contains(lower)
                        || (e.Description != null && e.Description.ToLower().Contains(lower))
                        || (e.Location != null && e.Location.ToLower().Contains(lower)));
                }

                var events = await query.OrderBy(e => e.StartDateTime).Take(100).ToListAsync(ct);
                var table = events.Count > 0 ? BuildEventsTable("Schedule Events", events, context.UserUtcOffset) : null;

                return McpToolCallResult.Ok(toolName, ModuleName, $"Retrieved {events.Count} event(s)", new
                {
                    count = events.Count,
                    events = MapEvents(events, context.UserUtcOffset)
                }, table);
            }

            case "get_today_events":
            {
                var (startUtc, endUtc, _) = McpSchemaHelper.GetTimeframeBoundsUtc("today", context.UserUtcOffset);
                var events = await _db.ScheduleEvents.AsNoTracking()
                    .Where(e => e.StartDateTime >= startUtc && e.StartDateTime < endUtc)
                    .OrderBy(e => e.StartDateTime)
                    .ToListAsync(ct);

                var table = events.Count > 0 ? BuildEventsTable("Today's Schedule", events, context.UserUtcOffset) : null;
                return McpToolCallResult.Ok(toolName, ModuleName, $"Checked today's schedule ({events.Count} event(s))", new
                {
                    count = events.Count,
                    events = MapEvents(events, context.UserUtcOffset)
                }, table);
            }

            case "get_upcoming_events":
            {
                int days = Math.Clamp(McpSchemaHelper.GetOptionalInt(arguments, "days") ?? 7, 1, 90);
                var nowUtc = DateTime.UtcNow;
                var endUtc = nowUtc.AddDays(days);

                var events = await _db.ScheduleEvents.AsNoTracking()
                    .Where(e => e.StartDateTime >= nowUtc.AddHours(-2) && e.StartDateTime <= endUtc)
                    .OrderBy(e => e.StartDateTime)
                    .ToListAsync(ct);

                var table = events.Count > 0 ? BuildEventsTable($"Upcoming Events (Next {days} Days)", events, context.UserUtcOffset) : null;
                return McpToolCallResult.Ok(toolName, ModuleName, $"Found {events.Count} upcoming event(s)", new
                {
                    days_ahead = days,
                    count = events.Count,
                    events = MapEvents(events, context.UserUtcOffset)
                }, table);
            }

            case "update_event":
            {
                var lookup = await FindEventAmbiguityAwareAsync(arguments, ct);
                if (lookup.IsAmbiguous)
                {
                    return McpToolCallResult.Ambiguous(
                        toolName,
                        ModuleName,
                        "Multiple events match that title. Ask the user which event to update.",
                        lookup.Matches.Select(e => new { id = e.Id, title = e.Title, start = McpSchemaHelper.FormatLocal(e.StartDateTime, context.UserUtcOffset) }).ToList());
                }

                var ev = lookup.Match;
                if (ev is null)
                {
                    return McpToolCallResult.Fail(toolName, ModuleName, "No matching event was found to update.");
                }

                if (McpSchemaHelper.GetOptionalString(arguments, "new_title", 250) is { } nt) ev.Title = nt;
                if (McpSchemaHelper.GetOptionalString(arguments, "start_datetime") is { } sStr
                    && McpSchemaHelper.ParseFlexibleDateTimeToUtc(sStr, context.UserUtcOffset) is { } sUtc)
                {
                    ev.StartDateTime = sUtc;
                }
                if (McpSchemaHelper.GetOptionalString(arguments, "end_datetime") is { } eStr
                    && McpSchemaHelper.ParseFlexibleDateTimeToUtc(eStr, context.UserUtcOffset) is { } eUtc)
                {
                    ev.EndDateTime = eUtc;
                }
                if (McpSchemaHelper.GetOptionalString(arguments, "description", 2000) is { } desc) ev.Description = desc;
                if (McpSchemaHelper.GetOptionalString(arguments, "location", 250) is { } loc) ev.Location = loc;
                if (McpSchemaHelper.GetOptionalString(arguments, "category", 100) is { } cat) ev.Category = cat;
                if (McpSchemaHelper.GetOptionalString(arguments, "reminder", 150) is { } rem) ev.Reminder = rem;

                ev.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);

                return McpToolCallResult.Ok(toolName, ModuleName, $"Updated event '{ev.Title}'", new
                {
                    id = ev.Id,
                    title = ev.Title,
                    start_datetime = McpSchemaHelper.FormatLocal(ev.StartDateTime, context.UserUtcOffset),
                    location = ev.Location
                });
            }

            case "delete_event":
            {
                var lookup = await FindEventAmbiguityAwareAsync(arguments, ct);
                if (lookup.IsAmbiguous)
                {
                    return McpToolCallResult.Ambiguous(
                        toolName,
                        ModuleName,
                        "Multiple events match that title. Ask the user which event to delete.",
                        lookup.Matches.Select(e => new { id = e.Id, title = e.Title, start = McpSchemaHelper.FormatLocal(e.StartDateTime, context.UserUtcOffset) }).ToList());
                }

                var ev = lookup.Match;
                if (ev is null)
                {
                    return McpToolCallResult.Fail(toolName, ModuleName, "No matching event was found to delete.");
                }

                var title = ev.Title;
                var id = ev.Id;
                _db.ScheduleEvents.Remove(ev);
                await _db.SaveChangesAsync(ct);

                return McpToolCallResult.Ok(toolName, ModuleName, $"Deleted event '{title}'", new
                {
                    deleted = true,
                    id,
                    title
                });
            }

            default:
                return McpToolCallResult.Fail(toolName, ModuleName, $"Unknown schedule tool '{toolName}'.");
        }
    }

    private async Task<(ScheduleEvent? Match, List<ScheduleEvent> Matches, bool IsAmbiguous)> FindEventAmbiguityAwareAsync(JsonElement arguments, CancellationToken ct)
    {
        if (McpSchemaHelper.GetOptionalInt(arguments, "id") is { } id)
        {
            var byId = await _db.ScheduleEvents.FirstOrDefaultAsync(e => e.Id == id, ct);
            return (byId, byId is null ? new List<ScheduleEvent>() : new List<ScheduleEvent> { byId }, false);
        }

        if (McpSchemaHelper.GetOptionalString(arguments, "title_match", 250) is { } match)
        {
            var lower = match.ToLowerInvariant();
            var list = await _db.ScheduleEvents.Where(e => e.Title.ToLower().Contains(lower))
                .OrderByDescending(e => e.StartDateTime)
                .Take(10)
                .ToListAsync(ct);
            if (list.Count <= 1) return (list.FirstOrDefault(), list, false);

            var exact = list.Where(e => string.Equals(e.Title, match, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return (exact[0], exact, false);

            return (null, list, true);
        }

        return (null, new List<ScheduleEvent>(), false);
    }

    private static object MapEvents(List<ScheduleEvent> events, TimeSpan offset)
        => events.Select(e => new
        {
            id = e.Id,
            title = e.Title,
            start_datetime = McpSchemaHelper.FormatLocal(e.StartDateTime, offset),
            end_datetime = McpSchemaHelper.FormatLocal(e.EndDateTime, offset),
            location = e.Location,
            category = e.Category,
            reminder = e.Reminder,
            description = e.Description
        }).ToList();

    private static TableResult BuildEventsTable(string title, List<ScheduleEvent> events, TimeSpan offset)
        => new()
        {
            Title = title,
            Columns = new List<string> { "ID", "Event", "Start", "End", "Location", "Category" },
            Rows = events.Select(e => new List<string>
            {
                $"#{e.Id}",
                e.Title,
                McpSchemaHelper.FormatLocal(e.StartDateTime, offset),
                McpSchemaHelper.FormatLocal(e.EndDateTime, offset),
                e.Location ?? "-",
                e.Category ?? "-"
            }).ToList(),
            FooterSummary = $"Total: {events.Count} event(s)",
            SourceDescription = "SQLite ScheduleEvents"
        };
}

