using System.ComponentModel;
using System.Text.Json;
using AzureDevopsMCPSharp.Services;
using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.Identity;
using Microsoft.VisualStudio.Services.Identity.Client;
using Microsoft.VisualStudio.Services.Security;
using Microsoft.VisualStudio.Services.Security.Client;
using Microsoft.VisualStudio.Services.WebApi;
using ModelContextProtocol.Server;

namespace AzureDevopsMCPSharp.Tools;

[McpServerToolType]
public static class SecurityTools
{
    // Git Repositories security namespace.
    private static readonly Guid GitRepositoriesNamespaceId = new("2e9eb7ed-3c0a-47d4-87c1-0ffdd275fd87");

    // "Bypass policies when pushing" — the PolicyExempt permission bit on the Git namespace.
    private const int PolicyExemptBit = 128;

    [McpServerTool(Name = "set_bypass_push_policy_self"),
     Description("Grant / deny / clear the 'Bypass policies when pushing' permission for the identity owning the configured PAT. " +
                 "Use action='allow' to grant, 'deny' to explicitly deny, 'inherit' to remove the entry so the user falls back to group membership. " +
                 "Disabled when the server is in read-only mode or the operation is not enabled.")]
    public static async Task<string> SetBypassPushPolicySelf(
        AzureDevOpsService svc,
        [Description("Repository id or name to scope the permission to")] string repository,
        [Description("Action: 'allow', 'deny', or 'inherit' (remove the entry)")] string action,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        [Description("Branch ref, e.g. 'refs/heads/main'. Omit to scope at the whole repository.")] string? branch = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("set_bypass_push_policy_self");
        await svc.Connection.ConnectAsync(ct);
        var self = svc.Connection.AuthorizedIdentity
            ?? throw new InvalidOperationException("Could not resolve the PAT's authorized identity from the connection.");
        return await ApplyBypassPushPolicy(svc, project, repository, branch, self.Descriptor, self.DisplayName ?? self.ProviderDisplayName ?? "self", action, "set_bypass_push_policy_self", ct);
    }

    [McpServerTool(Name = "set_bypass_push_policy_group"),
     Description("Grant / deny / clear the 'Bypass policies when pushing' permission for a group (e.g. 'Project Administrators', '[ProjectName]\\Build Administrators'). " +
                 "Disabled when the server is in read-only mode or the operation is not enabled.")]
    public static async Task<string> SetBypassPushPolicyGroup(
        AzureDevOpsService svc,
        [Description("Repository id or name to scope the permission to")] string repository,
        [Description("Group display name, e.g. 'Project Administrators' or '[MyProject]\\Build Administrators'")] string groupName,
        [Description("Action: 'allow', 'deny', or 'inherit' (remove the entry)")] string action,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        [Description("Branch ref, e.g. 'refs/heads/main'. Omit to scope at the whole repository.")] string? branch = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("set_bypass_push_policy_group");
        if (string.IsNullOrWhiteSpace(groupName)) throw new ArgumentException("groupName is required.", nameof(groupName));
        var identity = await ResolveIdentity(svc, groupName, expectGroup: true, ct);
        return await ApplyBypassPushPolicy(svc, project, repository, branch, identity.Descriptor, identity.DisplayName ?? groupName, action, "set_bypass_push_policy_group", ct);
    }

    [McpServerTool(Name = "set_bypass_push_policy_user"),
     Description("Grant / deny / clear the 'Bypass policies when pushing' permission for a single user (email, account name, or display name). " +
                 "Disabled when the server is in read-only mode or the operation is not enabled.")]
    public static async Task<string> SetBypassPushPolicyUser(
        AzureDevOpsService svc,
        [Description("Repository id or name to scope the permission to")] string repository,
        [Description("User identifier — email, sAMAccountName, or display name")] string userName,
        [Description("Action: 'allow', 'deny', or 'inherit' (remove the entry)")] string action,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        [Description("Branch ref, e.g. 'refs/heads/main'. Omit to scope at the whole repository.")] string? branch = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("set_bypass_push_policy_user");
        if (string.IsNullOrWhiteSpace(userName)) throw new ArgumentException("userName is required.", nameof(userName));
        var identity = await ResolveIdentity(svc, userName, expectGroup: false, ct);
        return await ApplyBypassPushPolicy(svc, project, repository, branch, identity.Descriptor, identity.DisplayName ?? userName, action, "set_bypass_push_policy_user", ct);
    }

