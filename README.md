# AzureDevopsMCPSharp

A standalone C# **MCP (Model Context Protocol) server** for **Azure DevOps Server / on-premises** (non-cloud) deployments over Streamable HTTP.

## Features

- HTTP MCP server using the Streamable HTTP transport.
- **Read-only mode by default** — write/delete tools stay disabled until explicitly enabled.
- Configuration via `AzureDevopsMCPSharp.json`, environment variables, or command line.
- Serilog logging to console and rolling files.
- Targets Azure DevOps Server (TFS) — works against your internal `https://devops.your-domain/tfs/...` collection.

## Configuration

Configure via `AzureDevopsMCPSharp.json` or environment variables. Environment variables win over JSON; in Docker, use the `AZDOMCP_` prefix and `__` for nested keys:

| Setting | Default | Description |
| --- | --- | --- |
| `AzureDevOps:OrganizationUrl` | _(required)_ | Collection URL, e.g. `https://devops.local/DefaultCollection` |
| `AzureDevOps:PersonalAccessToken` | _(required)_ | PAT with sufficient scopes |
| `AzureDevOps:DefaultProject` | _(none)_ | Project used when tools are called without one |
| `AzureDevOps:ReadOnly` | `true` | When `true`, all write/delete tools are disabled |
| `AzureDevOps:Operations:<tool_name>` | _(missing = blocked)_ | Per-tool allow switch applied when `ReadOnly=false`. Only explicit `true` enables a write tool. See [Per-operation switches](#per-operation-switches). |
| `Server:Host` | `localhost` | Host to bind |
| `Server:Port` | `5700` | HTTP port |
| `Server:Path` | `/mcp` | MCP endpoint path |
| `Server:Password` | blank | Optional MCP endpoint password; blank disables password auth |

When `Server:Password` is set, MCP requests must provide the password as `Authorization: Bearer <password>`, the Basic auth password, or `X-MCP-Password`.

Booleans use `true` or `false`; per-operation switches use keys such as `AZDOMCP_AzureDevOps__Operations__delete_pipeline=true`.

## Running

```sh
dotnet run
```

Then point your MCP client at `http://localhost:5700/mcp`.

## Docker

A `Dockerfile` is provided for HTTP-mode hosting:

```sh
docker build -t azdomcp .
docker run --rm -p 5700:5700 \
    -e AZDOMCP_AzureDevOps__OrganizationUrl="https://devops.your-domain/DefaultCollection" \
    -e AZDOMCP_AzureDevOps__PersonalAccessToken="$AZDO_PAT" \
    -e AZDOMCP_AzureDevOps__DefaultProject="MyProject" \
    -e AZDOMCP_Server__Password="change-me" \
    azdomcp
```

The container listens on `http://0.0.0.0:5700/mcp`. Example write opt-in: `AZDOMCP_AzureDevOps__ReadOnly=false` plus an operation switch such as `AZDOMCP_AzureDevOps__Operations__delete_pipeline=true`. Read-only mode is on by default.

Logs go to stdout/stderr (Serilog console sink). To persist the rolling file logs as well, mount a volume:

```sh
docker run --rm -p 5700:5700 -v azdomcp-logs:/app/logs ...
```

## Running as a Windows Service

The host detects when it's launched by the Service Control Manager and switches to service mode automatically (config and logs resolve from the executable directory, not the SCM's `C:\Windows\System32` working directory).

Publish a self-contained build, then register it with `sc.exe` (run as Administrator):

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o C:\Services\AzureDevopsMCPSharp

sc.exe create AzureDevopsMCPSharp `
    binPath= "C:\Services\AzureDevopsMCPSharp\AzureDevopsMCPSharp.exe" `
    start= auto `
    DisplayName= "Azure DevOps MCP (C#)"
sc.exe description AzureDevopsMCPSharp "MCP server for on-prem Azure DevOps Server."
sc.exe start AzureDevopsMCPSharp
```

Put credentials in `C:\Services\AzureDevopsMCPSharp\AzureDevopsMCPSharp.Local.json` (or set `AZDOMCP_AzureDevOps__PersonalAccessToken` as a machine-level env var) — never in `AzureDevopsMCPSharp.json`, which is checked in.

To remove:

```powershell
sc.exe stop AzureDevopsMCPSharp
sc.exe delete AzureDevopsMCPSharp
```

Logs land in `<install-dir>\logs\azdomcp-*.log`.

## Read-only mode

Read-only is **on by default**. To enable write tools, set `AzureDevOps:ReadOnly=false`.

## Per-operation switches

