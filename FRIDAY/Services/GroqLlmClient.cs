using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Friday.Core.Configuration;
using Friday.Core.Interfaces;
using Friday.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FRIDAY.Services;

public partial class GroqLlmClient : IGroqLlmClient
{
    private readonly HttpClient _httpClient;
    private readonly GroqOptions _options;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _env;
    private readonly ILogger<GroqLlmClient> _logger;

    public GroqLlmClient(
        HttpClient httpClient,
        IOptions<GroqOptions> options,
        IConfiguration configuration,
        IHostEnvironment env,
        ILogger<GroqLlmClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _configuration = configuration;
        _env = env;
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveApiKey());

    public string ConfiguredModel => string.IsNullOrWhiteSpace(_options.Model) ? "openai/gpt-oss-120b" : _options.Model;

    public async Task<LlmCompletionResult> CompleteAsync(
        IReadOnlyList<LlmChatMessage> messages,
        IReadOnlyList<McpToolDescriptor> availableTools,
        CancellationToken ct = default)
    {
        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning(
                "Groq completion aborted: API key not configured. Stage=CONFIG_CHECK GroqApiKeyConfigured=false ModelConfigured={ModelConfigured} ModelName={Model}",
                !string.IsNullOrWhiteSpace(_options.Model), ConfiguredModel);

            return new LlmCompletionResult
            {
                Success = false,
                IsMissingApiKey = true,
                ErrorCode = "GROQ_API_KEY_NOT_CONFIGURED",
                DiagnosticDetail = "Groq:ApiKey is not configured."
            };
        }

        var endpoint = $"{_options.BaseUrl.TrimEnd('/')}/chat/completions";
        var payload = BuildRequestPayload(messages, availableTools);
        var jsonPayload = JsonSerializer.Serialize(payload);

