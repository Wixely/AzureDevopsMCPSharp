using System.ComponentModel;
using System.Text.Json;
using AzureDevopsMCPSharp.Services;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.WebApi.Patch;
using Microsoft.VisualStudio.Services.WebApi.Patch.Json;
using ModelContextProtocol.Server;

namespace AzureDevopsMCPSharp.Tools;

[McpServerToolType]
public static class WorkItemTools
{
    [McpServerTool(Name = "get_work_item"),
     Description("Get a work item by id, including all fields and (optionally) relations.")]
    public static async Task<string> GetWorkItem(
        AzureDevOpsService svc,
        [Description("Work item id")] int id,
        [Description("Include relations (links, attachments)")] bool includeRelations = false,
        CancellationToken ct = default)
    {
        var client = svc.GetClient<WorkItemTrackingHttpClient>();
        var expand = includeRelations ? WorkItemExpand.Relations : WorkItemExpand.Fields;
        var wi = await client.GetWorkItemAsync(id, expand: expand, cancellationToken: ct);
        return JsonSerializer.Serialize(wi, JsonOpts.Default);
    }

    [McpServerTool(Name = "query_work_items"),
     Description("Run a WIQL query against the configured project and return matching work items.")]
    public static async Task<string> QueryWorkItems(
        AzureDevOpsService svc,
        [Description("WIQL query, e.g. SELECT [System.Id] FROM WorkItems WHERE [System.State]='Active'")] string wiql,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        [Description("Max items to return (default 50)")] int top = 50,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<WorkItemTrackingHttpClient>();
        var result = await client.QueryByWiqlAsync(new Wiql { Query = wiql }, resolved, top: top, cancellationToken: ct);
        var ids = result.WorkItems.Select(r => r.Id).ToArray();
        if (ids.Length == 0)
            return JsonSerializer.Serialize(Array.Empty<object>(), JsonOpts.Default);

        var items = await client.GetWorkItemsAsync(ids, expand: WorkItemExpand.Fields, cancellationToken: ct);
        return JsonSerializer.Serialize(items, JsonOpts.Default);
    }

    [McpServerTool(Name = "create_work_item"),
     Description("Create a new work item. Disabled when the server is in read-only mode.")]
    public static async Task<string> CreateWorkItem(
        AzureDevOpsService svc,
        [Description("Work item type, e.g. 'Bug', 'Task', 'User Story'")] string type,
        [Description("Title of the work item")] string title,
        [Description("Optional description / repro steps")] string? description = null,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("create_work_item");
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<WorkItemTrackingHttpClient>();

        var doc = new JsonPatchDocument
        {
            new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/System.Title", Value = title }
        };
        if (!string.IsNullOrWhiteSpace(description))
            doc.Add(new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/System.Description", Value = description });

        var wi = await client.CreateWorkItemAsync(doc, resolved, type, cancellationToken: ct);
        return JsonSerializer.Serialize(wi, JsonOpts.Default);
    }

    [McpServerTool(Name = "update_work_item"),
     Description("Patch fields on an existing work item. Disabled when the server is in read-only mode.")]
    public static async Task<string> UpdateWorkItem(
        AzureDevOpsService svc,
        [Description("Work item id")] int id,
        [Description("Field/value pairs, e.g. { \"System.State\": \"Active\" }")] Dictionary<string, string> fields,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("update_work_item");
        var client = svc.GetClient<WorkItemTrackingHttpClient>();
        var doc = new JsonPatchDocument();
        foreach (var (k, v) in fields)
            doc.Add(new JsonPatchOperation { Operation = Operation.Replace, Path = $"/fields/{k}", Value = v });

        var wi = await client.UpdateWorkItemAsync(doc, id, cancellationToken: ct);
        return JsonSerializer.Serialize(wi, JsonOpts.Default);
    }
}
