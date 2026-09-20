using System.Text.Json.Nodes;

namespace ProxyHub;

/// <summary>
/// 管理 API：/admin 单页与其数据端点。鉴权复用 proxy key（空 key = 本地开放）。
/// 一切"可配置修改"（分组/账号/熔断参数）都写回 config.json 单一事实来源，由热更新链路即时生效。
/// </summary>
public static class AdminApi
{
    public static void MapAdminApi(this WebApplication app, ProxyHubRuntime rt)
    {
        IResult Json(object payload, int status = StatusCodes.Status200OK) =>
            Results.Json(payload, statusCode: status);

        IResult Error(string message, int status) =>
            Results.Json(new { error = new { message } }, statusCode: status);

        bool Authorized(HttpContext ctx) => rt.Config.IsAuthorized(ctx);

        app.MapGet("/admin", (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Error("Unauthorized: missing or bad proxy key", StatusCodes.Status401Unauthorized);
            if (!rt.Config.Current.Admin.Enabled) return Error("Admin UI disabled (admin.enabled=false)", StatusCodes.Status404NotFound);
            return ServeHtml();
        });

        app.MapGet("/admin/api/overview", async (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Error("Unauthorized", StatusCodes.Status401Unauthorized);
            var cfg = rt.Config.Current;
            var adapters = await Task.WhenAll(rt.Adapters.Select(async a =>
            {
                var accounts = rt.Accounts.AccountsOf(a.Id);
                try
                {
                    var auth = await a.GetAuthAsync(accounts.Count > 0 ? accounts[0] : null, ctx.RequestAborted);
                    return new { id = a.Id, ready = auth.HasCredential, accounts = accounts.Count, models = rt.Registry.EffectiveCount(a.Id), error = (string?)null };
                }
                catch (Exception e)
                {
                    return new { id = a.Id, ready = false, accounts = accounts.Count, models = rt.Registry.EffectiveCount(a.Id), error = (string?)e.Message };
                }
            }));
            return Json(new
            {
                port = cfg.Port,
                timeoutMs = cfg.TimeoutMs,
                proxyKeySet = !string.IsNullOrEmpty(cfg.ProxyKey),
                adapters,
                groups = rt.Groups.Names().Count(),
                usage = rt.Usage.Summary(),
                breakers = rt.Breakers.Snapshot(),
            });
        });

        app.MapGet("/admin/api/groups", (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Error("Unauthorized", StatusCodes.Status401Unauthorized);
            var result = rt.Groups.All.Select(kv =>
            {
                var (name, g) = kv;
                var preview = rt.Groups.Expand(name, rt.Registry, rt.Accounts, rt.Adapters)
                    .Take(50).Select(n => n.Label).ToArray();
                return new
                {
                    name,
                    g.Match,
                    g.Prefer,
                    g.Description,
                    candidates = preview.Length,
                    preview,
                };
            });
            return Json(new { groups = result });
        });

        app.MapPut("/admin/api/groups/{name}", async (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Error("Unauthorized", StatusCodes.Status401Unauthorized);
            if (rt.ConfigStore is null) return Error("config store unavailable", StatusCodes.Status501NotImplemented);
            if (string.IsNullOrWhiteSpace(ctx.Request.RouteValues["name"]?.ToString()) || ctx.Request.RouteValues["name"]!.ToString()!.Contains('/'))
                return Error("invalid group name", StatusCodes.Status400BadRequest);

            GroupPayload? payload;
            try
            {
                payload = await ctx.Request.ReadFromJsonAsync<GroupPayload>();
            }
            catch
            {
                payload = null;
            }
            if (payload is null || payload.Match is not { Count: > 0 })
                return Error("body must be { match: [...], prefer: [...], description: \"...\" } and match non-empty", StatusCodes.Status400BadRequest);

            var name = ctx.Request.RouteValues["name"]!.ToString()!;
            var raw = rt.ConfigStore.ReadOrCreate();
            var groups = raw["groups"] as JsonObject ?? new JsonObject();
            groups[name] = new JsonObject
            {
                ["match"] = new JsonArray(payload.Match.Select(m => JsonValue.Create(m)).ToArray()),
                ["prefer"] = new JsonArray((payload.Prefer ?? Array.Empty<string>()).Select(p => JsonValue.Create(p)).ToArray()),
                ["description"] = payload.Description ?? "",
            };
            raw["groups"] = groups;
            rt.ConfigStore.WriteRaw(raw);
            return Json(new { ok = true, name });
        });

        app.MapDelete("/admin/api/groups/{name}", (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Error("Unauthorized", StatusCodes.Status401Unauthorized);
            if (rt.ConfigStore is null) return Error("config store unavailable", StatusCodes.Status501NotImplemented);
            var name = ctx.Request.RouteValues["name"]!.ToString()!;
            var raw = rt.ConfigStore.ReadRaw();
            if (raw?["groups"] is not JsonObject groups || !groups.ContainsKey(name))
                return Error($"group not found: {name}", StatusCodes.Status404NotFound);
            groups.Remove(name);
            rt.ConfigStore.WriteRaw(raw);
            return Json(new { ok = true });
        });

        // 草稿预览：纯内存展开当前账号/模型状态下的候选链（复用生产 Expand，含账号轮次语义），不落盘
        app.MapPost("/admin/api/groups/preview", async (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Error("Unauthorized", StatusCodes.Status401Unauthorized);

            JsonObject? body;
            try
            {
                body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted) as JsonObject;
            }
            catch { body = null; }
            if (body is null) return Error("Invalid JSON body", StatusCodes.Status400BadRequest);

            if (body["match"] is not JsonArray matchArr || matchArr.Count == 0)
                return Error("match 必须为非空数组", StatusCodes.Status400BadRequest);
            var match = new List<string>();
            foreach (var m in matchArr)
            {
                if (m is not JsonValue mv || !mv.TryGetValue<string>(out var pattern) || string.IsNullOrWhiteSpace(pattern))
                    return Error("match 必须为非空字符串数组", StatusCodes.Status400BadRequest);
                match.Add(pattern);
            }

            var prefer = new List<string>();
            if (body["prefer"] is JsonArray preferArr)
                foreach (var p in preferArr)
                    if (p is JsonValue pv && pv.TryGetValue<string>(out var platform) && !string.IsNullOrWhiteSpace(platform))
                        prefer.Add(platform);

            var draft = new ModelGroups(new Dictionary<string, GroupConfig>(StringComparer.Ordinal)
            {
                ["__preview"] = new GroupConfig { Match = match, Prefer = prefer },
            });
            var nodes = draft.Expand("__preview", rt.Registry, rt.Accounts, rt.Adapters);

            return Json(new
            {
                candidates = nodes.Count,
                preview = nodes.Take(50).Select(n => n.Label).ToArray(), // 上限 50 条
            });
        });

