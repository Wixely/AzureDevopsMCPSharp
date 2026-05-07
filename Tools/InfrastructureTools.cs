using System.ComponentModel;
using AzureDevopsMCPSharp.Services;
using ModelContextProtocol.Server;

namespace AzureDevopsMCPSharp.Tools;

// These endpoints aren't exposed by the on-prem TFS .NET SDK, so we hit the REST API directly
// using the PAT-authenticated HttpClient from AzureDevOpsService. The response bodies are
// already JSON, so we return them verbatim.
[McpServerToolType]
public static class InfrastructureTools
{
    private const string StableApi = "api-version=6.0";
    private const string PreviewApi = "api-version=6.0-preview.1";

    // ---------- Agent pools (collection-scoped) ----------

    [McpServerTool(Name = "list_agent_pools"),
     Description("List agent pools in the Azure DevOps collection.")]
    public static Task<string> ListAgentPools(
        AzureDevOpsService svc,
        [Description("Optional pool name filter")] string? name = null,
        CancellationToken ct = default)
    {
        var query = $"_apis/distributedtask/pools?{StableApi}";
        if (!string.IsNullOrWhiteSpace(name))
            query += $"&poolName={Uri.EscapeDataString(name)}";
        return GetJson(svc, query, ct);
    }

    [McpServerTool(Name = "get_agent_pool"),
     Description("Get full details for a single agent pool by id.")]
    public static Task<string> GetAgentPool(
        AzureDevOpsService svc,
        [Description("Agent pool id")] int poolId,
        CancellationToken ct = default)
        => GetJson(svc, $"_apis/distributedtask/pools/{poolId}?{StableApi}", ct);

    // ---------- Agents (machines inside a pool) ----------

    [McpServerTool(Name = "list_agents"),
     Description("List agents in a pool, including online/offline status, current and last completed job.")]
    public static Task<string> ListAgents(
        AzureDevOpsService svc,
        [Description("Agent pool id")] int poolId,
        [Description("Optional agent name filter")] string? name = null,
        [Description("Include hardware/software capabilities (verbose)")] bool includeCapabilities = false,
        CancellationToken ct = default)
    {
        var query = $"_apis/distributedtask/pools/{poolId}/agents?{StableApi}" +
                    $"&includeCapabilities={includeCapabilities.ToString().ToLowerInvariant()}" +
                    "&includeAssignedRequest=true&includeLastCompletedRequest=true";
        if (!string.IsNullOrWhiteSpace(name))
            query += $"&agentName={Uri.EscapeDataString(name)}";
        return GetJson(svc, query, ct);
    }

    [McpServerTool(Name = "get_agent"),
     Description("Get full details for a single agent by pool id and agent id, including capabilities and current job.")]
    public static Task<string> GetAgent(
        AzureDevOpsService svc,
        [Description("Agent pool id")] int poolId,
        [Description("Agent id")] int agentId,
        CancellationToken ct = default)
        => GetJson(svc,
            $"_apis/distributedtask/pools/{poolId}/agents/{agentId}?{StableApi}" +
            "&includeCapabilities=true&includeAssignedRequest=true&includeLastCompletedRequest=true",
            ct);

    // ---------- Environments (project-scoped, used by YAML deployments) ----------

    [McpServerTool(Name = "list_environments"),
     Description("List Pipeline Environments in a project.")]
    public static Task<string> ListEnvironments(
        AzureDevOpsService svc,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        [Description("Optional name filter")] string? name = null,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var query = $"{Uri.EscapeDataString(resolved)}/_apis/pipelines/environments?{PreviewApi}";
        if (!string.IsNullOrWhiteSpace(name))
            query += $"&name={Uri.EscapeDataString(name)}";
        return GetJson(svc, query, ct);
    }

    [McpServerTool(Name = "get_environment"),
     Description("Get full details for a single Pipeline Environment by id, including resource references.")]
    public static Task<string> GetEnvironment(
        AzureDevOpsService svc,
        [Description("Environment id")] int environmentId,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        return GetJson(svc,
            $"{Uri.EscapeDataString(resolved)}/_apis/pipelines/environments/{environmentId}?{PreviewApi}&expands=resourceReferences",
            ct);
    }

    [McpServerTool(Name = "list_environment_deployments"),
     Description("List recent deployment execution records for a Pipeline Environment.")]
    public static Task<string> ListEnvironmentDeployments(
        AzureDevOpsService svc,
        [Description("Environment id")] int environmentId,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        [Description("Max records (default 25)")] int top = 25,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        return GetJson(svc,
            $"{Uri.EscapeDataString(resolved)}/_apis/pipelines/environments/{environmentId}/environmentdeploymentrecords?{PreviewApi}&top={top}",
            ct);
    }

    // ---------- Deployment groups (project-scoped, machines for classic releases) ----------

    [McpServerTool(Name = "list_deployment_groups"),
     Description("List Deployment Groups (machine groups for classic Release Management) in a project.")]
    public static Task<string> ListDeploymentGroups(
        AzureDevOpsService svc,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        [Description("Optional name filter")] string? name = null,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var query = $"{Uri.EscapeDataString(resolved)}/_apis/distributedtask/deploymentgroups?{PreviewApi}";
        if (!string.IsNullOrWhiteSpace(name))
            query += $"&name={Uri.EscapeDataString(name)}";
        return GetJson(svc, query, ct);
    }

    [McpServerTool(Name = "get_deployment_group"),
     Description("Get full details for a single Deployment Group by id, expanding member machines.")]
    public static Task<string> GetDeploymentGroup(
        AzureDevOpsService svc,
        [Description("Deployment group id")] int deploymentGroupId,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        return GetJson(svc,
            $"{Uri.EscapeDataString(resolved)}/_apis/distributedtask/deploymentgroups/{deploymentGroupId}?{PreviewApi}&$expand=Machines",
            ct);
    }

    [McpServerTool(Name = "list_deployment_targets"),
     Description("List the deployment targets (machines/agents) registered in a Deployment Group, optionally filtered by tags.")]
    public static Task<string> ListDeploymentTargets(
        AzureDevOpsService svc,
        [Description("Deployment group id")] int deploymentGroupId,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        [Description("Optional comma-separated tag filter (machine must have all tags)")] string? tags = null,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var query = $"{Uri.EscapeDataString(resolved)}/_apis/distributedtask/deploymentgroups/{deploymentGroupId}/targets?{PreviewApi}";
        if (!string.IsNullOrWhiteSpace(tags))
            query += $"&tags={Uri.EscapeDataString(tags)}";
        return GetJson(svc, query, ct);
    }

    private static async Task<string> GetJson(AzureDevOpsService svc, string relativeUrl, CancellationToken ct)
    {
        using var resp = await svc.RestClient.GetAsync(relativeUrl, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Azure DevOps REST call failed: {(int)resp.StatusCode} {resp.ReasonPhrase} for {relativeUrl}\n{body}");
        }
        return body;
    }
}
