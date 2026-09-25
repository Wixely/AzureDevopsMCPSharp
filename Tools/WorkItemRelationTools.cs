using System.ComponentModel;
using System.Text.Json;
using AzureDevopsMCPSharp.Services;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.WebApi.Patch;
using Microsoft.VisualStudio.Services.WebApi.Patch.Json;
using ModelContextProtocol.Server;

namespace AzureDevopsMCPSharp.Tools;

/// <summary>
/// Work item <em>relations</em> — links to other work items, pull requests, branches, commits,
/// builds, hyperlinks and file attachments. Fields are handled by <see cref="WorkItemTools"/>.
/// </summary>
[McpServerToolType]
public static class WorkItemRelationTools
{
    /// <summary>What a shorthand relation kind points at, i.e. which parameter supplies the target.</summary>
    private enum TargetKind { WorkItem, PullRequest, Branch, Commit, Build, Url }

    private readonly record struct RelationKind(string Rel, string? ArtifactName, TargetKind Target);

    /// <summary>
    /// Friendly names for the link types callers actually reach for, mapped to the reference name
    /// the REST API wants. ArtifactLink entries also carry the "name" attribute Azure DevOps uses
    /// to decide how to render the link ("Pull Request", "Branch", …) — without it the link shows
    /// up as an unlabelled artifact.
    /// </summary>
    private static readonly Dictionary<string, RelationKind> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["related"] = new("System.LinkTypes.Related", null, TargetKind.WorkItem),
        ["parent"] = new("System.LinkTypes.Hierarchy-Reverse", null, TargetKind.WorkItem),
        ["child"] = new("System.LinkTypes.Hierarchy-Forward", null, TargetKind.WorkItem),
        ["duplicate"] = new("System.LinkTypes.Duplicate-Forward", null, TargetKind.WorkItem),
        ["duplicate-of"] = new("System.LinkTypes.Duplicate-Reverse", null, TargetKind.WorkItem),
        ["successor"] = new("System.LinkTypes.Dependency-Forward", null, TargetKind.WorkItem),
        ["predecessor"] = new("System.LinkTypes.Dependency-Reverse", null, TargetKind.WorkItem),
        ["tested-by"] = new("Microsoft.VSTS.Common.TestedBy-Forward", null, TargetKind.WorkItem),
        ["tests"] = new("Microsoft.VSTS.Common.TestedBy-Reverse", null, TargetKind.WorkItem),
        ["pull-request"] = new("ArtifactLink", "Pull Request", TargetKind.PullRequest),
        ["branch"] = new("ArtifactLink", "Branch", TargetKind.Branch),
        ["commit"] = new("ArtifactLink", "Fixed in Commit", TargetKind.Commit),
        ["build"] = new("ArtifactLink", "Build", TargetKind.Build),
        ["found-in-build"] = new("ArtifactLink", "Found in build", TargetKind.Build),
        ["integrated-in-build"] = new("ArtifactLink", "Integrated in build", TargetKind.Build),
        ["hyperlink"] = new("Hyperlink", null, TargetKind.Url),
    };

    private const string KnownKinds =
        "related, parent, child, duplicate, duplicate-of, successor, predecessor, tested-by, tests, " +
        "pull-request, branch, commit, build, found-in-build, integrated-in-build, hyperlink";

    [McpServerTool(Name = "azdo_list_work_item_relations"),
     Description("List a work item's relations with their array index — links to other work items, " +
                 "pull requests, branches, commits, builds, hyperlinks and attachments. " +
                 "The index is what azdo_remove_work_item_relation removes by.")]
    public static async Task<string> ListWorkItemRelations(
        AzureDevOpsService svc,
        [Description("Work item id")] int id,
        CancellationToken ct = default)
    {
        var client = svc.GetClient<WorkItemTrackingHttpClient>();
        var wi = await client.GetWorkItemAsync(id, expand: WorkItemExpand.Relations, cancellationToken: ct);
        return JsonSerializer.Serialize(new
        {
            id,
            rev = wi.Rev,
            relations = Describe(wi.Relations),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_add_work_item_relation"),
     Description("Link a work item to another work item, a pull request, a branch, a commit, a build, " +
                 "or an arbitrary URL. Pass 'relation' as one of: " +
                 "related, parent, child, duplicate, duplicate-of, successor, predecessor, tested-by, tests " +
                 "(these take targetWorkItemId); pull-request (pullRequestId + repository); branch (branch + repository); " +
                 "commit (commitSha + repository); build / found-in-build / integrated-in-build (buildId); " +
                 "hyperlink (url). A raw link type reference name such as 'System.LinkTypes.Related' also works. " +
                 "Re-adding an existing link is a no-op. Disabled when the server is in read-only mode.")]
    public static async Task<string> AddWorkItemRelation(
        AzureDevOpsService svc,
        [Description("Work item id to add the link to")] int id,
        [Description("Relation kind, e.g. 'related', 'parent', 'pull-request', 'branch', 'commit', 'hyperlink'")] string relation,
        [Description("Target work item id, for work-item link kinds")] int? targetWorkItemId = null,
        [Description("Pull request id, for relation='pull-request'")] int? pullRequestId = null,
        [Description("Branch name, e.g. 'feature/x', for relation='branch'")] string? branch = null,
        [Description("Commit SHA, for relation='commit'")] string? commitSha = null,
        [Description("Build id, for the build relation kinds")] int? buildId = null,
        [Description("Repository id or name — required for the pull-request, branch and commit kinds")] string? repository = null,
        [Description("Explicit link URL / vstfs artifact URI. Required for 'hyperlink'; overrides the target parameters for any other kind.")] string? url = null,
        [Description("Display name for a raw ArtifactLink, e.g. 'Pull Request'. Ignored when the shorthand already implies one.")] string? artifactName = null,
        [Description("Optional comment stored on the link")] string? comment = null,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("add_work_item_relation");
        if (string.IsNullOrWhiteSpace(relation))
            throw new ArgumentException("relation is required.", nameof(relation));

        var kind = ResolveKind(relation);
        var targetUrl = string.IsNullOrWhiteSpace(url)
            ? await BuildTargetUrlAsync(svc, kind, targetWorkItemId, pullRequestId, branch, commitSha, buildId, repository, project, ct)
            : url.Trim();

        var attributes = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var name = kind.ArtifactName ?? (kind.Rel == "ArtifactLink" ? artifactName : null);
        if (!string.IsNullOrWhiteSpace(name))
            attributes["name"] = name!;
        if (!string.IsNullOrWhiteSpace(comment))
            attributes["comment"] = TextUtil.NormalizeNewlines(comment)!;

        var client = svc.GetClient<WorkItemTrackingHttpClient>();
        return await AddRelationAsync(client, id, kind.Rel, targetUrl, attributes, ct);
    }

    [McpServerTool(Name = "azdo_remove_work_item_relation"),
     Description("Remove a relation from a work item, by its index from azdo_list_work_item_relations " +
                 "or by its exact url. Disabled when the server is in read-only mode.")]
    public static async Task<string> RemoveWorkItemRelation(
        AzureDevOpsService svc,
        [Description("Work item id")] int id,
        [Description("Relation index from azdo_list_work_item_relations")] int? index = null,
        [Description("Relation url to remove, as an alternative to index. Must match exactly one relation.")] string? url = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("remove_work_item_relation");
        if (index is null && string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Pass either index or url.", nameof(index));

        var client = svc.GetClient<WorkItemTrackingHttpClient>();
        var wi = await client.GetWorkItemAsync(id, expand: WorkItemExpand.Relations, cancellationToken: ct);
        var relations = wi.Relations ?? new List<WorkItemRelation>();

        int target;
        if (index is not null)
        {
            if (index < 0 || index >= relations.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index),
                    $"Work item {id} has {relations.Count} relation(s); index {index} is out of range.");
            }
            target = index.Value;
        }
        else
        {
            var matches = relations
                .Select((r, i) => (Relation: r, Index: i))
                .Where(x => string.Equals(x.Relation.Url, url!.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0)
                throw new InvalidOperationException($"Work item {id} has no relation with url '{url}'.");
            if (matches.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Work item {id} has {matches.Count} relations with url '{url}' " +
                    $"(indexes {string.Join(", ", matches.Select(m => m.Index))}). Remove by index instead.");
            }
            target = matches[0].Index;
        }

        var removed = relations[target];
        var doc = new JsonPatchDocument
        {
            // Guard against the work item having changed since the index was read: indexes shift
            // when someone else edits relations, and a stale index would remove the wrong link.
            new JsonPatchOperation { Operation = Operation.Test, Path = "/rev", Value = wi.Rev },
            new JsonPatchOperation { Operation = Operation.Remove, Path = $"/relations/{target}" },
        };

        var updated = await client.UpdateWorkItemAsync(
            doc, id, expand: WorkItemExpand.Relations, cancellationToken: ct);
        return JsonSerializer.Serialize(new
        {
            id,
            rev = updated.Rev,
            removed = new { index = target, rel = removed.Rel, url = removed.Url },
            relations = Describe(updated.Relations),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_add_work_item_attachment"),
     Description("Upload a file and attach it to a work item. Content is base64. " +
                 "Disabled when the server is in read-only mode.")]
    public static async Task<string> AddWorkItemAttachment(
        AzureDevOpsService svc,
        [Description("Work item id to attach the file to")] int id,
        [Description("File name, e.g. 'approval.pdf'")] string fileName,
        [Description("File content as base64")] string contentBase64,
        [Description("Optional comment stored on the attachment link")] string? comment = null,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("add_work_item_attachment");
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("fileName is required.", nameof(fileName));

        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<WorkItemTrackingHttpClient>();

        byte[] bytes;
        try { bytes = Convert.FromBase64String(contentBase64); }
        catch (FormatException) { throw new InvalidOperationException("contentBase64 is not valid base64."); }

        using var stream = new MemoryStream(bytes);
        var attachment = await client.CreateAttachmentAsync(
            stream, resolved, fileName, uploadType: null, areaPath: null, cancellationToken: ct);

        var attributes = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = fileName,
        };
        if (!string.IsNullOrWhiteSpace(comment))
            attributes["comment"] = TextUtil.NormalizeNewlines(comment)!;

        return await AddRelationAsync(
            client, id, "AttachedFile", attachment.Url, attributes, ct,
            extra: new { attachmentId = attachment.Id, fileName, bytes = bytes.Length });
    }

    /// <summary>
    /// Appends one relation, short-circuiting when the same rel+url is already present — Azure DevOps
    /// rejects duplicate relations outright, and a retrying caller should see success, not an error.
    /// </summary>
    private static async Task<string> AddRelationAsync(
        WorkItemTrackingHttpClient client,
        int id,
        string rel,
        string url,
        Dictionary<string, object> attributes,
        CancellationToken ct,
        object? extra = null)
    {
        var current = await client.GetWorkItemAsync(id, expand: WorkItemExpand.Relations, cancellationToken: ct);
        var existing = current.Relations?.FirstOrDefault(r =>
            string.Equals(r.Rel, rel, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Url, url, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return JsonSerializer.Serialize(new
            {
                id,
                rev = current.Rev,
                unchanged = true,
                message = $"Work item {id} is already linked to '{url}' via '{rel}'.",
                relations = Describe(current.Relations),
                extra,
            }, JsonOpts.Default);
        }

        var doc = new JsonPatchDocument
        {
            new JsonPatchOperation
            {
                Operation = Operation.Add,
                Path = "/relations/-",
                Value = new WorkItemRelation
                {
                    Rel = rel,
                    Url = url,
                    Attributes = attributes.Count > 0 ? attributes : null,
                },
            },
        };

        var updated = await client.UpdateWorkItemAsync(
            doc, id, expand: WorkItemExpand.Relations, cancellationToken: ct);
        return JsonSerializer.Serialize(new
        {
            id,
            rev = updated.Rev,
            added = new { rel, url, attributes },
            relations = Describe(updated.Relations),
            extra,
        }, JsonOpts.Default);
    }

    private static object[] Describe(IList<WorkItemRelation>? relations)
        => (relations ?? new List<WorkItemRelation>())
            .Select((r, i) => (object)new
            {
                index = i,
                rel = r.Rel,
                url = r.Url,
                name = r.Attributes is not null && r.Attributes.TryGetValue("name", out var n) ? n?.ToString() : null,
                comment = r.Attributes is not null && r.Attributes.TryGetValue("comment", out var c) ? c?.ToString() : null,
                targetWorkItemId = TryGetWorkItemId(r.Url),
            })
            .ToArray();

    /// <summary>Pulls the trailing id out of a .../_apis/wit/workItems/{id} link so callers can chase it.</summary>
    private static int? TryGetWorkItemId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            url.IndexOf("/_apis/wit/workItems/", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return null;
        }
        var tail = url[(url.LastIndexOf('/') + 1)..];
        return int.TryParse(tail, out var id) ? id : null;
    }

    private static RelationKind ResolveKind(string relation)
    {
        var key = relation.Trim().Replace('_', '-').Replace(" ", "");
        if (Kinds.TryGetValue(key, out var kind))
            return kind;

        // Not a shorthand — accept a raw link type reference name so uncommon and custom link
        // types stay reachable without waiting for a shorthand to be added here.
        var raw = relation.Trim();
        if (raw.Equals("ArtifactLink", StringComparison.OrdinalIgnoreCase))
            return new RelationKind("ArtifactLink", null, TargetKind.Url);
        if (raw.Equals("Hyperlink", StringComparison.OrdinalIgnoreCase))
            return new RelationKind("Hyperlink", null, TargetKind.Url);
        if (raw.Equals("AttachedFile", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use azdo_add_work_item_attachment to attach a file.", nameof(relation));
        if (raw.Contains('.') && raw.Contains('-'))
            return new RelationKind(raw, null, TargetKind.WorkItem);

        throw new ArgumentException(
            $"Unknown relation '{relation}'. Use one of: {KnownKinds} — " +
            "or a raw link type reference name such as 'System.LinkTypes.Related'.",
            nameof(relation));
    }

    /// <summary>
    /// Turns the target parameters into the link url. Every missing-parameter check runs before the
    /// first client call, so a malformed request fails with the parameter it is missing rather than
    /// with whatever the connection complains about first.
    /// </summary>
    private static async Task<string> BuildTargetUrlAsync(
        AzureDevOpsService svc,
        RelationKind kind,
        int? targetWorkItemId,
        int? pullRequestId,
        string? branch,
        string? commitSha,
        int? buildId,
        string? repository,
        string? project,
        CancellationToken ct)
    {
        switch (kind.Target)
        {
            case TargetKind.WorkItem:
            {
                if (targetWorkItemId is null)
                    throw new ArgumentException($"relation '{kind.Rel}' links two work items — targetWorkItemId is required.");
                // Read the target back rather than composing the URL: it validates the id exists and
                // yields the exact self link this collection uses.
                var target = await svc.GetClient<WorkItemTrackingHttpClient>()
                    .GetWorkItemAsync(targetWorkItemId.Value, cancellationToken: ct);
                if (string.IsNullOrWhiteSpace(target.Url))
                    throw new InvalidOperationException($"Work item {targetWorkItemId} returned no url to link to.");
                return target.Url;
            }
            case TargetKind.PullRequest:
            {
                if (pullRequestId is null)
                    throw new ArgumentException("relation 'pull-request' requires pullRequestId.");
                var (projectId, repoId) = await ResolveRepoAsync(svc, repository, project, "pull-request", ct);
                return ArtifactUri("PullRequestId", projectId, repoId, pullRequestId.Value.ToString());
            }
            case TargetKind.Branch:
            {
                if (string.IsNullOrWhiteSpace(branch))
                    throw new ArgumentException("relation 'branch' requires branch.");
                var (projectId, repoId) = await ResolveRepoAsync(svc, repository, project, "branch", ct);
                var name = branch.Trim();
                if (name.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase))
                    name = name["refs/heads/".Length..];
                return ArtifactUri("Ref", projectId, repoId, "GB" + name);
            }
            case TargetKind.Commit:
            {
                if (string.IsNullOrWhiteSpace(commitSha))
                    throw new ArgumentException("relation 'commit' requires commitSha.");
                var (projectId, repoId) = await ResolveRepoAsync(svc, repository, project, "commit", ct);
                return ArtifactUri("Commit", projectId, repoId, commitSha.Trim());
            }
            case TargetKind.Build:
            {
                if (buildId is null)
                    throw new ArgumentException($"relation for '{kind.ArtifactName}' requires buildId.");
                return $"vstfs:///Build/Build/{buildId}";
            }
            default:
                throw new ArgumentException($"relation '{kind.Rel}' needs an explicit url.");
        }
    }

    private static async Task<(string ProjectId, string RepoId)> ResolveRepoAsync(
        AzureDevOpsService svc, string? repository, string? project, string kind, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repository))
            throw new ArgumentException($"relation '{kind}' requires repository (id or name).");
        var resolved = svc.ResolveProject(project);
        var git = svc.GetClient<GitHttpClient>();
        var repo = await git.GetRepositoryAsync(resolved, repository, cancellationToken: ct);
        var projectId = repo.ProjectReference?.Id ?? Guid.Empty;
        if (projectId == Guid.Empty)
            throw new InvalidOperationException($"Repository '{repository}' returned no project id to build an artifact link with.");
        return (projectId.ToString(), repo.Id.ToString());
    }

    /// <summary>
    /// Builds a Git artifact URI. Azure DevOps encodes the whole "{project}/{repo}/{item}" tail as a
    /// single segment, so the separators arrive as %2F — and so do any slashes inside a branch name.
    /// </summary>
    private static string ArtifactUri(string type, params string[] parts)
        => $"vstfs:///Git/{type}/{Uri.EscapeDataString(string.Join("/", parts))}";
}
