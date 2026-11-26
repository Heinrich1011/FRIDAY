using System.Text;
using Friday.Core.Configuration;
using Friday.Core.Entities;
using Friday.Core.Interfaces;
using Friday.Infrastructure.Data;
using Friday.Infrastructure.Services;
using FRIDAY.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FRIDAY.Controllers;

public class ProfileController : Controller
{
    private readonly FridayDbContext _db;
    private readonly PrivacyAndDataManagementService _privacyService;
    private readonly IGroqLlmClient _groq;
    private readonly FileMcpOptions _fileOptions;

    public ProfileController(
        FridayDbContext db,
        PrivacyAndDataManagementService privacyService,
        IGroqLlmClient groq,
        IOptions<FileMcpOptions> fileOptions)
    {
        _db = db;
        _privacyService = privacyService;
        _groq = groq;
        _fileOptions = fileOptions.Value;
    }

    [HttpGet("/Profile")]
    public async Task<IActionResult> Index(string? status, CancellationToken ct)
    {
        var profile = await _db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(ct) ?? new UserProfile();
        var vm = new ProfileSettingsViewModel
        {
            Profile = profile,
            HasDemoData = await _privacyService.HasDemoDataAsync(ct),
            IsGroqConfigured = _groq.IsConfigured,
            ConfiguredModel = _groq.ConfiguredModel,
            ApprovedDirectories = _fileOptions.AllowedDirectories,
            StatusMessage = status
        };
        return View(vm);
    }

    [HttpPost("/Profile/Save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(UserProfile input, CancellationToken ct)
    {
        var existing = await _db.UserProfiles.FirstOrDefaultAsync(ct);
        if (existing is null)
        {
            existing = new UserProfile { CreatedAt = DateTime.UtcNow };
            _db.UserProfiles.Add(existing);
        }

        existing.FullName = TrimNull(input.FullName, 150);
        existing.PreferredName = TrimNull(input.PreferredName, 80);
        existing.BirthDate = input.BirthDate;
        existing.Location = TrimNull(input.Location, 150);
        existing.Occupation = TrimNull(input.Occupation, 150);
        existing.Education = TrimNull(input.Education, 250);
        existing.Skills = TrimNull(input.Skills, 1000);
        existing.Interests = TrimNull(input.Interests, 1000);
        existing.Goals = TrimNull(input.Goals, 2000);
        existing.Preferences = TrimNull(input.Preferences, 2000);
        existing.CurrencyCode = string.IsNullOrWhiteSpace(input.CurrencyCode) ? "USD" : input.CurrencyCode.Trim().ToUpperInvariant()[..Math.Min(10, input.CurrencyCode.Trim().Length)];
        existing.AllowFinanceContextInAi = input.AllowFinanceContextInAi;
        existing.AllowHealthContextInAi = input.AllowHealthContextInAi;
        existing.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Index), new { status = "Your profile and privacy settings were saved." });
    }

    [HttpGet("/Profile/ExportData")]
    public async Task<IActionResult> ExportData(CancellationToken ct)
    {
        var json = await _privacyService.ExportAllPersonalDataJsonAsync(ct);
        var bytes = Encoding.UTF8.GetBytes(json);
        return File(bytes, "application/json", $"friday-personal-export-{DateTime.UtcNow:yyyyMMdd}.json");
    }

    [HttpPost("/Profile/DeleteHealthData")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteHealthData(CancellationToken ct)
    {
        int count = await _privacyService.DeleteAllHealthRecordsAsync(ct);
        return RedirectToAction(nameof(Index), new { status = $"Deleted {count} stored wellness record(s)." });
    }

    [HttpPost("/Profile/LoadDemoData")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LoadDemoData(CancellationToken ct)
    {
        await _privacyService.SeedMarkedDemoDataAsync(ct);
        return RedirectToAction(nameof(Index), new { status = "Loaded clearly marked [DEMO] sample records for testing." });
    }

    [HttpPost("/Profile/RemoveDemoData")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveDemoData(CancellationToken ct)
    {
        int removed = await _privacyService.RemoveAllDemoDataAsync(ct);
        return RedirectToAction(nameof(Index), new { status = $"Removed {removed} marked [DEMO] record(s)." });
    }

    private static string? TrimNull(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        return t.Length <= max ? t : t[..max];
    }
}

