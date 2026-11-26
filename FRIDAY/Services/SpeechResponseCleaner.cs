using System.Text;
using System.Text.RegularExpressions;
using Friday.Core.Models;

namespace FRIDAY.Services;

/// <summary>
/// Converts rich assistant Markdown/table responses into clean, natural-language speech output
/// suitable for Text-to-Speech while excluding internal tool names, raw JSON, markdown tables,
/// stack traces, API keys, and technical diagnostics.
/// </summary>
public static partial class SpeechResponseCleaner
{
    private static readonly string[] InternalTermPatterns =
    {
        @"\b(?:get|create|update|delete|complete|search|add|set|generate|build|log|universal)_[a-z0-9_]+\b",
        @"\b(?:CorrelationId|SessionId|PendingConfirmation|GroqLlmClient|FridayOrchestrator|FridayMcpClient|Sqlite|StackTrace|ApiKey)\b",
        @"\bgsk_[A-Za-z0-9_-]{8,}\b",
        @"\b(?:Task|Project|Note|Event)\s*#\d+\b"
    };

    public static string CleanForSpeech(
        string? markdownText,
        TableResult? table = null,
        ChartConfiguration? chart = null,
        PendingConfirmationDto? pendingConfirmation = null,
        bool isError = false)
    {
        if (pendingConfirmation is not null && !string.IsNullOrWhiteSpace(pendingConfirmation.PromptMessage))
        {
            return SanitizeInlineText(
                $"{pendingConfirmation.PromptMessage} Say Yes to confirm, or Cancel to keep it.");
        }

        if (isError)
        {
            var errClean = StripMarkdownAndTables(markdownText ?? string.Empty);
            if (string.IsNullOrWhiteSpace(errClean) || ContainsInternalDiagnosticNoise(errClean))
            {
                return "Friday couldn't process that request right now.";
            }
            return TruncateForSpeech(errClean, 360);
        }

        var cleanedBody = StripMarkdownAndTables(markdownText ?? string.Empty);

        // If the visible message consisted mostly of a markdown table or is very short while a structured TableResult exists,
        // produce a concise spoken summary from the table rows.
        if (table is not null && table.Rows.Count > 0 && ShouldSpeakTableSummary(cleanedBody, table))
        {
            var tableSpeech = SummarizeTableForSpeech(table);
            if (!string.IsNullOrWhiteSpace(tableSpeech))
            {
                cleanedBody = string.IsNullOrWhiteSpace(cleanedBody)
                    ? tableSpeech
                    : $"{cleanedBody.TrimEnd('.', '!', '?')}. {tableSpeech}";
            }
        }
        else if (string.IsNullOrWhiteSpace(cleanedBody) && chart is not null && !string.IsNullOrWhiteSpace(chart.Title))
        {
            cleanedBody = $"I have prepared the chart for {SanitizeInlineText(chart.Title)}.";
        }

        if (string.IsNullOrWhiteSpace(cleanedBody))
        {
            return "Done.";
        }

        return TruncateForSpeech(cleanedBody, 700);
    }

    private static bool ShouldSpeakTableSummary(string cleanedBody, TableResult table)
    {
        if (string.IsNullOrWhiteSpace(cleanedBody))
            return true;

        // Avoid repeating tabular facts if the prose already spans multiple sentences covering them
        if (cleanedBody.Length < 90 && table.Rows.Count <= 6)
            return true;

        return false;
    }

    private static string SummarizeTableForSpeech(TableResult table)
    {
        if (table.Rows.Count == 0) return string.Empty;

        var cols = table.Columns;
        var title = SanitizeInlineText(table.Title);
        var count = table.Rows.Count;

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(title))
        {
            sb.Append($"Here is a summary of {title} ({count} item{(count == 1 ? "" : "s")}). ");
        }

        int maxSpokenRows = Math.Min(count, 4);
        var items = new List<string>();
        for (int i = 0; i < maxSpokenRows; i++)
        {
            var row = table.Rows[i];
            if (row.Count == 0) continue;

            // Combine first 2 meaningful non-empty cells cleanly
            var parts = new List<string>();
            for (int c = 0; c < Math.Min(row.Count, 3); c++)
            {
                var cell = SanitizeInlineText(row[c]);
                if (string.IsNullOrWhiteSpace(cell)) continue;
                if (c < cols.Count && string.Equals(cols[c], "ID", StringComparison.OrdinalIgnoreCase)) continue;
                if (c < cols.Count && string.Equals(cols[c], "#", StringComparison.OrdinalIgnoreCase)) continue;
                parts.Add(cell);
            }

            if (parts.Count >= 2)
                items.Add($"{parts[0]}: {string.Join(", ", parts.Skip(1))}");
            else if (parts.Count == 1)
                items.Add(parts[0]);
        }

        if (items.Count > 0)
        {
            sb.Append(string.Join("; ", items));
            if (count > maxSpokenRows)
            {
                sb.Append($", plus {count - maxSpokenRows} more listed on screen");
            }
            sb.Append('.');
        }

