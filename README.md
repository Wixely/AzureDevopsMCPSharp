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
| `Server:Host` | `localhost` | Host to bind |
| `Server:Port` | `5089` | HTTP port |
| `Server:Path` | `/mcp` | MCP endpoint path |

## Running

```sh
dotnet run
```

Then point your MCP client at `http://localhost:5089/mcp`.

### Claude Code

```sh
claude mcp add --transport http azdo http://localhost:5089/mcp
```

## Read-only mode

Read-only is **on by default**. To enable write tools, set `AzureDevOps:ReadOnly=false` (and understand the blast radius — agents can then create/edit work items, repos, etc.).
