using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProxyHub;

/// <summary>运行时组件聚合：AppFactory 与 Admin API 共享的全部部件。</summary>
public sealed class ProxyHubRuntime
{
    public required RuntimeConfig Config { get; init; }
    public required Registry Registry { get; init; }
    public required UsageTracker Usage { get; init; }
    public required IReadOnlyList<IAdapter> Adapters { get; init; }
    public required AccountRegistry Accounts { get; init; }
    public required CircuitBreakerRegistry Breakers { get; init; }
    public required ModelGroups Groups { get; init; }
    public required FailoverExecutor Executor { get; init; }

    /// <summary>配置存取（管理页写回用）；单元测试注入假适配器时可为 null。</summary>
    public ConfigStore? ConfigStore { get; init; }
}

/// <summary>
/// 应用组装工厂：把 配置/注册表/账号池/熔断/分组/执行器 组装成 WebApplication（全部路由）。
/// 从 Program 分离出来，使集成测试可以注入假适配器与独立配置（依赖注入模式）。
/// </summary>
public static class AppFactory
{
    public static WebApplication Build(ProxyHubRuntime rt, string? url = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url ?? $"http://127.0.0.1:{rt.Config.Current.Port}");
        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        var app = builder.Build();

        IResult Json(object payload, int status = StatusCodes.Status200OK) =>
            Results.Json(payload, statusCode: status);

        IResult Error(string message, int status) =>
            Json(new { error = new { message } }, status);

        bool Authorized(HttpContext ctx) => rt.Config.IsAuthorized(ctx);

        app.MapGet("/health", () => Json(new { status = "ok" }));

        app.MapGet("/v1/models", (HttpContext ctx) => !Authorized(ctx)
            ? Error("Unauthorized: missing or bad proxy key", StatusCodes.Status401Unauthorized)
            : Json(new { @object = "list", data = ModelsWithGroups(rt) }));
        app.MapGet("/models", (HttpContext ctx) => !Authorized(ctx)
            ? Error("Unauthorized: missing or bad proxy key", StatusCodes.Status401Unauthorized)
            : Json(new { @object = "list", data = ModelsWithGroups(rt) }));

        app.MapGet("/v1/models/matrix", (HttpContext ctx) => !Authorized(ctx)
            ? Error("Unauthorized: missing or bad proxy key", StatusCodes.Status401Unauthorized)
            : Json(new
            {
                families = rt.Registry.ModelMatrix(),
                pricingNote = "各平台采用订阅/积分计费，无公开单 token 单价；本接口仅提供模型能力对比。",
            }));
        app.MapGet("/models/matrix", (HttpContext ctx) => !Authorized(ctx)
            ? Error("Unauthorized: missing or bad proxy key", StatusCodes.Status401Unauthorized)
            : Json(new
            {
                families = rt.Registry.ModelMatrix(),
                pricingNote = "各平台采用订阅/积分计费，无公开单 token 单价；本接口仅提供模型能力对比。",
            }));

        app.MapGet("/usage", (HttpContext ctx) => !Authorized(ctx)
            ? Error("Unauthorized: missing or bad proxy key", StatusCodes.Status401Unauthorized)
            : Json(rt.Usage.Summary()));

        app.MapGet("/status", async (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Error("Unauthorized: missing or bad proxy key", StatusCodes.Status401Unauthorized);

            // Fail Fast 探测：凭据缺失时报告原因而非抛崩；按账号数与熔断状态一并汇报
            var probes = await Task.WhenAll(rt.Adapters.Select(async a =>
            {
                var accounts = rt.Accounts.AccountsOf(a.Id);
                try
                {
                    var auth = await a.GetAuthAsync(accounts.Count > 0 ? accounts[0] : null, ctx.RequestAborted);
                    return new
                    {
                        id = a.Id,
                        ready = auth.HasCredential,
                        detail = auth.HasCredential ? "ok" : "auth shape missing",
                        accounts = accounts.Count,
                    };
                }
                catch (Exception e)
                {
                    return new { id = a.Id, ready = false, detail = e.Message, accounts = accounts.Count };
                }
            }));
            return Json(new { adapters = probes });
        });

