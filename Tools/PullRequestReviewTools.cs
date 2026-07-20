using System.ComponentModel;
using System.Text;
using System.Text.Json;
using AzureDevopsMCPSharp.Services;
using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.TeamFoundation.Policy.WebApi;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.WebApi;
using ModelContextProtocol.Server;

namespace AzureDevopsMCPSharp.Tools;

[McpServerToolType]
public static class PullRequestReviewTools
{
    [McpServerTool(Name = "azdo_list_pull_request_iterations"),
     Description("List the iterations (push-by-push history) of a PR. Iterations expose successive versions of the changeset.")]
    public static async Task<string> ListIterations(
        AzureDevOpsService svc,
        [Description("Repository id or name")] string repository,
        [Description("Pull request id")] int pullRequestId,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<GitHttpClient>();
        var iterations = await client.GetPullRequestIterationsAsync(resolved, repository, pullRequestId, cancellationToken: ct);
        var summary = iterations.Select(i => new
        {
            i.Id,
            i.Description,
            i.CreatedDate,
            i.UpdatedDate,
            SourceRefCommit = i.SourceRefCommit?.CommitId,
            TargetRefCommit = i.TargetRefCommit?.CommitId,
            CommonRefCommit = i.CommonRefCommit?.CommitId,
            Reason = i.Reason.ToString(),
        });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_list_pull_request_changes"),
     Description("List file-level changes in a given PR iteration. Pass iterationId=0 to use the latest iteration.")]
    public static async Task<string> ListChanges(
        AzureDevOpsService svc,
        [Description("Repository id or name")] string repository,
        [Description("Pull request id")] int pullRequestId,
        [Description("Iteration id. Use 0 to auto-select the latest iteration.")] int iterationId = 0,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<GitHttpClient>();

        if (iterationId <= 0)
        {
            var iters = await client.GetPullRequestIterationsAsync(resolved, repository, pullRequestId, cancellationToken: ct);
            iterationId = iters.OrderByDescending(i => i.Id).First().Id ?? 0;
            if (iterationId <= 0) throw new InvalidOperationException("Could not resolve a PR iteration id.");
        }

        var changes = await client.GetPullRequestIterationChangesAsync(resolved, repository, pullRequestId, iterationId, cancellationToken: ct);
        var summary = changes.ChangeEntries.Select(c => new
        {
            c.ChangeId,
            ChangeType = c.ChangeType.ToString(),
            Path = c.Item?.Path,
            ObjectId = c.Item?.ObjectId,
            OriginalObjectId = c.Item?.OriginalObjectId,
            SourceServerItem = c.SourceServerItem,
        });
        return JsonSerializer.Serialize(new { iterationId, changes = summary }, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_get_pull_request_diff"),
     Description("Return the unified diff between the PR's source and target branches (via commit diffs).")]
    public static async Task<string> GetDiff(
        AzureDevOpsService svc,
        [Description("Repository id or name")] string repository,
        [Description("Pull request id")] int pullRequestId,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        [Description("Maximum changes to return (default 200).")] int top = 200,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<GitHttpClient>();
        var pr = await client.GetPullRequestAsync(resolved, repository, pullRequestId, cancellationToken: ct);
        var baseSha = pr.LastMergeTargetCommit?.CommitId;
        var targetSha = pr.LastMergeSourceCommit?.CommitId;
        if (string.IsNullOrEmpty(baseSha) || string.IsNullOrEmpty(targetSha))
            throw new InvalidOperationException("PR does not have base/target merge commits yet.");

        var baseDesc = new GitBaseVersionDescriptor { Version = baseSha, VersionType = GitVersionType.Commit };
        var targetDesc = new GitTargetVersionDescriptor { Version = targetSha, VersionType = GitVersionType.Commit };
        var diffs = await client.GetCommitDiffsAsync(resolved, repository, baseVersionDescriptor: baseDesc, targetVersionDescriptor: targetDesc, top: top, cancellationToken: ct);

        var summary = new
        {
            baseSha,
            targetSha,
            diffs.AheadCount,
            diffs.BehindCount,
            diffs.AllChangesIncluded,
            changes = diffs.Changes?.Select(c => new
            {
                ChangeType = c.ChangeType.ToString(),
                Path = c.Item?.Path,
                ObjectId = c.Item?.ObjectId,
            }),
        };
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_get_pull_request_file"),
     Description("Return the content of a single file at the PR's source-branch commit. Use list_pull_request_changes to discover paths.")]
    public static async Task<string> GetFile(
        AzureDevOpsService svc,
        [Description("Repository id or name")] string repository,
        [Description("Pull request id")] int pullRequestId,
        [Description("File path inside the repository (e.g. /src/Foo.cs).")] string path,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<GitHttpClient>();
        var pr = await client.GetPullRequestAsync(resolved, repository, pullRequestId, cancellationToken: ct);
        var sha = pr.LastMergeSourceCommit?.CommitId
            ?? throw new InvalidOperationException("PR has no source commit yet.");

        var descriptor = new GitVersionDescriptor { Version = sha, VersionType = GitVersionType.Commit };
        using var stream = await client.GetItemContentAsync(
            project: resolved,
            repositoryId: repository,
            path: path,
            scopePath: null,
            recursionLevel: VersionControlRecursionType.None,
            includeContentMetadata: false,
            latestProcessedChange: false,
            download: false,
            versionDescriptor: descriptor,
            includeContent: true,
            resolveLfs: false,
            sanitize: false,
            userState: null,
            cancellationToken: ct);
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync();
        var truncated = false;
        if (text.Length > 200_000) { text = text[..200_000]; truncated = true; }
        return JsonSerializer.Serialize(new { path, sha, length = text.Length, truncated, content = text }, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_list_pull_request_reviewers"),
     Description("List reviewers on a PR with their current vote (10=approve, 5=approve-with-suggestions, 0=no vote, -5=wait, -10=reject).")]
    public static async Task<string> ListReviewers(
        AzureDevOpsService svc,
        [Description("Repository id or name")] string repository,
        [Description("Pull request id")] int pullRequestId,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<GitHttpClient>();
        var reviewers = await client.GetPullRequestReviewersAsync(resolved, repository, pullRequestId, cancellationToken: ct);
        var summary = reviewers.Select(r => new
        {
            r.Id,
            r.DisplayName,
            r.UniqueName,
            r.Vote,
            VoteText = VoteToText(r.Vote),
            r.IsRequired,
            r.HasDeclined,
            r.IsContainer,
            r.ImageUrl,
        });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_list_pull_request_threads"),
     Description("List comment threads on a PR (each thread = one location with one or more comments).")]
    public static async Task<string> ListThreads(
        AzureDevOpsService svc,
        [Description("Repository id or name")] string repository,
        [Description("Pull request id")] int pullRequestId,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        [Description("Include deleted threads (default false).")] bool includeDeleted = false,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<GitHttpClient>();
        var threads = await client.GetThreadsAsync(resolved, repository, pullRequestId, cancellationToken: ct);
        var summary = threads
            .Where(t => includeDeleted || !t.IsDeleted)
            .Select(t => new
            {
                t.Id,
                Status = t.Status.ToString(),
                t.IsDeleted,
                FilePath = t.ThreadContext?.FilePath,
                LeftStart = t.ThreadContext?.LeftFileStart,
                LeftEnd = t.ThreadContext?.LeftFileEnd,
                RightStart = t.ThreadContext?.RightFileStart,
                RightEnd = t.ThreadContext?.RightFileEnd,
                t.PublishedDate,
                t.LastUpdatedDate,
                Comments = t.Comments?.Select(c => new
                {
                    c.Id,
                    Author = c.Author?.DisplayName,
                    c.Content,
                    CommentType = c.CommentType.ToString(),
                    c.PublishedDate,
                    c.LastUpdatedDate,
                }),
            });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_list_pull_request_work_items"),
     Description("List work items linked to a PR (id + url).")]
    public static async Task<string> ListWorkItems(
        AzureDevOpsService svc,
        [Description("Repository id or name")] string repository,
        [Description("Pull request id")] int pullRequestId,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<GitHttpClient>();
        var refs = await client.GetPullRequestWorkItemRefsAsync(resolved, repository, pullRequestId, cancellationToken: ct);
        var summary = refs.Select(r => new { r.Id, r.Url });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_get_pull_request_policy_evaluations"),
     Description("Return branch-policy evaluation results for a PR (build validation, required reviewers, comment resolution, …).")]
    public static async Task<string> GetPolicyEvaluations(
        AzureDevOpsService svc,
        [Description("Pull request id")] int pullRequestId,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var projectClient = svc.GetClient<ProjectHttpClient>();
        var teamProject = await projectClient.GetProject(resolved, includeCapabilities: false)
            ?? throw new InvalidOperationException($"Project '{resolved}' not found.");

        var artifactId = $"vstfs:///CodeReview/CodeReviewId/{teamProject.Id}/{pullRequestId}";
        var policyClient = svc.GetClient<PolicyHttpClient>();
        var evals = await policyClient.GetPolicyEvaluationsAsync(resolved, artifactId, cancellationToken: ct);
        var summary = evals.Select(e => new
        {
            e.EvaluationId,
            Status = e.Status.ToString(),
            PolicyType = e.Configuration?.Type?.DisplayName,
            DisplayName = e.Configuration?.Type?.DisplayName,
            e.StartedDate,
            e.CompletedDate,
            IsBlocking = e.Configuration?.IsBlocking,
        });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_set_pull_request_vote"),
     Description("Set the authenticated user's vote on a PR. Vote values: approve, approve-with-suggestions, no-vote (reset), waiting-for-author, reject. Requires write mode and operation enabled.")]
    public static async Task<string> SetVote(
        AzureDevOpsService svc,
        [Description("Repository id or name")] string repository,
        [Description("Pull request id")] int pullRequestId,
        [Description("Vote: approve, approve-with-suggestions, no-vote, waiting-for-author, reject.")] string vote,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("set_pull_request_vote");
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<GitHttpClient>();

        var voteValue = ParseVote(vote);

        // Resolve the calling user via connection identity to set their own vote.
        var connection = svc.Connection;
        var me = connection.AuthorizedIdentity
            ?? throw new InvalidOperationException("Could not resolve authenticated identity from the VSS connection.");

        var reviewer = new IdentityRefWithVote { Id = me.Id.ToString(), Vote = voteValue };
        var updated = await client.CreatePullRequestReviewerAsync(reviewer, repository, pullRequestId, me.Id.ToString(), cancellationToken: ct);
        return JsonSerializer.Serialize(new { reviewer = updated.DisplayName, updated.Vote, VoteText = VoteToText(updated.Vote) }, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_add_pull_request_comment"),
     Description("Add a comment to a PR. With no threadId a new conversation thread is started; with a threadId the comment replies in that thread. Requires write mode and operation enabled.")]
    public static async Task<string> AddComment(
        AzureDevOpsService svc,
        [Description("Repository id or name")] string repository,
        [Description("Pull request id")] int pullRequestId,
        [Description("Comment body markdown.")] string body,
        [Description("Optional thread id to reply into. Omit to create a new thread.")] int threadId = 0,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("add_pull_request_comment");
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<GitHttpClient>();

        if (threadId <= 0)
        {
            var thread = new GitPullRequestCommentThread
            {
                Comments = new List<Comment>
                {
                    new() { ParentCommentId = 0, Content = body, CommentType = CommentType.Text },
                },
                Status = CommentThreadStatus.Active,
            };
            var created = await client.CreateThreadAsync(thread, resolved, repository, pullRequestId, cancellationToken: ct);
            return JsonSerializer.Serialize(new { threadId = created.Id, commentId = created.Comments?.FirstOrDefault()?.Id }, JsonOpts.Default);
        }
        else
        {
            var comment = new Comment { ParentCommentId = 0, Content = body, CommentType = CommentType.Text };
            var created = await client.CreateCommentAsync(comment, resolved, repository, pullRequestId, threadId, cancellationToken: ct);
            return JsonSerializer.Serialize(new { threadId, commentId = created.Id }, JsonOpts.Default);
        }
    }

    [McpServerTool(Name = "azdo_complete_pull_request"),
     Description("Complete (merge) a PR. mergeStrategy: noFastForward (default merge commit), squash, rebase, rebaseMerge. " +
                 "Set bypassPolicy=true to override branch policies (e.g. required approvals/reviewers/checks not met) — this is the " +
                 "equivalent of the 'Override branch policies and enable merge' option in the Azure DevOps UI and needs the caller to have " +
                 "the 'Bypass policies when completing pull requests' permission; supply bypassReason for the audit trail. " +
                 "Requires write mode and operation enabled.")]
    public static async Task<string> Complete(
        AzureDevOpsService svc,
        [Description("Repository id or name")] string repository,
        [Description("Pull request id")] int pullRequestId,
        [Description("Merge strategy: noFastForward, squash, rebase, rebaseMerge (default noFastForward).")] string mergeStrategy = "noFastForward",
        [Description("Delete the source branch after completion (default false).")] bool deleteSourceBranch = false,
        [Description("Optional merge commit message.")] string? mergeCommitMessage = null,
        [Description("Override (bypass) branch policies when completing even if approvals/reviewers/checks are not satisfied. Requires the 'Bypass policies when completing pull requests' permission. Default false.")] bool bypassPolicy = false,
        [Description("Reason recorded in the audit trail for the policy override. Recommended whenever bypassPolicy=true.")] string? bypassReason = null,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("complete_pull_request");
        // Policy override is a privileged action gated separately, so an operator can permit normal
        // completions while keeping bypass off. (The Azure DevOps server still enforces the caller's
        // 'Bypass policies when completing pull requests' permission on top of this.)
        if (bypassPolicy) svc.EnsureWriteAllowed("bypass_pull_request_policy");

        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<GitHttpClient>();

        var strategy = mergeStrategy.ToLowerInvariant() switch
        {
            "squash" => GitPullRequestMergeStrategy.Squash,
            "rebase" => GitPullRequestMergeStrategy.Rebase,
            "rebasemerge" or "rebase-merge" => GitPullRequestMergeStrategy.RebaseMerge,
            "nofastforward" or "no-fast-forward" or "merge" or "" => GitPullRequestMergeStrategy.NoFastForward,
            _ => throw new ArgumentException(
                $"Unknown mergeStrategy '{mergeStrategy}'. Expected: noFastForward, squash, rebase, rebaseMerge.", nameof(mergeStrategy)),
        };

        // The server needs the source commit to know what to merge.
        var existing = await client.GetPullRequestAsync(resolved, repository, pullRequestId, cancellationToken: ct);
        if (existing.LastMergeSourceCommit is null)
            throw new InvalidOperationException("PR has no source commit yet; cannot complete.");

        var update = new GitPullRequest
        {
            Status = PullRequestStatus.Completed,
            LastMergeSourceCommit = existing.LastMergeSourceCommit,
            CompletionOptions = new GitPullRequestCompletionOptions
            {
                MergeStrategy = strategy,
                DeleteSourceBranch = deleteSourceBranch,
                MergeCommitMessage = mergeCommitMessage,
                BypassPolicy = bypassPolicy,
                // Only meaningful when bypassing; recorded on the PR's policy-override audit entry.
                BypassReason = bypassPolicy ? bypassReason : null,
            },
        };

        try
        {
            var pr = await client.UpdatePullRequestAsync(update, resolved, repository, pullRequestId, cancellationToken: ct);
            return JsonSerializer.Serialize(new
            {
                pr.PullRequestId,
                Status = pr.Status.ToString(),
                MergeStatus = pr.MergeStatus.ToString(),
                MergeStrategy = strategy.ToString(),
                PolicyBypassed = bypassPolicy,
                BypassReason = bypassPolicy ? bypassReason : null,
                pr.Url,
            }, JsonOpts.Default);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"complete_pull_request failed for PR {pullRequestId} in '{resolved}/{repository}': {ex.Message}. " +
                "Common causes: unresolved merge conflicts; required branch policies not satisfied (set bypassPolicy=true only if permitted); " +
                "the PR is a draft (publish it first); PAT lacks 'Code (read & write)'.", ex);
        }
    }

    [McpServerTool(Name = "azdo_abandon_pull_request"),
     Description("Mark a PR as Abandoned (Azure DevOps's equivalent of 'deny/cancel'). Requires write mode and operation enabled.")]
    public static async Task<string> Abandon(
        AzureDevOpsService svc,
        [Description("Repository id or name")] string repository,
        [Description("Pull request id")] int pullRequestId,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("abandon_pull_request");
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<GitHttpClient>();
        var pr = await client.UpdatePullRequestAsync(
            new GitPullRequest { Status = PullRequestStatus.Abandoned },
            resolved, repository, pullRequestId, cancellationToken: ct);
        return JsonSerializer.Serialize(new { pr.PullRequestId, Status = pr.Status.ToString(), pr.Url }, JsonOpts.Default);
    }

    [McpServerTool(Name = "azdo_reactivate_pull_request"),
     Description("Reactivate a previously abandoned PR (sets status back to Active). Requires write mode and operation enabled.")]
    public static async Task<string> Reactivate(
        AzureDevOpsService svc,
        [Description("Repository id or name")] string repository,
        [Description("Pull request id")] int pullRequestId,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("reactivate_pull_request");
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<GitHttpClient>();
        var pr = await client.UpdatePullRequestAsync(
            new GitPullRequest { Status = PullRequestStatus.Active },
            resolved, repository, pullRequestId, cancellationToken: ct);
        return JsonSerializer.Serialize(new { pr.PullRequestId, Status = pr.Status.ToString(), pr.Url }, JsonOpts.Default);
    }

    private static short ParseVote(string value) => value?.ToLowerInvariant() switch
    {
        "approve" or "approved" or "10" => 10,
        "approve-with-suggestions" or "suggestions" or "5" => 5,
        "no-vote" or "novote" or "reset" or "0" => 0,
        "waiting-for-author" or "wait" or "-5" => -5,
        "reject" or "rejected" or "deny" or "-10" => -10,
        _ => throw new ArgumentException(
            $"Unknown vote '{value}'. Expected: approve, approve-with-suggestions, no-vote, waiting-for-author, reject.", nameof(value)),
    };

    private static string VoteToText(short vote) => vote switch
    {
        10 => "approve",
        5 => "approve-with-suggestions",
        0 => "no-vote",
        -5 => "waiting-for-author",
        -10 => "reject",
        _ => $"unknown({vote})",
    };
}
