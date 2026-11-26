using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using Friday.Core.Configuration;
using Friday.Core.Enums;
using Friday.Core.Interfaces;
using Friday.Core.Models;
using Friday.Mcp.Protocol;
using Microsoft.Extensions.Options;

namespace Friday.Mcp.Modules;

public class FileMcpModule : IMcpToolModule
{
    private readonly FileMcpOptions _options;

    public FileMcpModule(IOptions<FileMcpOptions> options)
    {
        _options = options.Value;
    }

    public string ModuleName => "files";

    public IReadOnlyList<McpToolDescriptor> GetTools() => new List<McpToolDescriptor>
    {
        new()
        {
            Name = "list_files",
            ModuleName = ModuleName,
            HumanActionDescription = "Listed files in approved workspace folders",
            Description = "Lists files within configured approved directories only. Access outside approved locations is rejected.",
            Safety = McpToolSafety.Read,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["subdirectory"] = McpSchemaHelper.Prop("string", "Optional relative subfolder inside an approved directory")
            })
        },
        new()
        {
            Name = "search_files",
            ModuleName = ModuleName,
            HumanActionDescription = "Searched approved documents",
            Description = "Searches file names and text contents inside explicitly configured approved directories.",
            Safety = McpToolSafety.Read,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["query"] = McpSchemaHelper.Prop("string", "Search keyword for file name or document text")
            }, required: new[] { "query" })
        },
        new()
        {
            Name = "read_document",
            ModuleName = ModuleName,
            HumanActionDescription = "Read an approved document",
            Description = "Reads text content from a file located inside an approved directory (.txt, .md, .csv, .json, .docx, etc.).",
            Safety = McpToolSafety.Read,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["file_path"] = McpSchemaHelper.Prop("string", "Relative or full path within an approved directory")
            }, required: new[] { "file_path" })
        },
        new()
        {
            Name = "get_document_metadata",
            ModuleName = ModuleName,
            HumanActionDescription = "Checked document metadata",
            Description = "Retrieves size, extension, and modification timestamps for a file inside an approved directory.",
            Safety = McpToolSafety.Read,
            InputSchema = McpSchemaHelper.CreateObjectSchema(new Dictionary<string, object>
            {
                ["file_path"] = McpSchemaHelper.Prop("string", "Relative or full path within an approved directory")
            }, required: new[] { "file_path" })
        }
    };

    public Task<string?> DescribeTargetForDestructiveActionAsync(string toolName, JsonElement arguments, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public async Task<McpToolCallResult> ExecuteToolAsync(string toolName, JsonElement arguments, McpToolCallContext context, CancellationToken ct = default)
    {
        var roots = GetApprovedCanonicalRoots();
        if (roots.Count == 0)
        {
            return McpToolCallResult.Fail(toolName, ModuleName, "No approved FileMcp directories are configured on the server.");
        }

        switch (toolName)
        {
            case "list_files":
            {
                var sub = McpSchemaHelper.GetOptionalString(arguments, "subdirectory", 300);
                var filesOut = new List<object>();

                foreach (var root in roots)
                {
                    if (!Directory.Exists(root)) continue;
                    var targetDir = string.IsNullOrWhiteSpace(sub) ? root : ResolveSafePath(sub, new List<string> { root });
                    if (targetDir is null || !Directory.Exists(targetDir))
                    {
                        if (!string.IsNullOrWhiteSpace(sub))
                            return McpToolCallResult.Fail(toolName, ModuleName, "Requested directory is outside approved locations or does not exist.");
                        continue;
                    }

                    foreach (var file in Directory.EnumerateFiles(targetDir, "*", SearchOption.AllDirectories).Take(100))
                    {
                        if (!IsPathInsideApprovedRoots(file, roots) || IsSymlinkOrHidden(file)) continue;
                        var fi = new FileInfo(file);
                        if (!IsAllowedExtension(fi.Extension)) continue;

                        filesOut.Add(new
                        {
                            name = fi.Name,
                            relative_path = Path.GetRelativePath(root, file).Replace('\\', '/'),
                            extension = fi.Extension,
                            size_bytes = fi.Length,
                            modified = McpSchemaHelper.FormatLocal(fi.LastWriteTimeUtc, context.UserUtcOffset)
                        });
                    }
                }

                return McpToolCallResult.Ok(toolName, ModuleName, $"Listed {filesOut.Count} approved file(s)", new
                {
                    approved_roots_count = roots.Count,
                    count = filesOut.Count,
                    files = filesOut
                });
            }

            case "search_files":
            {
                var query = McpSchemaHelper.GetRequiredString(arguments, "query", 200);
                var matches = await SearchApprovedFilesInternalAsync(query, roots, context.UserUtcOffset, ct);
                return McpToolCallResult.Ok(toolName, ModuleName, $"Searched approved documents ({matches.Count} match(es))", new
                {
                    query,
                    count = matches.Count,
                    matches
                });
            }

            case "read_document":
            {
                var reqPath = McpSchemaHelper.GetRequiredString(arguments, "file_path", 500);
                var resolved = ResolveSafePath(reqPath, roots);
                if (resolved is null)
                {
                    return McpToolCallResult.Fail(toolName, ModuleName, "Access denied: path is outside configured approved directories.");
                }
                if (!File.Exists(resolved) || IsSymlinkOrHidden(resolved))
                {
                    return McpToolCallResult.Fail(toolName, ModuleName, "Requested document was not found in approved directories.");
                }

                var fi = new FileInfo(resolved);
                if (!IsAllowedExtension(fi.Extension))
                {
                    return McpToolCallResult.Fail(toolName, ModuleName, $"File type '{fi.Extension}' is not permitted by FileMcp policy.");
                }
                if (fi.Length > _options.MaxFileSizeBytes)
                {
                    return McpToolCallResult.Fail(toolName, ModuleName, $"File exceeds maximum allowed size ({_options.MaxFileSizeBytes} bytes).");
                }

                var text = await ReadDocumentTextAsync(resolved, fi.Extension, ct);
                if (text.Length > 8000) text = text[..8000] + "\n...[truncated]";

                return McpToolCallResult.Ok(toolName, ModuleName, $"Read document '{fi.Name}'", new
                {
                    file_name = fi.Name,
                    size_bytes = fi.Length,
                    modified = McpSchemaHelper.FormatLocal(fi.LastWriteTimeUtc, context.UserUtcOffset),
                    content = text
                });
            }

            case "get_document_metadata":
            {
                var reqPath = McpSchemaHelper.GetRequiredString(arguments, "file_path", 500);
                var resolved = ResolveSafePath(reqPath, roots);
                if (resolved is null)
                {
                    return McpToolCallResult.Fail(toolName, ModuleName, "Access denied: path is outside configured approved directories.");
                }
                if (!File.Exists(resolved) || IsSymlinkOrHidden(resolved))
                {
                    return McpToolCallResult.Fail(toolName, ModuleName, "Document not found in approved directories.");
                }

                var fi = new FileInfo(resolved);
                return McpToolCallResult.Ok(toolName, ModuleName, $"Inspected metadata for '{fi.Name}'", new
                {
                    file_name = fi.Name,
                    extension = fi.Extension,
                    size_bytes = fi.Length,
                    created = McpSchemaHelper.FormatLocal(fi.CreationTimeUtc, context.UserUtcOffset),
                    modified = McpSchemaHelper.FormatLocal(fi.LastWriteTimeUtc, context.UserUtcOffset)
                });
            }

            default:
                return McpToolCallResult.Fail(toolName, ModuleName, $"Unknown file tool '{toolName}'.");
        }
    }

    public async Task<List<object>> SearchApprovedFilesAsync(string query, TimeSpan offset, CancellationToken ct = default)
    {
        var roots = GetApprovedCanonicalRoots();
        if (roots.Count == 0) return new List<object>();
        return await SearchApprovedFilesInternalAsync(query, roots, offset, ct);
    }

    private async Task<List<object>> SearchApprovedFilesInternalAsync(string query, List<string> roots, TimeSpan offset, CancellationToken ct)
    {
        var lower = query.ToLowerInvariant();
        var results = new List<object>();

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (results.Count >= 25) break;
                if (!IsPathInsideApprovedRoots(file, roots) || IsSymlinkOrHidden(file)) continue;

                var fi = new FileInfo(file);
                if (!IsAllowedExtension(fi.Extension) || fi.Length > _options.MaxFileSizeBytes) continue;

                bool nameMatch = fi.Name.ToLowerInvariant().Contains(lower);
                string? snippet = null;

                if (!nameMatch)
                {
                    try
                    {
                        var text = await ReadDocumentTextAsync(file, fi.Extension, ct);
                        int idx = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0)
                        {
                            int start = Math.Max(0, idx - 60);
                            int len = Math.Min(180, text.Length - start);
                            snippet = text.Substring(start, len).Replace('\r', ' ').Replace('\n', ' ');
                        }
                    }
                    catch
                    {
                        // Ignore unreadable file
                    }
                }

                if (nameMatch || snippet is not null)
                {
                    results.Add(new
                    {
                        file_name = fi.Name,
                        relative_path = Path.GetRelativePath(root, file).Replace('\\', '/'),
                        matched_by = nameMatch ? "filename" : "content",
                        snippet,
                        modified = McpSchemaHelper.FormatLocal(fi.LastWriteTimeUtc, offset)
                    });
                }
            }
        }

        return results;
    }

    private List<string> GetApprovedCanonicalRoots()
    {
        var list = new List<string>();
        foreach (var dir in _options.AllowedDirectories)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                list.Add(full);
            }
            catch
            {
                // Ignore malformed configured path
            }
        }
        return list;
    }

    public static string? ResolveSafePath(string requestedPath, List<string> canonicalRootsWithSep)
    {
        if (string.IsNullOrWhiteSpace(requestedPath) || requestedPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return null;

        try
        {
            if (Path.IsPathRooted(requestedPath))
            {
                var full = Path.GetFullPath(requestedPath);
                return IsPathInsideApprovedRoots(full, canonicalRootsWithSep) ? full : null;
            }

            foreach (var root in canonicalRootsWithSep)
            {
                var combined = Path.GetFullPath(Path.Combine(root, requestedPath));
                if (IsPathInsideApprovedRoots(combined, canonicalRootsWithSep))
                {
                    if (File.Exists(combined) || Directory.Exists(combined))
                        return combined;
                }
            }

            // If not existing yet, still validate against first root to distinguish not-found vs traversal
            var candidate = Path.GetFullPath(Path.Combine(canonicalRootsWithSep[0], requestedPath));
            return IsPathInsideApprovedRoots(candidate, canonicalRootsWithSep) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPathInsideApprovedRoots(string fullPath, List<string> canonicalRootsWithSep)
    {
        var normalized = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return canonicalRootsWithSep.Any(root =>
            fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSymlinkOrHidden(string path)
    {
        try
        {
            var attr = File.GetAttributes(path);
            return (attr & FileAttributes.ReparsePoint) != 0
                || (attr & FileAttributes.Hidden) != 0
                || (attr & FileAttributes.System) != 0;
        }
        catch
        {
            return true;
        }
    }

    private bool IsAllowedExtension(string ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return false;
        return _options.AllowedExtensions.Any(a => string.Equals(a, ext, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string> ReadDocumentTextAsync(string fullPath, string extension, CancellationToken ct)
    {
        if (string.Equals(extension, ".docx", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = WordprocessingDocument.Open(fullPath, false);
            var body = doc.MainDocumentPart?.Document?.Body;
            return body?.InnerText ?? string.Empty;
        }

        return await File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
    }
}

