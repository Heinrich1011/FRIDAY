using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Friday.Core.Enums;
using Friday.Core.Interfaces;
using Friday.Core.Models;
using Friday.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Friday.Infrastructure.Services;

public class ReportGeneratorService : IReportGeneratorService
{
    private readonly FridayDbContext _db;
    private readonly string _reportsDirectory;

    static ReportGeneratorService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public ReportGeneratorService(FridayDbContext db, string contentRootPath)
    {
        _db = db;
        _reportsDirectory = Path.Combine(contentRootPath, "App_Data", "Reports");
        Directory.CreateDirectory(_reportsDirectory);
    }

    public async Task<ReportArtifactInfo> GenerateReportAsync(ReportGenerationRequest request, CancellationToken ct = default)
    {
        var reportId = Guid.NewGuid().ToString("N");
        var nowUtc = DateTime.UtcNow;
        var localNow = nowUtc + request.UserUtcOffset;
        var (startUtc, endUtc, periodLabel) = ResolveTimeRange(request.Timeframe, localNow, request.UserUtcOffset);

        var profile = await _db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(ct);
        var currency = profile?.CurrencyCode ?? "USD";

        string title;
        string summary;
        var headers = new List<string>();
        var rows = new List<List<string>>();
        var kpis = new List<(string Label, string Value)>();

        var normalizedType = (request.ReportType ?? "productivity").Trim().ToLowerInvariant();

        if (normalizedType.Contains("expense") || normalizedType.Contains("finance") || normalizedType.Contains("spend"))
        {
            var expenses = await _db.Expenses
                .AsNoTracking()
                .Where(e => e.Date >= startUtc && e.Date <= endUtc)
                .OrderByDescending(e => e.Date)
                .ToListAsync(ct);

            title = request.CustomTitle ?? $"Expenses Report ({periodLabel})";
            var total = expenses.Sum(e => e.Amount);
            var topCat = expenses
                .GroupBy(e => e.Category)
                .OrderByDescending(g => g.Sum(x => x.Amount))
                .FirstOrDefault()?.Key ?? "N/A";

            summary = expenses.Count == 0
                ? $"No expense records found in SQLite for {periodLabel}."
                : $"Generated from {expenses.Count} stored expense record(s) totaling {currency} {total:N2}. Highest category: {topCat}.";

            kpis.Add(("Transactions", expenses.Count.ToString()));
            kpis.Add(("Total Spent", $"{currency} {total:N2}"));
            kpis.Add(("Top Category", topCat));

            headers.AddRange(new[] { "Date", "Category", "Description", "Method", $"Amount ({currency})" });
            foreach (var e in expenses)
            {
                rows.Add(new List<string>
                {
                    (e.Date + request.UserUtcOffset).ToString("yyyy-MM-dd"),
                    e.Category,
                    e.Description ?? "-",
                    e.PaymentMethod ?? "-",
                    e.Amount.ToString("N2")
                });
            }
        }
        else if (normalizedType.Contains("task"))
        {
            var tasks = await _db.Tasks
                .AsNoTracking()
                .Include(t => t.Project)
                .Where(t => (t.CreatedAt >= startUtc && t.CreatedAt <= endUtc)
                            || (t.CompletedAt.HasValue && t.CompletedAt.Value >= startUtc && t.CompletedAt.Value <= endUtc)
                            || (t.DueDate.HasValue && t.DueDate.Value >= startUtc && t.DueDate.Value <= endUtc))
                .OrderByDescending(t => t.CompletedAt ?? t.UpdatedAt)
                .ToListAsync(ct);

            var completed = tasks.Count(t => t.Status == TaskItemStatus.Completed);
            var pending = tasks.Count(t => t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled);

            title = request.CustomTitle ?? $"Tasks Report ({periodLabel})";
            summary = tasks.Count == 0
                ? $"No task records found in SQLite for {periodLabel}."
                : $"Covers {tasks.Count} task(s): {completed} completed and {pending} open.";

            kpis.Add(("Total Tasks", tasks.Count.ToString()));
            kpis.Add(("Completed", completed.ToString()));
            kpis.Add(("Open", pending.ToString()));

            headers.AddRange(new[] { "Title", "Project", "Status", "Priority", "Due / Completed" });
            foreach (var t in tasks)
            {
                var dateText = t.CompletedAt.HasValue
                    ? $"Done {(t.CompletedAt.Value + request.UserUtcOffset):yyyy-MM-dd}"
                    : (t.DueDate.HasValue ? $"Due {(t.DueDate.Value + request.UserUtcOffset):yyyy-MM-dd}" : "-");

                rows.Add(new List<string>
                {
                    t.Title,
                    t.Project?.Name ?? "-",
                    t.Status.ToString(),
                    t.Priority.ToString(),
                    dateText
                });
            }
        }
        else if (normalizedType.Contains("project"))
        {
            var projects = await _db.Projects
                .AsNoTracking()
                .Include(p => p.Tasks)
                .OrderByDescending(p => p.Priority)
                .ToListAsync(ct);

            title = request.CustomTitle ?? "Projects Portfolio Report";
            summary = projects.Count == 0
                ? "No projects found in SQLite."
                : $"Covers {projects.Count} project(s), including {projects.Count(p => p.Status == ProjectStatus.Active)} active project(s).";

            kpis.Add(("Total Projects", projects.Count.ToString()));
            kpis.Add(("Active", projects.Count(p => p.Status == ProjectStatus.Active).ToString()));
            kpis.Add(("Linked Tasks", projects.Sum(p => p.Tasks.Count).ToString()));

            headers.AddRange(new[] { "Project", "Status", "Priority", "Progress", "Target Date" });
            foreach (var p in projects)
            {
                int totalTasks = p.Tasks.Count;
                int doneTasks = p.Tasks.Count(t => t.Status == TaskItemStatus.Completed);
                rows.Add(new List<string>
                {
                    p.Name,
                    p.Status.ToString(),
                    p.Priority.ToString(),
                    totalTasks == 0 ? "No tasks" : $"{doneTasks}/{totalTasks} tasks",
                    p.TargetDate.HasValue ? (p.TargetDate.Value + request.UserUtcOffset).ToString("yyyy-MM-dd") : "-"
                });
            }
        }
        else
        {
            // Productivity summary across Tasks, Events, and Projects
            var completedTasks = await _db.Tasks
                .AsNoTracking()
                .Include(t => t.Project)
                .Where(t => t.Status == TaskItemStatus.Completed && t.CompletedAt.HasValue && t.CompletedAt.Value >= startUtc && t.CompletedAt.Value <= endUtc)
                .OrderByDescending(t => t.CompletedAt)
                .ToListAsync(ct);

            var openOverdueCount = await _db.Tasks
                .AsNoTracking()
                .CountAsync(t => t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled && t.DueDate.HasValue && t.DueDate.Value < nowUtc, ct);

            var eventsCount = await _db.ScheduleEvents
                .AsNoTracking()
                .CountAsync(e => e.StartDateTime >= startUtc && e.StartDateTime <= endUtc, ct);

            var activeProjectsCount = await _db.Projects
                .AsNoTracking()
                .CountAsync(p => p.Status == ProjectStatus.Active, ct);

            title = request.CustomTitle ?? $"Productivity Report ({periodLabel})";
            summary = $"Completed {completedTasks.Count} task(s), attended/scheduled {eventsCount} event(s), with {activeProjectsCount} active project(s) and {openOverdueCount} overdue task(s).";

            kpis.Add(("Completed Tasks", completedTasks.Count.ToString()));
            kpis.Add(("Scheduled Events", eventsCount.ToString()));
            kpis.Add(("Overdue Tasks", openOverdueCount.ToString()));
            kpis.Add(("Active Projects", activeProjectsCount.ToString()));

            headers.AddRange(new[] { "Completed Date", "Task Title", "Project", "Priority", "Category" });
            foreach (var t in completedTasks)
            {
                rows.Add(new List<string>
                {
                    (t.CompletedAt!.Value + request.UserUtcOffset).ToString("yyyy-MM-dd HH:mm"),
                    t.Title,
                    t.Project?.Name ?? "-",
                    t.Priority.ToString(),
                    t.Category ?? "-"
                });
            }
        }

        var pdfPath = Path.Combine(_reportsDirectory, $"{reportId}.pdf");
        var docxPath = Path.Combine(_reportsDirectory, $"{reportId}.docx");

        BuildPdf(pdfPath, title, summary, localNow, kpis, headers, rows);
        BuildDocx(docxPath, title, summary, localNow, kpis, headers, rows);

        return new ReportArtifactInfo
        {
            ReportId = reportId,
            Title = title,
            Summary = summary,
            PdfDownloadUrl = $"/api/reports/{reportId}?format=pdf",
            DocxDownloadUrl = $"/api/reports/{reportId}?format=docx",
            GeneratedAtUtc = nowUtc
        };
    }