        async Task<IResult> HandleChatAsync(HttpContext ctx, bool isResponses)
        {
            if (!Authorized(ctx)) return Error("Unauthorized: missing or bad proxy key", StatusCodes.Status401Unauthorized);

            JsonObject? body;
            try
            {
                body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted) as JsonObject;
            }
            catch
            {
                body = null;
            }
            if (body is null) return Error("Invalid JSON body", StatusCodes.Status400BadRequest);

            if (body["messages"] is null && body["input"] is not null)
            {
                var inputVal = body["input"]?.ToString() ?? "";
                body["messages"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = inputVal
                    }
                };
            }

            var model = body["model"]?.GetValue<string>() ?? "auto";
            var chain = BuildChain(model, rt);
            if (chain.Count == 0)
            {
                var ids = string.Join(", ", ModelsWithGroups(rt).Select(m => m.Id));
                return Error($"Unknown model: {model}. Available: {ids}", StatusCodes.Status404NotFound);
            }

            var cfg = rt.Config.Current;
            var clientStream = body["stream"] is JsonValue sv && sv.TryGetValue<bool>(out var cs) && cs && !isResponses;

            if (clientStream)
            {
                // failoverHeader 为热更新配置：每请求读取当前值
                var sink = new SseSink(ctx.Response, rt.Config.Current.Admin.FailoverHeader);
                try
                {
                    var node = await rt.Executor.ExecuteAsync(chain, body, sink, ctx.RequestAborted);
                    await sink.WriteDoneAsync(ctx.RequestAborted);
                    RecordUsage(rt, node, sink);
                    return Results.Empty;
                }
                catch (ChainExhaustedException e)
                {
                    if (sink.Committed)
                    {
                        await sink.WriteDoneAsync(CancellationToken.None);
                        return Results.Empty;
                    }
                    return Error(e.Message, StatusCodes.Status502BadGateway);
                }
                catch (UpstreamException e) when (!e.IsRetryable)
                {
                    return Error(e.Message, MapStatus(e.StatusCode));
                }
            }

            var collector = new CollectingSink();
            try
            {
                var node = await rt.Executor.ExecuteAsync(chain, body, collector, ctx.RequestAborted);
                var result = Sse.Aggregate(collector.Chunks, body);
                RecordUsage(rt, node, collector);

                if (isResponses)
                {
                    var text = result["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
                    var respObj = new JsonObject
                    {
                        ["id"] = result["id"]?.GetValue<string>() ?? $"resp-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                        ["object"] = "response",
                        ["created"] = result["created"]?.GetValue<long>() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        ["model"] = model,
                        ["output"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "message",
                                ["role"] = "assistant",
                                ["content"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["type"] = "text",
                                        ["text"] = text
                                    }
                                }
                            }
                        },
                        ["usage"] = result["usage"]?.DeepClone()
                    };
                    return Results.Text(respObj.ToJsonString(), "application/json");
                }

                return Results.Text(result.ToJsonString(), "application/json");
            }
            catch (ChainExhaustedException e)
            {
                return Error(e.Message, StatusCodes.Status502BadGateway);
            }
            catch (UpstreamException e) when (!e.IsRetryable)
            {
                return Error(e.Message, MapStatus(e.StatusCode));
            }
        }

        app.MapPost("/v1/chat/completions", async (HttpContext ctx) => { var r = await HandleChatAsync(ctx, false); await r.ExecuteAsync(ctx); });
        app.MapPost("/chat/completions", async (HttpContext ctx) => { var r = await HandleChatAsync(ctx, false); await r.ExecuteAsync(ctx); });
        app.MapPost("/v1/responses", async (HttpContext ctx) => { var r = await HandleChatAsync(ctx, true); await r.ExecuteAsync(ctx); });
        app.MapPost("/responses", async (HttpContext ctx) => { var r = await HandleChatAsync(ctx, true); await r.ExecuteAsync(ctx); });

        AdminApi.MapAdminApi(app, rt);

        return app;
    }

    /// <summary>对外模型列表 = 各适配器生效模型 + 虚拟分组（owned_by=proxyhub）。</summary>
    private static IEnumerable<ModelInfo> ModelsWithGroups(ProxyHubRuntime rt)
    {
        foreach (var m in rt.Registry.ListModels())
            yield return m;
        foreach (var g in rt.Groups.Names())
            yield return new ModelInfo(g, "proxyhub");
    }

    /// <summary>
    /// 构建候选链：
    /// 1. 若为分组或命中特化分组规则（如请求 deepseek-v4.1-flash 自动命中 deepseek-v4.1-flash-auto 分组），按分组展开跨平台×多账号轮次链；
    /// 2. 若多平台均支持该模型，展开跨平台多适配器×多账号轮次候选链（首选平台熔断后无缝自动切下一平台）；
    /// 3. 兜底尝试 auto 分组。
    /// </summary>
    private static List<ChainNode> BuildChain(string model, ProxyHubRuntime rt)
    {
        // 1. 优先查命中分组（精确名或专属特化分组通配匹配）
        var matchedGroup = rt.Groups.FindMatchingGroup(model);
        if (matchedGroup is not null)
        {
            var nodes = rt.Groups.Expand(matchedGroup, rt.Registry, rt.Accounts, rt.Adapters);
            // 若特化分组候选链全部熔断/失败，自动追加 auto 备用模型，实现无缝切换
            if (!matchedGroup.Equals("auto", StringComparison.OrdinalIgnoreCase) && rt.Groups.IsGroup("auto"))
            {
                var fallback = rt.Groups.Expand("auto", rt.Registry, rt.Accounts, rt.Adapters);
                var seen = new HashSet<string>(nodes.Select(n => n.BreakerKey), StringComparer.OrdinalIgnoreCase);
                nodes.AddRange(fallback.Where(f => seen.Add(f.BreakerKey)));
            }
            return nodes;
        }

        // 2. 跨平台多适配器同名模型聚合轮次链
        var candidates = rt.Registry.ResolveAll(model);
        if (candidates.Count > 0)
        {
            var perAdapter = candidates.Select(c =>
            {
                var accs = rt.Accounts.AccountsOf(c.Adapter.Id).ToList();
                if (accs.Count == 0) accs.Add(null!);
                return (c.Adapter, c.UpstreamId, Accounts: accs);
            }).ToList();

            var nodes = new List<ChainNode>();
            var maxRounds = perAdapter.Max(x => x.Accounts.Count);
            for (var round = 0; round < maxRounds; round++)
            {
                foreach (var x in perAdapter)
                {
                    if (round < x.Accounts.Count)
                        nodes.Add(new ChainNode(x.Adapter, x.Accounts[round], x.UpstreamId, model));
                }
            }

            // 同理：若该同名模型在全部平台账号上均熔断，自动追加 auto 备用模型
            if (rt.Groups.IsGroup("auto"))
            {
                var fallback = rt.Groups.Expand("auto", rt.Registry, rt.Accounts, rt.Adapters);
                var seen = new HashSet<string>(nodes.Select(n => n.BreakerKey), StringComparer.OrdinalIgnoreCase);
                nodes.AddRange(fallback.Where(f => seen.Add(f.BreakerKey)));
            }
            return nodes;
        }

        // 3. 兜底回退 auto 分组
        if (rt.Groups.IsGroup("auto"))
            return rt.Groups.Expand("auto", rt.Registry, rt.Accounts, rt.Adapters);

        return new List<ChainNode>();
    }

    private static void RecordUsage(ProxyHubRuntime rt, ChainNode node, IChunkSink sink)
    {
        long? Prompt(JsonObject? u) => u?["prompt_tokens"] is JsonValue p && p.TryGetValue<long>(out var v) ? v : null;
        long? Completion(JsonObject? u) => u?["completion_tokens"] is JsonValue c && c.TryGetValue<long>(out var v) ? v : null;
        rt.Usage.Record(node.Adapter.Id, node.UpstreamId, Prompt(sink.Usage), Completion(sink.Usage),
            (int)Math.Min(sink.ContentChars, int.MaxValue));
    }

    private static int MapStatus(int statusCode) =>
        statusCode is >= 400 and <= 599 ? statusCode : StatusCodes.Status502BadGateway;
}
