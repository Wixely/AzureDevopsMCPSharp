# AzureDevopsMCPSharp

A standalone C# **MCP (Model Context Protocol) server** for **Azure DevOps Server / on-premises** (non-cloud) deployments. Speaks Claude Code style MCP commands over **HTTP streaming**.

## Features

- HTTP streaming MCP server (Streamable HTTP transport, compatible with Claude Code).
- **Read-only mode by default** — safe to attach to agents without risk of mutating projects, work items, repos, or pipelines.
- Configuration via `appsettings.json`, environment variables, or command line.
- Serilog logging to console and rolling files.
- Targets Azure DevOps Server (TFS) — works against your internal `https://devops.your-domain/tfs/...` collection.

## Configuration

Configure via `appsettings.json` or environment variables (env wins; use `AZDOMCP_` prefix or standard `__` separator):

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

## Running

```sh
dotnet run
```

Then point your MCP client at `http://localhost:5700/mcp`.

### Claude Code

```sh
claude mcp add --transport http azdo http://localhost:5700/mcp
```

## Docker

A `Dockerfile` is provided for HTTP-mode hosting:

```sh
docker build -t azdomcp .
docker run --rm -p 5700:5700 \
    -e AZDOMCP_AzureDevOps__OrganizationUrl="https://devops.your-domain/DefaultCollection" \
    -e AZDOMCP_AzureDevOps__PersonalAccessToken="$AZDO_PAT" \
    -e AZDOMCP_AzureDevOps__DefaultProject="MyProject" \
    azdomcp
```

The container listens on `http://0.0.0.0:5700/mcp`. Configure via the `AZDOMCP_` env-var prefix using ASP.NET Core's `__` separator for nested keys (e.g. `AZDOMCP_AzureDevOps__ReadOnly=false`). Read-only mode is on by default.

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
sc.exe description AzureDevopsMCPSharp "MCP server bridging Claude Code to on-prem Azure DevOps Server."
sc.exe start AzureDevopsMCPSharp
```

Put credentials in `C:\Services\AzureDevopsMCPSharp\appsettings.Local.json` (or set `AZDOMCP_AzureDevOps__PersonalAccessToken` as a machine-level env var) — never in `appsettings.json`, which is checked in.

To remove:

```powershell
sc.exe stop AzureDevopsMCPSharp
sc.exe delete AzureDevopsMCPSharp
```

Logs land in `<install-dir>\logs\azdomcp-*.log`.

## Read-only mode

Read-only is **on by default**. To enable write tools, set `AzureDevOps:ReadOnly=false` (and understand the blast radius — agents can then create/edit work items, repos, etc.).

## Per-operation switches

Even with `ReadOnly=false`, each write tool can be individually enabled or disabled via `AzureDevOps:Operations`. This lets you grant, say, work-item updates but keep pipeline deletion off.

`ReadOnly=true` overrides everything: it blocks all writes regardless of the per-op switches. `ReadOnly=false` then defers to the `Operations` map — **only an explicit `true` enables a tool**; missing entries and explicit `false` both block. This makes new write tools added in future releases opt-in by default for existing configs.

All write operations ship **disabled by default** in `appsettings.json`. Opt in to the ones you want:

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
    "set_bypass_push_policy_user": false
  }
}
```

| Operation | Tool | Notes |
| --- | --- | --- |
| `queue_pipeline_run` | Queue a pipeline run | |
| `cancel_pipeline_run` | Cancel an in-progress run | |
| `add_pipeline_run_tags` | Stamp tags onto a run | |
| `remove_pipeline_run_tag` | Remove a tag from a run | |
| `create_pipeline` | Create a YAML pipeline definition | |
| `rename_pipeline` | Rename a pipeline | |
| `move_pipeline` | Move a pipeline to a different folder | |
| `delete_pipeline` | **Permanently** delete a pipeline | No undo. Shipped default is `false`. |
| `authorize_pipeline_resource` | Grant a pipeline permission to use a protected resource | |
| `create_work_item` | Create a work item | |
| `update_work_item` | Patch fields on a work item | |
| `create_repository` | Create a new empty Git repository | |
| `rename_repository` | Rename a Git repository | |
| `delete_repository` | **Permanently** delete a Git repository | No undo. Wipes branches, history, PRs. |
| `delete_repo_policy` | Delete a branch/repo policy configuration | |
| `set_policy_enabled` | Toggle a policy on/off without changing its settings | |
| `set_repo_policy` | Generic create/update for any policy type (pass type Guid + raw settings JSON) | |
| `set_min_reviewers_policy` | Typed helper for the 'Minimum number of reviewers' policy | |
| `set_bypass_push_policy_self` | Allow / deny / clear 'Bypass policies when pushing' for the PAT's own identity | Repo-scoped or branch-scoped. |
| `set_bypass_push_policy_group` | Same, scoped to a named group (e.g. 'Project Administrators') | |
| `set_bypass_push_policy_user` | Same, scoped to a single user (email / account / display name) | |

When a tool is called but its operation is not explicitly enabled, the server throws a clear error naming the exact setting to flip:

> `Operation 'delete_pipeline' is blocked: not enabled in AzureDevOps:Operations (missing entries default to disabled). Set AzureDevOps:Operations:delete_pipeline=true to enable it.`

Override individual switches via env var, e.g. `AZDOMCP_AzureDevOps__Operations__delete_pipeline=true`.
