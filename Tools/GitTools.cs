using System.ComponentModel;
using System.Text.Json;
using AzureDevopsMCPSharp.Services;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using ModelContextProtocol.Server;

namespace AzureDevopsMCPSharp.Tools;

[McpServerToolType]
public static class GitTools
{
    [McpServerTool(Name = "list_repositories"),
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

    [McpServerTool(Name = "list_pull_requests"),
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

    [McpServerTool(Name = "create_repository"),
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

    [McpServerTool(Name = "rename_repository"),
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

    [McpServerTool(Name = "delete_repository"),
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

    [McpServerTool(Name = "get_pull_request"),
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
