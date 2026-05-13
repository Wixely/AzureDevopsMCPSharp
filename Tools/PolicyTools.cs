using System.ComponentModel;
using System.Text.Json;
using AzureDevopsMCPSharp.Services;
using Microsoft.TeamFoundation.Policy.WebApi;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace AzureDevopsMCPSharp.Tools;

[McpServerToolType]
public static class PolicyTools
{
    // Well-known policy type Guids. Source: Azure DevOps REST docs / Microsoft.TeamFoundation.Policy assembly.
    private static readonly Guid MinimumReviewersPolicyTypeId = new("fa4ab017-c95a-4c3f-9b5a-f0fc05cc1a48");

    [McpServerTool(Name = "list_repo_policies"),
     Description("List branch/repo policy configurations in a project, optionally filtered by repository.")]
    public static async Task<string> ListRepoPolicies(
        AzureDevOpsService svc,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        [Description("Optional repository id or name to filter to")] string? repository = null,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var policyClient = svc.GetClient<PolicyHttpClient>();
        var all = await policyClient.GetPolicyConfigurationsAsync(resolved, cancellationToken: ct);

        Guid? repoFilter = null;
        if (!string.IsNullOrWhiteSpace(repository))
            repoFilter = (await ResolveRepository(svc, resolved, repository, ct)).Id;

        var summary = all
            .Where(p => repoFilter is null || ScopeRepoIds(p).Contains(repoFilter.Value))
            .Select(p => new
            {
                p.Id,
                TypeId = p.Type?.Id,
                TypeName = p.Type?.DisplayName,
                p.IsEnabled,
                p.IsBlocking,
                p.IsDeleted,
                Scope = p.Settings?["scope"],
            });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "get_repo_policy"),
     Description("Get full configuration for a single policy by configuration id.")]
    public static async Task<string> GetRepoPolicy(
        AzureDevOpsService svc,
        [Description("Policy configuration id (from list_repo_policies)")] int policyId,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<PolicyHttpClient>();
        var cfg = await client.GetPolicyConfigurationAsync(resolved, policyId, cancellationToken: ct);
        return JsonSerializer.Serialize(new
        {
            cfg.Id,
            TypeId = cfg.Type?.Id,
            TypeName = cfg.Type?.DisplayName,
            cfg.IsEnabled,
            cfg.IsBlocking,
            cfg.IsDeleted,
            Settings = cfg.Settings?.ToString(),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "delete_repo_policy"),
     Description("Permanently delete a policy configuration. Disabled when the server is in read-only mode or the operation is not enabled.")]
    public static async Task<string> DeleteRepoPolicy(
        AzureDevOpsService svc,
        [Description("Policy configuration id to delete")] int policyId,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("delete_repo_policy");
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<PolicyHttpClient>();
        try
        {
            await client.DeletePolicyConfigurationAsync(resolved, policyId, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"delete_repo_policy failed for id {policyId} in project '{resolved}': {ex.Message}. " +
                "Common causes: id does not exist; PAT lacks 'Code (read, write & manage)'; user lacks 'Edit policies' on the repo.",
                ex);
        }
        return JsonSerializer.Serialize(new { deleted = true, policyId, project = resolved }, JsonOpts.Default);
    }

    [McpServerTool(Name = "set_policy_enabled"),
     Description("Enable or disable an existing policy configuration without changing its settings. Disabled when the server is in read-only mode or the operation is not enabled.")]
    public static async Task<string> SetPolicyEnabled(
        AzureDevOpsService svc,
        [Description("Policy configuration id")] int policyId,
        [Description("True to enable, false to disable")] bool enabled,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("set_policy_enabled");
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<PolicyHttpClient>();

        PolicyConfiguration existing;
        try
        {
            existing = await client.GetPolicyConfigurationAsync(resolved, policyId, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"set_policy_enabled failed: could not load policy {policyId} in project '{resolved}'. {ex.Message}", ex);
        }

        existing.IsEnabled = enabled;
        try
        {
            var updated = await client.UpdatePolicyConfigurationAsync(existing, resolved, policyId, cancellationToken: ct);
            return JsonSerializer.Serialize(new
            {
                updated.Id,
                updated.IsEnabled,
                updated.IsBlocking,
                TypeId = updated.Type?.Id,
            }, JsonOpts.Default);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"set_policy_enabled failed for policy {policyId} in project '{resolved}': {ex.Message}. " +
                "Common causes: PAT lacks 'Code (read, write & manage)'; user lacks 'Edit policies'.",
                ex);
        }
    }

    [McpServerTool(Name = "set_repo_policy"),
     Description("Create or update a policy configuration of any type, by passing the type Guid and a raw settings JSON object. " +
                 "Pass policyId to update an existing policy; omit it to create a new one. The 'scope' is built from repository/branch/matchKind " +
                 "and merged into the settings automatically. Disabled when the server is in read-only mode or the operation is not enabled.")]
    public static async Task<string> SetRepoPolicy(
        AzureDevOpsService svc,
        [Description("Policy type Guid (e.g. 'fa4ab017-c95a-4c3f-9b5a-f0fc05cc1a48' for minimum reviewers). Find type ids via the REST 'policy/types' endpoint or by inspecting an existing policy.")] string policyTypeId,
        [Description("Repository id or name to scope the policy to")] string repository,
        [Description("Settings JSON object for the policy's type-specific knobs (without the 'scope' key — pass via repository/branch/matchKind). Example for min reviewers: '{\"minimumApproverCount\":2,\"creatorVoteCounts\":false,\"resetOnSourcePush\":true}'")] string settingsJson,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        [Description("Branch ref name, e.g. 'refs/heads/main'. Omit for the whole repo.")] string? branch = null,
        [Description("Scope match kind: 'exact' (this exact ref) or 'prefix' (this ref and anything beneath). Default 'exact'.")] string matchKind = "exact",
        [Description("Whether the policy blocks merges when not satisfied. Default true.")] bool isBlocking = true,
        [Description("Whether the policy is active. Default true.")] bool isEnabled = true,
        [Description("Existing policy configuration id to update; omit to create a new policy")] int? policyId = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("set_repo_policy");
        if (!Guid.TryParse(policyTypeId, out var typeGuid))
            throw new ArgumentException($"policyTypeId '{policyTypeId}' is not a valid Guid.", nameof(policyTypeId));

        var resolved = svc.ResolveProject(project);
        var repo = await ResolveRepository(svc, resolved, repository, ct);

        JObject settingsObj;
        try
        {
            settingsObj = JObject.Parse(string.IsNullOrWhiteSpace(settingsJson) ? "{}" : settingsJson);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"settingsJson is not valid JSON: {ex.Message}", nameof(settingsJson), ex);
        }

        settingsObj["scope"] = BuildScope(repo.Id, branch, matchKind);

        var config = new PolicyConfiguration
        {
            Id = policyId ?? 0,
            Type = new PolicyTypeRef { Id = typeGuid },
            IsEnabled = isEnabled,
            IsBlocking = isBlocking,
            Settings = settingsObj,
        };

        return await UpsertPolicy(svc, resolved, config, policyId, "set_repo_policy", ct);
    }

    [McpServerTool(Name = "set_min_reviewers_policy"),
     Description("Create or update the 'Minimum number of reviewers' branch policy. Pass policyId to update an existing one; omit to create. " +
                 "Disabled when the server is in read-only mode or the operation is not enabled.")]
    public static async Task<string> SetMinReviewersPolicy(
        AzureDevOpsService svc,
        [Description("Repository id or name to scope the policy to")] string repository,
        [Description("Required number of reviewers (>=1)")] int minimumApproverCount,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        [Description("Branch ref name. Default 'refs/heads/main'.")] string branch = "refs/heads/main",
        [Description("Scope match kind: 'exact' or 'prefix'. Default 'exact'.")] string matchKind = "exact",
        [Description("Allow the PR creator's vote to count. Default false.")] bool creatorVoteCounts = false,
        [Description("Reset approvals when new commits are pushed. Default true.")] bool resetOnSourcePush = true,
        [Description("Allow reviewers to leave a Rejected vote that doesn't block. Default false.")] bool allowDownvotes = false,
        [Description("Block the most recent pusher from approving their own changes. Default false.")] bool blockLastPusherVote = false,
        [Description("Reset rejected reviews on new commits. Default false.")] bool resetRejectionsOnSourcePush = false,
        [Description("Require at least one vote on the latest iteration. Default false.")] bool requireVoteOnLastIteration = false,
        [Description("Whether the policy blocks merges. Default true.")] bool isBlocking = true,
        [Description("Whether the policy is active. Default true.")] bool isEnabled = true,
        [Description("Existing policy configuration id to update; omit to create a new policy")] int? policyId = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("set_min_reviewers_policy");
        if (minimumApproverCount < 1)
            throw new ArgumentException("minimumApproverCount must be at least 1.", nameof(minimumApproverCount));

        var resolved = svc.ResolveProject(project);
        var repo = await ResolveRepository(svc, resolved, repository, ct);

        var settings = new JObject
        {
            ["minimumApproverCount"] = minimumApproverCount,
            ["creatorVoteCounts"] = creatorVoteCounts,
            ["allowDownvotes"] = allowDownvotes,
            ["resetOnSourcePush"] = resetOnSourcePush,
            ["resetRejectionsOnSourcePush"] = resetRejectionsOnSourcePush,
            ["blockLastPusherVote"] = blockLastPusherVote,
            ["requireVoteOnLastIteration"] = requireVoteOnLastIteration,
            ["scope"] = BuildScope(repo.Id, branch, matchKind),
        };

        var config = new PolicyConfiguration
        {
            Id = policyId ?? 0,
            Type = new PolicyTypeRef { Id = MinimumReviewersPolicyTypeId },
            IsEnabled = isEnabled,
            IsBlocking = isBlocking,
            Settings = settings,
        };

        return await UpsertPolicy(svc, resolved, config, policyId, "set_min_reviewers_policy", ct);
    }

    // --- helpers ---

    private static async Task<GitRepository> ResolveRepository(
        AzureDevOpsService svc, string project, string repository, CancellationToken ct)
    {
        var gitClient = svc.GetClient<GitHttpClient>();
        var repo = await gitClient.GetRepositoryAsync(project, repository, cancellationToken: ct)
            ?? throw new InvalidOperationException(
                $"Repository '{repository}' not found in project '{project}'.");
        return repo;
    }

    private static JArray BuildScope(Guid repositoryId, string? branch, string matchKind)
    {
        var entry = new JObject
        {
            ["repositoryId"] = repositoryId.ToString(),
        };
        if (!string.IsNullOrWhiteSpace(branch))
        {
            entry["refName"] = branch;
            entry["matchKind"] = string.IsNullOrWhiteSpace(matchKind) ? "exact" : matchKind;
        }
        return new JArray { entry };
    }

    private static IEnumerable<Guid> ScopeRepoIds(PolicyConfiguration p)
    {
        var scope = p.Settings?["scope"] as JArray;
        if (scope is null) yield break;
        foreach (var entry in scope.OfType<JObject>())
        {
            var idTok = entry["repositoryId"];
            if (idTok is not null && Guid.TryParse(idTok.ToString(), out var id))
                yield return id;
        }
    }

    private static async Task<string> UpsertPolicy(
        AzureDevOpsService svc, string project, PolicyConfiguration config, int? policyId, string operation, CancellationToken ct)
    {
        var client = svc.GetClient<PolicyHttpClient>();
        try
        {
            PolicyConfiguration result = policyId.HasValue
                ? await client.UpdatePolicyConfigurationAsync(config, project, policyId.Value, cancellationToken: ct)
                : await client.CreatePolicyConfigurationAsync(config, project, cancellationToken: ct);
            return JsonSerializer.Serialize(new
            {
                result.Id,
                TypeId = result.Type?.Id,
                TypeName = result.Type?.DisplayName,
                result.IsEnabled,
                result.IsBlocking,
                Settings = result.Settings?.ToString(),
            }, JsonOpts.Default);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"{operation} failed in project '{project}': {ex.Message}. " +
                "Common causes: PAT lacks 'Code (read, write & manage)'; user lacks 'Edit policies' on the repo; " +
                "policyTypeId is wrong or not applicable here; settings JSON is missing a required field for this policy type; " +
                "branch ref must look like 'refs/heads/main' (not just 'main').",
                ex);
        }
    }
}
