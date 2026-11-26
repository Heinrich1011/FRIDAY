namespace Friday.Core.Configuration;

public class GroqOptions
{
    public const string SectionName = "Groq";

    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "openai/gpt-oss-120b";
    public string BaseUrl { get; set; } = "https://api.groq.com/openai/v1";
    public int TimeoutSeconds { get; set; } = 45;
    public int MaxToolRounds { get; set; } = 6;
    public double Temperature { get; set; } = 0.15;
}

public class FileMcpOptions
{
    public const string SectionName = "FileMcp";

    public List<string> AllowedDirectories { get; set; } = new();
    public List<string> AllowedExtensions { get; set; } = new()
    {
        ".txt", ".md", ".csv", ".json", ".xml", ".log", ".docx", ".html", ".cs", ".sql"
    };
    public long MaxFileSizeBytes { get; set; } = 2 * 1024 * 1024; // 2 MB
}

public class ExternalMcpServerOptions
{
    public const string SectionName = "ExternalMcpServers";

    public string Name { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public List<string> Arguments { get; set; } = new();
    public bool Enabled { get; set; }
}

