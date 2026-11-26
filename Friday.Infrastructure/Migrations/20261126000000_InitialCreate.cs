using System;
using Friday.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Friday.Infrastructure.Migrations;

[DbContext(typeof(FridayDbContext))]
[Migration("20261126000000_InitialCreate")]
public partial class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AuditLogs",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                CorrelationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                RequestType = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                ModuleName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                ToolName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                SafetyLevel = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                Success = table.Column<bool>(type: "INTEGER", nullable: false),
                DurationMs = table.Column<long>(type: "INTEGER", nullable: false),
                OutcomeSummary = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AuditLogs", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "ChatSessions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                LastContextJson = table.Column<string>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ChatSessions", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Expenses",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                Category = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                PaymentMethod = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                Notes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                IsDemoData = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Expenses", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "HealthRecords",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                WeightKg = table.Column<double>(type: "REAL", nullable: true),
                BloodPressure = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                HeartRateBpm = table.Column<int>(type: "INTEGER", nullable: true),
                SleepHours = table.Column<double>(type: "REAL", nullable: true),
                Exercise = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                ExerciseMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                WaterIntakeLiters = table.Column<double>(type: "REAL", nullable: true),
                Symptoms = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                IsDemoData = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_HealthRecords", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "MonthlyBudgets",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Category = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                MonthlyLimit = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                Year = table.Column<int>(type: "INTEGER", nullable: false),
                Month = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MonthlyBudgets", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Projects",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                Priority = table.Column<int>(type: "INTEGER", nullable: false),
                StartDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                TargetDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                Notes = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                IsDemoData = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Projects", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "ScheduleEvents",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Title = table.Column<string>(type: "TEXT", maxLength: 250, nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                StartDateTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                EndDateTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                Location = table.Column<string>(type: "TEXT", maxLength: 250, nullable: true),
                Category = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                Reminder = table.Column<string>(type: "TEXT", maxLength: 150, nullable: true),
                IsAllDay = table.Column<bool>(type: "INTEGER", nullable: false),
                IsDemoData = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ScheduleEvents", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "UserProfiles",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                FullName = table.Column<string>(type: "TEXT", maxLength: 150, nullable: true),
                PreferredName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                BirthDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                Location = table.Column<string>(type: "TEXT", maxLength: 150, nullable: true),
                Occupation = table.Column<string>(type: "TEXT", maxLength: 150, nullable: true),
                Education = table.Column<string>(type: "TEXT", maxLength: 250, nullable: true),
                Skills = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                Interests = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                Goals = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                Preferences = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                TimeZoneId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                CurrencyCode = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                AllowFinanceContextInAi = table.Column<bool>(type: "INTEGER", nullable: false),
                AllowHealthContextInAi = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserProfiles", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "ChatMessages",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                ChatSessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                Role = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                Content = table.Column<string>(type: "TEXT", nullable: false),
                TableJson = table.Column<string>(type: "TEXT", nullable: true),
                ChartJson = table.Column<string>(type: "TEXT", nullable: true),
                ReportJson = table.Column<string>(type: "TEXT", nullable: true),
                ActivitySummaryJson = table.Column<string>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ChatMessages", x => x.Id);
                table.ForeignKey(
                    name: "FK_ChatMessages_ChatSessions_ChatSessionId",
                    column: x => x.ChatSessionId,
                    principalTable: "ChatSessions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PendingConfirmations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ChatSessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                ToolName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                ArgumentsJson = table.Column<string>(type: "TEXT", nullable: false),
                TargetSummary = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                IsResolved = table.Column<bool>(type: "INTEGER", nullable: false),
                ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PendingConfirmations", x => x.Id);
                table.ForeignKey(
                    name: "FK_PendingConfirmations_ChatSessions_ChatSessionId",
                    column: x => x.ChatSessionId,
                    principalTable: "ChatSessions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Notes",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Title = table.Column<string>(type: "TEXT", maxLength: 250, nullable: false),
                Content = table.Column<string>(type: "TEXT", maxLength: 20000, nullable: false),
                Category = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                Tags = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                ProjectId = table.Column<int>(type: "INTEGER", nullable: true),
                IsDemoData = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Notes", x => x.Id);
                table.ForeignKey(
                    name: "FK_Notes_Projects_ProjectId",
                    column: x => x.ProjectId,
                    principalTable: "Projects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "Tasks",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Title = table.Column<string>(type: "TEXT", maxLength: 250, nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                Priority = table.Column<int>(type: "INTEGER", nullable: false),
                DueDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                Category = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                ProjectId = table.Column<int>(type: "INTEGER", nullable: true),
                IsDemoData = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Tasks", x => x.Id);
                table.ForeignKey(
                    name: "FK_Tasks_Projects_ProjectId",
                    column: x => x.ProjectId,
                    principalTable: "Projects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateIndex(name: "IX_AuditLogs_CorrelationId", table: "AuditLogs", column: "CorrelationId");
        migrationBuilder.CreateIndex(name: "IX_AuditLogs_CreatedAt", table: "AuditLogs", column: "CreatedAt");
        migrationBuilder.CreateIndex(name: "IX_ChatMessages_ChatSessionId_CreatedAt", table: "ChatMessages", columns: new[] { "ChatSessionId", "CreatedAt" });
        migrationBuilder.CreateIndex(name: "IX_ChatSessions_UpdatedAt", table: "ChatSessions", column: "UpdatedAt");
        migrationBuilder.CreateIndex(name: "IX_Expenses_Category", table: "Expenses", column: "Category");
        migrationBuilder.CreateIndex(name: "IX_Expenses_Date", table: "Expenses", column: "Date");
        migrationBuilder.CreateIndex(name: "IX_HealthRecords_Date", table: "HealthRecords", column: "Date");
        migrationBuilder.CreateIndex(name: "IX_MonthlyBudgets_Category_Year_Month", table: "MonthlyBudgets", columns: new[] { "Category", "Year", "Month" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_Notes_Category", table: "Notes", column: "Category");
        migrationBuilder.CreateIndex(name: "IX_Notes_ProjectId", table: "Notes", column: "ProjectId");
        migrationBuilder.CreateIndex(name: "IX_Notes_Title", table: "Notes", column: "Title");
        migrationBuilder.CreateIndex(name: "IX_Notes_UpdatedAt", table: "Notes", column: "UpdatedAt");
        migrationBuilder.CreateIndex(name: "IX_PendingConfirmations_ChatSessionId_IsResolved", table: "PendingConfirmations", columns: new[] { "ChatSessionId", "IsResolved" });
        migrationBuilder.CreateIndex(name: "IX_Projects_Name", table: "Projects", column: "Name");
        migrationBuilder.CreateIndex(name: "IX_Projects_Priority", table: "Projects", column: "Priority");
        migrationBuilder.CreateIndex(name: "IX_Projects_Status", table: "Projects", column: "Status");
        migrationBuilder.CreateIndex(name: "IX_Projects_TargetDate", table: "Projects", column: "TargetDate");
        migrationBuilder.CreateIndex(name: "IX_ScheduleEvents_Category", table: "ScheduleEvents", column: "Category");
        migrationBuilder.CreateIndex(name: "IX_ScheduleEvents_StartDateTime", table: "ScheduleEvents", column: "StartDateTime");
        migrationBuilder.CreateIndex(name: "IX_ScheduleEvents_Title", table: "ScheduleEvents", column: "Title");
        migrationBuilder.CreateIndex(name: "IX_Tasks_Category", table: "Tasks", column: "Category");
        migrationBuilder.CreateIndex(name: "IX_Tasks_CompletedAt", table: "Tasks", column: "CompletedAt");
        migrationBuilder.CreateIndex(name: "IX_Tasks_DueDate", table: "Tasks", column: "DueDate");
        migrationBuilder.CreateIndex(name: "IX_Tasks_Priority", table: "Tasks", column: "Priority");
        migrationBuilder.CreateIndex(name: "IX_Tasks_ProjectId", table: "Tasks", column: "ProjectId");
        migrationBuilder.CreateIndex(name: "IX_Tasks_Status", table: "Tasks", column: "Status");
        migrationBuilder.CreateIndex(name: "IX_Tasks_Title", table: "Tasks", column: "Title");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "AuditLogs");
        migrationBuilder.DropTable(name: "ChatMessages");
        migrationBuilder.DropTable(name: "Expenses");
        migrationBuilder.DropTable(name: "HealthRecords");
        migrationBuilder.DropTable(name: "MonthlyBudgets");
        migrationBuilder.DropTable(name: "Notes");
        migrationBuilder.DropTable(name: "PendingConfirmations");
        migrationBuilder.DropTable(name: "ScheduleEvents");
        migrationBuilder.DropTable(name: "Tasks");
        migrationBuilder.DropTable(name: "UserProfiles");
        migrationBuilder.DropTable(name: "ChatSessions");
        migrationBuilder.DropTable(name: "Projects");
    }
}

