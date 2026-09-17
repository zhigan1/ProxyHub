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
                var sink = new SseSink(ctx.Response);
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
    /// 构建候选链：分组模型 → 展开为跨平台×多账号有序链；真实模型 → 该模型×该平台全部账号（天然具备切号能力）。
    /// </summary>
    private static List<ChainNode> BuildChain(string model, ProxyHubRuntime rt)
    {
        if (rt.Groups.IsGroup(model))
            return rt.Groups.Expand(model, rt.Registry, rt.Accounts, rt.Adapters);

        var resolved = rt.Registry.ResolveModel(model);
        if (resolved is null) return new List<ChainNode>();

        var (adapter, upstreamId) = resolved.Value;
        var accounts = rt.Accounts.AccountsOf(adapter.Id);
        if (accounts.Count == 0)
            return new List<ChainNode> { new(adapter, null, upstreamId, model) };
        return accounts
            .Select(a => new ChainNode(adapter, a, upstreamId, model))
            .ToList();
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
