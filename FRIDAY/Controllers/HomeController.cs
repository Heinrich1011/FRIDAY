using System.Diagnostics;
using System.Text.Json;
using Friday.Core.Interfaces;
using Friday.Core.Models;
using Friday.Infrastructure.Data;
using FRIDAY.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FRIDAY.Controllers;

public class HomeController : Controller
{
    private readonly FridayDbContext _db;
    private readonly IGroqLlmClient _groq;
    private readonly IMcpClient _mcpClient;

    public HomeController(FridayDbContext db, IGroqLlmClient groq, IMcpClient mcpClient)
    {
        _db = db;
        _groq = groq;
        _mcpClient = mcpClient;
    }

    public async Task<IActionResult> Index(Guid? sessionId, CancellationToken ct)
    {
        var profile = await _db.UserProfiles.AsNoTracking().OrderBy(p => p.Id).FirstOrDefaultAsync(ct);
        var recentSessions = await _db.ChatSessions.AsNoTracking()
            .OrderByDescending(s => s.UpdatedAt)
            .Take(20)
            .Select(s => new SessionSummaryItem
            {
                Id = s.Id,
                Title = s.Title,
                UpdatedAtUtc = s.UpdatedAt
            })
            .ToListAsync(ct);

        Guid? selectedId = sessionId;
        var messages = new List<HydratedChatMessage>();
        string activeTitle = "Friday Assistant";

        if (selectedId.HasValue)
        {
            var session = await _db.ChatSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == selectedId.Value, ct);
            if (session is not null)
            {
                activeTitle = session.Title;
                var rawMsgs = await _db.ChatMessages.AsNoTracking()
                    .Where(m => m.ChatSessionId == session.Id)
                    .OrderBy(m => m.CreatedAt)
                    .ToListAsync(ct);

                messages = rawMsgs.Select(m => new HydratedChatMessage
                {
                    Id = m.Id,
                    Role = m.Role,
                    Content = m.Content,
                    Table = DeserializeSafe<TableResult>(m.TableJson),
                    Chart = DeserializeSafe<ChartConfiguration>(m.ChartJson),
                    Report = DeserializeSafe<ReportArtifactInfo>(m.ReportJson),
                    ActivitySummary = DeserializeSafe<List<string>>(m.ActivitySummaryJson) ?? new List<string>(),
                    CreatedAtUtc = m.CreatedAt
                }).ToList();
            }
            else
            {
                selectedId = null;
            }
        }

        var tools = await _mcpClient.ListToolsAsync(ct);

        var vm = new ChatWorkspaceViewModel
        {
            ActiveSessionId = selectedId,
            ActiveSessionTitle = activeTitle,
            UserGreetingName = profile?.PreferredName ?? profile?.FullName ?? string.Empty,
            IsGroqConfigured = _groq.IsConfigured,
            ConfiguredModel = _groq.ConfiguredModel,
            McpToolCount = tools.Count,
            RecentSessions = recentSessions,
            Messages = messages
        };

        return View(vm);
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }

    private static T? DeserializeSafe<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            return default;
        }
    }
}
