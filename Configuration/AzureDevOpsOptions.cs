namespace AzureDevopsMCPSharp.Configuration;

public sealed class AzureDevOpsOptions
{
    public const string SectionName = "AzureDevOps";

    public string OrganizationUrl { get; set; } = string.Empty;
    public string PersonalAccessToken { get; set; } = string.Empty;
    public string? DefaultProject { get; set; }
    public bool ReadOnly { get; set; } = true;
}

public sealed class ServerOptions
{
    public const string SectionName = "Server";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5089;
    public string Path { get; set; } = "/mcp";
}
