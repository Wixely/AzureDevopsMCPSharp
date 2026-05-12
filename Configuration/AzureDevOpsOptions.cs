namespace AzureDevopsMCPSharp.Configuration;

public sealed class AzureDevOpsOptions
{
    public const string SectionName = "AzureDevOps";

    public string OrganizationUrl { get; set; } = string.Empty;
    public string PersonalAccessToken { get; set; } = string.Empty;
    public string? DefaultProject { get; set; }
    public bool ReadOnly { get; set; } = true;

    /// <summary>
    /// Per-operation allow/deny switches keyed by tool name (e.g. "queue_pipeline_run").
    /// A missing entry is treated as allowed. Set an entry to false to disable that specific
    /// write tool even when ReadOnly is false. ReadOnly=true blocks everything regardless.
    /// Lookup is case-insensitive.
    /// </summary>
    public Dictionary<string, bool> Operations { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ServerOptions
{
    public const string SectionName = "Server";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5089;
    public string Path { get; set; } = "/mcp";
}
