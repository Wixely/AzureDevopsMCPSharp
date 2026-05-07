using AzureDevopsMCPSharp.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;

namespace AzureDevopsMCPSharp.Services;

public sealed class AzureDevOpsService : IDisposable
{
    private readonly Lazy<VssConnection> _connection;
    private readonly AzureDevOpsOptions _options;

    public AzureDevOpsService(IOptions<AzureDevOpsOptions> options)
    {
        _options = options.Value;
        _connection = new Lazy<VssConnection>(CreateConnection);
    }

    public AzureDevOpsOptions Options => _options;
    public bool IsReadOnly => _options.ReadOnly;
    public VssConnection Connection => _connection.Value;

    public T GetClient<T>() where T : VssHttpClientBase
        => Connection.GetClient<T>();

    public string ResolveProject(string? project)
    {
        var resolved = string.IsNullOrWhiteSpace(project) ? _options.DefaultProject : project;
        if (string.IsNullOrWhiteSpace(resolved))
        {
            throw new InvalidOperationException(
                "No project specified and AzureDevOps:DefaultProject is not configured.");
        }
        return resolved;
    }

    public void EnsureWriteAllowed(string operation)
    {
        if (_options.ReadOnly)
        {
            throw new InvalidOperationException(
                $"Operation '{operation}' is blocked: server is running in read-only mode. " +
                "Set AzureDevOps:ReadOnly=false to allow writes.");
        }
    }

    private VssConnection CreateConnection()
    {
        if (string.IsNullOrWhiteSpace(_options.OrganizationUrl))
            throw new InvalidOperationException("AzureDevOps:OrganizationUrl is required.");
        if (string.IsNullOrWhiteSpace(_options.PersonalAccessToken))
            throw new InvalidOperationException("AzureDevOps:PersonalAccessToken is required.");

        var credentials = new VssBasicCredential(string.Empty, _options.PersonalAccessToken);
        return new VssConnection(new Uri(_options.OrganizationUrl), credentials);
    }

    public void Dispose()
    {
        if (_connection.IsValueCreated)
            _connection.Value.Dispose();
    }
}