Even with `ReadOnly=false`, each write tool can be individually enabled or disabled via `AzureDevOps:Operations`. This lets you grant, say, work-item updates but keep pipeline deletion off.

`ReadOnly=true` overrides everything: it blocks all writes regardless of the per-op switches. `ReadOnly=false` then defers to the `Operations` map — **only an explicit `true` enables a tool**; missing entries and explicit `false` both block. This makes new write tools added in future releases opt-in by default for existing configs.

All write operations ship **disabled by default** in `AzureDevopsMCPSharp.json`. Opt in to the ones you want:

```json
"AzureDevOps": {
  "ReadOnly": false,
  "Operations": {
    "queue_pipeline_run": false,
    "cancel_pipeline_run": false,
    "add_pipeline_run_tags": false,
    "remove_pipeline_run_tag": false,
    "create_pipeline": false,
    "rename_pipeline": false,
    "move_pipeline": false,
    "delete_pipeline": false,
    "authorize_pipeline_resource": false,
    "create_work_item": false,
    "update_work_item": false,
    "create_repository": false,
    "rename_repository": false,
    "delete_repository": false,
    "delete_repo_policy": false,
    "set_policy_enabled": false,
    "set_repo_policy": false,
    "set_min_reviewers_policy": false,
    "set_bypass_push_policy_self": false,
    "set_bypass_push_policy_group": false,
    "set_bypass_push_policy_user": false,
    "create_or_update_wiki_page": false,
    "delete_wiki_page": false,
    "move_wiki_page": false,
    "create_wiki": false,
    "update_wiki": false,
    "delete_wiki": false,
    "create_wiki_attachment": false,
    "add_wiki_page_comment": false,
    "update_wiki_page_comment": false,
    "delete_wiki_page_comment": false
  }
}
```

| Operation | Tool | Notes |
| --- | --- | --- |
| `azdo_queue_pipeline_run` | Queue a pipeline run | |
| `azdo_cancel_pipeline_run` | Cancel an in-progress run | |
| `azdo_add_pipeline_run_tags` | Stamp tags onto a run | |
| `azdo_remove_pipeline_run_tag` | Remove a tag from a run | |
| `azdo_create_pipeline` | Create a YAML pipeline definition | |
| `azdo_rename_pipeline` | Rename a pipeline | |
| `azdo_move_pipeline` | Move a pipeline to a different folder | |
| `azdo_delete_pipeline` | **Permanently** delete a pipeline | No undo. Shipped default is `false`. |
| `azdo_authorize_pipeline_resource` | Grant a pipeline permission to use a protected resource | |
| `azdo_create_work_item` | Create a work item | |
| `azdo_update_work_item` | Patch fields on a work item | |
| `azdo_create_repository` | Create a new empty Git repository | |
| `azdo_rename_repository` | Rename a Git repository | |
| `azdo_delete_repository` | **Permanently** delete a Git repository | No undo. Wipes branches, history, PRs. |
| `azdo_delete_repo_policy` | Delete a branch/repo policy configuration | |
| `azdo_set_policy_enabled` | Toggle a policy on/off without changing its settings | |
| `azdo_set_repo_policy` | Generic create/update for any policy type (pass type Guid + raw settings JSON) | |
| `azdo_set_min_reviewers_policy` | Typed helper for the 'Minimum number of reviewers' policy | |
| `azdo_set_bypass_push_policy_self` | Allow / deny / clear 'Bypass policies when pushing' for the PAT's own identity | Repo-scoped or branch-scoped. |
| `azdo_set_bypass_push_policy_group` | Same, scoped to a named group (e.g. 'Project Administrators') | |
| `azdo_set_bypass_push_policy_user` | Same, scoped to a single user (email / account / display name) | |
| `azdo_set_pull_request_vote` | Set the caller's vote on a PR (approve / approve-with-suggestions / no-vote / waiting-for-author / reject) | |
| `azdo_add_pull_request_comment` | Add a comment to a PR (new thread, or reply into a given `threadId`) | |
| `azdo_add_pull_request_review_comment` | Add an inline review comment anchored to a file + line | Multi-line via `startLine`; `left`/`right` side. |
| `azdo_request_pull_request_reviewers` | Request reviewers on a PR (optional `required`) | Resolves emails / unique names via the Identity API. |
| `azdo_create_pull_request` | Create a pull request (source → target branch) | |
| `azdo_complete_pull_request` | Complete (merge) a PR — noFastForward / squash / rebase / rebaseMerge | Optional delete source branch. |
| `bypass_pull_request_policy` | Allow `azdo_complete_pull_request` to override unmet branch policies (`bypassPolicy=true`) | Gates the override only; the AzDO 'Bypass policies when completing' permission is still enforced server-side. |
| `azdo_abandon_pull_request` | Mark a PR as Abandoned (AzDO equivalent of 'deny/cancel') | Reversible via `azdo_reactivate_pull_request`. |
| `azdo_reactivate_pull_request` | Set an abandoned PR back to Active | |
| `azdo_create_or_update_wiki_page` | Create or edit a project-wiki page (Markdown) | Editing uses ETag/If-Match concurrency. Project wikis only. |
| `azdo_delete_wiki_page` | **Permanently** delete a wiki page | No undo. Project wikis only. |
| `azdo_move_wiki_page` | Move / rename a wiki page and its sub-pages | Project wikis only. |
| `azdo_create_wiki` | Create a project wiki, or publish a Git folder as a code wiki | |
| `azdo_update_wiki` | Rename a wiki | |
| `azdo_delete_wiki` | **Permanently** delete / unpublish a wiki | Code wiki: unpublishes only (repo untouched). Project wiki: deletes pages. |
| `azdo_create_wiki_attachment` | Upload a binary attachment (e.g. an image) to a wiki | Content is base64. |
| `azdo_add_wiki_page_comment` | Add a comment to a wiki page (optionally a reply) | |
| `azdo_update_wiki_page_comment` | Edit a wiki page comment | |
| `azdo_delete_wiki_page_comment` | Delete a wiki page comment | |

