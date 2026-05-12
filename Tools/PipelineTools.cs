using System.ComponentModel;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AzureDevopsMCPSharp.Services;
using Microsoft.TeamFoundation.Build.WebApi;
using ModelContextProtocol.Server;

namespace AzureDevopsMCPSharp.Tools;

[McpServerToolType]
public static class PipelineTools
{
    [McpServerTool(Name = "list_pipelines"),
     Description("List build/YAML pipeline definitions in a project.")]
    public static async Task<string> ListPipelines(
        AzureDevOpsService svc,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        [Description("Optional name filter (supports * wildcards)")] string? nameFilter = null,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<BuildHttpClient>();
        var defs = await client.GetDefinitionsAsync(resolved, name: nameFilter, cancellationToken: ct);
        var summary = defs.Select(d => new
        {
            d.Id,
            d.Name,
            Path = d.Path,
            Type = d.Type.ToString(),
            QueueStatus = d.QueueStatus.ToString(),
            Repository = d.Project?.Name,
            d.Revision,
        });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "get_pipeline"),
     Description("Get full definition for a single pipeline by id.")]
    public static async Task<string> GetPipeline(
        AzureDevOpsService svc,
        [Description("Pipeline (build definition) id")] int definitionId,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<BuildHttpClient>();
        var def = await client.GetDefinitionAsync(resolved, definitionId, cancellationToken: ct);
        return JsonSerializer.Serialize(def, JsonOpts.Default);
    }

    [McpServerTool(Name = "list_pipeline_runs"),
     Description("List runs (builds) for a project, optionally filtered by pipeline id, branch, status or result.")]
    public static async Task<string> ListPipelineRuns(
        AzureDevOpsService svc,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        [Description("Pipeline (definition) id to filter on")] int? definitionId = null,
        [Description("Branch ref (e.g. refs/heads/main)")] string? branch = null,
        [Description("Status filter: inProgress, completed, cancelling, postponed, notStarted, all")] string? status = null,
        [Description("Result filter: succeeded, partiallySucceeded, failed, canceled")] string? result = null,
        [Description("Max items to return (default 25)")] int top = 25,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<BuildHttpClient>();

        var defs = definitionId.HasValue ? new[] { definitionId.Value } : null;
        BuildStatus? statusFilter = ParseEnumOrNull<BuildStatus>(status);
        BuildResult? resultFilter = ParseEnumOrNull<BuildResult>(result);

        var builds = await client.GetBuildsAsync(
            project: resolved,
            definitions: defs,
            branchName: branch,
            statusFilter: statusFilter,
            resultFilter: resultFilter,
            top: top,
            cancellationToken: ct);

        var summary = builds.Select(b => new
        {
            b.Id,
            b.BuildNumber,
            Definition = b.Definition?.Name,
            DefinitionId = b.Definition?.Id,
            Status = b.Status?.ToString(),
            Result = b.Result?.ToString(),
            b.SourceBranch,
            b.SourceVersion,
            RequestedFor = b.RequestedFor?.DisplayName,
            b.QueueTime,
            b.StartTime,
            b.FinishTime,
            b.Url,
        });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "get_pipeline_run"),
     Description("Get full details for a single pipeline run (build) by id.")]
    public static async Task<string> GetPipelineRun(
        AzureDevOpsService svc,
        [Description("Build / run id")] int buildId,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<BuildHttpClient>();
        var build = await client.GetBuildAsync(resolved, buildId, cancellationToken: ct);
        return JsonSerializer.Serialize(build, JsonOpts.Default);
    }

    [McpServerTool(Name = "list_build_logs"),
     Description("List log entries for a pipeline run. Use get_build_log to fetch the content of a specific entry.")]
    public static async Task<string> ListBuildLogs(
        AzureDevOpsService svc,
        [Description("Build / run id")] int buildId,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<BuildHttpClient>();
        var logs = await client.GetBuildLogsAsync(resolved, buildId, cancellationToken: ct);
        var summary = logs.Select(l => new { l.Id, l.Type, l.LineCount, l.CreatedOn, l.LastChangedOn, l.Url });
        return JsonSerializer.Serialize(summary, JsonOpts.Default);
    }

    [McpServerTool(Name = "get_build_log"),
     Description("Fetch the text content of a single build log entry. Output is truncated to maxBytes (default 200KB) to protect agent context.")]
    public static async Task<string> GetBuildLog(
        AzureDevOpsService svc,
        [Description("Build / run id")] int buildId,
        [Description("Log id (from list_build_logs)")] int logId,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        [Description("Max bytes to return; remainder is truncated (default 204800)")] int maxBytes = 204800,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<BuildHttpClient>();
        await using var stream = await client.GetBuildLogAsync(resolved, buildId, logId, cancellationToken: ct);

        using var reader = new StreamReader(stream);
        var buffer = new char[Math.Max(1024, maxBytes)];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maxBytes)), ct);
        var truncated = !reader.EndOfStream;
        var text = new string(buffer, 0, read);
        if (truncated)
            text += $"\n\n[truncated at {maxBytes} bytes — fetch with a larger maxBytes for more]";
        return text;
    }

    [McpServerTool(Name = "queue_pipeline_run"),
     Description("Queue a new run of a pipeline. Disabled when the server is in read-only mode.")]
    public static async Task<string> QueuePipelineRun(
        AzureDevOpsService svc,
        [Description("Pipeline (definition) id to queue")] int definitionId,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        [Description("Optional source branch, e.g. refs/heads/main")] string? branch = null,
        [Description("Optional source commit/version")] string? sourceVersion = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("queue_pipeline_run");
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<BuildHttpClient>();

        var build = new Build
        {
            Definition = new DefinitionReference { Id = definitionId },
            Project = new Microsoft.TeamFoundation.Core.WebApi.TeamProjectReference { Name = resolved },
        };
        if (!string.IsNullOrWhiteSpace(branch)) build.SourceBranch = branch;
        if (!string.IsNullOrWhiteSpace(sourceVersion)) build.SourceVersion = sourceVersion;

        var queued = await client.QueueBuildAsync(build, cancellationToken: ct);
        return JsonSerializer.Serialize(queued, JsonOpts.Default);
    }

    [McpServerTool(Name = "cancel_pipeline_run"),
     Description("Cancel an in-progress pipeline run. Disabled when the server is in read-only mode.")]
    public static async Task<string> CancelPipelineRun(
        AzureDevOpsService svc,
        [Description("Build / run id to cancel")] int buildId,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("cancel_pipeline_run");
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<BuildHttpClient>();

        var update = new Build { Id = buildId, Status = BuildStatus.Cancelling };
        var updated = await client.UpdateBuildAsync(update, cancellationToken: ct);
        return JsonSerializer.Serialize(updated, JsonOpts.Default);
    }

    [McpServerTool(Name = "list_pipeline_run_tags"),
     Description("List the tags stamped on a single pipeline run.")]
    public static async Task<string> ListPipelineRunTags(
        AzureDevOpsService svc,
        [Description("Build / run id")] int buildId,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<BuildHttpClient>();
        var tags = await client.GetBuildTagsAsync(resolved, buildId, cancellationToken: ct);
        return JsonSerializer.Serialize(tags, JsonOpts.Default);
    }

    [McpServerTool(Name = "add_pipeline_run_tags"),
     Description("Add one or more tags to a pipeline run. Returns the resulting full tag list. Disabled when the server is in read-only mode.")]
    public static async Task<string> AddPipelineRunTags(
        AzureDevOpsService svc,
        [Description("Build / run id")] int buildId,
        [Description("Tags to add")] string[] tags,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("add_pipeline_run_tags");
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<BuildHttpClient>();
        var updated = await client.AddBuildTagsAsync(tags.ToList(), resolved, buildId, cancellationToken: ct);
        return JsonSerializer.Serialize(updated, JsonOpts.Default);
    }

    [McpServerTool(Name = "remove_pipeline_run_tag"),
     Description("Remove a single tag from a pipeline run. Returns the resulting full tag list. Disabled when the server is in read-only mode.")]
    public static async Task<string> RemovePipelineRunTag(
        AzureDevOpsService svc,
        [Description("Build / run id")] int buildId,
        [Description("Tag to remove")] string tag,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("remove_pipeline_run_tag");
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<BuildHttpClient>();
        var updated = await client.DeleteBuildTagAsync(resolved, buildId, tag, cancellationToken: ct);
        return JsonSerializer.Serialize(updated, JsonOpts.Default);
    }

    [McpServerTool(Name = "create_pipeline"),
     Description("Create a new YAML pipeline definition pointing at an existing repository and YAML file. Disabled when the server is in read-only mode.")]
    public static async Task<string> CreatePipeline(
        AzureDevOpsService svc,
        [Description("Display name for the new pipeline")] string name,
        [Description("Repository name (must already exist in the project)")] string repositoryName,
        [Description("Path to the YAML file in the repo, e.g. 'azure-pipelines.yml' or '/src/build/ci.yml'")] string yamlPath,
        [Description("Agent queue id to run on (from list_agent_pools / project queues)")] int agentQueueId,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        [Description("Folder path, e.g. '\\\\MyFolder\\\\Sub'. Defaults to root ('\\\\').")] string folderPath = "\\",
        [Description("Default branch ref. Defaults to 'refs/heads/main'.")] string defaultBranch = "refs/heads/main",
        [Description("Repository type. Defaults to 'TfsGit' (Azure Repos Git). Other values: 'GitHub', 'Bitbucket', 'Git'.")] string repositoryType = "TfsGit",
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("create_pipeline");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(repositoryName)) throw new ArgumentException("repositoryName is required.", nameof(repositoryName));
        if (string.IsNullOrWhiteSpace(yamlPath)) throw new ArgumentException("yamlPath is required.", nameof(yamlPath));

        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<BuildHttpClient>();

        Guid repoId;
        if (string.Equals(repositoryType, "TfsGit", StringComparison.OrdinalIgnoreCase))
        {
            var gitClient = svc.GetClient<Microsoft.TeamFoundation.SourceControl.WebApi.GitHttpClient>();
            var repo = await gitClient.GetRepositoryAsync(resolved, repositoryName, cancellationToken: ct)
                ?? throw new InvalidOperationException(
                    $"Repository '{repositoryName}' not found in project '{resolved}'.");
            repoId = repo.Id;
        }
        else
        {
            throw new NotSupportedException(
                $"Repository type '{repositoryType}' is not yet supported by this tool — only 'TfsGit' (Azure Repos Git) is implemented.");
        }

        var definition = new BuildDefinition
        {
            Name = name,
            Path = folderPath,
            Project = new Microsoft.TeamFoundation.Core.WebApi.TeamProjectReference { Name = resolved },
            Repository = new BuildRepository
            {
                Id = repoId.ToString(),
                Name = repositoryName,
                Type = repositoryType,
                DefaultBranch = defaultBranch,
            },
            Process = new YamlProcess { YamlFilename = yamlPath },
            Queue = new AgentPoolQueue { Id = agentQueueId },
        };

        try
        {
            var created = await client.CreateDefinitionAsync(definition, resolved, cancellationToken: ct);
            return JsonSerializer.Serialize(created, JsonOpts.Default);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"create_pipeline failed for '{name}' in project '{resolved}': {ex.Message}. " +
                "Common causes: agent queue id wrong (use list_agent_pools to find the queue id, not pool id); " +
                "repository or yamlPath does not exist; PAT lacks 'Build: read & execute' or the user lacks 'Edit build pipeline' on the folder.",
                ex);
        }
    }

    [McpServerTool(Name = "rename_pipeline"),
     Description("Rename an existing pipeline definition. Disabled when the server is in read-only mode.")]
    public static async Task<string> RenamePipeline(
        AzureDevOpsService svc,
        [Description("Pipeline (definition) id to rename")] int definitionId,
        [Description("New display name")] string newName,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("rename_pipeline");
        if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("newName is required.", nameof(newName));
        return await UpdatePipelineCore(svc, definitionId, project, d => d.Name = newName, "rename_pipeline", ct);
    }

    [McpServerTool(Name = "move_pipeline"),
     Description("Move a pipeline to a different folder (e.g. '\\\\Team A\\\\CI'). Use '\\\\' for the root. Disabled when the server is in read-only mode.")]
    public static async Task<string> MovePipeline(
        AzureDevOpsService svc,
        [Description("Pipeline (definition) id to move")] int definitionId,
        [Description("New folder path, e.g. '\\\\MyFolder\\\\Sub'. Use '\\\\' for the root.")] string newFolderPath,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("move_pipeline");
        if (string.IsNullOrWhiteSpace(newFolderPath))
            throw new ArgumentException("newFolderPath is required (use '\\\\' for the root).", nameof(newFolderPath));
        return await UpdatePipelineCore(svc, definitionId, project, d => d.Path = newFolderPath, "move_pipeline", ct);
    }

    [McpServerTool(Name = "delete_pipeline"),
     Description("Permanently delete a pipeline definition. There is no recycle bin — this cannot be undone. Disabled when the server is in read-only mode.")]
    public static async Task<string> DeletePipeline(
        AzureDevOpsService svc,
        [Description("Pipeline (definition) id to delete")] int definitionId,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("delete_pipeline");
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<BuildHttpClient>();
        try
        {
            await client.DeleteDefinitionAsync(resolved, definitionId, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"delete_pipeline failed for id {definitionId} in project '{resolved}': {ex.Message}. " +
                "Common causes: id does not exist; PAT lacks 'Build: read & execute' or the user lacks 'Delete build pipeline' on the folder.",
                ex);
        }
        return JsonSerializer.Serialize(new { deleted = true, definitionId, project = resolved }, JsonOpts.Default);
    }

    private static async Task<string> UpdatePipelineCore(
        AzureDevOpsService svc,
        int definitionId,
        string? project,
        Action<BuildDefinition> mutate,
        string operation,
        CancellationToken ct)
    {
        var resolved = svc.ResolveProject(project);
        var client = svc.GetClient<BuildHttpClient>();

        BuildDefinition existing;
        try
        {
            existing = await client.GetDefinitionAsync(resolved, definitionId, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"{operation} failed: could not load pipeline {definitionId} in project '{resolved}'. {ex.Message}", ex);
        }

        mutate(existing);

        try
        {
            var updated = await client.UpdateDefinitionAsync(existing, cancellationToken: ct);
            return JsonSerializer.Serialize(updated, JsonOpts.Default);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"{operation} failed for pipeline {definitionId} in project '{resolved}': {ex.Message}. " +
                "Common causes: PAT lacks 'Build: read & execute' write scope; user lacks 'Edit build pipeline' on the source/target folder; " +
                "name collides with an existing pipeline in the same folder; the pipeline was modified concurrently (revision mismatch).",
                ex);
        }
    }

    [McpServerTool(Name = "authorize_pipeline_resource"),
     Description("Grant a pipeline permanent permission to use a protected resource (service connection, agent queue, variable group, secure file, environment, or repository). " +
                 "Equivalent to clicking 'Permit' on the pending-authorization banner with 'Permit permanently' enabled. Disabled when the server is in read-only mode.")]
    public static async Task<string> AuthorizePipelineResource(
        AzureDevOpsService svc,
        [Description("Pipeline (build definition) id that should be allowed to use the resource")] int definitionId,
        [Description("Resource type: endpoint (service connection), queue (agent queue), variablegroup, securefile, environment, or repository")] string resourceType,
        [Description("Resource id. For 'endpoint'/'variablegroup'/'securefile'/'environment' this is the numeric id; for 'queue' it is the queue id (not the pool id); for 'repository' use the form '{projectId}.{repositoryId}'.")] string resourceId,
        [Description("Project name (optional, falls back to DefaultProject)")] string? project = null,
        [Description("Set false to revoke instead of grant (default true)")] bool authorized = true,
        CancellationToken ct = default)
    {
        svc.EnsureWriteAllowed("authorize_pipeline_resource");

        var resolvedProject = svc.ResolveProject(project);
        var normalizedType = (resourceType ?? string.Empty).Trim().ToLowerInvariant();
        var allowed = new[] { "endpoint", "queue", "variablegroup", "securefile", "environment", "repository" };
        if (Array.IndexOf(allowed, normalizedType) < 0)
        {
            throw new ArgumentException(
                $"Invalid resourceType '{resourceType}'. Expected one of: {string.Join(", ", allowed)}.",
                nameof(resourceType));
        }
        if (string.IsNullOrWhiteSpace(resourceId))
            throw new ArgumentException("resourceId is required.", nameof(resourceId));

        var url = $"{Uri.EscapeDataString(resolvedProject)}/_apis/pipelines/pipelinepermissions/" +
                  $"{normalizedType}/{Uri.EscapeDataString(resourceId)}?api-version=7.1-preview.1";

        var payload = new
        {
            pipelines = new[] { new { id = definitionId, authorized } },
        };

        using var resp = await svc.RestClient.PatchAsJsonAsync(url, payload, JsonOpts.Default, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw BuildAuthorizationError(resp.StatusCode, body, normalizedType, resourceId, definitionId);

        return body;
    }

    private static InvalidOperationException BuildAuthorizationError(
        HttpStatusCode status, string body, string resourceType, string resourceId, int definitionId)
    {
        var hint = status switch
        {
            HttpStatusCode.Unauthorized =>
                "PAT rejected (401). The token is invalid or expired.",
            HttpStatusCode.Forbidden =>
                $"PAT/user lacks permission to authorize this {resourceType} (403). " +
                "The PAT needs the scope for this resource type (e.g. 'Service Connections: read, query & manage' for endpoints, " +
                "'Agent Pools: read & manage' for queues, 'Environment: read & manage' for environments), " +
                "AND the user must have Administrator role on the resource itself.",
            HttpStatusCode.NotFound =>
                $"Not found (404). Check that resourceType='{resourceType}' and resourceId='{resourceId}' exist in this project, " +
                "and that pipeline id is correct. Note: 'queue' takes the queue id, not the pool id; 'repository' uses '{projectId}.{repositoryId}'.",
            HttpStatusCode.BadRequest =>
                "Bad request (400). The resource id format is likely wrong for this resource type.",
            _ => $"HTTP {(int)status} {status}.",
        };
        return new InvalidOperationException(
            $"authorize_pipeline_resource failed for pipeline {definitionId} on {resourceType}/{resourceId}: {hint}\nResponse body: {body}");
    }

    private static T? ParseEnumOrNull<T>(string? value) where T : struct, Enum
        => string.IsNullOrWhiteSpace(value) ? null
            : Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed
            : null;
}