    public async Task<StoredReportFile?> GetReportFileAsync(string reportId, string format, CancellationToken ct = default)
    {
        if (!Guid.TryParseExact(reportId, "N", out _))
        {
            return null;
        }

        var ext = string.Equals(format, "docx", StringComparison.OrdinalIgnoreCase) ? "docx" : "pdf";
        var fullPath = Path.Combine(_reportsDirectory, $"{reportId}.{ext}");
        if (!File.Exists(fullPath))
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(fullPath, ct);
        return new StoredReportFile
        {
            Bytes = bytes,
            ContentType = ext == "docx"
                ? "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
                : "application/pdf",
            FileName = $"Friday-Report-{reportId[..8]}.{ext}"
        };
    }

    private static (DateTime StartUtc, DateTime EndUtc, string Label) ResolveTimeRange(string timeframe, DateTime localNow, TimeSpan offset)
    {
        var tf = (timeframe ?? "this_month").Trim().ToLowerInvariant();
        var todayStartLocal = localNow.Date;

        if (tf.Contains("today"))
        {
            return (todayStartLocal - offset, todayStartLocal.AddDays(1) - offset, "Today");
        }
        if (tf.Contains("week"))
        {
            int diff = (7 + (localNow.DayOfWeek - DayOfWeek.Monday)) % 7;
            var weekStartLocal = todayStartLocal.AddDays(-diff);
            return (weekStartLocal - offset, weekStartLocal.AddDays(7) - offset, "This Week");
        }
        if (tf.Contains("all"))
        {
            return (DateTime.UtcNow.AddYears(-10), DateTime.UtcNow.AddYears(2), "All Time");
        }

        var monthStartLocal = new DateTime(localNow.Year, localNow.Month, 1);
        return (monthStartLocal - offset, monthStartLocal.AddMonths(1) - offset, localNow.ToString("MMMM yyyy"));
    }