When a tool is called but its operation is not explicitly enabled, the server throws a clear error naming the exact setting to flip:

> `Operation 'delete_pipeline' is blocked: not enabled in AzureDevOps:Operations (missing entries default to disabled). Set AzureDevOps:Operations:delete_pipeline=true to enable it.`

Override individual switches via env var, e.g. `AZDOMCP_AzureDevOps__Operations__delete_pipeline=true`.

## Work items

Work item types differ from collection to collection: a customised type can require fields
that stock types do not, and picklist fields only accept their declared values. Two read-only
tools expose that schema so a payload can be built against the live definition rather than
guesswork:

- `azdo_list_work_item_types` — the types available in a project.
- `azdo_get_work_item_type_fields` — every field on a type: reference name, `alwaysRequired`,
  default value, help text, and `allowedValues` for picklists. Pass `requiredOnly: true` to
  narrow it to the fields the type insists on.

`azdo_create_work_item` takes `title` and `description` directly; everything else goes in
`fields`, keyed by field reference name — area and iteration paths, plus any custom fields the
type requires:

```json
{
  "type": "Bug",
  "title": "Login page returns 500",
  "fields": {
    "System.AreaPath": "MyProject\Web",
    "System.IterationPath": "MyProject\Sprint 12",
    "Microsoft.VSTS.Common.Priority": "1"
  }
}
```

Creating a work item without a required field fails server-side, so query the type first when
the type is unfamiliar. `azdo_update_work_item` takes the same `fields` shape.

## Pull request review

Full PR review surface:

- **View**: `azdo_list_pull_requests`, `azdo_get_pull_request`, `azdo_list_pull_request_iterations`, `azdo_list_pull_request_changes` (per-iteration file changes), `azdo_get_pull_request_diff` (source-vs-target commit diff), `azdo_get_pull_request_file` (file content at the source-branch commit), `azdo_list_pull_request_reviewers` (with current votes), `azdo_list_pull_request_threads` (review threads + comments), `azdo_list_pull_request_work_items`, `azdo_get_pull_request_policy_evaluations` (build validation, required reviewers, …).
- **Create**: `azdo_create_pull_request` (title, source → target branch, optional description, `isDraft`).
- **Request review**: `azdo_request_pull_request_reviewers` (identity ids, or emails / unique names / display names resolved via the Identity API; optional `required`).
- **Decide**: `azdo_set_pull_request_vote` sets the caller's vote — `approve`, `approve-with-suggestions`, `no-vote`, `waiting-for-author`, or `reject` (AzDO's "deny").
- **Discuss**: `azdo_add_pull_request_comment` opens a new thread or replies into an existing one by `threadId`; `azdo_add_pull_request_review_comment` anchors a new thread to a file + `line`/`side` (multi-line via `startLine`; markdown supported).
- **Complete**: `azdo_complete_pull_request` (`mergeStrategy` = noFastForward / squash / rebase / rebaseMerge, optional `deleteSourceBranch`). Set `bypassPolicy=true` with a `bypassReason` to **override branch policies** (unmet approvals / required reviewers / checks) — the equivalent of "Override branch policies and enable merge" in the UI. This needs the caller's PAT/identity to hold the *Bypass policies when completing pull requests* permission **and** the separate `bypass_pull_request_policy` operation switch to be enabled (on top of `complete_pull_request`).
- **Cancel**: `azdo_abandon_pull_request`; `azdo_reactivate_pull_request` to undo.

