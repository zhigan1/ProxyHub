# ProxyHub P4 Validation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore a buildable ProxyHub, close P4's high-value test and documentation gaps, and demonstrate the gateway's local HTTP surface without accessing upstream services or credentials.

**Architecture:** Retain the existing `Program -> AppFactory -> FailoverExecutor` composition and fix only its broken contracts. Treat `ProxyHubConfig.Port = 8265` as the single default, because the production default and README already agree; update stale test expectations rather than changing runtime behavior. Add tests at the public HTTP and deterministic component boundaries, using the existing fake adapter, temporary files, and loopback Kestrel.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, xUnit 2.9, `System.Text.Json.Nodes`.

**Spec:** `ProxyHub/docs/设计方案-v2.md` (P4 only; P3 UI features not yet implemented are explicitly deferred below).

## Global Constraints

- Preserve the OCP adapter structure and existing OpenAI-compatible routes.
- Do not call a real upstream service or inspect local account credentials in tests.
- Use only the BCL and ASP.NET Core shared framework; add no package dependency.
- Keep port and adapter-enable changes restart-only; retain hot reload for groups, breakers, accounts, and admin configuration.
- P4 does not implement the unbuilt P3 admin UI capabilities: per-row matrix health, form-level group preview, manual-account form, visual `accounts`/`groups` editors, or embedded-resource deployment. Track them as a later P3-completion plan instead of claiming they are present.
- The workspace currently has no Git repository at `E:\Project\vibe\net`; do not add commit steps until the implementation owner supplies the repository root.

---

## File Structure

| File | Responsibility in this plan |
| --- | --- |
| `ProxyHub/ConfigStore.cs` | Make file-watch configuration compile without changing debounce behavior. |
| `ProxyHub/FailoverExecutor.cs` | Consume the complete chunk-sink contract when a committed SSE stream fails. |
| `ProxyHub/ModelGroups.cs` | Declare the common sink error-finalization contract and provide collecting-sink no-op behavior. |
| `ProxyHub/AppFactory.cs` | Apply the hot-reloadable failover-header option per streaming request. |
| `ProxyHub/Adapters/CodeBuddyAdapter.cs` | Enumerate auth files in a legal iterator-free `try/catch` implementation. |
| `ProxyHub/AdminApi.cs` | Return one stable anonymous projection for adapter overview rows. |
| `ProxyHub.Tests/*.cs` | Add offline tests for headers, routes, breaker snapshot, transport parsing, accounts, reloader, and account discovery. |
| `ProxyHub/README.md` | Make public API, default-port, risk, and P3/P4 scope statements match running behavior. |
| `ProxyHub/docs/设计方案-v2.md` | Mark the obsolete account-reset path and deferred P3 UI work accurately. |

### Task 1: Restore the Compiler Contract and Canonical Default

**Files:**
- Modify: `ProxyHub/ConfigStore.cs:113`
- Modify: `ProxyHub/FailoverExecutor.cs:1-205`
- Modify: `ProxyHub/ModelGroups.cs` (declares `IChunkSink`, `SseSink`, and `CollectingSink`)
- Modify: `ProxyHub/Adapters/CodeBuddyAdapter.cs:69-86`
- Modify: `ProxyHub/AdminApi.cs:27-45`
- Modify: `ProxyHub.Tests/ConfigTests.cs:13`
- Modify: `ProxyHub.Tests/ConfigStoreTests.cs:34,55`
- Modify: `ProxyHub.Tests/AdminApiTests.cs:67,182,194`

**Interfaces:**
- Produces: `IChunkSink.WriteErrorAsync(string message, CancellationToken ct)` returning `Task`.
- Produces: one stable `AdminApi` overview row shape containing `id`, `ready`, `accounts`, `models`, and nullable `error`.
- Produces: `8265` as the only asserted default port.

- [ ] **Step 1: Reproduce the five compiler errors without executing adapters**

Run: `dotnet build ProxyHub/ProxyHub.csproj`

Expected: `CS0117` in `ConfigStore`, `CS1061` in `FailoverExecutor`, `CS0411` and `CS1626` in `CodeBuddyAdapter`, and `CS0411` in `AdminApi`.

- [ ] **Step 2: Make the file-watch enum legal**

Replace the nonexistent enum member in the notification mask. `FileName` already covers rename/create semantics for the subscribed `Created` and `Renamed` events, so retain `LastWrite | Size | FileName` and remove `NotifyFilters.Creation`; do not alter the 300 ms delay or event subscriptions.

- [ ] **Step 3: Complete the chunk-sink error contract**

Add this member to `IChunkSink` and implement it in both sink classes:

