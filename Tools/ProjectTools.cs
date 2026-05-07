using System.ComponentModel;
using System.Text.Json;
using AzureDevopsMCPSharp.Services;
using Microsoft.TeamFoundation.Core.WebApi;
using ModelContextProtocol.Server;

namespace AzureDevopsMCPSharp.Tools;

[McpServerToolType]
public static class ProjectTools
{
    [McpServerTool(Name = "list_projects"),
     Description("List Team Projects in the configured Azure DevOps collection.")]
    public static async Task<string> ListProjects(AzureDevOpsService svc, CancellationToken ct)
    {
        var client = svc.GetClient<ProjectHttpClient>();
        var projects = await client.GetProjects();
        var summary = projects.Select(p => new { p.Id, p.Name, p.Description, State = p.State.ToString() });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "get_project"),
     Description("Get details for a single Team Project by name or id.")]
    public static async Task<string> GetProject(
        AzureDevOpsService svc,
        [Description("Project name or id")] string project,
        CancellationToken ct)
    {
        var client = svc.GetClient<ProjectHttpClient>();
        var p = await client.GetProject(project, includeCapabilities: true);
        return JsonSerializer.Serialize(p, JsonOpts.Default);
    }
}