        return sb.ToString().Trim();
    }

    private static string StripMarkdownAndTables(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        // Remove fenced code blocks (may contain JSON/code not meant for TTS)
        var text = FencedCodeRegex().Replace(raw, " ");

        // Remove JSON-like object blobs
        text = RawJsonObjectRegex().Replace(text, " ");

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Skip Markdown horizontal rules and table separator lines
            if (HorizontalRuleRegex().IsMatch(line) || TableSeparatorRegex().IsMatch(line))
                continue;

            // Skip raw Markdown pipe table rows (extract natural prose instead of reading pipes)
            if (line.Contains('|') && !line.StartsWith('>'))
            {
                // Detect markdown table block: convert up to 4 body rows to brief spoken phrases if no other prose exists
                if (i + 1 < lines.Length && TableSeparatorRegex().IsMatch(lines[i + 1].Trim()))
                {
                    var headers = SplitPipeCells(line);
                    i += 2; // skip header + separator
                    var spokenRows = new List<string>();
                    while (i < lines.Length)
                    {
                        var rLine = lines[i].Trim();
                        if (string.IsNullOrWhiteSpace(rLine))
                        {
                            if (i + 1 < lines.Length && lines[i + 1].Trim().Contains('|') && !TableSeparatorRegex().IsMatch(lines[i + 1].Trim()))
                            {
                                i++;
                                continue;
                            }
                            break;
                        }
                        if (!rLine.Contains('|') || TableSeparatorRegex().IsMatch(rLine))
                        {
                            i--;
                            break;
                        }

                        var cells = SplitPipeCells(rLine);
                        var rowDesc = BuildSpokenRowPhrase(headers, cells);
                        if (!string.IsNullOrWhiteSpace(rowDesc) && spokenRows.Count < 4)
                            spokenRows.Add(rowDesc);
                        i++;
                    }
                    if (spokenRows.Count > 0)
                    {
                        kept.Add(string.Join(". ", spokenRows) + ".");
                    }
                    continue;
                }

                // Standalone stray pipe row: skip pipes cleanly
                line = string.Join(", ", SplitPipeCells(line).Where(x => !string.IsNullOrWhiteSpace(x)));
                if (string.IsNullOrWhiteSpace(line)) continue;
            }

            line = SanitizeInlineText(line);
            if (!string.IsNullOrWhiteSpace(line))
            {
                kept.Add(line);
            }
        }

        return MultiWhitespaceRegex().Replace(string.Join(" ", kept), " ").Trim();
    }

    private static string BuildSpokenRowPhrase(List<string> headers, List<string> cells)
    {
        var items = new List<string>();
        for (int c = 0; c < cells.Count; c++)
        {
            var val = SanitizeInlineText(cells[c]);
            if (string.IsNullOrWhiteSpace(val)) continue;
            var hdr = c < headers.Count ? headers[c].Trim() : string.Empty;
            if (string.Equals(hdr, "ID", StringComparison.OrdinalIgnoreCase) || hdr == "#") continue;
            items.Add(val);
        }

        if (items.Count == 0) return string.Empty;
        if (items.Count == 1) return items[0];
        return $"{items[0]} ({string.Join(", ", items.Skip(1))})";
    }

    private static List<string> SplitPipeCells(string line)
    {
        var trimmed = line.Trim().Trim('|');
        return trimmed.Split('|').Select(c => c.Trim()).ToList();
    }

    private static string SanitizeInlineText(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var s = input;
        s = MarkdownLinkRegex().Replace(s, "$1");
        s = UrlRegex().Replace(s, "");
        s = HeadingPrefixRegex().Replace(s, "");
        s = BulletPrefixRegex().Replace(s, "");
        s = s.Replace("**", "").Replace("__", "").Replace("`", "").Replace("*", "");
        s = EntityIdParentheticalRegex().Replace(s, "");

        foreach (var pattern in InternalTermPatterns)
        {
            s = Regex.Replace(s, pattern, "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        // Remove common UI emojis so TTS doesn't spell out symbols awkwardly
        s = Regex.Replace(s, @"[\uD83C-\uDBFF\uDC00-\uDFFF]+|[\u2600-\u27BF]+", " ");
        s = MultiWhitespaceRegex().Replace(s, " ").Trim();
        return s;
    }

    private static bool ContainsInternalDiagnosticNoise(string text)
        => text.Contains("Exception", StringComparison.OrdinalIgnoreCase)
           || text.Contains("StackTrace", StringComparison.OrdinalIgnoreCase)
           || text.Contains("Groq:", StringComparison.OrdinalIgnoreCase)
           || text.Contains("HTTP_", StringComparison.OrdinalIgnoreCase);

    private static string TruncateForSpeech(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        var cut = text[..maxChars];
        var lastStop = Math.Max(cut.LastIndexOf('.'), Math.Max(cut.LastIndexOf('!'), cut.LastIndexOf('?')));
        if (lastStop > maxChars / 2)
        {
            return cut[..(lastStop + 1)].Trim();
        }
        var lastSpace = cut.LastIndexOf(' ');
        return (lastSpace > 0 ? cut[..lastSpace] : cut).Trim() + "...";
    }

    [GeneratedRegex(@"```[\s\S]*?```", RegexOptions.Compiled)]
    private static partial Regex FencedCodeRegex();

    [GeneratedRegex(@"\{[\s\S]{0,1200}?\}", RegexOptions.Compiled)]
    private static partial Regex RawJsonObjectRegex();

    [GeneratedRegex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)+\|?$", RegexOptions.Compiled)]
    private static partial Regex TableSeparatorRegex();

    [GeneratedRegex(@"^([-*_]){3,}$", RegexOptions.Compiled)]
    private static partial Regex HorizontalRuleRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled)]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"^(#{1,6}|>+)\s*", RegexOptions.Compiled)]
    private static partial Regex HeadingPrefixRegex();

    [GeneratedRegex(@"^([-*]|\d+\.)\s+", RegexOptions.Compiled)]
    private static partial Regex BulletPrefixRegex();

    [GeneratedRegex(@"\s*\(ID\s*#\d+[^)]*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex EntityIdParentheticalRegex();

    [GeneratedRegex(@"\s{2,}", RegexOptions.Compiled)]
    private static partial Regex MultiWhitespaceRegex();
}