All create/decide/discuss/complete/cancel tools require both `AzureDevOps:ReadOnly=false` AND the per-operation switch (`AzureDevOps:Operations:<tool_name>=true`).

> **Line breaks:** PR descriptions and comment bodies accept markdown. If a caller sends literal `\n` escape sequences instead of real line breaks (a common mistake) and the text has no real newlines, the server converts them to actual line breaks so the content renders correctly. Text that already contains real newlines is left untouched.

The create → review → comment → approve → complete lifecycle is unified across the GitHub, Azure DevOps and GitLab MCP servers (see each server's README).

## Wiki

Read and edit the wiki built into Azure DevOps.

**Reads** (no per-operation switch needed):

- `azdo_list_wikis` — project + code wikis with their type/repo mapping; `azdo_get_wiki` — one wiki by name/GUID.
- `azdo_list_wiki_pages` — the page tree (paths only); `azdo_get_wiki_page` — one page's Markdown content, its page **Id** and its **ETag** (large content is truncated at ~50 KB with a `truncated` flag).
- `azdo_get_wiki_attachment` — download an attachment by name (returned as base64; large files rejected).
- `azdo_list_wiki_page_comments` — comments on a page (by page id); `azdo_get_wiki_page_stats` — page view counts over the last N days (by page id).
- `azdo_search_wiki` — full-text search across pages. **Needs the Azure DevOps Server 'Search' extension** installed/configured on the collection; when it isn't, the tool returns `{ "available": false, ... }` rather than erroring.

**Writes** (require `ReadOnly=false` **and** the matching per-operation switch):

- Pages: `azdo_create_or_update_wiki_page` (create/edit), `azdo_delete_wiki_page`, `azdo_move_wiki_page` (move/rename a page and its sub-pages).
- Wikis: `azdo_create_wiki` (project wiki, or publish a Git folder as a code wiki), `azdo_update_wiki` (rename), `azdo_delete_wiki`.
- Attachments: `azdo_create_wiki_attachment` (upload base64 content, e.g. an image, and get back the path to embed in Markdown).
- Comments: `azdo_add_wiki_page_comment` (optionally a reply via `parentCommentId`), `azdo_update_wiki_page_comment`, `azdo_delete_wiki_page_comment`.

Page editing is guarded by optimistic concurrency (ETag / If-Match): a **create** sends no ETag, an **update** must send the page's current ETag. `azdo_get_wiki_page` returns it, and `azdo_create_or_update_wiki_page` accepts an optional `etag` — if omitted it auto-probes the current value, and it returns the **new** ETag so an agent can chain consecutive edits. A stale ETag yields a clear "page changed on the server, re-read and retry" error (HTTP 412). `wikiIdentifier` accepts either the wiki **name** or its **GUID**; page paths start with `/` (e.g. `/Home`, `/Folder/Page`). Comment and stats tools take the numeric **page id** from `azdo_get_wiki_page`.

Page create/update/delete/move target **project wikis**. Code wikis are Git-backed, so page edits there go through the backing repo's commit flow — use the Git repo tools for those (`azdo_create_wiki` / `azdo_delete_wiki` still manage the code-wiki publication itself).

**PAT scopes:** reading needs **Wiki (Read)**; create/update/delete/move and attachments/comments need **Wiki (Read & Write)**. Search additionally depends on the Search extension being installed.

## Pipelines / CI

Read tools for diagnosing a failing run down to the individual job (no per-operation switch needed — these are reads):

- **Runs**: `azdo_list_pipelines`, `azdo_get_pipeline`, `azdo_list_pipeline_runs`, `azdo_get_pipeline_run`.
- **Per-job**: `azdo_list_pipeline_jobs` reads the run's timeline and lists each job with its state, result, timing, worker and the `LogId` to fetch (pass `includeTasks=true` to expand the task steps, or `onlyFailed=true` to narrow to the jobs that broke); `azdo_get_job_log` fetches a single job's log by its timeline record id, truncated to `maxBytes` (default 200 KB) to protect agent context.
- **Whole-run logs**: `azdo_list_build_logs` / `azdo_get_build_log` still expose the raw log blobs by log id.

Typical flow: `azdo_list_pipeline_runs` → `azdo_list_pipeline_jobs buildId onlyFailed=true` → `azdo_get_job_log buildId jobId`. This mirrors the per-job log flow in the GitHub and GitLab MCP servers.