```csharp
Task WriteErrorAsync(string message, CancellationToken ct);
```

Keep `SseSink.WriteErrorAsync` as its existing real SSE error-plus-`[DONE]` implementation. Add `CollectingSink.WriteErrorAsync` returning `Task.CompletedTask`, because non-streaming collection is never committed and `FailoverExecutor` only calls this member after `Committed` is true.

- [ ] **Step 4: Remove the illegal iterator pattern from CodeBuddy credential discovery**

Replace the `yield`-based `AuthFilesOrdered` implementation with a materialized return value so the `catch` can safely return an empty collection:

```csharp
private IReadOnlyList<FileInfo> AuthFilesOrdered()
{
    try
    {
        var dir = new DirectoryInfo(AuthDir);
        if (!dir.Exists) return Array.Empty<FileInfo>();
        return dir.GetFiles("*.info")
            .OrderBy(file => AuthFileRank(file.Name))
            .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
    catch
    {
        return Array.Empty<FileInfo>();
    }
}
```

- [ ] **Step 5: Make the overview projection one type**

In both branches of the `Task.WhenAll` selector, return these same properties; in the success branch set `error = (string?)null`:

```csharp
new { id = a.Id, ready = auth.HasCredential, accounts = accounts.Count,
      models = rt.Registry.EffectiveCount(a.Id), error = (string?)null }
```

The catch branch uses the same property list with `ready = false` and `error = e.Message`.

- [ ] **Step 6: Align stale assertions with the production default**

Replace each default-port expectation of `8787` in `ConfigTests`, `ConfigStoreTests`, and `AdminApiTests` with `8265`. Keep explicit non-default test values (`9000`, `9100`, and environment override `9999`) unchanged.

- [ ] **Step 7: Verify compilation and the existing configuration subset**

Run: `dotnet build ProxyHub/ProxyHub.csproj; dotnet test ProxyHub.Tests/ProxyHub.Tests.csproj --filter "FullyQualifiedName~ConfigTests|FullyQualifiedName~ConfigStoreTests|FullyQualifiedName~AdminApiTests"`

Expected: build succeeds; selected tests pass with no network access.

### Task 2: Make the Failover Header a Tested, Hot-Reloadable Contract

**Files:**
- Modify: `ProxyHub/ModelGroups.cs`
- Modify: `ProxyHub/AppFactory.cs` (streaming `SseSink` construction)
- Modify: `ProxyHub.Tests/FailoverExecutorTests.cs:130-149`
- Modify: `ProxyHub.Tests/EndpointTests.cs`

**Interfaces:**
- Consumes: `AdminConfig.FailoverHeader` from `rt.Config.Current.Admin`.
- Produces: `SseSink(HttpResponse response, bool writeFailoverHeader)`; its default argument is `true` to preserve direct unit-test construction.

- [ ] **Step 1: Add failing route-level header tests**

Create two streaming requests using the existing fake adapter's first-attempt failure. Assert the enabled configuration returns a response header containing `default` and `backup`; assert `new AdminConfig { FailoverHeader = false }` returns no `X-ProxyHub-Failover` header.

```csharp
Assert.True(response.Headers.TryGetValues("X-ProxyHub-Failover", out var values));
Assert.Contains("default", values.Single());
```

- [ ] **Step 2: Run the new tests to prove the disabled case fails**

Run: `dotnet test ProxyHub.Tests/ProxyHub.Tests.csproj --filter "FullyQualifiedName~FailoverHeader"`

Expected: the enabled assertion passes under current behavior; the disabled assertion fails because the header is currently unconditional.

- [ ] **Step 3: Gate header emission at the sink boundary**

Give `SseSink` a `_writeFailoverHeader` field set by its constructor. In `WriteAsync`, set `X-ProxyHub-Failover` only when `_writeFailoverHeader && FailoverNote is not null`. Construct the sink in `AppFactory` with `rt.Config.Current.Admin.FailoverHeader` for every request so later `RuntimeConfig.Apply` changes are observed without an app restart.

- [ ] **Step 4: Re-run the focused tests**

Run: `dotnet test ProxyHub.Tests/ProxyHub.Tests.csproj --filter "FullyQualifiedName~FailoverHeader|FullyQualifiedName~Execute_SseSink"`

Expected: enabled and disabled header behavior passes; the committed-stream error chunk and `[DONE]` behavior remains passing.

### Task 3: Cover the Public HTTP Routes Missed by P2

**Files:**
- Modify: `ProxyHub.Tests/EndpointTests.cs`
- Modify: `ProxyHub.Tests/AdminApiTests.cs`

