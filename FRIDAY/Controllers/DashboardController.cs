using Friday.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace FRIDAY.Controllers;

public class DashboardController : Controller
{
    private readonly IDashboardService _dashboardService;

    public DashboardController(IDashboardService dashboardService)
    {
        _dashboardService = dashboardService;
    }

    [HttpGet("/Dashboard")]
    public async Task<IActionResult> Index([FromQuery] int? tzOffsetMinutes, CancellationToken ct)
    {
        // Use browser tzOffset cookie or query parameter if available
        int offsetMins = tzOffsetMinutes ?? 0;
        if (!tzOffsetMinutes.HasValue && Request.Cookies.TryGetValue("friday_tz_offset", out var cookieVal) && int.TryParse(cookieVal, out int parsed))
        {
            offsetMins = parsed;
        }

        var vm = await _dashboardService.GetDashboardAsync(TimeSpan.FromMinutes(Math.Clamp(offsetMins, -840, 840)), ct);
        return View(vm);
    }
}

