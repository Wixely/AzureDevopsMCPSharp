# Implementation Brief: Azure DevOps Wiki read/edit tools

**For:** an implementing agent working in this repo (`AzureDevopsMCPSharp`).
**Goal:** add MCP tools to **read and edit** the wiki built into Azure DevOps (project wikis),
following this server's existing conventions. Additive only — a new tool group, no new
dependency, no `Program.cs` change.

---

## 0. Read these first (existing conventions)

- `Services/AzureDevOpsService.cs` — the shared service. Key members you will use:
  - `GetClient<T>()` where `T : VssHttpClientBase` — returns a typed WebApi client from the
    `VssConnection` (PAT auth already wired).
  - `RestClient` — a raw `HttpClient` (BaseAddress = org URL + `/`, Basic-auth PAT, Accepts JSON)
    for REST endpoints not covered by the SDK. **Fallback only** — prefer the typed client.
  - `ResolveProject(string? project)` — returns the project or falls back to
    `AzureDevOps:DefaultProject`; throws a clear error if neither is set.
  - `EnsureWriteAllowed(string operation)` — the **double gate** (see §4).
- `Tools/WorkItemTools.cs` — canonical tool pattern (read tools ungated; write tools call
  `EnsureWriteAllowed`, use `GetClient<T>()`, serialize with `JsonOpts.Default`).
- `Configuration/AzureDevOpsOptions.cs` — `ReadOnly` (default true) and the
  `Operations` dictionary (per-tool allow switches; missing/false = blocked).
- Tools auto-register via `AddMcpServer().WithToolsFromAssembly()` in `Program.cs`, so a new
  `[McpServerToolType] public static class WikiTools` is discovered automatically — **do not edit
  `Program.cs`.**

Tool naming: `azdo_*`. The write-gate operation name is the tool name **without** the `azdo_`
prefix (e.g. tool `azdo_create_or_update_wiki_page` → gate key `create_or_update_wiki_page`).

---

## 1. Dependency & client

No new NuGet package. The Wiki types live in `Microsoft.TeamFoundation.Wiki.WebApi`, inside the
already-referenced `Microsoft.TeamFoundationServer.Client` (currently 20.256.2):

```csharp
using Microsoft.TeamFoundation.Wiki.WebApi;         // WikiHttpClient, WikiV2, WikiPage, WikiPageResponse
// WikiPageCreateOrUpdateParameters, WikiCreateParametersV2 also here
using Microsoft.TeamFoundation.SourceControl.WebApi; // VersionControlRecursionType (recursion level)
```

Get the client with `var wiki = svc.GetClient<WikiHttpClient>();`.

---

## 2. Tools to implement — `Tools/WikiTools.cs`

Create `[McpServerToolType] public static class WikiTools` with these tools. All take an optional
`project` (→ `svc.ResolveProject`) and a `CancellationToken ct = default`, and serialize results
with `JsonSerializer.Serialize(result, JsonOpts.Default)`.

| Tool | Kind | Purpose |
| --- | --- | --- |
| `azdo_list_wikis` | read | List wikis in a project. |
| `azdo_list_wiki_pages` | read | List the page tree of a wiki. |
| `azdo_get_wiki_page` | read | Get one page's content + ETag. |
| `azdo_create_or_update_wiki_page` | **write** | Create a page, or edit an existing one. |
| `azdo_delete_wiki_page` | **write** | Delete a page. |

### 2.1 `azdo_list_wikis` (read)
- Params: `project?`.
- Call: `await wiki.GetAllWikisAsync(project: resolvedProject, cancellationToken: ct)` → `List<WikiV2>`.
- Return per wiki: `Id`, `Name`, `Type` (`projectWiki` | `codeWiki`), `MappedPath`, `ProjectId`,
  `RepositoryId`, `RemoteUrl`, `Url`.

### 2.2 `azdo_list_wiki_pages` (read)
- Params: `wikiIdentifier` (wiki name or GUID), `project?`, `path` (default `/`).
- Call: `await wiki.GetPageAsync(project: p, wikiIdentifier: wikiId, path: path,
  recursionLevel: VersionControlRecursionType.Full, includeContent: false, cancellationToken: ct)`
  → `WikiPageResponse`. Walk `response.Page.SubPages` recursively.
- Return a flattened list (or tree) of `{ Path, Order, IsParentPage (SubPages.Any), GitItemPath }`.
  Do **not** include content here (keep it light).

