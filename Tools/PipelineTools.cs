using System.ComponentModel;
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

    private static T? ParseEnumOrNull<T>(string? value) where T : struct, Enum
        => string.IsNullOrWhiteSpace(value) ? null
            : Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed
            : null;
}
