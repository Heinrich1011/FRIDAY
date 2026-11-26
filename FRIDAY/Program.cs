using Friday.Core.Configuration;
using Friday.Core.Interfaces;
using Friday.Infrastructure.Data;
using Friday.Infrastructure.Services;
using Friday.Mcp.Client;
using FRIDAY.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Ensure App_Data, ApprovedDocs sandbox, and DataProtection directories exist
var appDataDir = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
var approvedDocsDir = Path.Combine(appDataDir, "ApprovedDocs");
var dpKeysDir = Path.Combine(appDataDir, "DataProtectionKeys");
Directory.CreateDirectory(appDataDir);
Directory.CreateDirectory(approvedDocsDir);
Directory.CreateDirectory(dpKeysDir);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dpKeysDir))
    .SetApplicationName("FRIDAY.PersonalAiAssistant");

var sampleReadmePath = Path.Combine(approvedDocsDir, "README-Approved-Folder.md");
if (!File.Exists(sampleReadmePath))
{
    File.WriteAllText(sampleReadmePath,
        "# Friday Approved Documents Folder\n\n" +
        "Only files placed inside explicitly configured `FileMcp:AllowedDirectories` (such as this folder) " +
        "can be listed, searched, or read by Friday's File MCP module.\n");
}

// Configure SQLite connection string (resolve relative Data Source inside ContentRootPath)
var rawConnStr = builder.Configuration.GetConnectionString("FridayDb") ?? "Data Source=App_Data/friday.db";
if (rawConnStr.StartsWith("Data Source=App_Data", StringComparison.OrdinalIgnoreCase))
{
    var dbFile = Path.Combine(appDataDir, "friday.db");
    rawConnStr = $"Data Source={dbFile}";
}

builder.Services.AddDbContext<FridayDbContext>(options => options.UseSqlite(rawConnStr));

// Bind strongly typed options (Groq:ApiKey comes from User Secrets or Groq__ApiKey env var — never appsettings.json)
builder.Services.Configure<GroqOptions>(builder.Configuration.GetSection(GroqOptions.SectionName));
builder.Services.Configure<FileMcpOptions>(opts =>
{
    builder.Configuration.GetSection(FileMcpOptions.SectionName).Bind(opts);
    // Resolve relative paths under ContentRootPath
    for (int i = 0; i < opts.AllowedDirectories.Count; i++)
    {
        if (!Path.IsPathRooted(opts.AllowedDirectories[i]))
        {
            opts.AllowedDirectories[i] = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, opts.AllowedDirectories[i]));
        }
    }
});
builder.Services.Configure<List<ExternalMcpServerOptions>>(builder.Configuration.GetSection(ExternalMcpServerOptions.SectionName));

// Register Groq HttpClient WITHOUT default HTTP header/request loggers to prevent Authorization header leakage
builder.Services.AddHttpClient<IGroqLlmClient, GroqLlmClient>(client =>
{
    var timeoutSec = builder.Configuration.GetValue<int?>("Groq:TimeoutSeconds") ?? 45;
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSec, 10, 120));
}).RemoveAllLoggers();

builder.Services.AddScoped<IAuditLogger, AuditLoggerService>();
builder.Services.AddScoped<IReportGeneratorService>(sp =>
    new ReportGeneratorService(sp.GetRequiredService<FridayDbContext>(), builder.Environment.ContentRootPath));
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<PrivacyAndDataManagementService>();
builder.Services.AddFridayMcpModules();
builder.Services.AddSingleton<McpToolRelevanceSelector>();
builder.Services.AddScoped<IFridayOrchestrator, FridayOrchestrator>();

builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
});

builder.Services.AddControllersWithViews();

var app = builder.Build();

// Apply EF Core SQLite migrations safely on startup (preserves existing data) and log safe runtime diagnostics
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<FridayDbContext>();
    await db.Database.MigrateAsync();

    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("FridayStartup");
    var groq = scope.ServiceProvider.GetRequiredService<IGroqLlmClient>();
    var mcpClient = scope.ServiceProvider.GetRequiredService<IMcpClient>();
    var tools = await mcpClient.ListToolsAsync();

    logger.LogInformation(
        "FRIDAY Runtime Check | Environment={Environment} GroqApiKeyConfigured={KeyConfigured} GroqModelConfigured={ModelConfigured} GroqModelName={ModelName} McpToolsCount={ToolCount} DbCanConnect={DbOk}",
        app.Environment.EnvironmentName,
        groq.IsConfigured,
        !string.IsNullOrWhiteSpace(groq.ConfiguredModel),
        groq.ConfiguredModel,
        tools.Count,
        await db.Database.CanConnectAsync());
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}
else
{
    // Even in development, use safe exception handling for API endpoints so secrets/stacktraces aren't leaked to normal UI users
    app.UseExceptionHandler("/Home/Error");
}

// Security headers middleware
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
    ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    await next();
});

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();

public partial class Program { }
