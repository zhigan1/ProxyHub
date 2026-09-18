# P3 Admin Completion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (\`- [ ]\`) syntax for tracking.

**Goal:** Complete deferred P3 administration-console capabilities while preserving local-only configuration and cross-platform-before-backup failover.

**Architecture:** Keep \`config.json\` as the only writable configuration source. Add protected endpoints for live group drafts and a model/health matrix; have the single-page console consume them while it persists structured groups/manual accounts through the existing config merge endpoint. Embed \`admin.html\` as a manifest resource so published output no longer needs a disk \`wwwroot\` directory.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, System.Text.Json.Nodes, embedded resources, static HTML/JavaScript, xUnit.

**Spec:** \`docs/设计方案-v2.md\`

## Global Constraints

- Keep OCP adapter boundaries and add no third-party packages.
- All administration endpoints use the current proxy-key authorization rule.
- Do not contact upstreams, read credentials, or decrypt account data in rendering or tests.
- Candidate order remains account rounds: try each preferred platform's account N before any account N+1.
- \`ProxyHub\` is the Git repository; \`../ProxyHub.Tests\` is outside it and remains verification-only.
- Before every commit run the listed tests, inspect \`git status --short\`, and stage only stated files.

---

### Task 1: Create a Verified Git Baseline

**Files:**
- Modify: none; stage the existing approved P1-P4 source/docs only.
- Test: \`../ProxyHub.Tests/ProxyHub.Tests.csproj\`

**Interfaces:**
- Consumes: completed P1-P4 working-tree changes.
- Produces: a clean Git checkpoint for P3 completion.

- [ ] **Step 1: Review scope before staging**

\`\`\`powershell
git status --short
git diff --stat
git ls-files --others --exclude-standard
\`\`\`

Expected: stage only v2 sources, \`docs\`, and \`wwwroot\`; exclude \`bin\`, \`obj\`, \`config.json\`, and credentials.

- [ ] **Step 2: Verify the baseline**

\`\`\`powershell
dotnet build ProxyHub.csproj --nologo
dotnet test ..\ProxyHub.Tests\ProxyHub.Tests.csproj --nologo
\`\`\`

Expected: zero build errors and all tests pass.

- [ ] **Step 3: Commit the reviewed checkpoint**

\`\`\`powershell
git add Adapters AppFactory.cs IAdapter.cs Program.cs ProxyHub.csproj ProxyHubConfig.cs README.md Registry.cs Sse.cs start.bat AccountRegistry.cs AdapterAccount.cs AdminApi.cs CircuitBreaker.cs ConfigStore.cs FailoverExecutor.cs ModelGroups.cs RuntimeConfig.cs UpstreamException.cs docs wwwroot
git diff --cached --stat
git commit -m "feat: complete ProxyHub v2 core"
\`\`\`

Expected: the commit contains only reviewed P1-P4 implementation files.

### Task 2: Add Admin Data Contracts and Embedded Delivery

**Files:**
- Modify: \`AdminApi.cs\`, \`AppFactory.cs\`, \`FailoverExecutor.cs\`, \`ProxyHub.csproj\`
- Test: \`../ProxyHub.Tests/AdminApiTests.cs\`, \`../ProxyHub.Tests/FailoverExecutorTests.cs\`

**Interfaces:**
- Consumes: \`Registry.ModelMatrix()\`, \`AccountRegistry.AccountsOf()\`, \`CircuitBreakerRegistry.Snapshot()\`, \`GroupRegistry.Expand()\`, and \`RuntimeConfig.Current.Admin\`.
- Produces: \`POST /admin/api/groups/preview\`, \`GET /admin/api/models/matrix\`, an embedded \`/admin\` page, and an effective \`admin.failoverHeader\` switch.

- [ ] **Step 1: Write failing API/resource tests**

Add a preview test:

\`\`\`csharp
[Fact]
public async Task GroupPreview_ExpandsUnsavedDraft()
{
    var response = await _http.PostAsJsonAsync("/admin/api/groups/preview",
        new { match = new[] { "*flash" }, prefer = new[] { "cb" } });
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var body = await response.Content.ReadFromJsonAsync<JsonObject>();
    Assert.Contains("cb-glm-5.2-flash@default", body!["preview"]!.AsArray());
}
\`\`\`

Add matrix assertions for one model/provider, available account count, and open/half-open breaker counts. Add an \`/admin\` test proving the manifest page is served even if \`AppContext.BaseDirectory/wwwroot/admin.html\` is absent. Add a streaming failover test that sets \`AdminConfig.FailoverHeader = false\` and asserts the header is absent.

- [ ] **Step 2: Verify red**

\`\`\`powershell
dotnet test ..\ProxyHub.Tests\ProxyHub.Tests.csproj --filter "FullyQualifiedName~AdminApiTests|FullyQualifiedName~FailoverExecutorTests" --nologo
\`\`\`

Expected: the new preview/matrix/resource/header assertions fail.

- [ ] **Step 3: Implement narrow, protected endpoints**

In \`AdminApi.MapAdminApi\`, validate and expand an in-memory \`GroupConfig\` without writing \`config.json\`; return \`{ candidates, preview }\` capped at 50. Project \`Registry.ModelMatrix()\` deterministically and attach account availability plus breaker counts without exposing paths, PATs, or credentials. Return \`400\` for invalid JSON or an empty \`match\`.

- [ ] **Step 4: Make the header setting real**

Pass a live configuration predicate into \`SseSink\` through the AppFactory request path. \`SseSink.WriteAsync\` writes \`X-ProxyHub-Failover\` only when \`Admin.FailoverHeader\` is true, while retaining \`Uri.EscapeDataString\` for ASCII-safe output.

- [ ] **Step 5: Embed the console**

Replace the copy-only project item with:

\`\`\`xml
<EmbeddedResource Include="wwwroot\admin.html" LogicalName="ProxyHub.wwwroot.admin.html" />
\`\`\`

Serve \`typeof(AdminApi).Assembly.GetManifestResourceStream("ProxyHub.wwwroot.admin.html")\` using \`Results.Stream(stream, "text/html; charset=utf-8")\`. Return a clear server error only if the manifest resource is unexpectedly absent.

- [ ] **Step 6: Verify green and commit**

\`\`\`powershell
dotnet test ..\ProxyHub.Tests\ProxyHub.Tests.csproj --filter "FullyQualifiedName~AdminApiTests|FullyQualifiedName~FailoverExecutorTests" --nologo
dotnet build ProxyHub.csproj --nologo
git add AdminApi.cs AppFactory.cs FailoverExecutor.cs ProxyHub.csproj
git commit -m "feat(admin): add live preview matrix and embedded console"
\`\`\`

Expected: focused tests pass, build has zero errors, and the commit contains only Task 2 production files.

### Task 3: Complete the Console Workflows

**Files:**
- Modify: \`wwwroot/admin.html\`
- Test: \`../ProxyHub.Tests/AdminApiTests.cs\`; local fake-adapter smoke.

**Interfaces:**
- Consumes: current overview/groups/accounts/config endpoints, plus Task 2 preview and matrix endpoints.
- Produces: health-aware model matrix, live group-draft preview, manual-account editor, structured groups/accounts editor, and correct initial authentication detection.

- [ ] **Step 1: Lock the embedded-page contract**

Extend the \`/admin\` response test to assert the HTML references \`/admin/api/groups/preview\` and \`/admin/api/models/matrix\`, so packaging cannot silently serve an outdated file.

- [ ] **Step 2: Render health-aware matrix**

Change \`renderModels\` to call \`GET /admin/api/models/matrix\`. Render family/provider rows with available-account counts and open/half-open breaker indicators. Keep this view read-only; do not put account paths or secrets into the DOM.

- [ ] **Step 3: Preview group edits without persistence**

In \`editGroup\`, debounce \`match\`/preferred-platform input by roughly 200 ms and post the draft to the preview endpoint. Replace the modal preview area with returned labels and display validation errors inline. Saving still calls \`PUT /admin/api/groups/{name}\`.

- [ ] **Step 4: Add structured accounts and groups editors**

Add a manual-account modal requiring a nonempty label and exactly one adapter-specific value: \`authFile\` for CodeBuddy, \`storageFile\` for TraeCN/TraeWork, or \`pat\` for Qoder. Read the config, update only \`accounts[adapter]\`, and call \`PUT /admin/api/config\`.

Add structured \`groups\` and \`accounts\` sections to configuration. Preserve raw JSON only as diagnostic output and mask PAT values; never populate a saved PAT value back into an input.

- [ ] **Step 5: Correct boot authentication**

Replace the boot probe \`/health\` with \`/admin/api/overview\`: a \`401\` opens the key modal before data render; a success renders the active tab. Do not change public \`/health\`.

- [ ] **Step 6: Smoke test and commit**

\`\`\`powershell
dotnet test ..\ProxyHub.Tests\ProxyHub.Tests.csproj --filter "FullyQualifiedName~AdminApiTests" --nologo
dotnet build ProxyHub.csproj --nologo
git add wwwroot/admin.html
git commit -m "feat(admin): complete visual configuration workflows"
\`\`\`

Then start with all real adapters disabled, assert \`/admin\` exposes the new controls, and verify proxy-key protection. Stop the process in \`finally\`.

### Task 4: Reconcile Documentation and Publish Evidence

**Files:**
- Modify: \`docs/设计方案-v2.md\`, \`README.md\`
- Test: \`../ProxyHub.Tests/ProxyHub.Tests.csproj\`

**Interfaces:**
- Consumes: externally observable Task 2 and Task 3 behavior.
- Produces: accurate failover, P3, and scope documentation.

- [ ] **Step 1: Correct candidate-chain semantics**

Replace the stale claim that all accounts of a model run before another platform with: every preferred platform's first account is attempted before any preferred platform's backup; model order within a platform/account is stable lexical order.

- [ ] **Step 2: Record delivered P3 features and retain scope limits**

Document embedded admin delivery, enriched matrix, live preview, and structured account/group editing. Keep L1-L3 load balancing explicitly unimplemented and retain the multi-account risk warning.

- [ ] **Step 3: Final verification and documentation commit**

\`\`\`powershell
dotnet build ProxyHub.csproj --nologo
dotnet test ..\ProxyHub.Tests\ProxyHub.Tests.csproj --nologo
git add README.md docs/设计方案-v2.md
git commit -m "docs: align v2 design with completed admin workflows"
git status --short
\`\`\`

Expected: zero build errors, all tests pass, and the \`ProxyHub\` working tree is clean.

## Plan Self-Review

- All deferred P3 features are covered by Tasks 2-3.
- The stale failover narrative is corrected in Task 4; L1 balancing remains out of scope.
- Each production task begins with a failing test and ends with focused verification plus a scoped Git commit.
- The external test-project boundary is explicit; no test files are falsely represented as committed.