        if (_env.IsDevelopment())
        {
            _logger.LogInformation(
                "Sending Groq request | Stage=GROQ_HTTP_SEND Endpoint={Endpoint} Model={Model} Messages={MessageCount} Tools={ToolCount} PayloadBytes={PayloadBytes} AuthHeaderAttached=true",
                endpoint, ConfiguredModel, messages.Count, availableTools.Count, jsonPayload.Length);
        }

        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
                };
                // Authorization header is attached per-request and NEVER logged
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                var respBody = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                {
                    int statusCode = (int)resp.StatusCode;

                    // Recover cleanly from Llama's occasional <function=name>{...}</function> tool_use_failed 400 response
                    if (statusCode == 400 && TryRecoverFailedToolGeneration(respBody, out var recoveredCalls))
                    {
                        return new LlmCompletionResult
                        {
                            Success = true,
                            ToolCalls = recoveredCalls
                        };
                    }

                    // Recover cleanly if synthesis round (Tools=0) triggers "Tool choice is none, but model called a tool"
                    if (statusCode == 400
                        && attempt == 0
                        && availableTools.Count == 0
                        && respBody.Contains("Tool choice is none", StringComparison.OrdinalIgnoreCase))
                    {
                        var textOnlyMessages = BuildFlattenedSynthesisMessages(messages);
                        jsonPayload = JsonSerializer.Serialize(BuildRequestPayload(textOnlyMessages, Array.Empty<McpToolDescriptor>()));
                        continue;
                    }

                    // Handle transient 429 rate-limit bursts with short bounded retry (<= 15s)
                    if (statusCode == 429 && attempt == 0 && TryGetRetryDelay(resp, respBody, out var retryDelay) && retryDelay <= TimeSpan.FromSeconds(15))
                    {
                        _logger.LogWarning(
                            "Groq API returned HTTP 429 (rate limit); retrying once after {DelayMs}ms | Model={Model}",
                            (int)retryDelay.TotalMilliseconds, ConfiguredModel);
                        await Task.Delay(retryDelay, ct);
                        continue;
                    }

                    var safeErrorSummary = ExtractSafeGroqError(respBody);
                    if (_env.IsDevelopment())
                    {
                        _logger.LogError(
                            "Groq API call failed | FailingService=GroqLlmClient Stage=GROQ_HTTP_RESPONSE StatusCode={StatusCode} Endpoint={Endpoint} Model={Model} GroqError={GroqError}",
                            statusCode, endpoint, ConfiguredModel, safeErrorSummary);
                    }
                    else
                    {
                        _logger.LogWarning("Groq API returned non-success HTTP status {StatusCode} for model {Model}", statusCode, ConfiguredModel);
                    }

                    return new LlmCompletionResult
                    {
                        Success = false,
                        ErrorCode = $"HTTP_{statusCode}",
                        DiagnosticDetail = safeErrorSummary
                    };
                }

                return ParseSuccessResponse(respBody);
            }

            return new LlmCompletionResult
            {
                Success = false,
                ErrorCode = "HTTP_429",
                DiagnosticDetail = "Rate limit persisted after retry."
            };
        }
        catch (TaskCanceledException ex)
        {
            if (_env.IsDevelopment())
                _logger.LogError(ex, "Groq API request timed out | FailingService=GroqLlmClient Stage=GROQ_HTTP_SEND Endpoint={Endpoint} Model={Model}", endpoint, ConfiguredModel);
            else
                _logger.LogWarning("Groq API request timed out.");

            return new LlmCompletionResult
            {
                Success = false,
                ErrorCode = "TIMEOUT",
                DiagnosticDetail = $"{ex.GetType().Name}: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            if (_env.IsDevelopment())
                _logger.LogError(ex, "Groq API communication error | FailingService=GroqLlmClient Stage=GROQ_HTTP_SEND Endpoint={Endpoint} Model={Model} ExceptionType={ExceptionType}", endpoint, ConfiguredModel, ex.GetType().Name);
            else
                _logger.LogError("Groq API communication error: {ExceptionType}", ex.GetType().Name);

            return new LlmCompletionResult
            {
                Success = false,
                ErrorCode = "NETWORK_ERROR",
                DiagnosticDetail = $"{ex.GetType().Name}: {ex.Message}"
            };
        }
    }

    private LlmCompletionResult ParseSuccessResponse(string respBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(respBody);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                _logger.LogWarning("Groq response contained no choices | Stage=GROQ_RESPONSE_PARSE Model={Model}", ConfiguredModel);
                return new LlmCompletionResult { Success = false, ErrorCode = "EMPTY_CHOICES", DiagnosticDetail = "Choices array empty." };
            }

            var choice = choices[0];
            var finishReason = choice.TryGetProperty("finish_reason", out var frEl) ? frEl.GetString() : null;
            var messageEl = choice.GetProperty("message");
            var content = messageEl.TryGetProperty("content", out var cEl) && cEl.ValueKind == JsonValueKind.String
                ? cEl.GetString()
                : null;

            var toolCalls = new List<LlmToolCall>();
            if (messageEl.TryGetProperty("tool_calls", out var tcArray) && tcArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in tcArray.EnumerateArray())
                {
                    var id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? Guid.NewGuid().ToString("N")[..10] : Guid.NewGuid().ToString("N")[..10];
                    if (tc.TryGetProperty("function", out var fn))
                    {
                        var name = fn.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                        var args = fn.TryGetProperty("arguments", out var aEl) ? aEl.GetString() ?? "{}" : "{}";
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            toolCalls.Add(new LlmToolCall
                            {
                                Id = id,
                                Name = name,
                                ArgumentsJson = string.IsNullOrWhiteSpace(args) ? "{}" : args
                            });
                        }
                    }
                }
            }

            // Also handle inline <function=...> if emitted inside text content when toolCalls is empty
            if (toolCalls.Count == 0 && !string.IsNullOrWhiteSpace(content) && TryExtractInlineFunctionTags(content, out var inlineCalls, out var cleanedText))
            {
                toolCalls.AddRange(inlineCalls);
                content = cleanedText;
            }

            if (_env.IsDevelopment())
            {
                _logger.LogInformation(
                    "Groq response parsed | Stage=GROQ_RESPONSE_PARSE Model={Model} FinishReason={FinishReason} ToolCalls={ToolCallCount} HasContent={HasContent}",
                    ConfiguredModel, finishReason ?? "unknown", toolCalls.Count, !string.IsNullOrWhiteSpace(content));
            }

            return new LlmCompletionResult
            {
                Success = true,
                Content = content,
                ToolCalls = toolCalls
            };
        }
        catch (Exception ex)
        {
            if (_env.IsDevelopment())
                _logger.LogError(ex, "Failed to parse Groq JSON response | FailingService=GroqLlmClient Stage=GROQ_RESPONSE_PARSE Model={Model}", ConfiguredModel);

            return new LlmCompletionResult
            {
                Success = false,
                ErrorCode = "RESPONSE_PARSE_ERROR",
                DiagnosticDetail = $"{ex.GetType().Name}: {ex.Message}"
            };
        }
    }

    private string? ResolveApiKey()
    {
        var candidate = !string.IsNullOrWhiteSpace(_options.ApiKey)
            ? _options.ApiKey.Trim()
            : (_configuration["Groq:ApiKey"]
                ?? _configuration["Groq__ApiKey"]
                ?? Environment.GetEnvironmentVariable("Groq__ApiKey")
                ?? Environment.GetEnvironmentVariable("GROQ_API_KEY"))?.Trim();

        if (string.IsNullOrWhiteSpace(candidate)
            || candidate.Equals("PASTE_YOUR_GROQ_API_KEY_HERE", StringComparison.OrdinalIgnoreCase)
            || candidate.Equals("YOUR_GROQ_API_KEY", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return candidate;
    }

    private object BuildRequestPayload(IReadOnlyList<LlmChatMessage> messages, IReadOnlyList<McpToolDescriptor> availableTools)
    {
        var mappedMessages = messages.Select<LlmChatMessage, object>(m =>
        {
            if (m.Role == "assistant" && m.ToolCalls is { Count: > 0 })
            {
                return new Dictionary<string, object?>
                {
                    ["role"] = "assistant",
                    ["content"] = m.Content ?? string.Empty,
                    ["tool_calls"] = m.ToolCalls.Select(tc => new
                    {
                        id = tc.Id,
                        type = "function",
                        function = new
                        {
                            name = tc.Name,
                            arguments = tc.ArgumentsJson
                        }
                    }).ToList()
                };
            }

            if (m.Role == "tool")
            {
                return new Dictionary<string, object?>
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = m.ToolCallId ?? "",
                    ["name"] = m.Name ?? "",
                    ["content"] = m.Content ?? "{}"
                };
            }

            return new Dictionary<string, object?>
            {
                ["role"] = m.Role,
                ["content"] = m.Content ?? string.Empty
            };
        }).ToList();

        var dict = new Dictionary<string, object?>
        {
            ["model"] = ConfiguredModel,
            ["messages"] = mappedMessages,
            ["temperature"] = _options.Temperature,
            ["max_tokens"] = 2048
        };

        if (availableTools.Count > 0)
        {
            dict["tools"] = availableTools.Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = TruncateText(t.Description, 60),
                    parameters = CompactToolSchemaForLlm(t.InputSchema)
                }
            }).ToList();
            dict["tool_choice"] = "auto";
        }

        return dict;
    }

    private static List<LlmChatMessage> BuildFlattenedSynthesisMessages(IReadOnlyList<LlmChatMessage> messages)
    {
        var result = new List<LlmChatMessage>();
        var retrievedParts = new List<string>();

        foreach (var m in messages)
        {
            if (m.Role == "tool")
            {
                retrievedParts.Add($"[{m.Name ?? "tool"} result]: {m.Content}");
                continue;
            }

            if (m.Role == "assistant" && m.ToolCalls is { Count: > 0 } && string.IsNullOrWhiteSpace(m.Content))
            {
                continue;
            }

            result.Add(m);
        }

        if (retrievedParts.Count > 0)
        {
            result.Add(new LlmChatMessage
            {
                Role = "system",
                Content = "Retrieved records from tools:\n" + string.Join("\n", retrievedParts) + "\n\nRespond to the user in plain natural language ONLY using these exact records. Do not call any tools."
            });
        }

        return result;
    }

    private static Dictionary<string, object> CompactToolSchemaForLlm(JsonElement schema)
    {
        var result = new Dictionary<string, object> { ["type"] = "object" };

        if (schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            var propDict = new Dictionary<string, object>();
            foreach (var p in props.EnumerateObject())
            {
                var pMeta = new Dictionary<string, object>();
                if (p.Value.TryGetProperty("type", out var tEl) && tEl.ValueKind == JsonValueKind.String)
                    pMeta["type"] = tEl.GetString()!;
                if (p.Value.TryGetProperty("enum", out var eEl) && eEl.ValueKind == JsonValueKind.Array)
                    pMeta["enum"] = JsonSerializer.Deserialize<string[]>(eEl.GetRawText()) ?? Array.Empty<string>();
                if (p.Value.TryGetProperty("description", out var dEl) && dEl.ValueKind == JsonValueKind.String
                    && (p.Name.Contains("date", StringComparison.OrdinalIgnoreCase)
                        || p.Name.Contains("time", StringComparison.OrdinalIgnoreCase)
                        || p.Name.Contains("match", StringComparison.OrdinalIgnoreCase)
                        || p.Name.Contains("csv", StringComparison.OrdinalIgnoreCase)
                        || p.Name.Contains("include", StringComparison.OrdinalIgnoreCase)
                        || p.Name.Contains("days", StringComparison.OrdinalIgnoreCase)))
                {
                    pMeta["description"] = TruncateText(dEl.GetString() ?? "", 32);
                }
                propDict[p.Name] = pMeta;
            }
            result["properties"] = propDict;
        }
        else
        {
            result["properties"] = new Dictionary<string, object>();
        }

        if (schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("required", out var reqEl) && reqEl.ValueKind == JsonValueKind.Array)
        {
            var req = JsonSerializer.Deserialize<string[]>(reqEl.GetRawText());
            if (req is { Length: > 0 })
                result["required"] = req;
        }

        return result;
    }

    private static string TruncateText(string text, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= maxLen) return text;
        return text[..maxLen].TrimEnd();
    }

    private static bool TryGetRetryDelay(HttpResponseMessage resp, string respBody, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        if (resp.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            delay = delta + TimeSpan.FromMilliseconds(250);
            return true;
        }

        var match = RetryInSecondsRegex().Match(respBody);
        if (match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double seconds) && seconds > 0)
        {
            delay = TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(350);
            return true;
        }

        return false;
    }

    private static string ExtractSafeGroqError(string respBody)
    {
        if (string.IsNullOrWhiteSpace(respBody)) return "Empty error response body.";

        // Defense-in-depth: scrub any API key pattern if ever reflected
        var scrubbed = ApiKeyScrubRegex().Replace(respBody, "[REDACTED_KEY]");
        try
        {
            using var doc = JsonDocument.Parse(scrubbed);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                var type = err.TryGetProperty("type", out var tEl) ? tEl.GetString() : null;
                var code = err.TryGetProperty("code", out var cEl) ? cEl.GetString() : null;
                var msg = err.TryGetProperty("message", out var mEl) ? mEl.GetString() : null;
                var summary = $"type={type ?? "unknown"}, code={code ?? "none"}, message={msg ?? "none"}";
                return summary.Length <= 500 ? summary : summary[..500];
            }
        }
        catch
        {
            // Fall through to truncated raw safe text
        }

        return scrubbed.Length <= 500 ? scrubbed : scrubbed[..500];
    }

    private static bool TryRecoverFailedToolGeneration(string errorJson, out List<LlmToolCall> recovered)
    {
        recovered = new List<LlmToolCall>();
        try
        {
            using var doc = JsonDocument.Parse(errorJson);
            if (doc.RootElement.TryGetProperty("error", out var err)
                && err.TryGetProperty("failed_generation", out var fg)
                && fg.ValueKind == JsonValueKind.String)
            {
                var raw = fg.GetString() ?? "";
                if (TryExtractInlineFunctionTags(raw, out recovered, out _))
                {
                    return recovered.Count > 0;
                }
            }
        }
        catch
        {
            // Ignore malformed error body
        }
        return false;
    }

    private static bool TryExtractInlineFunctionTags(string text, out List<LlmToolCall> calls, out string cleanedText)
    {
        calls = new List<LlmToolCall>();
        cleanedText = text;

        var matches = InlineFunctionRegex().Matches(text);
        if (matches.Count == 0) return false;

        foreach (Match m in matches)
        {
            var fnName = m.Groups[1].Value.Trim();
            var rawArgs = m.Groups[2].Value.Trim();
            if (string.IsNullOrWhiteSpace(rawArgs)) rawArgs = "{}";

            // Ensure valid JSON object
            try
            {
                using var _ = JsonDocument.Parse(rawArgs);
                calls.Add(new LlmToolCall
                {
                    Id = $"call_{Guid.NewGuid():N}"[..16],
                    Name = fnName,
                    ArgumentsJson = rawArgs
                });
            }
            catch
            {
                // Skip invalid JSON
            }
        }

        cleanedText = InlineFunctionRegex().Replace(text, "").Trim();
        return calls.Count > 0;
    }

    [GeneratedRegex(@"<function=([a-zA-Z0-9_]+)[>:\s]*(\{[\s\S]*?\})?\s*</function>", RegexOptions.Compiled)]
    private static partial Regex InlineFunctionRegex();

    [GeneratedRegex(@"try again in\s+(\d+(?:\.\d+)?)s", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex RetryInSecondsRegex();

    [GeneratedRegex(@"gsk_[A-Za-z0-9_]+", RegexOptions.Compiled)]
    private static partial Regex ApiKeyScrubRegex();
}
