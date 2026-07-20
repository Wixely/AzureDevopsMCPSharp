using System.ComponentModel;
using System.Text.Json;
using AzureDevopsMCPSharp.Services;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using ModelContextProtocol.Server;

namespace AzureDevopsMCPSharp.Tools;

[McpServerToolType]
public static class GitTools
{
    [McpServerTool(Name = "azdo_list_repositories"),
     Description("List Git repositories in a project.")]
    public static async Task<string> ListRepositories(
        AzureDevOpsService svc,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<GitHttpClient>();
        var repos = await client.GetRepositoriesAsync(resolved, cancellationToken: ct);
        var summary = repos.Select(r => new { r.Id, r.Name, r.DefaultBranch, r.WebUrl, r.Size });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_list_pull_requests"),
     Description("List pull requests in a repository.")]
    public static async Task<string> ListPullRequests(
        AzureDevOpsService svc,
        [Description("Repository id or name")] string repository,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        [Description("Status filter: active, completed, abandoned, all (default active)")] string status = "active",
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<GitHttpClient>();
        var criteria = new GitPullRequestSearchCriteria
        {
            Status = status.ToLowerInvariant() switch
            {
                "completed" => PullRequestStatus.Completed,
                "abandoned" => PullRequestStatus.Abandoned,
                "all" => PullRequestStatus.All,
                _ => PullRequestStatus.Active,
            }
        };
        var prs = await client.GetPullRequestsAsync(resolved, repository, criteria, cancellationToken: ct);
        var summary = prs.Select(p => new
        {
            p.PullRequestId,
            p.Title,
            p.Status,
            p.SourceRefName,
            p.TargetRefName,
            CreatedBy = p.CreatedBy?.DisplayName,
            p.CreationDate,
        });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_create_pull_request"),
     Description("Create a pull request from a source branch into a target branch. Disabled when the server is in read-only mode or the operation is not enabled.")]
    public static async Task<string> CreatePullRequest(
        AzureDevOpsService svc,
        [Description("Repository id or name")] string repository,
        [Description("PR title.")] string title,
        [Description("Source branch with the changes, e.g. 'feature/x' or 'refs/heads/feature/x'.")] string sourceBranch,
        [Description("Target branch to merge into, e.g. 'main' or 'refs/heads/main'.")] string targetBranch,
        [Description("Optional description / body.")] string? description = null,
        [Description("Create as a draft PR (default false).")] bool isDraft = false,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("create_pull_request");
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("title is required.", nameof(title));
        if (string.IsNullOrWhiteSpace(sourceBranch)) throw new ArgumentException("sourceBranch is required.", nameof(sourceBranch));
        if (string.IsNullOrWhiteSpace(targetBranch)) throw new ArgumentException("targetBranch is required.", nameof(targetBranch));

        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<GitHttpClient>();
        var pr = new GitPullRequest
        {
            Title = title,
            Description = description,
            SourceRefName = ToRef(sourceBranch),
            TargetRefName = ToRef(targetBranch),
            IsDraft = isDraft,
        };

        try
        {
            var created = await client.CreatePullRequestAsync(pr, resolved, repository, cancellationToken: ct);
            return JsonSerializer.Serialize(new
            {
                created.PullRequestId,
                created.Title,
                Status = created.Status.ToString(),
                created.SourceRefName,
                created.TargetRefName,
                created.IsDraft,
                created.Url,
            }, JsonOpts.Default);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"create_pull_request failed in '{resolved}/{repository}': {ex.Message}. " +
                "Common causes: source or target branch does not exist; an active PR already exists for this source→target; " +
                "PAT lacks 'Code (read & write)' / 'Pull Request Contribute'.", ex);
        }
    }

    private static string ToRef(string branch) =>
        branch.StartsWith("refs/", StringComparison.OrdinalIgnoreCase) ? branch : $"refs/heads/{branch}";

    [McpServerTool(Name = "azdo_create_repository"),
     Description("Create a new empty Git repository in a project. Disabled when the server is in read-only mode or the operation is not enabled.")]
    public static async Task<string> CreateRepository(
        AzureDevOpsService svc,
        [Description("Name for the new repository")] string name,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("create_repository");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required.", nameof(name));

        var resolved = svc.ResolveProject(project);
        var projClient = svc.GetClient<Microsoft.TeamFoundation.Core.WebApi.ProjectHttpClient>();
        var teamProject = await projClient.GetProject(resolved, includeCapabilities: false)
            ?? throw new InvalidOperationException($"Project '{resolved}' not found.");

        var client = svc.GetClient<GitHttpClient>();
        var options = new GitRepositoryCreateOptions
        {
            Name = name,
            ProjectReference = new Microsoft.TeamFoundation.Core.WebApi.TeamProjectReference
            {
                Id = teamProject.Id,
                Name = teamProject.Name,
            },
        };

        try
        {
            var created = await client.CreateRepositoryAsync(options, project: resolved, userState: null, cancellationToken: ct);
            return JsonSerializer.Serialize(created, JsonOpts.Default);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"create_repository failed for '{name}' in project '{resolved}': {ex.Message}. " +
                "Common causes: a repository with this name already exists; PAT lacks 'Code (read, write & manage)'; " +
                "user lacks 'Create repository' permission on the project.",
                ex);
        }
    }

    [McpServerTool(Name = "azdo_rename_repository"),
     Description("Rename an existing Git repository. Disabled when the server is in read-only mode or the operation is not enabled.")]
    public static async Task<string> RenameRepository(
        AzureDevOpsService svc,
        [Description("Existing repository id or name")] string repository,
        [Description("New repository name")] string newName,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("rename_repository");
        if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("newName is required.", nameof(newName));

        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<GitHttpClient>();

        GitRepository existing;
        try
        {
            existing = await client.GetRepositoryAsync(resolved, repository, cancellationToken: ct)
                ?? throw new InvalidOperationException($"Repository '{repository}' not found in project '{resolved}'.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"rename_repository failed: could not load repository '{repository}' in project '{resolved}'. {ex.Message}", ex);
        }

        try
        {
            var updated = await client.UpdateRepositoryAsync(
                new GitRepository { Name = newName }, existing.Id, cancellationToken: ct);
            return JsonSerializer.Serialize(updated, JsonOpts.Default);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"rename_repository failed for '{repository}' → '{newName}' in project '{resolved}': {ex.Message}. " +
                "Common causes: another repository in the project already has the new name; PAT lacks 'Code (read, write & manage)'; " +
                "user lacks 'Rename repository' permission.",
                ex);
        }
    }

    [McpServerTool(Name = "azdo_delete_repository"),
     Description("Permanently delete a Git repository, including all branches, tags, history and pull requests. There is no recycle bin — this cannot be undone. Disabled when the server is in read-only mode or the operation is not enabled.")]
    public static async Task<string> DeleteRepository(
        AzureDevOpsService svc,
        [Description("Repository id or name to delete")] string repository,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("delete_repository");

        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<GitHttpClient>();

        GitRepository existing;
        try
        {
            existing = await client.GetRepositoryAsync(resolved, repository, cancellationToken: ct)
                ?? throw new InvalidOperationException($"Repository '{repository}' not found in project '{resolved}'.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"delete_repository failed: could not load repository '{repository}' in project '{resolved}'. {ex.Message}", ex);
        }

        try
        {
            await client.DeleteRepositoryAsync(existing.Id, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"delete_repository failed for '{repository}' in project '{resolved}': {ex.Message}. " +
                "Common causes: PAT lacks 'Code (read, write & manage)'; user lacks 'Delete repository' permission; " +
                "repository is the project's default repository (delete or reassign default first).",
                ex);
        }

        return JsonSerializer.Serialize(
            new { deleted = true, repositoryId = existing.Id, repositoryName = existing.Name, project = resolved },
            JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_list_commits"),
     Description("List commits in a Git repository. Optional filters by branch/tag/sha, path, author, and date range.")]
    public static async Task<string> ListCommits(
        AzureDevOpsService svc,
        [Description("Repository id or name")] string repository,
        [Description("Branch name, tag, or commit sha. Defaults to the repository's default branch.")] string? version = null,
        [Description("Version type for 'version': branch, tag, or commit. Defaults to branch.")] string versionType = "branch",
        [Description("Only commits that touch this path.")] string? path = null,
        [Description("Filter by author display name or email.")] string? author = null,
        [Description("Only commits from this UTC date/time (ISO 8601, e.g. 2024-01-01T00:00:00Z).")] string? fromDate = null,
        [Description("Only commits up to this UTC date/time (ISO 8601).")] string? toDate = null,
        [Description("Max commits to return (default 50, capped at 500).")] int top = 50,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<GitHttpClient>();
        var criteria = new GitQueryCommitsCriteria();
        if (!string.IsNullOrWhiteSpace(version))
        {
            criteria.ItemVersion = new GitVersionDescriptor
            {
                Version = version,
                VersionType = versionType.ToLowerInvariant() switch
                {
                    "tag" => GitVersionType.Tag,
                    "commit" => GitVersionType.Commit,
                    _ => GitVersionType.Branch,
                },
            };
        }
        if (!string.IsNullOrWhiteSpace(path)) criteria.ItemPath = path;
        if (!string.IsNullOrWhiteSpace(author)) criteria.Author = author;
        if (!string.IsNullOrWhiteSpace(fromDate)) criteria.FromDate = fromDate;
        if (!string.IsNullOrWhiteSpace(toDate)) criteria.ToDate = toDate;

        var cap = Math.Clamp(top, 1, 500);
        var commits = await client.GetCommitsAsync(resolved, repository, criteria, top: cap, cancellationToken: ct);
        var summary = commits.Select(c => new
        {
            c.CommitId,
            c.Comment,
            Author = c.Author?.Name,
            AuthorEmail = c.Author?.Email,
            AuthoredDate = c.Author?.Date,
            Committer = c.Committer?.Name,
            CommittedDate = c.Committer?.Date,
            c.RemoteUrl,
        });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_get_pull_request"),
     Description("Get full details for a single pull request by id.")]
    public static async Task<string> GetPullRequest(
        AzureDevOpsService svc,
        [Description("Repository id or name")] string repository,
        [Description("Pull request id")] int pullRequestId,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<GitHttpClient>();
        var pr = await client.GetPullRequestAsync(resolved, repository, pullRequestId, cancellationToken: ct);
        return JsonSerializer.Serialize(pr, JsonOpts.Default);
    }
}
