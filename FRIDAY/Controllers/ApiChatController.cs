using Friday.Core.Interfaces;
using Friday.Core.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FRIDAY.Controllers;

[ApiController]
public class ApiChatController : ControllerBase
{
    private readonly IFridayOrchestrator _orchestrator;
    private readonly IReportGeneratorService _reports;
    private readonly IHostEnvironment _env;
    private readonly ILogger<ApiChatController> _logger;

    public ApiChatController(
        IFridayOrchestrator orchestrator,
        IReportGeneratorService reports,
        IHostEnvironment env,
        ILogger<ApiChatController> logger)
    {
        _orchestrator = orchestrator;
        _reports = reports;
        _env = env;
        _logger = logger;
    }

    [HttpPost("/api/chat/send")]
    [ValidateAntiForgeryToken]
    public async Task<ActionResult<FridayChatResponse>> Send([FromBody] FridayChatRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest(new FridayChatResponse
            {
                Message = "Please enter a message.",
                IsError = true
            });
        }

        try
        {
            var response = await _orchestrator.ProcessMessageAsync(request, ct);
            return Ok(response);
        }
        catch (Exception ex)
        {
            var correlationId = Guid.NewGuid().ToString("N")[..12];
            if (_env.IsDevelopment())
            {
                _logger.LogError(
                    ex,
                    "Unhandled exception in ApiChatController.Send | CorrelationId={CorrelationId} FailingService=ApiChatController Stage=API_CHAT_SEND ExceptionType={ExceptionType}",
                    correlationId, ex.GetType().Name);
            }
            else
            {
                _logger.LogError("Unhandled exception in ApiChatController.Send | CorrelationId={CorrelationId} ExceptionType={ExceptionType}", correlationId, ex.GetType().Name);
            }

            return Ok(new FridayChatResponse
            {
                Message = "Friday is temporarily unable to process that request.",
                CorrelationId = correlationId,
                IsError = true,
                TimestampUtc = DateTime.UtcNow
            });
        }
    }

    public class ClearSessionRequest
    {
        public Guid SessionId { get; set; }
    }

    [HttpPost("/api/chat/clear")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Clear([FromBody] ClearSessionRequest request, CancellationToken ct)
    {
        bool cleared = false;
        if (request is not null && request.SessionId != Guid.Empty)
        {
            cleared = await _orchestrator.ClearSessionAsync(request.SessionId, ct);
        }
        return Ok(new { cleared });
    }

    [HttpDelete("/api/chat/sessions/{sessionId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteSession(Guid sessionId, CancellationToken ct)
    {
        if (sessionId == Guid.Empty)
        {
            return BadRequest(new { deleted = false });
        }

        bool deleted = await _orchestrator.ClearSessionAsync(sessionId, ct);
        return Ok(new { deleted, sessionId });
    }

    [HttpPost("/api/chat/clear-all")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearAll(CancellationToken ct)
    {
        int deletedCount = await _orchestrator.ClearAllSessionsAsync(ct);
        return Ok(new { cleared = true, deletedCount });
    }

    [HttpGet("/api/reports/{reportId}")]
    public async Task<IActionResult> DownloadReport(string reportId, [FromQuery] string format = "pdf", CancellationToken ct = default)
    {
        var file = await _reports.GetReportFileAsync(reportId, format, ct);
        if (file is null)
        {
            return NotFound("Report artifact was not found.");
        }

        return File(file.Bytes, file.ContentType, file.FileName);
    }
}