**Interfaces:**
- Consumes: existing `AppFactory.Build`, `TestHost.Build`, and `FakeAdapter`.
- Produces: route-level regression coverage without starting `Program.cs` or calling an adapter upstream.

- [ ] **Step 1: Add failing `/v1/responses` and model-matrix tests**

Post the normal fake-adapter conversation to `/v1/responses` and assert `200`, `object == "response"`, and `output[0].content[0].text == "hi"`. Get `/v1/models/matrix` and assert `200`, a `families` array, and a nonempty `pricingNote`.

- [ ] **Step 2: Add the missing breaker snapshot test**

Record one breaker failure through `_rt.Breakers`, call `GET /admin/api/breakers`, then assert the response includes that key and its `closed` state with `consecutiveFailures == 1`.

- [ ] **Step 3: Run the tests to establish route coverage**

Run: `dotnet test ProxyHub.Tests/ProxyHub.Tests.csproj --filter "FullyQualifiedName~Responses|FullyQualifiedName~Matrix|FullyQualifiedName~Breakers"`

Expected: all new route tests pass after Task 1; no process uses a real configured adapter.

### Task 4: Cover Deterministic Infrastructure Boundaries

**Files:**
- Create: `ProxyHub.Tests/UpstreamHttpTests.cs`
- Create: `ProxyHub.Tests/AccountRegistryTests.cs`
- Modify: `ProxyHub.Tests/ConfigStoreTests.cs`
- Modify: `ProxyHub.Tests/AdapterTests.cs`

**Interfaces:**
- Consumes: `UpstreamHttp.ReadSseAsync`, `AccountRegistry.RefreshAsync`, `ConfigReloader`, and `CodeBuddyAdapter(TimeSpan? timeout, string? cliCommand, string? authDir)`.
- Produces: deterministic tests for SSE parsing, account merge order, reloader debounce, and CodeBuddy auth-file discovery.

- [ ] **Step 1: Add an SSE line-parser test**

Build an `HttpResponseMessage` with `StringContent("event: message\\ndata: first\\n\\ndata: second\\n")`, enumerate `ReadSseAsync()`, and assert the exact sequence `("message", "first")`, `("message", "second")`. This captures the current event-retention contract.

- [ ] **Step 2: Add account registry merge and failure-isolation tests**

Use two in-test `IAdapter` implementations: one returns a discovered account, one throws. Set a manual Qoder PAT before `RefreshAsync`. Assert the throwing adapter produces an empty list, discovered accounts precede `manual-*`, and the manual PAT remains present.

- [ ] **Step 3: Add a condition-polled reloader test**

Create a temporary existing JSON file before constructing `ConfigReloader`; subscribe to `RuntimeConfig.Changed`; overwrite its `timeoutMs`; poll a `TaskCompletionSource` for at most two seconds. Assert it fires once with the new value, then dispose the reloader. Do not use fixed sleeps as the assertion mechanism.

- [ ] **Step 4: Add CodeBuddy directory-order discovery coverage**

Create a temporary auth directory with valid fixture `.info` files named `workbuddy-desktop.info`, `Tencent-Cloud.coding-copilot.info`, and `z.info`. Construct `CodeBuddyAdapter(authDir: tempDir)`, call `DiscoverAccountsAsync`, and assert those labels are ordered exactly as the rank policy specifies. Delete the directory in `finally`.

- [ ] **Step 5: Run the focused infrastructure suite**

Run: `dotnet test ProxyHub.Tests/ProxyHub.Tests.csproj --filter "FullyQualifiedName~UpstreamHttpTests|FullyQualifiedName~AccountRegistryTests|FullyQualifiedName~ConfigReloader|FullyQualifiedName~CodeBuddy"`

Expected: all tests pass locally; no test connects to an upstream endpoint.

### Task 5: Align the README and Design Document to the Shipped Contract

**Files:**
- Modify: `ProxyHub/README.md:123-145,238`
- Modify: `ProxyHub/docs/设计方案-v2.md:131,166,201-205`

**Interfaces:**
- Consumes: the actual management API registered in `AdminApi.MapAdminApi`.
- Produces: documentation that names `POST /admin/api/breakers/reset` as the canonical reset endpoint and does not claim unimplemented P3 UI controls.

- [ ] **Step 1: Correct the API table and reset description**

Keep the implemented `POST /admin/api/breakers/reset` route, with optional JSON body `{ "key": "platform|account|model" }`, as the canonical API. Remove the unsupported `GET /admin/api/groups/{name}` claim from README, and change the design document's obsolete `/accounts/{id}/reset` row to the implemented breaker-reset route.

- [ ] **Step 2: Document the risk and P3 scope accurately**