    private static void BuildPdf(
        string filePath,
        string title,
        string summary,
        DateTime generatedLocal,
        List<(string Label, string Value)> kpis,
        List<string> headers,
        List<List<string>> rows)
    {
        QuestPDF.Fluent.Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(x => x.FontSize(10).FontColor(Colors.Grey.Darken4));

                page.Header().Column(col =>
                {
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text("FRIDAY — Personal AI Life Assistant").FontSize(9).SemiBold().FontColor(Colors.Indigo.Medium);
                            c.Item().Text(title).FontSize(18).Bold().FontColor(Colors.Grey.Darken4);
                        });
                        row.ConstantItem(140).AlignRight().Text($"Generated: {generatedLocal:yyyy-MM-dd HH:mm}").FontSize(8).FontColor(Colors.Grey.Darken1);
                    });
                    col.Item().PaddingTop(6).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
                });

                page.Content().PaddingVertical(14).Column(col =>
                {
                    col.Spacing(12);
                    col.Item().Background(Colors.Grey.Lighten4).Padding(10).Text(summary).FontSize(10);

                    if (kpis.Count > 0)
                    {
                        col.Item().Row(row =>
                        {
                            row.Spacing(8);
                            foreach (var kpi in kpis)
                            {
                                row.RelativeItem().Border(1).BorderColor(Colors.Grey.Lighten2).Padding(8).Column(kc =>
                                {
                                    kc.Item().Text(kpi.Label).FontSize(8).FontColor(Colors.Grey.Darken1);
                                    kc.Item().Text(kpi.Value).FontSize(13).Bold().FontColor(Colors.Indigo.Darken2);
                                });
                            }
                        });
                    }

                    if (rows.Count == 0)
                    {
                        col.Item().PaddingTop(10).Text("No matching database records for this period.").Italic().FontColor(Colors.Grey.Darken1);
                    }
                    else if (headers.Count > 0)
                    {
                        col.Item().Table(table =>
                        {
                            table.ColumnsDefinition(cols =>
                            {
                                for (int i = 0; i < headers.Count; i++)
                                {
                                    cols.RelativeColumn();
                                }
                            });

                            table.Header(header =>
                            {
                                foreach (var h in headers)
                                {
                                    header.Cell().Background(Colors.Indigo.Darken2).Padding(6).Text(h).FontSize(9).SemiBold().FontColor(Colors.White);
                                }
                            });

                            for (int r = 0; r < rows.Count; r++)
                            {
                                var bg = r % 2 == 0 ? Colors.White : Colors.Grey.Lighten5;
                                foreach (var cell in rows[r])
                                {
                                    table.Cell().Background(bg).BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten3).Padding(5).Text(cell ?? "-").FontSize(9);
                                }
                            }
                        });
                    }
                });

                page.Footer().AlignCenter().Text(x =>
                {
                    x.Span("Source: Verified SQLite Database | Page ").FontSize(8).FontColor(Colors.Grey.Darken1);
                    x.CurrentPageNumber().FontSize(8);
                    x.Span(" of ").FontSize(8);
                    x.TotalPages().FontSize(8);
                });
            });
        }).GeneratePdf(filePath);
    }

    private static void BuildDocx(
        string filePath,
        string title,
        string summary,
        DateTime generatedLocal,
        List<(string Label, string Value)> kpis,
        List<string> headers,
        List<List<string>> rows)
    {
        using var wordDoc = WordprocessingDocument.Create(filePath, WordprocessingDocumentType.Document);
        var mainPart = wordDoc.AddMainDocumentPart();
        mainPart.Document = new DocumentFormat.OpenXml.Wordprocessing.Document();
        var body = mainPart.Document.AppendChild(new Body());

        AddParagraph(body, "FRIDAY — Personal AI Life Assistant", bold: true, fontSizeHalfPts: "20", hexColor: "4F46E5");
        AddParagraph(body, title, bold: true, fontSizeHalfPts: "32", hexColor: "111827");
        AddParagraph(body, $"Generated on {generatedLocal:yyyy-MM-dd HH:mm} from SQLite records", bold: false, fontSizeHalfPts: "18", hexColor: "6B7280");
        AddParagraph(body, summary, bold: false, fontSizeHalfPts: "22", hexColor: "1F2937");

        if (kpis.Count > 0)
        {
            var kpiLine = string.Join("   |   ", kpis.Select(k => $"{k.Label}: {k.Value}"));
            AddParagraph(body, kpiLine, bold: true, fontSizeHalfPts: "20", hexColor: "312E81");
        }

        if (rows.Count > 0 && headers.Count > 0)
        {
            var table = new Table();
            var tblProps = new TableProperties(
                new TableBorders(
                    new TopBorder { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 4, Color = "D1D5DB" },
                    new BottomBorder { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 4, Color = "D1D5DB" },
                    new LeftBorder { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 4, Color = "D1D5DB" },
                    new RightBorder { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 4, Color = "D1D5DB" },
                    new InsideHorizontalBorder { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 4, Color = "E5E7EB" },
                    new InsideVerticalBorder { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 4, Color = "E5E7EB" }
                ));
            table.AppendChild(tblProps);

            var headerRow = new TableRow();
            foreach (var h in headers)
            {
                headerRow.Append(CreateDocxCell(h, bold: true));
            }
            table.Append(headerRow);

            foreach (var r in rows)
            {
                var tr = new TableRow();
                foreach (var cellValue in r)
                {
                    tr.Append(CreateDocxCell(cellValue ?? "-", bold: false));
                }
                table.Append(tr);
            }

            body.Append(table);
        }
        else
        {
            AddParagraph(body, "No matching database records found for the selected timeframe.", bold: false, fontSizeHalfPts: "20", hexColor: "6B7280");
        }

        mainPart.Document.Save();
    }

    private static void AddParagraph(Body body, string text, bool bold, string fontSizeHalfPts, string hexColor)
    {
        var runProps = new RunProperties(
            new FontSize { Val = fontSizeHalfPts },
            new DocumentFormat.OpenXml.Wordprocessing.Color { Val = hexColor });
        if (bold) runProps.Append(new Bold());

        var run = new Run(runProps, new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        body.AppendChild(new Paragraph(run));
    }

    private static TableCell CreateDocxCell(string text, bool bold)
    {
        var runProps = new RunProperties(new FontSize { Val = "18" });
        if (bold) runProps.Append(new Bold());
        var run = new Run(runProps, new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return new TableCell(new Paragraph(run));
    }
}
