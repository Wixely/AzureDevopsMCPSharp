using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureDevopsMCPSharp.Services;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using ModelContextProtocol.Server;

namespace AzureDevopsMCPSharp.Tools;

[McpServerToolType]
public static class MentionsTools
{
    [McpServerTool(Name = "list_mentions_since"),
     Description("Find recent work items and discussion comments in an Azure DevOps project where a given substring appears in the description, history, or discussion threads (typically a user/group mention such as \"@bot\" or any other phrase). Designed for polling-style consumers - returns a stable JSON shape with the match kind, the author, the body, the URL and timestamps.")]
    public static async Task<string> ListMentionsSince(
        AzureDevOpsService svc,
        [Description("Substring to search for in work item descriptions, history, and discussion comments. Required. Case-insensitive.")] string mention,
        [Description("Project name. Falls back to AzureDevOps:DefaultProject.")] string? project = null,
        [Description("ISO-8601 UTC timestamp. Only return matches updated after this. Omit for no lower bound.")] string? sinceUtc = null,
        [Description("Include closed/resolved work items. Default false.")] bool includeClosed = false,
        [Description("Max matches returned. Default 50, hard cap 200.")] int limit = 50,
        CancellationToken ct = default)
    {
        var polledAt = DateTimeOffset.UtcNow;
        try
        {
            var resolvedProject = svc.ResolveProject(project);
            if (string.IsNullOrWhiteSpace(mention))
                throw new ArgumentException("mention must be non-empty.", nameof(mention));

            DateTimeOffset? sinceParsed = null;
            if (!string.IsNullOrWhiteSpace(sinceUtc))
            {
                sinceParsed = DateTimeOffset.Parse(
                    sinceUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            }

            var cap = Math.Min(limit <= 0 ? 50 : limit, 200);
            var client = svc.GetClient<WorkItemTrackingHttpClient>();

            // WIQL doesn't accept parameters - escape single-quotes in the literals.
            var escapedProject = resolvedProject.Replace("'", "''");
            var escapedMention = mention.Replace("'", "''");

            var wiqlSb = new System.Text.StringBuilder();
            wiqlSb.Append("SELECT [System.Id], [System.Title], [System.WorkItemType], [System.AssignedTo], [System.ChangedDate], [System.CreatedDate] ");
            wiqlSb.Append("FROM WorkItems ");
            wiqlSb.Append($"WHERE [System.TeamProject] = '{escapedProject}' ");
            if (sinceParsed.HasValue)
            {
                var sinceLiteral = sinceParsed.Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                wiqlSb.Append($"AND [System.ChangedDate] >= '{sinceLiteral}' ");
            }
            wiqlSb.Append($"AND ([System.Description] CONTAINS WORDS '{escapedMention}' OR [System.History] CONTAINS WORDS '{escapedMention}') ");
            if (!includeClosed)
                wiqlSb.Append("AND [System.State] NOT IN ('Closed', 'Resolved', 'Done', 'Removed') ");
            wiqlSb.Append("ORDER BY [System.ChangedDate] DESC");

            var wiqlResult = await client.QueryByWiqlAsync(new Wiql { Query = wiqlSb.ToString() }, resolvedProject, top: cap, cancellationToken: ct);
            var ids = wiqlResult.WorkItems.Select(r => r.Id).ToArray();

            var matches = new List<MentionMatch>();
            var baseUri = svc.Connection.Uri.AbsoluteUri.TrimEnd('/');

            if (ids.Length > 0)
            {
                // Batch GetWorkItemsAsync caps at 200 ids per call - cap already enforces this.
                var workItems = await client.GetWorkItemsAsync(ids, expand: WorkItemExpand.Fields, cancellationToken: ct);

                foreach (var wi in workItems)
                {
                    if (wi.Id is not int wiId)
                        continue;

                    var title = GetField<string>(wi, "System.Title") ?? string.Empty;
                    var workItemType = GetField<string>(wi, "System.WorkItemType") ?? string.Empty;
                    var description = GetField<string>(wi, "System.Description") ?? string.Empty;
                    var history = GetField<string>(wi, "System.History") ?? string.Empty;
                    var createdDate = GetField<DateTime?>(wi, "System.CreatedDate");
                    var changedDate = GetField<DateTime?>(wi, "System.ChangedDate");
                    var author = ResolveIdentity(GetField<object>(wi, "System.ChangedBy"))
                                 ?? ResolveIdentity(GetField<object>(wi, "System.CreatedBy"))
                                 ?? string.Empty;

                    var url = $"{baseUri}/{Uri.EscapeDataString(resolvedProject)}/_workitems/edit/{wiId}";

                    var mentionedInWi =
                        description.Contains(mention, StringComparison.OrdinalIgnoreCase) ||
                        history.Contains(mention, StringComparison.OrdinalIgnoreCase);

                    if (mentionedInWi)
                    {
                        var body = description.Contains(mention, StringComparison.OrdinalIgnoreCase)
                            ? description
                            : history;
                        matches.Add(new MentionMatch
                        {
                            Kind = "work_item",
                            Project = resolvedProject,
                            Id = wiId,
                            CommentId = null,
                            WorkItemType = workItemType,
                            Title = title,
                            Author = author,
                            Body = body,
                            Url = url,
                            CreatedAt = ToUtc(createdDate),
                            UpdatedAt = ToUtc(changedDate),
                        });
                    }

                    // Now fetch comments and emit a match per matching comment.
                    try
                    {
                        var comments = await client.GetCommentsAsync(resolvedProject, wiId, cancellationToken: ct);
                        if (comments?.Comments != null)
                        {
                            foreach (var c in comments.Comments)
                            {
                                var text = c.Text ?? string.Empty;
                                if (!text.Contains(mention, StringComparison.OrdinalIgnoreCase))
                                    continue;

                                var commentUpdated = c.ModifiedDate != default ? c.ModifiedDate : c.CreatedDate;
                                if (sinceParsed.HasValue && commentUpdated < sinceParsed.Value.UtcDateTime)
                                    continue;

                                var commentAuthor = c.ModifiedBy?.UniqueName
                                                    ?? c.ModifiedBy?.DisplayName
                                                    ?? c.CreatedBy?.UniqueName
                                                    ?? c.CreatedBy?.DisplayName
                                                    ?? string.Empty;

                                matches.Add(new MentionMatch
                                {
                                    Kind = "work_item_comment",
                                    Project = resolvedProject,
                                    Id = wiId,
                                    CommentId = c.Id,
                                    WorkItemType = workItemType,
                                    Title = title,
                                    Author = commentAuthor,
                                    Body = text,
                                    Url = url,
                                    CreatedAt = ToUtc(c.CreatedDate),
                                    UpdatedAt = ToUtc(commentUpdated),
                                });
                            }
                        }
                    }
                    catch
                    {
                        // Comment fetch failure for a single work item shouldn't sink the poll.
                    }
                }
            }

            var ordered = matches
                .OrderByDescending(m => m.UpdatedAt ?? DateTimeOffset.MinValue)
                .Take(cap)
                .ToList();

            var payload = new
            {
                matches = ordered,
                polledAt = polledAt.UtcDateTime,
                since = sinceParsed?.UtcDateTime,
                mention,
            };

            return JsonSerializer.Serialize(payload, JsonOpts.Default);
        }
        catch (Exception ex)
        {
            var errorPayload = new
            {
                error = ex.Message,
                matches = Array.Empty<object>(),
                polledAt = polledAt.UtcDateTime,
                since = sinceUtc,
                mention,
            };
            return JsonSerializer.Serialize(errorPayload, JsonOpts.Default);
        }
    }

    private static T? GetField<T>(WorkItem wi, string name)
    {
        if (wi.Fields != null && wi.Fields.TryGetValue(name, out var raw) && raw is not null)
        {
            if (raw is T typed) return typed;
            try { return (T?)Convert.ChangeType(raw, typeof(T), CultureInfo.InvariantCulture); }
            catch { return default; }
        }
        return default;
    }

    private static string? ResolveIdentity(object? identity)
    {
        if (identity is null) return null;
        // IdentityRef has UniqueName/DisplayName but we treat it as a loose object to avoid SDK coupling.
        var t = identity.GetType();
        var uniqueName = t.GetProperty("UniqueName")?.GetValue(identity) as string;
        if (!string.IsNullOrWhiteSpace(uniqueName)) return uniqueName;
        var displayName = t.GetProperty("DisplayName")?.GetValue(identity) as string;
        return string.IsNullOrWhiteSpace(displayName) ? identity.ToString() : displayName;
    }

    private static DateTimeOffset? ToUtc(DateTime? dt)
    {
        if (!dt.HasValue) return null;
        var v = dt.Value;
        return v.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(v, TimeSpan.Zero),
            DateTimeKind.Local => new DateTimeOffset(v.ToUniversalTime(), TimeSpan.Zero),
            _ => new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc), TimeSpan.Zero),
        };
    }

    private sealed class MentionMatch
    {
        [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
        [JsonPropertyName("project")] public string Project { get; set; } = string.Empty;
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("commentId")] public int? CommentId { get; set; }
        [JsonPropertyName("workItemType")] public string WorkItemType { get; set; } = string.Empty;
        [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
        [JsonPropertyName("author")] public string Author { get; set; } = string.Empty;
        [JsonPropertyName("body")] public string Body { get; set; } = string.Empty;
        [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
        [JsonPropertyName("createdAt")] public DateTimeOffset? CreatedAt { get; set; }
        [JsonPropertyName("updatedAt")] public DateTimeOffset? UpdatedAt { get; set; }
    }
}
