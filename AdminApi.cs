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

        app.MapGet("/admin/api/accounts", (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Error("Unauthorized", StatusCodes.Status401Unauthorized);
            var breakers = rt.Breakers.Snapshot();
            var result = rt.Adapters.Select(a =>
            {
                var accounts = rt.Accounts.AccountsOf(a.Id);
                return new
                {
                    adapter = a.Id,
                    accounts = accounts.Select(acc => new
                    {
                        acc.AccountId,
                        acc.Label,
                        source = acc.SourceFile is not null ? acc.SourceFile : acc.Pat is not null ? "pat" : "builtin",
                        acc.Discovered,
                        openBreakers = breakers.Count(b => b.Key.StartsWith($"{a.Id}|{acc.AccountId}|") && b.State is "open" or "half-open"),
                    }),
                };
            });
            return Json(new { accounts = result, breakers });
        });

        app.MapPost("/admin/api/accounts/refresh", async (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Error("Unauthorized", StatusCodes.Status401Unauthorized);
            await rt.Accounts.RefreshAsync(rt.Adapters, ctx.RequestAborted);
            return Json(new { ok = true, total = rt.Accounts.TotalCount, manual = rt.Accounts.ManualCount });
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
    }

    private sealed record GroupPayload(IReadOnlyList<string>? Match, IReadOnlyList<string>? Prefer, string? Description);

    private static IResult ServeHtml()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "wwwroot", "admin.html");
        if (!File.Exists(path))
            return Results.Text("admin.html 缺失（构建产物未包含 wwwroot）。", "text/plain");
        return Results.File(path, "text/html; charset=utf-8");
    }
}