Add a concise warning under multi-account behavior: high-frequency concurrent use of several accounts on one platform can trigger provider risk controls; the gateway's default ordered failover uses backups only after a node failure, and users remain responsible for provider terms. Keep the existing L1/L2/L3 load-balancing reference.

Add a P4 note to the design document stating that the `/admin` page ships disk-backed and supports overview, group CRUD, account refresh, breaker reset, and raw top-level config merge; the richer P3 form controls and embedded-resource packaging are deferred.

- [ ] **Step 3: Verify the documented endpoints against offline tests**

Run: `dotnet test ProxyHub.Tests/ProxyHub.Tests.csproj --filter "FullyQualifiedName~AdminApiTests|FullyQualifiedName~AdminAuthTests|FullyQualifiedName~AdminDisabledTests"`

Expected: all named API contracts are exercised against loopback Kestrel.

### Task 6: Execute P4's Offline Integration Smoke and Final Regression

**Files:**
- Modify: `ProxyHub.Tests/EndpointTests.cs`

**Interfaces:**
- Consumes: `AppFactory.Build` with `TestHost.Build`, empty adapters, a temporary config path, and `http://127.0.0.1:0`.
- Produces: a testable local startup surface for `/health` and `/v1/models` without `Program.cs` adapter composition or background upstream activity.

- [ ] **Step 1: Add an offline smoke test**

Start `AppFactory.Build(TestHost.Build(config: new ProxyHubConfig { Adapters = new Dictionary<string, bool>() }), "http://127.0.0.1:0")`. Resolve the assigned loopback address via `IServerAddressesFeature`. Assert `GET /health` returns `200` and `{ "status": "ok" }`; assert `GET /v1/models` returns `200` with a `data` array. Stop and dispose the app in `finally`.

- [ ] **Step 2: Run the smoke test independently**

Run: `dotnet test ProxyHub.Tests/ProxyHub.Tests.csproj --filter "FullyQualifiedName~Smoke"`

Expected: the process uses only loopback Kestrel and no adapter collection, credentials, or upstream calls.

- [ ] **Step 3: Smoke the real `Program` composition with every adapter disabled**

Run this PowerShell block after a successful build; it starts no adapter and makes no upstream call:

```powershell
$env:PROXY_HUB_PORT = '19083'
$env:PROXY_ADAPTER_CODEBUDDY = 'false'
$env:PROXY_ADAPTER_TRAECN = 'false'
$env:PROXY_ADAPTER_TRAEWORK = 'false'
$env:PROXY_ADAPTER_QODER = 'false'
$proc = Start-Process dotnet -ArgumentList 'run', '--no-build', '--project', 'ProxyHub' -PassThru -WindowStyle Hidden
try {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    do {
        try { $health = Invoke-RestMethod 'http://127.0.0.1:19083/health'; break } catch { Start-Sleep -Milliseconds 100 }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if ($health.status -ne 'ok') { throw 'ProxyHub health check did not return ok' }
    $models = Invoke-RestMethod 'http://127.0.0.1:19083/v1/models'
    if ($null -eq $models.data) { throw 'ProxyHub model-list response did not contain data' }
}
finally {
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id }
    'PROXY_HUB_PORT','PROXY_ADAPTER_CODEBUDDY','PROXY_ADAPTER_TRAECN','PROXY_ADAPTER_TRAEWORK','PROXY_ADAPTER_QODER' | ForEach-Object {
        Remove-Item "Env:$_" -ErrorAction SilentlyContinue
    }
}
```

Expected: `/health` reports `ok` and `/v1/models` returns an empty `data` array; the process is stopped in `finally`.

- [ ] **Step 4: Run the full regression suite**

Run: `dotnet test ProxyHub.Tests/ProxyHub.Tests.csproj`

Expected: all tests pass. Record the test count and duration in the implementation handoff; do not call the result “all green” until this command exits with code 0.

## Deferred P3 Completion Work

The following findings are deliberately outside strict P4 scope and require a separate approved plan: matrix breaker/account columns; a group-edit preview UI; a manual-account form; form controls for `accounts` and `groups`; changing the initial admin auth probe from `/health`; and embedding `admin.html` into the assembly. This is a product-scope decision, not a test failure.

## Plan Review

- Spec coverage: P4's buildability, test completion, README update, and local smoke requirements map to Tasks 1-6. P3 UI implementation is explicitly deferred rather than silently omitted.
- Placeholder scan: no unresolved placeholders remain; every task names files, verification commands, and expected outcomes.
- Type consistency: the only new cross-file contract is `IChunkSink.WriteErrorAsync(string, CancellationToken): Task`; both sink implementations and `FailoverExecutor` are accounted for.
