using System.Text.Json;
using Friday.Core.Entities;
using Friday.Core.Enums;
using Friday.Core.Interfaces;
using Friday.Core.Models;
using Friday.Infrastructure.Data;
using Friday.Mcp.Protocol;
using Microsoft.EntityFrameworkCore;

namespace Friday.Mcp.Modules;

public class FinanceMcpModule : IMcpToolModule
{
    private readonly FridayDbContext _db;

    public FinanceMcpModule(FridayDbContext db)
    {
        _db = db;
    }

    public string ModuleName => "finance";

    public IReadOnlyList<McpToolDescriptor> GetTools() => new List<McpToolDescriptor>
    {
        new()
        {
            Name = "add_expense",
            ModuleName = ModuleName,
            HumanActionDescription = "Recorded an expense",
            Description = "Records a new financial expense with amount, category, optional description, date, payment method, and notes.",
            Safety = McpToolSafety.Write,
            Sensitivity = DataSensitivityLevel.Financial,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["amount"] = McpSchemaHelper.Prop("number", "Expense amount (must be positive)"),
                ["category"] = McpSchemaHelper.Prop("string", "Expense category (e.g., Food, Transportation, Utilities, Rent, Study, Shopping)"),
                ["description"] = McpSchemaHelper.Prop("string", "Short description of the expense"),
                ["date"] = McpSchemaHelper.Prop("string", "Date of expense YYYY-MM-DD or 'today'/'yesterday'"),
                ["payment_method"] = McpSchemaHelper.Prop("string", "Payment method (e.g., Cash, Card, E-Wallet, Bank Transfer)"),
                ["notes"] = McpSchemaHelper.Prop("string", "Optional notes")
            }, required: new[] { "amount", "category" })
        },
        new()
        {
            Name = "get_expenses",
            ModuleName = ModuleName,
            HumanActionDescription = "Checked your expense records",
            Description = "Retrieves stored expense records filtered by timeframe, category, or search term.",
            Safety = McpToolSafety.Read,
            Sensitivity = DataSensitivityLevel.Financial,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["timeframe"] = McpSchemaHelper.Prop("string", "Timeframe filter", new[] { "today", "this_week", "last_week", "this_month", "last_month", "all" }),
                ["category"] = McpSchemaHelper.Prop("string", "Optional category filter (e.g., Transportation)"),
                ["search"] = McpSchemaHelper.Prop("string", "Optional description keyword")
            })
        },
        new()
        {
            Name = "get_expenses_by_category",
            ModuleName = ModuleName,
            HumanActionDescription = "Analyzed spending by category",
            Description = "Aggregates stored expenses by category for a given timeframe, returning category totals, shares, table, and chart.",
            Safety = McpToolSafety.Read,
            Sensitivity = DataSensitivityLevel.Financial,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["timeframe"] = McpSchemaHelper.Prop("string", "Timeframe to aggregate", new[] { "this_week", "this_month", "last_month", "all" }),
                ["chart_type"] = McpSchemaHelper.Prop("string", "Preferred chart style", new[] { "doughnut", "pie", "bar" })
            })
        },
        new()
        {
            Name = "get_monthly_expenses",
            ModuleName = ModuleName,
            HumanActionDescription = "Calculated monthly spending",
            Description = "Retrieves monthly spending totals and daily trend for this_month or last_month, or compares spending totals across all stored months (compare_months).",
            Safety = McpToolSafety.Read,
            Sensitivity = DataSensitivityLevel.Financial,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["month_scope"] = McpSchemaHelper.Prop("string", "Which month to inspect or compare", new[] { "this_month", "last_month", "compare_months" }),
                ["include_chart"] = McpSchemaHelper.Prop("boolean", "Set true to include daily or monthly comparison chart")
            })
        },
        new()
        {
            Name = "get_spending_statistics",
            ModuleName = ModuleName,
            HumanActionDescription = "Computed spending statistics",
            Description = "Calculates verified spending statistics (total, count, average, largest expense, top categories, payment methods) from stored records.",
            Safety = McpToolSafety.Read,
            Sensitivity = DataSensitivityLevel.Financial,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["timeframe"] = McpSchemaHelper.Prop("string", "Timeframe for statistics", new[] { "this_week", "this_month", "last_month", "all" })
            })
        },
        new()
        {
            Name = "set_monthly_budget",
            ModuleName = ModuleName,
            HumanActionDescription = "Updated monthly budget",
            Description = "Sets a monthly budget limit for 'Overall' or a specific category for the current month.",
            Safety = McpToolSafety.Write,
            Sensitivity = DataSensitivityLevel.Financial,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["monthly_limit"] = McpSchemaHelper.Prop("number", "Monthly budget amount"),
                ["category"] = McpSchemaHelper.Prop("string", "Category name, or 'Overall' for total monthly budget")
            }, required: new[] { "monthly_limit" })
        },
        new()
        {
            Name = "get_budget_status",
            ModuleName = ModuleName,
            HumanActionDescription = "Checked budget status",
            Description = "Compares current month's stored expenses against configured monthly budgets.",
            Safety = McpToolSafety.Read,
            Sensitivity = DataSensitivityLevel.Financial,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>())
        },
        new()
        {
            Name = "delete_expense",
            ModuleName = ModuleName,
            HumanActionDescription = "Deleted an expense record",
            Description = "Deletes an expense record by id. Requires user confirmation.",
            Safety = McpToolSafety.Destructive,
            Sensitivity = DataSensitivityLevel.Financial,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["id"] = McpSchemaHelper.Prop("integer", "Expense ID to delete")
            }, required: new[] { "id" })
        }
    };

    public async Task<string?> DescribeTargetForDestructiveActionAsync(string toolName, JsonElement arguments, CancellationToken ct = default)
    {
        if (toolName != "delete_expense") return null;
        if (McpSchemaHelper.GetOptionalInt(arguments, "id") is not { } id) return null;
        var exp = await _db.Expenses.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
        return exp is null ? null : $"expense #{exp.Id} ({exp.Category}: {exp.Amount:N2} - {exp.Description ?? "no description"})";
    }

    public async Task<McpToolCallResult> ExecuteToolAsync(string toolName, JsonElement arguments, McpToolCallContext context, CancellationToken ct = default)
    {
        var profile = await _db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(ct);
        if (profile is { AllowFinanceContextInAi: false } && toolName.StartsWith("get_", StringComparison.Ordinal))
        {
            return McpToolCallResult.Fail(toolName, ModuleName, "Financial AI access is currently disabled in your Profile privacy settings.");
        }

        var currency = profile?.CurrencyCode ?? "USD";

        switch (toolName)
        {
            case "add_expense":
            {
                var amount = McpSchemaHelper.GetOptionalDecimal(arguments, "amount")
                    ?? throw new ArgumentException("Valid positive 'amount' is required.");
                if (amount <= 0 || amount > 100_000_000)
                    throw new ArgumentException("Amount must be greater than zero.");

                var category = McpSchemaHelper.GetRequiredString(arguments, "category", 100);
                var dateUtc = McpSchemaHelper.ParseFlexibleDateTimeToUtc(
                    McpSchemaHelper.GetOptionalString(arguments, "date"),
                    context.UserUtcOffset) ?? DateTime.UtcNow;

                var expense = new Expense
                {
                    Amount = Math.Round(amount, 2),
                    Category = category,
                    Description = McpSchemaHelper.GetOptionalString(arguments, "description", 500),
                    Date = dateUtc,
                    PaymentMethod = McpSchemaHelper.GetOptionalString(arguments, "payment_method", 80),
                    Notes = McpSchemaHelper.GetOptionalString(arguments, "notes", 1000),
                    CreatedAt = DateTime.UtcNow
                };

                _db.Expenses.Add(expense);
                await _db.SaveChangesAsync(ct);

                return McpToolCallResult.Ok(toolName, ModuleName, $"Recorded expense of {currency} {expense.Amount:N2} under {expense.Category}", new
                {
                    id = expense.Id,
                    amount = expense.Amount,
                    currency,
                    category = expense.Category,
                    description = expense.Description,
                    date = McpSchemaHelper.FormatLocal(expense.Date, context.UserUtcOffset, "yyyy-MM-dd")
                });
            }

            case "get_expenses":
            {
                var tf = McpSchemaHelper.GetOptionalString(arguments, "timeframe") ?? "this_month";
                var (startUtc, endUtc, label) = McpSchemaHelper.GetTimeframeBoundsUtc(tf, context.UserUtcOffset);

                var query = _db.Expenses.AsNoTracking().AsQueryable();
                if (tf != "all")
                {
                    query = query.Where(e => e.Date >= startUtc && e.Date < endUtc);
                }

                if (McpSchemaHelper.GetOptionalString(arguments, "category", 100) is { } cat)
                {
                    query = query.Where(e => e.Category.ToLower().Contains(cat.ToLower()));
                }

                if (McpSchemaHelper.GetOptionalString(arguments, "search", 200) is { } kw)
                {
                    var lower = kw.ToLower();
                    query = query.Where(e => (e.Description != null && e.Description.ToLower().Contains(lower))
                        || e.Category.ToLower().Contains(lower));
                }

                var items = await query.OrderByDescending(e => e.Date).Take(100).ToListAsync(ct);
                var total = items.Sum(e => e.Amount);

                var table = items.Count > 0 ? new TableResult
                {
                    Title = $"Expenses ({label})",
                    Columns = new List<string> { "ID", "Date", "Category", "Description", "Payment", $"Amount ({currency})" },
                    Rows = items.Select(e => new List<string>
                    {
                        $"#{e.Id}",
                        McpSchemaHelper.FormatLocal(e.Date, context.UserUtcOffset, "yyyy-MM-dd"),
                        e.Category,
                        e.Description ?? "-",
                        e.PaymentMethod ?? "-",
                        e.Amount.ToString("N2")
                    }).ToList(),
                    FooterSummary = $"Total ({items.Count} record(s)): {currency} {total:N2}",
                    SourceDescription = "SQLite Expenses"
                } : null;

                return McpToolCallResult.Ok(toolName, ModuleName, $"Retrieved {items.Count} expense(s) ({currency} {total:N2})", new
                {
                    timeframe = label,
                    currency,
                    count = items.Count,
                    total_spent = total,
                    expenses = items.Select(e => new
                    {
                        id = e.Id,
                        date = McpSchemaHelper.FormatLocal(e.Date, context.UserUtcOffset, "yyyy-MM-dd"),
                        category = e.Category,
                        description = e.Description,
                        payment_method = e.PaymentMethod,
                        amount = e.Amount
                    }).ToList()
                }, table);
            }

            case "get_expenses_by_category":
            {
                var tf = McpSchemaHelper.GetOptionalString(arguments, "timeframe") ?? "this_month";
                var (startUtc, endUtc, label) = McpSchemaHelper.GetTimeframeBoundsUtc(tf, context.UserUtcOffset);

                var query = _db.Expenses.AsNoTracking().AsQueryable();
                if (tf != "all") query = query.Where(e => e.Date >= startUtc && e.Date < endUtc);

                var items = await query.ToListAsync(ct);
                var grandTotal = items.Sum(x => x.Amount);

                var groups = items
                    .GroupBy(e => e.Category.Trim())
                    .Select(g => new
                    {
                        Category = g.Key,
                        Count = g.Count(),
                        Total = g.Sum(x => x.Amount),
                        SharePct = grandTotal > 0 ? Math.Round((double)(g.Sum(x => x.Amount) / grandTotal * 100m), 1) : 0
                    })
                    .OrderByDescending(g => g.Total)
                    .ToList();

                var table = groups.Count > 0 ? new TableResult
                {
                    Title = $"Spending by Category ({label})",
                    Columns = new List<string> { "Category", "Transactions", $"Amount ({currency})", "Share" },
                    Rows = groups.Select(g => new List<string>
                    {
                        g.Category,
                        g.Count.ToString(),
                        g.Total.ToString("N2"),
                        $"{g.SharePct:F1}%"
                    }).ToList(),
                    FooterSummary = $"Grand Total: {currency} {grandTotal:N2}",
                    SourceDescription = "SQLite Expenses"
                } : null;

                var chartType = McpSchemaHelper.GetOptionalString(arguments, "chart_type") ?? "doughnut";
                var chart = groups.Count > 0 ? new ChartConfiguration
                {
                    ChartType = chartType,
                    Title = $"Spending by Category ({label})",
                    Labels = groups.Select(g => g.Category).ToList(),
                    Datasets = new List<ChartDataset>
                    {
                        new()
                        {
                            Label = $"Spent ({currency})",
                            Data = groups.Select(g => (double)g.Total).ToList(),
                            BackgroundColor = new List<string> { "#6366f1", "#06b6d4", "#10b981", "#f59e0b", "#ec4899", "#8b5cf6", "#14b8a6" }
                        }
                    },
                    XAxisLabel = "Category",
                    YAxisLabel = currency,
                    Legend = true,
                    SourceDescription = $"SQLite Expenses ({label})"
                } : null;

                return McpToolCallResult.Ok(toolName, ModuleName, $"Grouped {items.Count} expense(s) across {groups.Count} categor(ies)", new
                {
                    timeframe = label,
                    currency,
                    grand_total = grandTotal,
                    categories = groups
                }, table, chart);
            }

            case "get_monthly_expenses":
            {
                var scope = McpSchemaHelper.GetOptionalString(arguments, "month_scope") ?? "this_month";
                if (scope == "compare_months")
                {
                    var allItems = await _db.Expenses.AsNoTracking()
                        .OrderBy(e => e.Date)
                        .ToListAsync(ct);

                    var byMonth = allItems
                        .GroupBy(e =>
                        {
                            var local = e.Date + context.UserUtcOffset;
                            return new { local.Year, local.Month };
                        })
                        .OrderBy(g => g.Key.Year)
                        .ThenBy(g => g.Key.Month)
                        .Select(g =>
                        {
                            var monthStart = new DateTime(g.Key.Year, g.Key.Month, 1);
                            return new
                            {
                                month_key = $"{g.Key.Year:D4}-{g.Key.Month:D2}",
                                month = monthStart.ToString("MMM yyyy"),
                                transaction_count = g.Count(),
                                total_spent = g.Sum(x => x.Amount)
                            };
                        })
                        .ToList();

                    var highestMonth = byMonth.OrderByDescending(m => m.total_spent).FirstOrDefault();
                    var grandTotal = byMonth.Sum(m => m.total_spent);

                    var compareTable = byMonth.Count > 0
                        ? new TableResult
                        {
                            Title = "Monthly Spending Comparison",
                            Columns = new List<string> { "Month", "Transactions", $"Total ({currency})" },
                            Rows = byMonth.Select(m => new List<string>
                            {
                                m.month,
                                m.transaction_count.ToString(),
                                m.total_spent.ToString("N2")
                            }).ToList(),
                            FooterSummary = highestMonth is not null
                                ? $"Highest Month: {highestMonth.month} ({currency} {highestMonth.total_spent:N2})"
                                : null,
                            SourceDescription = "SQLite Expenses"
                        }
                        : null;

                    ChartConfiguration? compareChart = null;
                    if ((McpSchemaHelper.GetOptionalBool(arguments, "include_chart") ?? true) && byMonth.Count > 0)
                    {
                        compareChart = new ChartConfiguration
                        {
                            ChartType = "bar",
                            Title = "Monthly Spending Comparison",
                            Labels = byMonth.Select(m => m.month).ToList(),
                            Datasets = new List<ChartDataset>
                            {
                                new()
                                {
                                    Label = $"Total Spent ({currency})",
                                    Data = byMonth.Select(m => (double)m.total_spent).ToList(),
                                    BackgroundColor = new List<string> { "#6366f1", "#3b82f6", "#10b981", "#f59e0b", "#ef4444" }
                                }
                            },
                            XAxisLabel = "Month",
                            YAxisLabel = currency,
                            Legend = false,
                            SourceDescription = $"SQLite Expenses ({byMonth.Count} month(s))"
                        };
                    }

                    return McpToolCallResult.Ok(toolName, ModuleName, $"Compared spending across {byMonth.Count} month(s)", new
                    {
                        month = "All Stored Months",
                        currency,
                        months_count = byMonth.Count,
                        total_spent = grandTotal,
                        highest_spending_month = highestMonth,
                        monthly_breakdown = byMonth,
                        note = "Values reflect stored personal records only, not financial advice."
                    }, compareTable, compareChart);
                }

                var (startUtc, endUtc, label) = McpSchemaHelper.GetTimeframeBoundsUtc(scope, context.UserUtcOffset);

                var items = await _db.Expenses.AsNoTracking()
                    .Where(e => e.Date >= startUtc && e.Date < endUtc)
                    .OrderBy(e => e.Date)
                    .ToListAsync(ct);

                var total = items.Sum(x => x.Amount);
                var daily = items
                    .GroupBy(e => (e.Date + context.UserUtcOffset).Date)
                    .OrderBy(g => g.Key)
                    .ToList();

                ChartConfiguration? chart = null;
                if ((McpSchemaHelper.GetOptionalBool(arguments, "include_chart") ?? true) && daily.Count > 1)
                {
                    chart = new ChartConfiguration
                    {
                        ChartType = "line",
                        Title = $"Daily Spending Trend ({label})",
                        Labels = daily.Select(d => d.Key.ToString("MMM dd")).ToList(),
                        Datasets = new List<ChartDataset>
                        {
                            new()
                            {
                                Label = $"Daily Total ({currency})",
                                Data = daily.Select(d => (double)d.Sum(x => x.Amount)).ToList(),
                                BorderColor = "#6366f1",
                                Fill = true
                            }
                        },
                        XAxisLabel = "Date",
                        YAxisLabel = currency,
                        Legend = false,
                        SourceDescription = $"SQLite Expenses ({label})"
                    };
                }

                return McpToolCallResult.Ok(toolName, ModuleName, $"Calculated {label} spending ({currency} {total:N2})", new
                {
                    month = label,
                    currency,
                    transaction_count = items.Count,
                    total_spent = total,
                    note = "Values reflect stored personal records only, not financial advice."
                }, chart: chart);
            }

            case "get_spending_statistics":
            {
                var tf = McpSchemaHelper.GetOptionalString(arguments, "timeframe") ?? "this_month";
                var (startUtc, endUtc, label) = McpSchemaHelper.GetTimeframeBoundsUtc(tf, context.UserUtcOffset);

                var query = _db.Expenses.AsNoTracking().AsQueryable();
                if (tf != "all") query = query.Where(e => e.Date >= startUtc && e.Date < endUtc);
                var items = await query.ToListAsync(ct);

                if (items.Count == 0)
                {
                    return McpToolCallResult.Ok(toolName, ModuleName, $"No expense records found for {label}", new
                    {
                        timeframe = label,
                        count = 0,
                        total_spent = 0m,
                        currency
                    });
                }

                var total = items.Sum(x => x.Amount);
                var avg = Math.Round(total / items.Count, 2);
                var largest = items.OrderByDescending(x => x.Amount).First();
                var topCategories = items.GroupBy(x => x.Category)
                    .Select(g => new { category = g.Key, count = g.Count(), total = g.Sum(x => x.Amount) })
                    .OrderByDescending(g => g.total)
                    .Take(5)
                    .ToList();

                var monthlyBreakdown = items
                    .GroupBy(e =>
                    {
                        var local = e.Date + context.UserUtcOffset;
                        return new { local.Year, local.Month };
                    })
                    .OrderBy(g => g.Key.Year)
                    .ThenBy(g => g.Key.Month)
                    .Select(g =>
                    {
                        var monthStart = new DateTime(g.Key.Year, g.Key.Month, 1);
                        return new
                        {
                            month_key = $"{g.Key.Year:D4}-{g.Key.Month:D2}",
                            month = monthStart.ToString("MMM yyyy"),
                            count = g.Count(),
                            total = g.Sum(x => x.Amount)
                        };
                    })
                    .ToList();
                var highestMonth = monthlyBreakdown.OrderByDescending(m => m.total).FirstOrDefault();

                ChartConfiguration? statsChart = null;
                TableResult? statsTable = null;
                if (monthlyBreakdown.Count > 1 || tf == "all")
                {
                    statsTable = new TableResult
                    {
                        Title = $"Monthly Spending Summary ({label})",
                        Columns = new List<string> { "Month", "Transactions", $"Total ({currency})" },
                        Rows = monthlyBreakdown.Select(m => new List<string>
                        {
                            m.month,
                            m.count.ToString(),
                            m.total.ToString("N2")
                        }).ToList(),
                        FooterSummary = highestMonth is not null
                            ? $"Highest Month: {highestMonth.month} ({currency} {highestMonth.total:N2})"
                            : null,
                        SourceDescription = "SQLite Expenses"
                    };

                    statsChart = new ChartConfiguration
                    {
                        ChartType = "bar",
                        Title = $"Monthly Spending ({label})",
                        Labels = monthlyBreakdown.Select(m => m.month).ToList(),
                        Datasets = new List<ChartDataset>
                        {
                            new()
                            {
                                Label = $"Total ({currency})",
                                Data = monthlyBreakdown.Select(m => (double)m.total).ToList(),
                                BackgroundColor = new List<string> { "#6366f1", "#3b82f6", "#10b981", "#f59e0b" }
                            }
                        },
                        XAxisLabel = "Month",
                        YAxisLabel = currency,
                        Legend = false,
                        SourceDescription = $"SQLite Expenses ({label})"
                    };
                }

                return McpToolCallResult.Ok(toolName, ModuleName, $"Computed spending statistics for {label}", new
                {
                    timeframe = label,
                    currency,
                    transaction_count = items.Count,
                    total_spent = total,
                    average_transaction = avg,
                    largest_expense = new
                    {
                        id = largest.Id,
                        amount = largest.Amount,
                        category = largest.Category,
                        description = largest.Description,
                        date = McpSchemaHelper.FormatLocal(largest.Date, context.UserUtcOffset, "yyyy-MM-dd")
                    },
                    top_categories = topCategories,
                    monthly_breakdown = monthlyBreakdown,
                    highest_spending_month = highestMonth
                }, statsTable, statsChart);
            }

            case "set_monthly_budget":
            {
                var limit = McpSchemaHelper.GetOptionalDecimal(arguments, "monthly_limit")
                    ?? throw new ArgumentException("Positive 'monthly_limit' is required.");
                if (limit <= 0) throw new ArgumentException("Budget limit must be positive.");

                var cat = McpSchemaHelper.GetOptionalString(arguments, "category", 100) ?? "Overall";
                var localNow = DateTime.UtcNow + context.UserUtcOffset;

                var existing = await _db.MonthlyBudgets.FirstOrDefaultAsync(
                    b => b.Category.ToLower() == cat.ToLower() && b.Year == localNow.Year && b.Month == localNow.Month, ct);

                if (existing is null)
                {
                    existing = new MonthlyBudget
                    {
                        Category = cat,
                        MonthlyLimit = Math.Round(limit, 2),
                        Year = localNow.Year,
                        Month = localNow.Month,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    _db.MonthlyBudgets.Add(existing);
                }
                else
                {
                    existing.MonthlyLimit = Math.Round(limit, 2);
                    existing.UpdatedAt = DateTime.UtcNow;
                }

                await _db.SaveChangesAsync(ct);

                return McpToolCallResult.Ok(toolName, ModuleName, $"Set {cat} monthly budget to {currency} {existing.MonthlyLimit:N2}", new
                {
                    category = existing.Category,
                    monthly_limit = existing.MonthlyLimit,
                    currency,
                    month = $"{localNow:MMMM yyyy}"
                });
            }

            case "get_budget_status":
            {
                var localNow = DateTime.UtcNow + context.UserUtcOffset;
                var (startUtc, endUtc, label) = McpSchemaHelper.GetTimeframeBoundsUtc("this_month", context.UserUtcOffset);

                var budgets = await _db.MonthlyBudgets.AsNoTracking()
                    .Where(b => b.Year == localNow.Year && b.Month == localNow.Month)
                    .ToListAsync(ct);

                var expenses = await _db.Expenses.AsNoTracking()
                    .Where(e => e.Date >= startUtc && e.Date < endUtc)
                    .ToListAsync(ct);

                var totalSpent = expenses.Sum(e => e.Amount);
                var statusRows = budgets.Select(b =>
                {
                    var spent = string.Equals(b.Category, "Overall", StringComparison.OrdinalIgnoreCase)
                        ? totalSpent
                        : expenses.Where(e => string.Equals(e.Category, b.Category, StringComparison.OrdinalIgnoreCase)).Sum(e => e.Amount);

                    return new
                    {
                        category = b.Category,
                        budget_limit = b.MonthlyLimit,
                        spent,
                        remaining = b.MonthlyLimit - spent,
                        percent_used = b.MonthlyLimit > 0 ? Math.Round((double)(spent / b.MonthlyLimit * 100m), 1) : 0
                    };
                }).ToList();

                return McpToolCallResult.Ok(toolName, ModuleName, $"Checked budget status for {label}", new
                {
                    month = label,
                    currency,
                    total_spent_this_month = totalSpent,
                    configured_budgets_count = budgets.Count,
                    budgets = statusRows
                });
            }

            case "delete_expense":
            {
                int id = McpSchemaHelper.GetOptionalInt(arguments, "id")
                    ?? throw new ArgumentException("Expense 'id' is required.");

                var exp = await _db.Expenses.FirstOrDefaultAsync(e => e.Id == id, ct);
                if (exp is null)
                {
                    return McpToolCallResult.Fail(toolName, ModuleName, $"No expense with ID #{id} was found.");
                }

                _db.Expenses.Remove(exp);
                await _db.SaveChangesAsync(ct);

                return McpToolCallResult.Ok(toolName, ModuleName, $"Deleted expense #{id}", new
                {
                    deleted = true,
                    id
                });
            }

            default:
                return McpToolCallResult.Fail(toolName, ModuleName, $"Unknown finance tool '{toolName}'.");
        }
    }
}