    // --- helpers ---

    private static async Task<Identity> ResolveIdentity(AzureDevOpsService svc, string query, bool expectGroup, CancellationToken ct)
    {
        var idClient = svc.GetClient<IdentityHttpClient>();
        var results = await idClient.ReadIdentitiesAsync(
            IdentitySearchFilter.General,
            query,
            cancellationToken: ct);
        if (results is null || results.Count == 0)
        {
            throw new InvalidOperationException(
                $"Identity '{query}' not found. Tip: groups often need the project-qualified form '[ProjectName]\\Group Name'. " +
                "Users can be looked up by email, sAMAccountName, or display name.");
        }
        // Prefer a match of the expected kind, but accept the first hit if nothing else fits.
        var preferred = results.FirstOrDefault(i => i.IsContainer == expectGroup) ?? results.First();
        return preferred;
    }

    private static async Task<string> ApplyBypassPushPolicy(
        AzureDevOpsService svc, string? project, string repository, string? branch,
        IdentityDescriptor identityDescriptor, string identityDisplay,
        string action, string operation, CancellationToken ct)
    {
        var resolvedProject = svc.ResolveProject(project);

        var projClient = svc.GetClient<ProjectHttpClient>();
        var teamProject = await projClient.GetProject(resolvedProject, includeCapabilities: false)
            ?? throw new InvalidOperationException($"Project '{resolvedProject}' not found.");

        var gitClient = svc.GetClient<GitHttpClient>();
        var repo = await gitClient.GetRepositoryAsync(resolvedProject, repository, cancellationToken: ct)
            ?? throw new InvalidOperationException($"Repository '{repository}' not found in project '{resolvedProject}'.");

        var token = $"repoV2/{teamProject.Id}/{repo.Id}";
        if (!string.IsNullOrWhiteSpace(branch))
        {
            if (!branch.StartsWith("refs/", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("branch must be a full ref like 'refs/heads/main'.", nameof(branch));
            token += $"/{branch}";
        }

        var normalized = (action ?? string.Empty).Trim().ToLowerInvariant();
        var security = svc.GetClient<SecurityHttpClient>();

        try
        {
            if (normalized == "inherit")
            {
                var removed = await security.RemoveAccessControlEntriesAsync(
                    GitRepositoriesNamespaceId, token,
                    new[] { identityDescriptor }, cancellationToken: ct);
                return JsonSerializer.Serialize(new
                {
                    action = "inherit",
                    token,
                    identity = identityDisplay,
                    removed,
                }, JsonOpts.Default);
            }

            int allow = 0, deny = 0;
            switch (normalized)
            {
                case "allow": allow = PolicyExemptBit; break;
                case "deny": deny = PolicyExemptBit; break;
                default:
                    throw new ArgumentException(
                        $"Invalid action '{action}'. Expected 'allow', 'deny', or 'inherit'.", nameof(action));
            }

            var ace = new AccessControlEntry
            {
                Descriptor = identityDescriptor,
                Allow = allow,
                Deny = deny,
            };

            var result = await security.SetAccessControlEntriesAsync(
                GitRepositoriesNamespaceId, token, new[] { ace }, merge: true, cancellationToken: ct);
            return JsonSerializer.Serialize(new
            {
                action = normalized,
                token,
                identity = identityDisplay,
                entries = result,
            }, JsonOpts.Default);
        }
        catch (Exception ex) when (ex is not ArgumentException and not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"{operation} failed for identity '{identityDisplay}' on '{token}': {ex.Message}. " +
                "Common causes: PAT lacks 'Identity (read)' or 'Code (read, write & manage)'; " +
                "calling user lacks 'Manage permissions' on the repository; " +
                "branch ref malformed (must look like 'refs/heads/main').",
                ex);
        }
    }
}