        // 自动调度实时路由：每个分组当前"会命中"的候选（平台/账号/积分/真实模型/调度状态）。
        // 不调用 TryEnter（有半开探测占用副作用），纯 Snapshot 只读推导，与执行器的选择规则一致。
        app.MapGet("/admin/api/groups/active-routes", (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Error("Unauthorized", StatusCodes.Status401Unauthorized);

            var states = rt.Breakers.Snapshot().ToDictionary(s => s.Key, s => s.State);
            string StateOf(string key) => states.TryGetValue(key, out var st) ? st : "closed";
            static string Worst(string a, string b) =>
                a == "open" || b == "open" ? "open"
                : a == "half-open" || b == "half-open" ? "half-open"
                : "closed";

            var routes = rt.Groups.Names().Select(name =>
            {
                var g = rt.Groups.Get(name);
                var chain = rt.Groups.Expand(name, rt.Registry, rt.Accounts, rt.Adapters);
                var idx = chain.FindIndex(n => StateOf(n.AccountBreakerKey) != "open" && StateOf(n.BreakerKey) != "open");
                var node = idx >= 0 ? chain[idx] : null;
                var self = node is null ? "open" : Worst(StateOf(node.AccountBreakerKey), StateOf(node.BreakerKey));
                // normal=正常直通 half-open=降级切换中（半开探测）avoiding=避让中（首选熔断已后移）blocked=全部熔断 empty=无候选
                var status = chain.Count == 0 ? "empty"
                    : node is null ? "blocked"
                    : idx > 0 ? "avoiding"
                    : self == "half-open" ? "half-open"
                    : "normal";
                return new
                {
                    group = name,
                    match = g?.Match ?? (IReadOnlyList<string>)Array.Empty<string>(),
                    description = g?.Description ?? "",
                    status,
                    candidates = chain.Count,
                    nodeIndex = idx >= 0 ? (int?)idx : null,
                    adapterId = node?.Adapter.Id,
                    accountId = node?.Account?.AccountId,
                    accountLabel = node?.Account?.Label,
                    userId = node?.Account?.UserId ?? node?.Account?.Label,
                    credits = node?.Account?.Credits,
                    model = node?.UpstreamId,
                    preferredAdapterId = chain.Count > 0 ? chain[0].Adapter.Id : null,
                    preferredAccountId = chain.Count > 0 ? chain[0].Account?.AccountId : null,
                    preferredModel = chain.Count > 0 ? chain[0].UpstreamId : null,
                };
            }).ToArray();
            return Json(new { routes });
        });

