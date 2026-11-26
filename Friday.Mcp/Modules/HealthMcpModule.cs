using System.Text.Json;
using Friday.Core.Entities;
using Friday.Core.Enums;
using Friday.Core.Interfaces;
using Friday.Core.Models;
using Friday.Infrastructure.Data;
using Friday.Mcp.Protocol;
using Microsoft.EntityFrameworkCore;

namespace Friday.Mcp.Modules;

public class HealthMcpModule : IMcpToolModule
{
    private const string WellnessDisclaimer =
        "Personal wellness record keeping only — NOT medical advice or diagnosis. Consult a qualified healthcare professional for medical concerns.";

    private readonly FridayDbContext _db;

    public HealthMcpModule(FridayDbContext db)
    {
        _db = db;
    }

    public string ModuleName => "health";

    public IReadOnlyList<McpToolDescriptor> GetTools() => new List<McpToolDescriptor>
    {
        new()
        {
            Name = "add_health_record",
            ModuleName = ModuleName,
            HumanActionDescription = "Logged a wellness record",
            Description = "Records personal wellness tracking data (sleep hours, water intake, exercise, weight, blood pressure, heart rate, symptoms, notes). Not for medical diagnosis.",
            Safety = McpToolSafety.Write,
            Sensitivity = DataSensitivityLevel.RestrictedHealth,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["date"] = McpSchemaHelper.Prop("string", "Record date YYYY-MM-DD or 'today'"),
                ["sleep_hours"] = McpSchemaHelper.Prop("number", "Hours of sleep (0-24)"),
                ["water_intake_liters"] = McpSchemaHelper.Prop("number", "Water intake in liters"),
                ["exercise"] = McpSchemaHelper.Prop("string", "Exercise activity description"),
                ["exercise_minutes"] = McpSchemaHelper.Prop("integer", "Duration of exercise in minutes"),
                ["weight_kg"] = McpSchemaHelper.Prop("number", "Weight in kilograms"),
                ["blood_pressure"] = McpSchemaHelper.Prop("string", "Blood pressure reading, e.g., '120/80'"),
                ["heart_rate_bpm"] = McpSchemaHelper.Prop("integer", "Resting heart rate in BPM"),
                ["symptoms"] = McpSchemaHelper.Prop("string", "Any user-noted symptoms"),
                ["notes"] = McpSchemaHelper.Prop("string", "Personal wellness notes")
            })
        },
        new()
        {
            Name = "get_health_records",
            ModuleName = ModuleName,
            HumanActionDescription = "Retrieved your wellness logs",
            Description = "Retrieves stored wellness records for the past N days (default 30).",
            Safety = McpToolSafety.Read,
            Sensitivity = DataSensitivityLevel.RestrictedHealth,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["days"] = McpSchemaHelper.Prop("integer", "Number of days back to query (default 30)")
            })
        },
        new()
        {
            Name = "get_latest_health_record",
            ModuleName = ModuleName,
            HumanActionDescription = "Checked your latest wellness entry",
            Description = "Retrieves the most recent personal health/wellness record.",
            Safety = McpToolSafety.Read,
            Sensitivity = DataSensitivityLevel.RestrictedHealth,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>())
        },
        new()
        {
            Name = "get_health_history",
            ModuleName = ModuleName,
            HumanActionDescription = "Checked your wellness history",
            Description = "Retrieves chronological wellness history over a specified number of days.",
            Safety = McpToolSafety.Read,
            Sensitivity = DataSensitivityLevel.RestrictedHealth,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["days"] = McpSchemaHelper.Prop("integer", "Days of history to retrieve (default 30)")
            })
        },
        new()
        {
            Name = "get_sleep_history",
            ModuleName = ModuleName,
            HumanActionDescription = "Analyzed your logged sleep history",
            Description = "Retrieves logged sleep hours over time with average sleep calculation and line chart.",
            Safety = McpToolSafety.Read,
            Sensitivity = DataSensitivityLevel.RestrictedHealth,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["days"] = McpSchemaHelper.Prop("integer", "Number of days back to inspect (default 14)")
            })
        },
        new()
        {
            Name = "get_health_trends",
            ModuleName = ModuleName,
            HumanActionDescription = "Computed wellness trends",
            Description = "Computes trends across logged sleep, hydration, exercise minutes, weight, and heart rate.",
            Safety = McpToolSafety.Read,
            Sensitivity = DataSensitivityLevel.RestrictedHealth,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["days"] = McpSchemaHelper.Prop("integer", "Window in days (default 30)")
            })
        },
        new()
        {
            Name = "generate_health_summary",
            ModuleName = ModuleName,
            HumanActionDescription = "Generated your wellness tracking summary",
            Description = "Summarizes user-entered wellness metrics over a given period without making medical diagnoses.",
            Safety = McpToolSafety.Read,
            Sensitivity = DataSensitivityLevel.RestrictedHealth,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["days"] = McpSchemaHelper.Prop("integer", "Number of days to summarize (default 30)")
            })
        },
        new()
        {
            Name = "delete_health_record",
            ModuleName = ModuleName,
            HumanActionDescription = "Deleted a wellness record",
            Description = "Deletes a stored health/wellness record by id. Requires user confirmation.",
            Safety = McpToolSafety.Destructive,
            Sensitivity = DataSensitivityLevel.RestrictedHealth,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["id"] = McpSchemaHelper.Prop("integer", "Health record ID to delete")
            }, required: new[] { "id" })
        }
    };

    public async Task<string?> DescribeTargetForDestructiveActionAsync(string toolName, JsonElement arguments, CancellationToken ct = default)
    {
        if (toolName != "delete_health_record") return null;
        if (McpSchemaHelper.GetOptionalInt(arguments, "id") is not { } id) return null;
        var rec = await _db.HealthRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        return rec is null ? null : $"wellness record #{rec.Id} from {rec.Date:yyyy-MM-dd}";
    }

    public async Task<McpToolCallResult> ExecuteToolAsync(string toolName, JsonElement arguments, McpToolCallContext context, CancellationToken ct = default)
    {
        var profile = await _db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(ct);
        if (profile is { AllowHealthContextInAi: false } && toolName != "delete_health_record")
        {
            return McpToolCallResult.Fail(toolName, ModuleName, "Wellness data access for AI is disabled in your Profile privacy settings.");
        }

        switch (toolName)
        {
            case "add_health_record":
            {
                var dateUtc = McpSchemaHelper.ParseFlexibleDateTimeToUtc(
                    McpSchemaHelper.GetOptionalString(arguments, "date"),
                    context.UserUtcOffset) ?? DateTime.UtcNow;

                var sleep = McpSchemaHelper.GetOptionalDouble(arguments, "sleep_hours");
                if (sleep is < 0 or > 24) throw new ArgumentException("sleep_hours must be between 0 and 24.");

                var water = McpSchemaHelper.GetOptionalDouble(arguments, "water_intake_liters");
                if (water is < 0 or > 30) throw new ArgumentException("water_intake_liters must be between 0 and 30.");

                var weight = McpSchemaHelper.GetOptionalDouble(arguments, "weight_kg");
                if (weight is <= 0 or > 500) throw new ArgumentException("weight_kg must be realistic (1-500).");

                var hr = McpSchemaHelper.GetOptionalInt(arguments, "heart_rate_bpm");
                if (hr is < 20 or > 260) throw new ArgumentException("heart_rate_bpm must be between 20 and 260.");

                var now = DateTime.UtcNow;
                var rec = new HealthRecord
                {
                    Date = dateUtc,
                    SleepHours = sleep,
                    WaterIntakeLiters = water,
                    Exercise = McpSchemaHelper.GetOptionalString(arguments, "exercise", 500),
                    ExerciseMinutes = McpSchemaHelper.GetOptionalInt(arguments, "exercise_minutes"),
                    WeightKg = weight,
                    BloodPressure = McpSchemaHelper.GetOptionalString(arguments, "blood_pressure", 30),
                    HeartRateBpm = hr,
                    Symptoms = McpSchemaHelper.GetOptionalString(arguments, "symptoms", 1000),
                    Notes = McpSchemaHelper.GetOptionalString(arguments, "notes", 2000),
                    CreatedAt = now,
                    UpdatedAt = now
                };

                _db.HealthRecords.Add(rec);
                await _db.SaveChangesAsync(ct);

                return McpToolCallResult.Ok(toolName, ModuleName, "Logged personal wellness entry", new
                {
                    id = rec.Id,
                    date = McpSchemaHelper.FormatLocal(rec.Date, context.UserUtcOffset, "yyyy-MM-dd"),
                    sleep_hours = rec.SleepHours,
                    water_intake_liters = rec.WaterIntakeLiters,
                    exercise = rec.Exercise,
                    disclaimer = WellnessDisclaimer
                });
            }

            case "get_latest_health_record":
            {
                var latest = await _db.HealthRecords.AsNoTracking()
                    .OrderByDescending(r => r.Date)
                    .FirstOrDefaultAsync(ct);

                if (latest is null)
                {
                    return McpToolCallResult.Ok(toolName, ModuleName, "No wellness records stored yet", new
                    {
                        exists = false,
                        disclaimer = WellnessDisclaimer
                    });
                }

                return McpToolCallResult.Ok(toolName, ModuleName, "Retrieved latest wellness record", new
                {
                    exists = true,
                    record = MapRecord(latest, context.UserUtcOffset),
                    disclaimer = WellnessDisclaimer
                });
            }

            case "get_health_records":
            case "get_health_history":
            {
                int days = Math.Clamp(McpSchemaHelper.GetOptionalInt(arguments, "days") ?? 30, 1, 365);
                var cutoffUtc = DateTime.UtcNow.AddDays(-days);

                var records = await _db.HealthRecords.AsNoTracking()
                    .Where(r => r.Date >= cutoffUtc)
                    .OrderByDescending(r => r.Date)
                    .Take(90)
                    .ToListAsync(ct);

                var table = records.Count > 0 ? new TableResult
                {
                    Title = $"Wellness Records (Last {days} Days)",
                    Columns = new List<string> { "Date", "Sleep (h)", "Water (L)", "Exercise", "HR (bpm)", "BP", "Weight (kg)" },
                    Rows = records.Select(r => new List<string>
                    {
                        McpSchemaHelper.FormatLocal(r.Date, context.UserUtcOffset, "yyyy-MM-dd"),
                        r.SleepHours?.ToString("F1") ?? "-",
                        r.WaterIntakeLiters?.ToString("F1") ?? "-",
                        r.Exercise ?? "-",
                        r.HeartRateBpm?.ToString() ?? "-",
                        r.BloodPressure ?? "-",
                        r.WeightKg?.ToString("F1") ?? "-"
                    }).ToList(),
                    FooterSummary = $"{records.Count} personal wellness log(s)",
                    SourceDescription = "SQLite HealthRecords (Wellness Tracking)"
                } : null;

                return McpToolCallResult.Ok(toolName, ModuleName, $"Retrieved {records.Count} wellness record(s)", new
                {
                    days,
                    count = records.Count,
                    records = records.Select(r => MapRecord(r, context.UserUtcOffset)).ToList(),
                    disclaimer = WellnessDisclaimer
                }, table);
            }

            case "get_sleep_history":
            {
                int days = Math.Clamp(McpSchemaHelper.GetOptionalInt(arguments, "days") ?? 14, 1, 180);
                var cutoffUtc = DateTime.UtcNow.AddDays(-days);

                var sleepLogs = await _db.HealthRecords.AsNoTracking()
                    .Where(r => r.Date >= cutoffUtc && r.SleepHours.HasValue)
                    .OrderBy(r => r.Date)
                    .ToListAsync(ct);

                double avgSleep = sleepLogs.Count > 0 ? Math.Round(sleepLogs.Average(r => r.SleepHours!.Value), 2) : 0;

                var chart = sleepLogs.Count > 0 ? new ChartConfiguration
                {
                    ChartType = "line",
                    Title = $"Logged Sleep Hours (Last {days} Days)",
                    Labels = sleepLogs.Select(r => McpSchemaHelper.FormatLocal(r.Date, context.UserUtcOffset, "MMM dd")).ToList(),
                    Datasets = new List<ChartDataset>
                    {
                        new()
                        {
                            Label = "Sleep Hours",
                            Data = sleepLogs.Select(r => r.SleepHours!.Value).ToList(),
                            BorderColor = "#06b6d4",
                            Fill = true
                        }
                    },
                    XAxisLabel = "Date",
                    YAxisLabel = "Hours",
                    Legend = false,
                    SourceDescription = "SQLite HealthRecords (Sleep logs)"
                } : null;

                return McpToolCallResult.Ok(toolName, ModuleName, $"Retrieved {sleepLogs.Count} sleep log(s)", new
                {
                    days,
                    count = sleepLogs.Count,
                    average_sleep_hours = avgSleep,
                    entries = sleepLogs.Select(r => new
                    {
                        date = McpSchemaHelper.FormatLocal(r.Date, context.UserUtcOffset, "yyyy-MM-dd"),
                        sleep_hours = r.SleepHours
                    }).ToList(),
                    disclaimer = WellnessDisclaimer
                }, chart: chart);
            }

            case "get_health_trends":
            case "generate_health_summary":
            {
                int days = Math.Clamp(McpSchemaHelper.GetOptionalInt(arguments, "days") ?? 30, 1, 365);
                var cutoffUtc = DateTime.UtcNow.AddDays(-days);

                var logs = await _db.HealthRecords.AsNoTracking()
                    .Where(r => r.Date >= cutoffUtc)
                    .OrderBy(r => r.Date)
                    .ToListAsync(ct);

                if (logs.Count == 0)
                {
                    return McpToolCallResult.Ok(toolName, ModuleName, "No wellness logs found for summary", new
                    {
                        days,
                        count = 0,
                        disclaimer = WellnessDisclaimer
                    });
                }

                var sleepVals = logs.Where(x => x.SleepHours.HasValue).Select(x => x.SleepHours!.Value).ToList();
                var waterVals = logs.Where(x => x.WaterIntakeLiters.HasValue).Select(x => x.WaterIntakeLiters!.Value).ToList();
                var exMins = logs.Where(x => x.ExerciseMinutes.HasValue).Select(x => x.ExerciseMinutes!.Value).ToList();
                var hrVals = logs.Where(x => x.HeartRateBpm.HasValue).Select(x => x.HeartRateBpm!.Value).ToList();
                var weightVals = logs.Where(x => x.WeightKg.HasValue).Select(x => x.WeightKg!.Value).ToList();

                ChartConfiguration? chart = null;
                if (logs.Count >= 2 && (sleepVals.Count > 0 || waterVals.Count > 0))
                {
                    chart = new ChartConfiguration
                    {
                        ChartType = "line",
                        Title = $"Wellness Trends (Last {days} Days)",
                        Labels = logs.Select(l => McpSchemaHelper.FormatLocal(l.Date, context.UserUtcOffset, "MMM dd")).ToList(),
                        Datasets = new List<ChartDataset>
                        {
                            new()
                            {
                                Label = "Sleep (hrs)",
                                Data = logs.Select(l => l.SleepHours ?? 0).ToList(),
                                BorderColor = "#6366f1"
                            },
                            new()
                            {
                                Label = "Water (L)",
                                Data = logs.Select(l => l.WaterIntakeLiters ?? 0).ToList(),
                                BorderColor = "#10b981"
                            }
                        },
                        XAxisLabel = "Date",
                        YAxisLabel = "Value",
                        Legend = true,
                        SourceDescription = "SQLite HealthRecords"
                    };
                }

                return McpToolCallResult.Ok(toolName, ModuleName, $"Summarized {logs.Count} wellness record(s)", new
                {
                    days,
                    entries_count = logs.Count,
                    average_sleep_hours = sleepVals.Count > 0 ? Math.Round(sleepVals.Average(), 2) : (double?)null,
                    average_water_liters = waterVals.Count > 0 ? Math.Round(waterVals.Average(), 2) : (double?)null,
                    total_exercise_minutes = exMins.Sum(),
                    average_heart_rate_bpm = hrVals.Count > 0 ? Math.Round(hrVals.Average(), 1) : (double?)null,
                    latest_weight_kg = weightVals.Count > 0 ? weightVals.Last() : (double?)null,
                    disclaimer = WellnessDisclaimer
                }, chart: chart);
            }

            case "delete_health_record":
            {
                int id = McpSchemaHelper.GetOptionalInt(arguments, "id")
                    ?? throw new ArgumentException("Health record 'id' is required.");

                var rec = await _db.HealthRecords.FirstOrDefaultAsync(r => r.Id == id, ct);
                if (rec is null)
                {
                    return McpToolCallResult.Fail(toolName, ModuleName, $"No wellness record #{id} found.");
                }

                _db.HealthRecords.Remove(rec);
                await _db.SaveChangesAsync(ct);

                return McpToolCallResult.Ok(toolName, ModuleName, $"Deleted wellness record #{id}", new
                {
                    deleted = true,
                    id
                });
            }

            default:
                return McpToolCallResult.Fail(toolName, ModuleName, $"Unknown health tool '{toolName}'.");
        }
    }

    private static object MapRecord(HealthRecord r, TimeSpan offset) => new
    {
        id = r.Id,
        date = McpSchemaHelper.FormatLocal(r.Date, offset, "yyyy-MM-dd"),
        sleep_hours = r.SleepHours,
        water_intake_liters = r.WaterIntakeLiters,
        exercise = r.Exercise,
        exercise_minutes = r.ExerciseMinutes,
        weight_kg = r.WeightKg,
        blood_pressure = r.BloodPressure,
        heart_rate_bpm = r.HeartRateBpm,
        symptoms = r.Symptoms,
        notes = r.Notes
    };
}

