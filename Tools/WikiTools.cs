using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json;
using AzureDevopsMCPSharp.Services;
using Microsoft.Azure.DevOps.Comments.WebApi;
using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.TeamFoundation.Wiki.WebApi;
using Microsoft.VisualStudio.Services.Common;
using ModelContextProtocol.Server;

namespace AzureDevopsMCPSharp.Tools;

[McpServerToolType]
public static class WikiTools
{
    [McpServerTool(Name = "azdo_list_wikis"),
     Description("List the wikis in a project (project wikis and code wikis).")]
    public static async Task<string> ListWikis(
        AzureDevOpsService svc,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var wiki = svc.GetClient<WikiHttpClient>();
        var wikis = await wiki.GetAllWikisAsync(project: resolved, cancellationToken: ct);
        var result = wikis.Select(w => new
        {
            w.Id,
            w.Name,
            Type = w.Type.ToString(),
            w.MappedPath,
            w.ProjectId,
            w.RepositoryId,
            w.RemoteUrl,
            w.Url,
        });
        return JsonSerializer.Serialize(result, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_list_wiki_pages"),
     Description("List the page tree of a wiki (paths only, no content). Walks sub-pages recursively from the given path.")]
    public static async Task<string> ListWikiPages(
        AzureDevOpsService svc,
        [Description("Wiki name or GUID")] string wikiIdentifier,
        [Description("Root path to list from (default '/')")] string path = "/",
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var wiki = svc.GetClient<WikiHttpClient>();
        var response = await wiki.GetPageAsync(
            project: resolved,
            wikiIdentifier: wikiIdentifier,
            path: path,
            recursionLevel: VersionControlRecursionType.Full,
            includeContent: false,
            cancellationToken: ct);

        var pages = new List<object>();
        Flatten(response.Page, pages);
        return JsonSerializer.Serialize(pages, JsonOpts.Default);
    }

    private static void Flatten(WikiPage page, List<object> into)
    {
        if (page is null) return;
        var subPages = page.SubPages?.ToList();
        // The response root echoes the queried path; include every node that names a page.
        if (page.Path is not null)
        {
            into.Add(new
            {
                page.Path,
                page.Order,
                IsParentPage = subPages is { Count: > 0 },
                page.GitItemPath,
            });
        }
        if (subPages is null) return;
        foreach (var sub in subPages)
            Flatten(sub, into);
    }

    [McpServerTool(Name = "azdo_get_wiki_page"),
     Description("Get one wiki page's Markdown content and its ETag (needed to edit it). Large content is truncated.")]
    public static async Task<string> GetWikiPage(
        AzureDevOpsService svc,
        [Description("Wiki name or GUID")] string wikiIdentifier,
        [Description("Page path, e.g. '/Home' or '/Folder/Page'")] string path,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var wiki = svc.GetClient<WikiHttpClient>();
        var response = await wiki.GetPageAsync(
            project: resolved,
            wikiIdentifier: wikiIdentifier,
            path: path,
            includeContent: true,
            cancellationToken: ct);

        var content = response.Page?.Content ?? string.Empty;
        var truncated = false;
        if (content.Length > 50_000) { content = content[..50_000]; truncated = true; }

        return JsonSerializer.Serialize(new
        {
            Id = response.Page?.Id,          // needed to address comments / view-stats
            Path = response.Page?.Path,
            Content = content,
            Truncated = truncated,
            ETag = response.ETag?.FirstOrDefault(),
            GitItemPath = response.Page?.GitItemPath,
            RemoteUrl = response.Page?.RemoteUrl,
            Order = response.Page?.Order,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_create_or_update_wiki_page"),
     Description("Create a new project-wiki page, or edit an existing one. Editing requires the page's current ETag; " +
                 "if omitted it is probed automatically. Disabled unless writes are enabled. Project wikis only.")]
    public static async Task<string> CreateOrUpdateWikiPage(
        AzureDevOpsService svc,
        [Description("Wiki name or GUID")] string wikiIdentifier,
        [Description("Page path, e.g. '/Home' or '/Folder/Page'")] string path,
        [Description("Page content (Markdown)")] string content,
        [Description("Current ETag of the page for an update (from azdo_get_wiki_page). Omit to create, or to auto-probe.")] string? etag = null,
        [Description("Optional revision comment")] string? comment = null,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("create_or_update_wiki_page");
        var resolved = svc.ResolveProject(project);
        var wiki = svc.GetClient<WikiHttpClient>();

        string? version = etag;                 // If-Match for an update; null to create
        if (string.IsNullOrEmpty(version))
        {
            // No etag supplied: if the page already exists we must send its current etag or the
            // API rejects the update. Probe for it; treat "not found" as a create.
            try
            {
                var existing = await wiki.GetPageAsync(
                    project: resolved,
                    wikiIdentifier: wikiIdentifier,
                    path: path,
                    includeContent: false,
                    cancellationToken: ct);
                version = existing.ETag?.FirstOrDefault();
            }
            catch (VssServiceException) { /* page doesn't exist -> create (version stays null) */ }
        }

        var parameters = new WikiPageCreateOrUpdateParameters { Content = TextUtil.NormalizeNewlines(content) };

        WikiPageResponse result;
        try
        {
            result = await wiki.CreateOrUpdatePageAsync(
                parameters,
                resolved,
                wikiIdentifier,
                path,
                Version: version,                 // positional 'Version' param == If-Match ETag
                comment: comment,
                cancellationToken: ct);
        }
        catch (VssServiceException ex) when (IsPreconditionFailed(ex))
        {
            throw new InvalidOperationException(
                $"Wiki page '{path}' changed on the server since it was read. " +
                "Re-read it with azdo_get_wiki_page and retry azdo_create_or_update_wiki_page with the new etag.", ex);
        }

        return JsonSerializer.Serialize(new
        {
            Path = result.Page?.Path,
            ETag = result.ETag?.FirstOrDefault(),
            GitItemPath = result.Page?.GitItemPath,
            RemoteUrl = result.Page?.RemoteUrl,
            Created = version is null,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_delete_wiki_page"),
     Description("Delete a wiki page. Disabled unless writes are enabled. Project wikis only.")]
    public static async Task<string> DeleteWikiPage(
        AzureDevOpsService svc,
        [Description("Wiki name or GUID")] string wikiIdentifier,
        [Description("Page path, e.g. '/Folder/Page'")] string path,
        [Description("Optional revision comment")] string? comment = null,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("delete_wiki_page");
        var resolved = svc.ResolveProject(project);
        var wiki = svc.GetClient<WikiHttpClient>();
        await wiki.DeletePageAsync(
            project: resolved,
            wikiIdentifier: wikiIdentifier,
            path: path,
            comment: comment,
            cancellationToken: ct);
        return JsonSerializer.Serialize(new { path, deleted = true }, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_get_wiki"),
     Description("Get a single wiki by name or GUID.")]
    public static async Task<string> GetWiki(
        AzureDevOpsService svc,
        [Description("Wiki name or GUID")] string wikiIdentifier,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var wiki = svc.GetClient<WikiHttpClient>();
        var w = await wiki.GetWikiAsync(project: resolved, wikiIdentifier: wikiIdentifier, cancellationToken: ct);
        return JsonSerializer.Serialize(new
        {
            w.Id, w.Name, Type = w.Type.ToString(), w.MappedPath,
            w.ProjectId, w.RepositoryId, w.RemoteUrl, w.Url,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_move_wiki_page"),
     Description("Move or rename a wiki page (and its sub-pages) to a new path. Disabled unless writes are enabled. Project wikis only.")]
    public static async Task<string> MoveWikiPage(
        AzureDevOpsService svc,
        [Description("Wiki name or GUID")] string wikiIdentifier,
        [Description("Current page path, e.g. '/Old/Name'")] string path,
        [Description("New page path, e.g. '/New/Name'")] string newPath,
        [Description("Optional new sort order among siblings")] int? newOrder = null,
        [Description("Optional revision comment")] string? comment = null,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("move_wiki_page");
        var resolved = svc.ResolveProject(project);
        var wiki = svc.GetClient<WikiHttpClient>();
        var parameters = new WikiPageMoveParameters { Path = path, NewPath = newPath };
        if (newOrder is not null) parameters.NewOrder = newOrder.Value;

        WikiPageMoveResponse result;
        try
        {
            result = await wiki.CreatePageMoveAsync(
                parameters, resolved, wikiIdentifier, comment: comment, cancellationToken: ct);
        }
        catch (VssServiceException ex) when (IsPreconditionFailed(ex))
        {
            throw new InvalidOperationException(
                $"Wiki page '{path}' changed on the server since it was read. Re-read it with azdo_get_wiki_page and retry.", ex);
        }

        return JsonSerializer.Serialize(new
        {
            OldPath = path,
            NewPath = result.PageMove?.Page?.Path ?? newPath,
            ETag = result.ETag?.FirstOrDefault(),
            moved = true,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_create_wiki"),
     Description("Create a wiki in a project. type='projectWiki' provisions the built-in project wiki; " +
                 "type='codeWiki' publishes a Git repo folder as a wiki (requires repositoryId, mappedPath, version). " +
                 "Disabled unless writes are enabled.")]
    public static async Task<string> CreateWiki(
        AzureDevOpsService svc,
        [Description("Wiki name")] string name,
        [Description("Wiki type: 'projectWiki' or 'codeWiki'")] string type = "projectWiki",
        [Description("For codeWiki: the backing Git repository id (GUID)")] string? repositoryId = null,
        [Description("For codeWiki: the folder path in the repo to publish, e.g. '/docs'")] string? mappedPath = null,
        [Description("For codeWiki: the branch to publish from, e.g. 'main'")] string? version = null,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("create_wiki");
        var resolved = svc.ResolveProject(project);

        var projects = svc.GetClient<ProjectHttpClient>();
        var proj = await projects.GetProject(resolved);

        var wikiType = type.Equals("codeWiki", StringComparison.OrdinalIgnoreCase)
            ? WikiType.CodeWiki
            : WikiType.ProjectWiki;

        var parameters = new WikiCreateParametersV2
        {
            Name = name,
            ProjectId = proj.Id,
            Type = wikiType,
        };
        if (wikiType == WikiType.CodeWiki)
        {
            if (string.IsNullOrWhiteSpace(repositoryId) || string.IsNullOrWhiteSpace(mappedPath) || string.IsNullOrWhiteSpace(version))
                throw new InvalidOperationException("codeWiki requires repositoryId, mappedPath and version (branch).");
            parameters.RepositoryId = Guid.Parse(repositoryId);
            parameters.MappedPath = mappedPath;
            parameters.Version = new GitVersionDescriptor { Version = version, VersionType = GitVersionType.Branch };
        }

        var wiki = svc.GetClient<WikiHttpClient>();
        var w = await wiki.CreateWikiAsync(parameters, resolved, cancellationToken: ct);
        return JsonSerializer.Serialize(new
        {
            w.Id, w.Name, Type = w.Type.ToString(), w.MappedPath, w.ProjectId, w.RepositoryId, w.RemoteUrl, w.Url,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_update_wiki"),
     Description("Rename a wiki. Disabled unless writes are enabled.")]
    public static async Task<string> UpdateWiki(
        AzureDevOpsService svc,
        [Description("Wiki name or GUID")] string wikiIdentifier,
        [Description("New wiki name")] string newName,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("update_wiki");
        var resolved = svc.ResolveProject(project);
        var wiki = svc.GetClient<WikiHttpClient>();
        var parameters = new WikiUpdateParameters { Name = newName };
        var w = await wiki.UpdateWikiAsync(parameters, resolved, wikiIdentifier, cancellationToken: ct);
        return JsonSerializer.Serialize(new
        {
            w.Id, w.Name, Type = w.Type.ToString(), w.MappedPath, w.ProjectId, w.RepositoryId, w.RemoteUrl, w.Url,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_delete_wiki"),
     Description("Delete (unpublish) a wiki. For a code wiki this unpublishes it; the backing Git repo is untouched. " +
                 "For a project wiki this permanently deletes its pages. Disabled unless writes are enabled.")]
    public static async Task<string> DeleteWiki(
        AzureDevOpsService svc,
        [Description("Wiki name or GUID")] string wikiIdentifier,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("delete_wiki");
        var resolved = svc.ResolveProject(project);
        var wiki = svc.GetClient<WikiHttpClient>();
        var w = await wiki.DeleteWikiAsync(resolved, wikiIdentifier, cancellationToken: ct);
        return JsonSerializer.Serialize(new { id = w.Id, name = w.Name, deleted = true }, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_create_wiki_attachment"),
     Description("Upload a binary attachment (e.g. an image) to a wiki so pages can reference it. " +
                 "Content is base64. Returns the attachment path to embed in Markdown. Disabled unless writes are enabled.")]
    public static async Task<string> CreateWikiAttachment(
        AzureDevOpsService svc,
        [Description("Wiki name or GUID")] string wikiIdentifier,
        [Description("Attachment file name, e.g. 'diagram.png'")] string name,
        [Description("File content as base64")] string contentBase64,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("create_wiki_attachment");
        var resolved = svc.ResolveProject(project);
        var wiki = svc.GetClient<WikiHttpClient>();

        byte[] bytes;
        try { bytes = Convert.FromBase64String(contentBase64); }
        catch (FormatException) { throw new InvalidOperationException("contentBase64 is not valid base64."); }

        using var stream = new MemoryStream(bytes);
        var att = await wiki.CreateAttachmentAsync(stream, resolved, wikiIdentifier, name, cancellationToken: ct);
        return JsonSerializer.Serialize(new
        {
            att.Attachment?.Name,
            att.Attachment?.Path,
            bytes = bytes.Length,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_get_wiki_attachment"),
     Description("Download a wiki attachment by name; returns the content as base64. Large attachments are rejected.")]
    public static async Task<string> GetWikiAttachment(
        AzureDevOpsService svc,
        [Description("Wiki name or GUID")] string wikiIdentifier,
        [Description("Attachment name, e.g. 'diagram.png'")] string name,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        // The typed GetAttachmentContentAsync addresses attachments by page+attachment id; the REST
        // route resolves them by name, which is what callers actually have. Use REST here.
        var url = $"{Uri.EscapeDataString(resolved)}/_apis/wiki/wikis/{Uri.EscapeDataString(wikiIdentifier)}" +
                  $"/attachments/{Uri.EscapeDataString(name)}?api-version=7.1";
        using var resp = await svc.RestClient.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Get wiki attachment failed: {(int)resp.StatusCode} {resp.ReasonPhrase} for {url}\n{err}");
        }
        var data = await resp.Content.ReadAsByteArrayAsync(ct);
        if (data.Length > 10_000_000)
            throw new InvalidOperationException($"Attachment '{name}' is {data.Length} bytes — too large to return inline.");
        return JsonSerializer.Serialize(new
        {
            name, bytes = data.Length, contentBase64 = Convert.ToBase64String(data),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_list_wiki_page_comments"),
     Description("List comments on a wiki page. pageId comes from azdo_get_wiki_page.")]
    public static async Task<string> ListWikiPageComments(
        AzureDevOpsService svc,
        [Description("Wiki name or GUID")] string wikiIdentifier,
        [Description("Wiki page id (from azdo_get_wiki_page)")] int pageId,
        [Description("Max comments to return (default 100)")] int top = 100,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var wiki = svc.GetClient<WikiHttpClient>();
        var comments = await wiki.ListCommentsAsync(resolved, wikiIdentifier, pageId, top: top, cancellationToken: ct);
        return JsonSerializer.Serialize(comments, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_add_wiki_page_comment"),
     Description("Add a comment to a wiki page (optionally as a reply via parentCommentId). Disabled unless writes are enabled.")]
    public static async Task<string> AddWikiPageComment(
        AzureDevOpsService svc,
        [Description("Wiki name or GUID")] string wikiIdentifier,
        [Description("Wiki page id (from azdo_get_wiki_page)")] int pageId,
        [Description("Comment text (Markdown)")] string text,
        [Description("Parent comment id to reply to (optional)")] int? parentCommentId = null,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("add_wiki_page_comment");
        var resolved = svc.ResolveProject(project);
        var wiki = svc.GetClient<WikiHttpClient>();
        var parameters = new CommentCreateParameters { Text = TextUtil.NormalizeNewlines(text) };
        if (parentCommentId is not null) parameters.ParentId = parentCommentId.Value;
        var comment = await wiki.AddCommentAsync(parameters, resolved, wikiIdentifier, pageId, cancellationToken: ct);
        return JsonSerializer.Serialize(comment, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_update_wiki_page_comment"),
     Description("Edit the text of an existing wiki page comment. Disabled unless writes are enabled.")]
    public static async Task<string> UpdateWikiPageComment(
        AzureDevOpsService svc,
        [Description("Wiki name or GUID")] string wikiIdentifier,
        [Description("Wiki page id (from azdo_get_wiki_page)")] int pageId,
        [Description("Comment id to edit")] int commentId,
        [Description("New comment text (Markdown)")] string text,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("update_wiki_page_comment");
        var resolved = svc.ResolveProject(project);
        var wiki = svc.GetClient<WikiHttpClient>();
        var parameters = new CommentUpdateParameters { Text = TextUtil.NormalizeNewlines(text) };
        var comment = await wiki.UpdateCommentAsync(parameters, resolved, wikiIdentifier, pageId, commentId, cancellationToken: ct);
        return JsonSerializer.Serialize(comment, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_delete_wiki_page_comment"),
     Description("Delete a comment from a wiki page. Disabled unless writes are enabled.")]
    public static async Task<string> DeleteWikiPageComment(
        AzureDevOpsService svc,
        [Description("Wiki name or GUID")] string wikiIdentifier,
        [Description("Wiki page id (from azdo_get_wiki_page)")] int pageId,
        [Description("Comment id to delete")] int commentId,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("delete_wiki_page_comment");
        var resolved = svc.ResolveProject(project);
        var wiki = svc.GetClient<WikiHttpClient>();
        await wiki.DeleteCommentAsync(resolved, wikiIdentifier, pageId, commentId, cancellationToken: ct);
        return JsonSerializer.Serialize(new { pageId, commentId, deleted = true }, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_get_wiki_page_stats"),
     Description("Get view statistics for a wiki page over the last N days. pageId comes from azdo_get_wiki_page.")]
    public static async Task<string> GetWikiPageStats(
        AzureDevOpsService svc,
        [Description("Wiki name or GUID")] string wikiIdentifier,
        [Description("Wiki page id (from azdo_get_wiki_page)")] int pageId,
        [Description("Number of days back to report views for (default 30)")] int days = 30,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var wiki = svc.GetClient<WikiHttpClient>();
        var detail = await wiki.GetPageDataAsync(resolved, wikiIdentifier, pageId, days, cancellationToken: ct);
        return JsonSerializer.Serialize(new
        {
            detail.Id,
            detail.Path,
            ViewStats = detail.ViewStats?.Select(v => new { v.Count, v.Day }),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_search_wiki"),
     Description("Full-text search across wiki pages. Requires the Azure DevOps Server 'Search' extension to be installed; " +
                 "if it is not available this returns { available: false } rather than failing.")]
    public static async Task<string> SearchWiki(
        AzureDevOpsService svc,
        [Description("Search text")] string searchText,
        [Description("Restrict to a specific wiki name (optional)")] string? wiki = null,
        [Description("Max results (default 20)")] int top = 20,
        [Description("Results to skip for paging (default 0)")] int skip = 0,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var filters = new Dictionary<string, string[]> { ["Project"] = new[] { resolved } };
        if (!string.IsNullOrWhiteSpace(wiki)) filters["Wiki"] = new[] { wiki };
        var payload = JsonSerializer.Serialize(new { searchText, top, skip, filters }, JsonOpts.Default);

        var url = $"{Uri.EscapeDataString(resolved)}/_apis/search/wikisearchresults?api-version=7.1";
        try
        {
            using var body = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await svc.RestClient.PostAsync(url, body, ct);
            if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway)
            {
                return JsonSerializer.Serialize(new
                {
                    available = false,
                    message = "Wiki search is unavailable — the Azure DevOps Server 'Search' extension is not installed or configured on this collection.",
                }, JsonOpts.Default);
            }
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                return JsonSerializer.Serialize(new
                {
                    available = false,
                    message = $"Wiki search failed: {(int)resp.StatusCode} {resp.ReasonPhrase}.",
                    detail = text,
                }, JsonOpts.Default);
            }
            return text;   // pass the search service response through
        }
        catch (HttpRequestException ex)
        {
            return JsonSerializer.Serialize(new
            {
                available = false,
                message = "Wiki search is unavailable — could not reach the Search service. Is the 'Search' extension installed?",
                detail = ex.Message,
            }, JsonOpts.Default);
        }
    }

    private static bool IsPreconditionFailed(VssServiceException ex)
    {
        // The wiki API surfaces a stale/blank If-Match as HTTP 412; the SDK wraps it in a
        // VssServiceException whose message references the version/precondition mismatch.
        var msg = ex.Message ?? string.Empty;
        return msg.Contains("412")
            || msg.Contains("precondition", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("version", StringComparison.OrdinalIgnoreCase);
    }
}