### 2.3 `azdo_get_wiki_page` (read) — the "read" half
- Params: `wikiIdentifier`, `path` (e.g. `/Home`), `project?`.
- Call: `await wiki.GetPageAsync(project: p, wikiIdentifier: wikiId, path: path,
  includeContent: true, cancellationToken: ct)` → `WikiPageResponse`.
- Return: `Path`, `Content` (Markdown), `ETag` (see §3 — take the single value of
  `response.ETag`), `GitItemPath`, `RemoteUrl`, `Order`.
- **Truncate** very large content (e.g. > ~50 KB) and set a `truncated: true` flag, per the
  product-line output rules.

### 2.4 `azdo_create_or_update_wiki_page` (write) — the "edit" half
- Gate: `svc.EnsureWriteAllowed("create_or_update_wiki_page");` **first line.**
- Params: `wikiIdentifier`, `path`, `content` (Markdown), `etag?` (optional), `comment?`, `project?`.
- Logic (handles both create and edit — see §3 for why the ETag matters):
  ```csharp
  svc.EnsureWriteAllowed("create_or_update_wiki_page");
  var p = svc.ResolveProject(project);
  var wiki = svc.GetClient<WikiHttpClient>();

  string? version = etag;                 // If-Match for an update; null to create
  if (string.IsNullOrEmpty(version))
  {
      // No etag supplied: if the page already exists we MUST send its current etag,
      // otherwise the API rejects the update. Probe for it; treat 404 as "create".
      try
      {
          var existing = await wiki.GetPageAsync(p, wikiIdentifier, path,
              includeContent: false, cancellationToken: ct);
          version = existing.ETag?.FirstOrDefault();
      }
      catch (VssServiceException) { /* page doesn't exist -> create (version stays null) */ }
  }

  var parameters = new WikiPageCreateOrUpdateParameters { Content = content };
  var result = await wiki.CreateOrUpdatePageAsync(
      parameters, p, wikiIdentifier, path,
      Version: version,                   // positional 'Version' param == If-Match ETag
      comment: comment, cancellationToken: ct);
  ```
- Return the updated page: `Path`, `ETag` (new value, so the agent can chain further edits),
  `GitItemPath`, `RemoteUrl`, and `created: version is null`.
- **Error mapping:** catch the precondition-failed case (HTTP 412 / `VssServiceException` whose
  message/inner indicates a version mismatch) and rethrow a clear
  `InvalidOperationException`: *"Wiki page '{path}' changed on the server since it was read.
  Re-read it with azdo_get_wiki_page and retry azdo_create_or_update_wiki_page with the new etag."*

### 2.5 `azdo_delete_wiki_page` (write)
- Gate: `svc.EnsureWriteAllowed("delete_wiki_page");`
- Params: `wikiIdentifier`, `path`, `comment?`, `project?`.
- Call: `await wiki.DeletePageAsync(p, wikiIdentifier, path, comment: comment, cancellationToken: ct);`
- Return: `{ path, deleted: true }`.

> **Verify the two SDK signatures** against `WikiHttpClient` in your restored SDK version before
> finalizing: `GetPageAsync(..., bool? includeContent, ...)` and
> `CreateOrUpdatePageAsync(WikiPageCreateOrUpdateParameters, project, wikiIdentifier, path,
> string Version, string comment, ...)`. Parameter order/names have been stable but confirm.
> If a method is awkward, use the REST fallback in §5.

---

## 3. The one real gotcha: optimistic concurrency (ETag / If-Match)

Azure DevOps wiki page edits are guarded by an ETag:
- **Create** (page does not exist): send **no** `If-Match` → pass `Version: null`.
- **Update** (page exists): you **must** send the page's **current ETag** as `If-Match`, or the
  request is rejected. Get it from `GetPageAsync(...).ETag` (type is `IEnumerable<string>`; use the
  single value). A stale ETag → **HTTP 412 Precondition Failed**.

`azdo_get_wiki_page` returns the ETag; `azdo_create_or_update_wiki_page` accepts an optional `etag`
and also self-heals by probing (see §2.4). Always return the **new** ETag from an update so an
agent can make consecutive edits.

---

## 4. Safety model (mirror the existing pattern)

