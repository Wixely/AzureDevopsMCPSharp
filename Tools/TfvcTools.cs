using System.ComponentModel;
using System.Text.Json;
using AzureDevopsMCPSharp.Services;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using ModelContextProtocol.Server;

namespace AzureDevopsMCPSharp.Tools;

// TFVC (Team Foundation Version Control) — the legacy centralised source control kept around
// when projects are upgraded from older TFS. Paths look like "$/ProjectName/Folder/File.cs".
// Read-only by design: TFVC writes are out of scope for an MCP agent surface.
[McpServerToolType]
public static class TfvcTools
{
    [McpServerTool(Name = "list_tfvc_items"),
     Description("List TFVC items (folders/files) at a server path, e.g. $/MyProject/Source. Use recursionLevel=OneLevel to drill, Full only for small subtrees.")]
    public static async Task<string> ListTfvcItems(
        AzureDevOpsService svc,
        [Description("TFVC server path (defaults to $/{project} when omitted)")] string? path = null,
        [Description("Recursion: None, OneLevel, Full (default OneLevel)")] string recursionLevel = "OneLevel",
        [Description("Project name (used to construct default path; optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        var resolvedProject = svc.ResolveProject(project);
        var scopePath = string.IsNullOrWhiteSpace(path) ? $"$/{resolvedProject}" : path;
        var recursion = ParseRecursion(recursionLevel);

        var client = svc.GetClient<TfvcHttpClient>();
        var items = await client.GetItemsAsync(
            scopePath: scopePath,
            recursionLevel: recursion,
            cancellationToken: ct);

        var summary = items.Select(i => new
        {
            i.Path,
            IsFolder = i.IsFolder,
            i.Size,
            ChangeDate = i.ChangeDate,
            ChangesetVersion = i.ChangesetVersion,
            i.Url,
        });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "get_tfvc_item_content"),
     Description("Fetch the content of a TFVC file. Output is text, truncated to maxBytes (default 200KB) to protect agent context.")]
    public static async Task<string> GetTfvcItemContent(
        AzureDevOpsService svc,
        [Description("Full TFVC server path, e.g. $/MyProject/src/File.cs")] string path,
        [Description("Optional changeset id to read at a specific version")] int? changeset = null,
        [Description("Max bytes to return; remainder is truncated (default 204800)")] int maxBytes = 204800,
        CancellationToken ct = default)
    {
        var client = svc.GetClient<TfvcHttpClient>();
        TfvcVersionDescriptor? versionDescriptor = changeset.HasValue
            ? new TfvcVersionDescriptor
            {
                VersionType = TfvcVersionType.Changeset,
                Version = changeset.Value.ToString(),
            }
            : null;

        await using var stream = await client.GetItemContentAsync(
            path: path,
            versionDescriptor: versionDescriptor,
            cancellationToken: ct);

        using var reader = new StreamReader(stream);
        var buffer = new char[Math.Max(1024, maxBytes)];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maxBytes)), ct);
        var text = new string(buffer, 0, read);
        if (!reader.EndOfStream)
            text += $"\n\n[truncated at {maxBytes} bytes — fetch with a larger maxBytes for more]";
        return text;
    }

    [McpServerTool(Name = "list_tfvc_changesets"),
     Description("List TFVC changesets, optionally filtered by author, path, or id range.")]
    public static async Task<string> ListTfvcChangesets(
        AzureDevOpsService svc,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        [Description("Optional path filter (e.g. $/MyProject/src) — only changesets touching this scope")] string? path = null,
        [Description("Optional author display name or unique name")] string? author = null,
        [Description("Optional minimum changeset id")] int? fromId = null,
        [Description("Optional maximum changeset id")] int? toId = null,
        [Description("Max changesets to return (default 25)")] int top = 25,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<TfvcHttpClient>();

        var criteria = new TfvcChangesetSearchCriteria
        {
            ItemPath = path,
            Author = author,
            FromId = fromId ?? 0,
            ToId = toId ?? 0,
        };

        var changesets = await client.GetChangesetsAsync(
            project: resolved,
            top: top,
            searchCriteria: criteria,
            cancellationToken: ct);

        var summary = changesets.Select(c => new
        {
            c.ChangesetId,
            Author = c.Author?.DisplayName,
            CreatedDate = c.CreatedDate,
            Comment = Truncate(c.Comment, 240),
            c.Url,
        });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "get_tfvc_changeset"),
     Description("Get full details for a single TFVC changeset by id, including comment and check-in metadata.")]
    public static async Task<string> GetTfvcChangeset(
        AzureDevOpsService svc,
        [Description("Changeset id")] int changesetId,
        CancellationToken ct = default)
    {
        var client = svc.GetClient<TfvcHttpClient>();
        var changeset = await client.GetChangesetAsync(changesetId, cancellationToken: ct);
        return JsonSerializer.Serialize(changeset, JsonOpts.Default);
    }

    [McpServerTool(Name = "list_tfvc_changeset_changes"),
     Description("List the file changes (add/edit/delete/rename) made in a TFVC changeset.")]
    public static async Task<string> ListTfvcChangesetChanges(
        AzureDevOpsService svc,
        [Description("Changeset id")] int changesetId,
        [Description("Max changes to return (default 200)")] int top = 200,
        CancellationToken ct = default)
    {
        var client = svc.GetClient<TfvcHttpClient>();
        var changes = await client.GetChangesetChangesAsync(id: changesetId, top: top, cancellationToken: ct);
        var summary = changes.Select(c => new
        {
            ChangeType = c.ChangeType.ToString(),
            Path = c.Item?.Path,
            IsFolder = c.Item?.IsFolder,
            Version = c.Item?.ChangesetVersion,
        });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "list_tfvc_branches"),
     Description("List TFVC branches in a project (only relevant when the project uses TFVC branching).")]
    public static async Task<string> ListTfvcBranches(
        AzureDevOpsService svc,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        [Description("Optional path scope, e.g. $/MyProject")] string? path = null,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<TfvcHttpClient>();
        var scope = string.IsNullOrWhiteSpace(path) ? $"$/{resolved}" : path;
        var branches = await client.GetBranchesAsync(
            includeParent: true,
            includeChildren: true,
            cancellationToken: ct);

        var filtered = branches.Where(b => b.Path != null && b.Path.StartsWith(scope, StringComparison.OrdinalIgnoreCase));
        var summary = filtered.Select(b => new
        {
            b.Path,
            Owner = b.Owner?.DisplayName,
            CreatedDate = b.CreatedDate,
            ParentPath = b.Parent?.Path,
            ChildCount = b.Children?.Count ?? 0,
            b.Description,
        });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    private static VersionControlRecursionType ParseRecursion(string value) => value?.ToLowerInvariant() switch
    {
        "none" => VersionControlRecursionType.None,
        "full" => VersionControlRecursionType.Full,
        "onelevel" or null or "" => VersionControlRecursionType.OneLevel,
        _ => VersionControlRecursionType.OneLevel,
    };

    private static string? Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}
