using System.Globalization;
using System.Text.Json;

namespace Friday.Mcp.Protocol;

public static class McpSchemaHelper
{
    public static JsonElement CreateObjectSchema(
        Dictionary<string, object> properties,
        string[]? required = null)
    {
        var schemaObj = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false
        };

        if (required is { Length: > 0 })
        {
            schemaObj["required"] = required;
        }

        var json = JsonSerializer.Serialize(schemaObj);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static Dictionary<string, object> Prop(string type, string description, string[]? enumValues = null)
    {
        var dict = new Dictionary<string, object>
        {
            ["type"] = type,
            ["description"] = description
        };
        if (enumValues is { Length: > 0 })
        {
            dict["enum"] = enumValues;
        }
        return dict;
    }

    public static string? GetOptionalString(JsonElement args, string name, int maxLength = 4000)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var prop))
            return null;

        if (prop.ValueKind == JsonValueKind.Null || prop.ValueKind == JsonValueKind.Undefined)
            return null;

        var str = prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();
        if (string.IsNullOrWhiteSpace(str))
            return null;

        str = str.Trim();
        return str.Length <= maxLength ? str : str[..maxLength];
    }

    public static string GetRequiredString(JsonElement args, string name, int maxLength = 4000)
    {
        var val = GetOptionalString(args, name, maxLength);
        if (string.IsNullOrWhiteSpace(val))
            throw new ArgumentException($"Missing required parameter '{name}'.");
        return val;
    }

    public static int? GetOptionalInt(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var prop))
            return null;

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out int v))
            return v;

        if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int sv))
            return sv;

        return null;
    }

    public static decimal? GetOptionalDecimal(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var prop))
            return null;

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDecimal(out decimal v))
            return v;

        if (prop.ValueKind == JsonValueKind.String && decimal.TryParse(prop.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal sv))
            return sv;

        return null;
    }

    public static double? GetOptionalDouble(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var prop))
            return null;

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out double v))
            return v;

        if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double sv))
            return sv;

        return null;
    }

    public static bool? GetOptionalBool(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var prop))
            return null;

        if (prop.ValueKind == JsonValueKind.True) return true;
        if (prop.ValueKind == JsonValueKind.False) return false;
        if (prop.ValueKind == JsonValueKind.String && bool.TryParse(prop.GetString(), out bool b)) return b;
        return null;
    }

    public static int ClampLimit(int? requested, int defaultLimit = 50, int maxLimit = 200)
    {
        if (!requested.HasValue || requested.Value <= 0) return defaultLimit;
        return Math.Min(requested.Value, maxLimit);
    }

    /// <summary>
    /// Parses ISO 8601 or natural relative date strings (in user's local offset) into UTC DateTime.
    /// </summary>
    public static DateTime? ParseFlexibleDateTimeToUtc(string? input, TimeSpan userUtcOffset, bool endOfDayIfDateOnly = false)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var trimmed = input.Trim();
        var localNow = DateTime.UtcNow + userUtcOffset;
        var lower = trimmed.ToLowerInvariant();

        if (lower == "today")
        {
            var localTarget = endOfDayIfDateOnly ? localNow.Date.AddHours(23).AddMinutes(59) : localNow.Date.AddHours(9);
            return DateTime.SpecifyKind(localTarget - userUtcOffset, DateTimeKind.Utc);
        }
        if (lower == "tomorrow")
        {
            var localTarget = endOfDayIfDateOnly ? localNow.Date.AddDays(1).AddHours(23).AddMinutes(59) : localNow.Date.AddDays(1).AddHours(9);
            return DateTime.SpecifyKind(localTarget - userUtcOffset, DateTimeKind.Utc);
        }
        if (lower == "yesterday")
        {
            var localTarget = localNow.Date.AddDays(-1).AddHours(12);
            return DateTime.SpecifyKind(localTarget - userUtcOffset, DateTimeKind.Utc);
        }
        if (lower is "next_week" or "next week")
        {
            var localTarget = localNow.Date.AddDays(7).AddHours(9);
            return DateTime.SpecifyKind(localTarget - userUtcOffset, DateTimeKind.Utc);
        }

        if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dto))
        {
            // Check if input had an explicit timezone offset or 'Z'
            bool hasExplicitOffset = trimmed.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
                || (trimmed.Length > 10 && (trimmed[10..].Contains('+') || trimmed[10..].Contains('-')));

            if (hasExplicitOffset)
            {
                return dto.UtcDateTime;
            }

            var rawDateTime = dto.DateTime;
            if (trimmed.Length <= 10 && rawDateTime.TimeOfDay == TimeSpan.Zero && endOfDayIfDateOnly)
            {
                rawDateTime = rawDateTime.Date.AddHours(17);
            }

            return DateTime.SpecifyKind(rawDateTime - userUtcOffset, DateTimeKind.Utc);
        }

        return null;
    }

    public static (DateTime StartUtc, DateTime EndUtc, string Label) GetTimeframeBoundsUtc(string? timeframe, TimeSpan userUtcOffset)
    {
        var localNow = DateTime.UtcNow + userUtcOffset;
        var todayLocal = localNow.Date;
        var tf = (timeframe ?? "all").Trim().ToLowerInvariant();

        if (tf is "today")
        {
            return (todayLocal - userUtcOffset, todayLocal.AddDays(1) - userUtcOffset, "Today");
        }
        if (tf is "tomorrow")
        {
            return (todayLocal.AddDays(1) - userUtcOffset, todayLocal.AddDays(2) - userUtcOffset, "Tomorrow");
        }
        if (tf is "this_week" or "week")
        {
            int diff = (7 + (localNow.DayOfWeek - DayOfWeek.Monday)) % 7;
            var weekStart = todayLocal.AddDays(-diff);
            return (weekStart - userUtcOffset, weekStart.AddDays(7) - userUtcOffset, "This Week");
        }
        if (tf is "last_week")
        {
            int diff = (7 + (localNow.DayOfWeek - DayOfWeek.Monday)) % 7;
            var lastWeekStart = todayLocal.AddDays(-diff - 7);
            return (lastWeekStart - userUtcOffset, lastWeekStart.AddDays(7) - userUtcOffset, "Last Week");
        }
        if (tf is "this_month" or "month")
        {
            var monthStart = new DateTime(localNow.Year, localNow.Month, 1);
            return (monthStart - userUtcOffset, monthStart.AddMonths(1) - userUtcOffset, localNow.ToString("MMMM yyyy"));
        }
        if (tf is "last_month")
        {
            var lastMonthStart = new DateTime(localNow.Year, localNow.Month, 1).AddMonths(-1);
            return (lastMonthStart - userUtcOffset, lastMonthStart.AddMonths(1) - userUtcOffset, lastMonthStart.ToString("MMMM yyyy"));
        }

        return (DateTime.UtcNow.AddYears(-20), DateTime.UtcNow.AddYears(20), "All Time");
    }

    public static string FormatLocal(DateTime? utcDateTime, TimeSpan offset, string format = "yyyy-MM-dd HH:mm")
    {
        if (!utcDateTime.HasValue) return "-";
        return (utcDateTime.Value + offset).ToString(format);
    }
}
