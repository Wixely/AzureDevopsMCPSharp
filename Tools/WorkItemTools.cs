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
    [McpServerTool(Name = "azdo_get_work_item"),
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

    [McpServerTool(Name = "azdo_query_work_items"),
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

    [McpServerTool(Name = "azdo_create_work_item"),
     Description("Create a new work item. Use 'fields' for anything beyond title/description — " +
                 "area/iteration paths and any custom fields the work item type requires. " +
                 "Call azdo_get_work_item_type_fields first to discover which fields are required " +
                 "and what values they accept. Disabled when the server is in read-only mode.")]
    public static async Task<string> CreateWorkItem(
        AzureDevOpsService svc,
        [Description("Work item type, e.g. 'Bug', 'Task', 'User Story'")] string type,
        [Description("Title of the work item")] string title,
        [Description("Optional description / repro steps")] string? description = null,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        [Description("Additional field/value pairs keyed by field reference name. Takes precedence over title/description if it repeats them.")] Dictionary<string, string>? fields = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("create_work_item");
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<WorkItemTrackingHttpClient>();

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["System.Title"] = title
        };
        if (!string.IsNullOrWhiteSpace(description))
            values["System.Description"] = description;
        if (fields is not null)
        {
            foreach (var (k, v) in fields)
            {
                if (string.IsNullOrWhiteSpace(k))
                    continue;
                values[k.Trim()] = v;
            }
        }

        var doc = new JsonPatchDocument();
        foreach (var (k, v) in values)
            doc.Add(new JsonPatchOperation { Operation = Operation.Add, Path = $"/fields/{k}", Value = v });

        var wi = await client.CreateWorkItemAsync(doc, resolved, type, cancellationToken: ct);
        return JsonSerializer.Serialize(wi, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_update_work_item"),
     Description("Patch fields on an existing work item. Disabled when the server is in read-only mode.")]
    public static async Task<string> UpdateWorkItem(
        AzureDevOpsService svc,
        [Description("Work item id")] int id,
        [Description("Field/value pairs keyed by field reference name, e.g. { \"System.State\": \"Active\" }")] Dictionary<string, string> fields,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("update_work_item");
        var client = svc.GetClient<WorkItemTrackingHttpClient>();
        var doc = new JsonPatchDocument();
        foreach (var (k, v) in fields)
        {
            if (string.IsNullOrWhiteSpace(k))
                continue;
            // "Add" upserts: "Replace" fails on a field the work item does not already carry.
            doc.Add(new JsonPatchOperation { Operation = Operation.Add, Path = $"/fields/{k.Trim()}", Value = v });
        }

        var wi = await client.UpdateWorkItemAsync(doc, id, cancellationToken: ct);
        return JsonSerializer.Serialize(wi, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_list_work_item_types"),
     Description("List the work item types available in a project, so a caller can discover what can be created.")]
    public static async Task<string> ListWorkItemTypes(
        AzureDevOpsService svc,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<WorkItemTrackingHttpClient>();
        var types = await client.GetWorkItemTypesAsync(resolved, cancellationToken: ct);
        var summary = types.Select(t => new
        {
            t.Name,
            t.ReferenceName,
            t.Description
        });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_get_work_item_type_fields"),
     Description("List every field on a work item type: reference name, whether it is always required, " +
                 "its default, help text, and the allowed values for picklist fields. " +
                 "Use this before azdo_create_work_item to build a valid 'fields' payload for any work item type.")]
    public static async Task<string> GetWorkItemTypeFields(
        AzureDevOpsService svc,
        [Description("Work item type name, e.g. 'Bug', 'Task', 'User Story'")] string type,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        [Description("Return only the fields the type always requires")] bool requiredOnly = false,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<WorkItemTrackingHttpClient>();
        var fields = await client.GetWorkItemTypeFieldsWithReferencesAsync(
            resolved, type, WorkItemTypeFieldsExpandLevel.AllowedValues, cancellationToken: ct);

        var selected = requiredOnly ? fields.Where(f => f.AlwaysRequired) : fields;
        var summary = selected.Select(f => new
        {
            f.ReferenceName,
            f.Name,
            f.AlwaysRequired,
            f.DefaultValue,
            f.HelpText,
            AllowedValues = f.AllowedValues is { Length: > 0 } ? f.AllowedValues : null
        });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }
}