        // 模型健康矩阵：家族 → 平台 → 模型 / 可用账号数 / 熔断计数；只含统计，不含凭据、路径或 PAT
        app.MapGet("/admin/api/models/matrix", (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Error("Unauthorized", StatusCodes.Status401Unauthorized);

            var breakerStats = rt.Breakers.Snapshot()
                .GroupBy(s => s.Key[..s.Key.IndexOf('|')])
                .ToDictionary(
                    g => g.Key,
                    g => (Open: g.Count(x => x.State == "open"), HalfOpen: g.Count(x => x.State == "half-open")));

            var families = new SortedDictionary<string, SortedDictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var adapter in rt.Adapters)
            {
                foreach (var m in rt.Registry.EffectiveModelsOf(adapter.Id))
                {
                    var family = Registry.FamilyOf(m.UpstreamId);
                    if (!families.TryGetValue(family, out var providers))
                        families[family] = providers = new(StringComparer.OrdinalIgnoreCase);
                    if (!providers.TryGetValue(adapter.Id, out var models))
                        providers[adapter.Id] = models = new List<string>();
                    if (!models.Contains(m.UpstreamId, StringComparer.OrdinalIgnoreCase)) models.Add(m.UpstreamId);
                }
            }

            return Json(new
            {
                families = families.Select(f => new
                {
                    family = f.Key,
                    models = f.Value.Values.SelectMany(v => v)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToArray(),
                    providers = f.Value.ToDictionary(
                        p => p.Key,
                        p =>
                        {
                            breakerStats.TryGetValue(p.Key, out var stat);
                            return (object)new
                            {
                                models = p.Value.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToArray(),
                                availableAccounts = rt.Accounts.AccountsOf(p.Key).Count,
                                openBreakers = stat.Open,
                                halfOpenBreakers = stat.HalfOpen,
                            };
                        }),
                }).ToArray(),
            });
        });

        app.MapGet("/admin/api/accounts", (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Error("Unauthorized", StatusCodes.Status401Unauthorized);
            var breakers = rt.Breakers.Snapshot();
            var result = rt.Adapters.Select(a =>
            {
                var accounts = rt.Accounts.AllAccountsOf(a.Id);
                return new
                {
                    adapter = a.Id,
                    accounts = accounts.Select(acc => new
                    {
                        acc.AccountId,
                        acc.Label,
                        userId = acc.UserId ?? acc.Label,
                        credits = acc.Credits,
                        acc.Enabled,
                        acc.Order,
                        source = acc.SourceFile is not null ? acc.SourceFile : acc.Pat is not null ? "pat" : "builtin",
                        acc.Discovered,
                        openBreakers = breakers.Count(b => (b.Key == $"{a.Id}|{acc.AccountId}" || b.Key.StartsWith($"{a.Id}|{acc.AccountId}|")) && b.State is "open" or "half-open"),
                    }),
                };
            });
            return Json(new { accounts = result, breakers });
        });

        app.MapPost("/admin/api/accounts/toggle", async (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Error("Unauthorized", StatusCodes.Status401Unauthorized);
            var body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted) as JsonObject;
            var adapter = body?["adapter"]?.GetValue<string>();
            var accountId = body?["accountId"]?.GetValue<string>();
            var enabled = body?["enabled"]?.GetValue<bool>() ?? true;
            if (string.IsNullOrEmpty(adapter) || string.IsNullOrEmpty(accountId))
                return Error("adapter and accountId required", StatusCodes.Status400BadRequest);

            rt.Accounts.ToggleAccount(adapter, accountId, enabled, rt.ConfigStore);
            return Json(new { ok = true, adapter, accountId, enabled });
        });

        app.MapPost("/admin/api/accounts/move", async (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Error("Unauthorized", StatusCodes.Status401Unauthorized);
            var body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted) as JsonObject;
            var adapter = body?["adapter"]?.GetValue<string>();
            var accountId = body?["accountId"]?.GetValue<string>();
            var direction = body?["direction"]?.GetValue<int>() ?? 0;
            if (string.IsNullOrEmpty(adapter) || string.IsNullOrEmpty(accountId) || direction == 0)
                return Error("adapter, accountId and direction required", StatusCodes.Status400BadRequest);

            rt.Accounts.MoveOrder(adapter, accountId, direction, rt.ConfigStore);
            return Json(new { ok = true });
        });

        app.MapPost("/admin/api/accounts/credit", async (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Error("Unauthorized", StatusCodes.Status401Unauthorized);
            var body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted) as JsonObject;
            var adapter = body?["adapter"]?.GetValue<string>();
            var accountId = body?["accountId"]?.GetValue<string>();
            var credits = body?["credits"]?.GetValue<int>();
            if (string.IsNullOrEmpty(adapter) || string.IsNullOrEmpty(accountId) || !credits.HasValue)
                return Error("adapter, accountId and credits required", StatusCodes.Status400BadRequest);

            rt.Accounts.UpdateCredit(adapter, accountId, credits.Value, rt.ConfigStore);
            return Json(new { ok = true, adapter, accountId, credits = credits.Value });
        });

        app.MapDelete("/admin/api/accounts/{adapter}/{*accountId}", (string adapter, string accountId, HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Error("Unauthorized", StatusCodes.Status401Unauthorized);
            var decodedId = Uri.UnescapeDataString(accountId);
            rt.Accounts.DeleteAccount(adapter, decodedId, rt.ConfigStore);
            return Json(new { ok = true });
        });

        app.MapPost("/admin/api/accounts/refresh", async (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Error("Unauthorized", StatusCodes.Status401Unauthorized);
            await rt.Accounts.RefreshAsync(rt.Adapters, ctx.RequestAborted);
            try { await SigninServiceInstance.GetStatusAllAsync(rt, ctx.RequestAborted); } catch { }
            return Json(new { ok = true, total = rt.Accounts.TotalCount, manual = rt.Accounts.ManualCount });
        });

        app.MapPost("/admin/api/accounts/refresh-credits", async (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Error("Unauthorized", StatusCodes.Status401Unauthorized);
            var results = await SigninServiceInstance.GetStatusAllAsync(rt, ctx.RequestAborted);
            return Json(new { ok = true, accounts = results.Select(ToJson) });
        });

        app.MapGet("/admin/api/breakers", (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Error("Unauthorized", StatusCodes.Status401Unauthorized);
            return Json(new { breakers = rt.Breakers.Snapshot() });
        });

        app.MapPost("/admin/api/breakers/reset", async (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Error("Unauthorized", StatusCodes.Status401Unauthorized);
            string? key = null;
            try
            {
                // 不能以 ContentLength 判空：HttpClient 的 PostAsJsonAsync 走 chunked 编码时该值为 null
                var body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted) as JsonObject;
                key = body?["key"]?.GetValue<string>();
            }
            catch { /* body 可省略 */ }
            if (key is null) rt.Breakers.ResetAll();
            else rt.Breakers.Reset(key);
            return Json(new { ok = true, reset = key ?? "all" });
        });

        // 用量多维聚合：按平台汇总 / 模型 TOP 排行 / 按日趋势；可选 from/to（yyyy-MM-dd 闭区间）与 top。
        // SQLite 持久化可用时查库（含历史），否则回退内存聚合（仅本次进程数据），两种 source 结构一致。
        app.MapGet("/admin/api/usage/summary", async (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Error("Unauthorized", StatusCodes.Status401Unauthorized);
            var from = ctx.Request.Query["from"].FirstOrDefault();
            var to = ctx.Request.Query["to"].FirstOrDefault();
            var top = int.TryParse(ctx.Request.Query["top"].FirstOrDefault(), out var t) ? t : 10;

            if (rt.Usage.Store is { } store)
            {
                var rows = await store.QueryAsync(from, to);
                return Json(UsageAggregates.Summarize(rows
                    .Select(r => new UsageRow(r.AdapterId, r.Model, r.AccountId, r.Date, r.Requests, r.PromptTokens, r.CompletionTokens))
                    .ToList(), from, to, top, "sqlite"));
            }
            return Json(rt.Usage.Aggregate(from, to, top));
        });

        app.MapGet("/admin/api/config", (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Error("Unauthorized", StatusCodes.Status401Unauthorized);
            if (rt.ConfigStore is null) return Error("config store unavailable", StatusCodes.Status501NotImplemented);
            return Json(new { path = rt.ConfigStore.Path, config = rt.ConfigStore.ReadOrCreate() });
        });

        app.MapPut("/admin/api/config", async (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Error("Unauthorized", StatusCodes.Status401Unauthorized);
            if (rt.ConfigStore is null) return Error("config store unavailable", StatusCodes.Status501NotImplemented);

            JsonObject? incoming;
            try
            {
                incoming = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted) as JsonObject;
            }
            catch
            {
                incoming = null;
            }
            if (incoming is null) return Error("Invalid JSON body", StatusCodes.Status400BadRequest);

            // 合并语义：body 中出现的顶层键覆盖，未出现的保留（groups/accounts 也可整体替换）
            var raw = rt.ConfigStore.ReadOrCreate();
            foreach (var (key, value) in incoming)
                raw[key] = value?.DeepClone();

            rt.ConfigStore.WriteRaw(raw);
            return Json(new { ok = true, path = rt.ConfigStore.Path });
        });

        // ─── 签到 API ────────────────────────────────────────────────────────

        app.MapGet("/admin/api/signin/status", async (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Error("Unauthorized", StatusCodes.Status401Unauthorized);
            var results = (await _signin.GetStatusAllAsync(rt, ctx.RequestAborted)).ToList();
            // 无签到能力的平台（如 qoder）显式标 UNSUPPORTED，便于前端统一渲染而非默默缺席
            foreach (var adapter in rt.Adapters.Where(a => !SigninService.SigninAdapters.Contains(a.Id, StringComparer.OrdinalIgnoreCase)))
                foreach (var acc in rt.Accounts.AllAccountsOf(adapter.Id))
                    results.Add(new SigninResult(acc.AdapterId, acc.AccountId, acc.Label, adapter.Id, "UNSUPPORTED",
                        "该平台暂无签到/积分查询能力", null, null, null, null));
            return Json(new
            {
                autoSigninEnabled = rt.Config.Current.Admin.AutoSignin,
                lastRun = _signin.LastRunAt?.ToUnixTimeMilliseconds(),
                accounts = results.Select(ToJson)
            });
        });

        app.MapPost("/admin/api/signin/run", async (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Error("Unauthorized", StatusCodes.Status401Unauthorized);
            var results = await _signin.RunAllAsync(rt, ctx.RequestAborted);
            return Json(new
            {
                lastRun = _signin.LastRunAt?.ToUnixTimeMilliseconds(),
                accounts = results.Select(ToJson)
            });
        });
    }

    // ─── 签到辅助 ─────────────────────────────────────────────────────────

    private static readonly SigninService _signin = new();
    public static SigninService SigninServiceInstance => _signin;

    private static object ToJson(SigninResult r) => new
    {
        adapterId = r.AdapterId,
        accountId = r.AccountId,
        label = r.AccountLabel,
        platform = r.Platform,
        result = r.Result,
        report = r.Report,
        totalCredits = r.TotalCredits,   // 剩余可用积分（= creditsRemain，语义不变）
        todayCredit = r.TodayCredit,
        streakDays = r.StreakDays,
        todayCheckedIn = r.TodayCheckedIn,
        creditsUsed = r.CreditsUsed,     // 已用积分（get-user-resource 聚合，经 size-remain 补全与 TotalDosage 校准）
        creditsTotal = r.CreditsTotal,   // 总量（含 TotalDosage 校准）
        packCount = r.PackCount,         // 套餐包数量
        errorDetail = r.ErrorDetail,
        signinSupported = SigninService.SigninAdapters.Contains(r.AdapterId, StringComparer.OrdinalIgnoreCase),
    };

    // ─── 其他私有成员 ─────────────────────────────────────────────────────

    private sealed record GroupPayload(IReadOnlyList<string>? Match, IReadOnlyList<string>? Prefer, string? Description);

    private static IResult ServeHtml()
    {
        var stream = typeof(AdminApi).Assembly.GetManifestResourceStream("ProxyHub.wwwroot.admin.html");
        if (stream is null)
            return Results.Problem("admin.html 资源缺失（程序集未嵌入 ProxyHub.wwwroot.admin.html）。", statusCode: StatusCodes.Status500InternalServerError);
        return Results.Stream(stream, "text/html; charset=utf-8");
    }
}