Writes are **double-gated** by `svc.EnsureWriteAllowed(op)`:
1. `AzureDevOps:ReadOnly` must be `false`, **and**
2. `AzureDevOps:Operations:<op>` must be explicitly `true` (missing/false = blocked).

Add the two new keys to `AzureDevopsMCPSharp.json` under `AzureDevOps:Operations`, defaulting
**false** (shipped safe):

```jsonc
"AzureDevOps": {
  "Operations": {
    // ...existing entries...
    "create_or_update_wiki_page": false,
    "delete_wiki_page": false
  }
}
```

Reads (`azdo_list_wikis`, `azdo_list_wiki_pages`, `azdo_get_wiki_page`) are **ungated**.

**PAT scope (operator concern, document only):** reading needs the PAT scope **Wiki (Read)**;
create/update/delete needs **Wiki (Read & Write)**. This is the operator's PAT, not code.

---

## 5. REST fallback (only if a typed method is missing/awkward)

`svc.RestClient` is preconfigured (BaseAddress = org URL + `/`, Basic PAT auth). Wiki REST API
(`api-version=7.1`), paths are relative to the org:

- List wikis: `GET {project}/_apis/wiki/wikis?api-version=7.1`
- Get page: `GET {project}/_apis/wiki/wikis/{wikiId}/pages?path={urlEncodedPath}&includeContent=true&api-version=7.1`
  — the **ETag is in the `ETag` response header**; body has `content` when `includeContent=true`.
- Create/update: `PUT {project}/_apis/wiki/wikis/{wikiId}/pages?path={urlEncodedPath}&api-version=7.1`
  with header `If-Match: {etag}` (omit for create) and JSON body `{ "content": "..." }`.
- Delete: `DELETE {project}/_apis/wiki/wikis/{wikiId}/pages?path={urlEncodedPath}&api-version=7.1`

URL-encode `path` (leading `/` kept, e.g. `/Area/Sub-Page`).

---

## 6. Edge cases to handle
- **Path format:** wiki paths start with `/` (`/Home`, `/Folder/Page`). Accept as given; don't rewrite.
- **`wikiIdentifier`:** accept either the wiki **name** or its **GUID** (the SDK/REST accept both).
- **Project vs code wiki:** `azdo_list_wikis` returns `Type`. Create/update/delete via
  `CreateOrUpdatePageAsync` targets **project wikis**. For **code wikis** (Git-backed), page edits
  really go through the backing repo commit flow — document that editing a code wiki page this way
  may error, and direct users to the Git repo tools for those. (v1 scope = project wikis.)
- **Missing wiki/page:** surface a clear error naming the wiki/path (map 404).
- **No project:** `ResolveProject` already throws a clear message.

---

## 7. Build checklist
1. Add `Tools/WikiTools.cs` with the five tools (§2), matching `WorkItemTools.cs` style.
2. Add the two `Operations` keys (default `false`) to `AzureDevopsMCPSharp.json` (§4).
3. Update `README.md`:
   - Add a **Wiki** group to the tools list: `azdo_list_wikis`, `azdo_list_wiki_pages`,
     `azdo_get_wiki_page`, `azdo_create_or_update_wiki_page`, `azdo_delete_wiki_page`.
   - Add `create_or_update_wiki_page` and `delete_wiki_page` to the Operations table.
   - Note the required PAT **Wiki (Read / Read & Write)** scopes.
4. `dotnet build` clean (0 warnings).
5. No new package, no `Program.cs` / options-class changes required (only the JSON `Operations`
   entries; the `Dictionary<string,bool> Operations` already binds them).

---

## 8. Verification
- Server starts; `/healthz` ok.
- `azdo_list_wikis` returns the project's wiki(s).
- `azdo_get_wiki_page` on `/Home` returns content + an `etag`.
- With `ReadOnly=false` **and** `Operations:create_or_update_wiki_page=true`:
  - Create a new page (no etag) → succeeds, returns new etag.
  - Edit it: `azdo_get_wiki_page` → take etag → `azdo_create_or_update_wiki_page` with that etag →
    succeeds.
  - Edit with a stale/blank etag against a now-changed page → returns the clean 412 retry message.
- With `Operations:create_or_update_wiki_page` unset/false → write is blocked with the standard
  operation-disabled error.

---

## 9. Decision left open
- Include `azdo_delete_wiki_page`, or ship read + create/update only? (Plan includes delete, gated
  and default-off; drop it if a smaller surface is preferred.)
