using System.Net.Http.Headers;
using System.Text;
using AzureDevopsMCPSharp.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;

namespace AzureDevopsMCPSharp.Services;

public sealed class AzureDevOpsService : IDisposable
{
    private readonly Lazy<VssConnection> _connection;
    private readonly Lazy<HttpClient> _restClient;
    private readonly AzureDevOpsOptions _options;

    public AzureDevOpsService(IOptions<AzureDevOpsOptions> options)
    {
        _options = options.Value;
        _connection = new Lazy<VssConnection>(CreateConnection);
        _restClient = new Lazy<HttpClient>(CreateRestClient);
    }

    public AzureDevOpsOptions Options => _options;
    public bool IsReadOnly => _options.ReadOnly;
    public VssConnection Connection => _connection.Value;

    /// <summary>HTTP client preconfigured with the collection base URL and PAT auth, for REST endpoints not exposed by the SDK.</summary>
    public HttpClient RestClient => _restClient.Value;

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

        var ops = _options.Operations;
        if (ops is null || !ops.TryGetValue(operation, out var enabled) || !enabled)
        {
            throw new InvalidOperationException(
                $"Operation '{operation}' is blocked: not enabled in AzureDevOps:Operations " +
                $"(missing entries default to disabled). " +
                $"Set AzureDevOps:Operations:{operation}=true to enable it.");
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

    private HttpClient CreateRestClient()
    {
        if (string.IsNullOrWhiteSpace(_options.OrganizationUrl))
            throw new InvalidOperationException("AzureDevOps:OrganizationUrl is required.");
        if (string.IsNullOrWhiteSpace(_options.PersonalAccessToken))
            throw new InvalidOperationException("AzureDevOps:PersonalAccessToken is required.");

        var http = new HttpClient
        {
            BaseAddress = new Uri(_options.OrganizationUrl.TrimEnd('/') + "/"),
        };
        var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{_options.PersonalAccessToken}"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    public void Dispose()
    {
        if (_connection.IsValueCreated)
            _connection.Value.Dispose();
        if (_restClient.IsValueCreated)
            _restClient.Value.Dispose();
    }
}
