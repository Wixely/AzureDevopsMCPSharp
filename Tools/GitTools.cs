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
