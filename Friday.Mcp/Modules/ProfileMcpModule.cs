using System.Globalization;
using System.Text.Json;
using Friday.Core.Entities;
using Friday.Core.Enums;
using Friday.Core.Interfaces;
using Friday.Core.Models;
using Friday.Infrastructure.Data;
using Friday.Mcp.Protocol;
using Microsoft.EntityFrameworkCore;

namespace Friday.Mcp.Modules;

public class ProfileMcpModule : IMcpToolModule
{
    private readonly FridayDbContext _db;

    public ProfileMcpModule(FridayDbContext db)
    {
        _db = db;
    }

    public string ModuleName => "profile";

    public IReadOnlyList<McpToolDescriptor> GetTools() => new List<McpToolDescriptor>
    {
        new()
        {
            Name = "get_profile",
            ModuleName = ModuleName,
            HumanActionDescription = "Checked your personal profile preferences",
            Description = "Retrieves the user's stored personal profile (name, occupation, skills, goals, preferences, currency). Sensitive birth date is omitted unless explicitly requested.",
            Safety = McpToolSafety.Read,
            Sensitivity = DataSensitivityLevel.Personal,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["include_birth_date"] = McpSchemaHelper.Prop("boolean", "Set true only if the user explicitly asks about their birth date or age.")
            })
        },
        new()
        {
            Name = "update_profile",
            ModuleName = ModuleName,
            HumanActionDescription = "Updated your personal profile",
            Description = "Creates or updates fields in the user's personal profile.",
            Safety = McpToolSafety.Write,
            Sensitivity = DataSensitivityLevel.Personal,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["full_name"] = McpSchemaHelper.Prop("string", "User's full name"),
                ["preferred_name"] = McpSchemaHelper.Prop("string", "Name Friday should call the user"),
                ["birth_date"] = McpSchemaHelper.Prop("string", "Birth date in YYYY-MM-DD format"),
                ["location"] = McpSchemaHelper.Prop("string", "City/Country or location"),
                ["occupation"] = McpSchemaHelper.Prop("string", "Current role or occupation"),
                ["education"] = McpSchemaHelper.Prop("string", "Education details"),
                ["skills"] = McpSchemaHelper.Prop("string", "Comma-separated skills"),
                ["interests"] = McpSchemaHelper.Prop("string", "Comma-separated interests"),
                ["goals"] = McpSchemaHelper.Prop("string", "Personal or professional goals"),
                ["preferences"] = McpSchemaHelper.Prop("string", "Assistant or work preferences"),
                ["currency_code"] = McpSchemaHelper.Prop("string", "3-letter currency code such as USD, PHP, EUR")
            })
        }
    };

    public Task<string?> DescribeTargetForDestructiveActionAsync(string toolName, JsonElement arguments, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public async Task<McpToolCallResult> ExecuteToolAsync(string toolName, JsonElement arguments, McpToolCallContext context, CancellationToken ct = default)
    {
        switch (toolName)
        {
            case "get_profile":
            {
                var profile = await _db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(ct);
                if (profile is null)
                {
                    return McpToolCallResult.Ok(toolName, ModuleName, "Checked your profile (not configured yet)", new
                    {
                        exists = false,
                        message = "No personal profile has been set up yet."
                    });
                }

                bool includeBirthDate = McpSchemaHelper.GetOptionalBool(arguments, "include_birth_date") == true;
                return McpToolCallResult.Ok(toolName, ModuleName, "Retrieved your personal profile", new
                {
                    exists = true,
                    full_name = profile.FullName,
                    preferred_name = profile.PreferredName,
                    birth_date = includeBirthDate ? profile.BirthDate?.ToString("yyyy-MM-dd") : null,
                    location = profile.Location,
                    occupation = profile.Occupation,
                    education = profile.Education,
                    skills = profile.Skills,
                    interests = profile.Interests,
                    goals = profile.Goals,
                    preferences = profile.Preferences,
                    currency_code = profile.CurrencyCode,
                    updated_at = McpSchemaHelper.FormatLocal(profile.UpdatedAt, context.UserUtcOffset)
                });
            }

            case "update_profile":
            {
                var profile = await _db.UserProfiles.FirstOrDefaultAsync(ct);
                if (profile is null)
                {
                    profile = new UserProfile { CreatedAt = DateTime.UtcNow };
                    _db.UserProfiles.Add(profile);
                }

                if (McpSchemaHelper.GetOptionalString(arguments, "full_name", 150) is { } fn) profile.FullName = fn;
                if (McpSchemaHelper.GetOptionalString(arguments, "preferred_name", 80) is { } pn) profile.PreferredName = pn;
                if (McpSchemaHelper.GetOptionalString(arguments, "location", 150) is { } loc) profile.Location = loc;
                if (McpSchemaHelper.GetOptionalString(arguments, "occupation", 150) is { } occ) profile.Occupation = occ;
                if (McpSchemaHelper.GetOptionalString(arguments, "education", 250) is { } edu) profile.Education = edu;
                if (McpSchemaHelper.GetOptionalString(arguments, "skills", 1000) is { } sk) profile.Skills = sk;
                if (McpSchemaHelper.GetOptionalString(arguments, "interests", 1000) is { } intr) profile.Interests = intr;
                if (McpSchemaHelper.GetOptionalString(arguments, "goals", 2000) is { } gl) profile.Goals = gl;
                if (McpSchemaHelper.GetOptionalString(arguments, "preferences", 2000) is { } pref) profile.Preferences = pref;
                if (McpSchemaHelper.GetOptionalString(arguments, "currency_code", 10) is { } curr) profile.CurrencyCode = curr.ToUpperInvariant();

                if (McpSchemaHelper.GetOptionalString(arguments, "birth_date", 30) is { } bdStr
                    && DateOnly.TryParse(bdStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var bd))
                {
                    profile.BirthDate = bd;
                }

                profile.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);

                return McpToolCallResult.Ok(toolName, ModuleName, "Updated your personal profile", new
                {
                    updated = true,
                    preferred_name = profile.PreferredName,
                    full_name = profile.FullName,
                    occupation = profile.Occupation,
                    goals = profile.Goals,
                    currency_code = profile.CurrencyCode
                });
            }

            default:
                return McpToolCallResult.Fail(toolName, ModuleName, $"Unknown profile tool '{toolName}'.");
        }
    }
}

