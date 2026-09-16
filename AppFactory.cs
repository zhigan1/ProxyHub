using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProxyHub;

/// <summary>
/// 应用组装工厂：把 配置/注册表/用量/适配器 组装成 WebApplication（全部路由）。
/// 从 Program 分离出来，使集成测试可以注入假适配器与独立配置（依赖注入模式）。
/// </summary>
public static class AppFactory
{
    public static WebApplication Build(
        ProxyHubConfig config,
        Registry registry,
        UsageTracker usage,
        IReadOnlyList<IAdapter> adapters,
        string? url = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url ?? $"http://127.0.0.1:{config.Port}");
        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        var app = builder.Build();

        IResult Json(object payload, int status = StatusCodes.Status200OK) =>
            Results.Json(payload, statusCode: status);

        IResult Error(string message, int status) =>
            Json(new { error = new { message } }, status);

        bool Authorized(HttpContext ctx)
        {
            if (string.IsNullOrEmpty(config.ProxyKey)) return true;
            var h = ctx.Request.Headers.Authorization.ToString();
            return h == $"Bearer {config.ProxyKey}" || h == $"x-api-key {config.ProxyKey}";
        }

        app.MapGet("/health", () => Json(new { status = "ok" }));

        app.MapGet("/v1/models", (HttpContext ctx) => !Authorized(ctx)
            ? Error("Unauthorized: missing or bad proxy key", StatusCodes.Status401Unauthorized)
            : Json(new { @object = "list", data = registry.ListModels() }));
        app.MapGet("/models", (HttpContext ctx) => !Authorized(ctx)
            ? Error("Unauthorized: missing or bad proxy key", StatusCodes.Status401Unauthorized)
            : Json(new { @object = "list", data = registry.ListModels() }));

        app.MapGet("/v1/models/matrix", (HttpContext ctx) => !Authorized(ctx)
            ? Error("Unauthorized: missing or bad proxy key", StatusCodes.Status401Unauthorized)
            : Json(new
            {
                families = registry.ModelMatrix(),
                pricingNote = "各平台采用订阅/积分计费，无公开单 token 单价；本接口仅提供模型能力对比。",
            }));
        app.MapGet("/models/matrix", (HttpContext ctx) => !Authorized(ctx)
            ? Error("Unauthorized: missing or bad proxy key", StatusCodes.Status401Unauthorized)
            : Json(new
            {
                families = registry.ModelMatrix(),
                pricingNote = "各平台采用订阅/积分计费，无公开单 token 单价；本接口仅提供模型能力对比。",
            }));

        app.MapGet("/usage", (HttpContext ctx) => !Authorized(ctx)
            ? Error("Unauthorized: missing or bad proxy key", StatusCodes.Status401Unauthorized)
            : Json(usage.Summary()));

        app.MapGet("/status", async (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Error("Unauthorized: missing or bad proxy key", StatusCodes.Status401Unauthorized);

            // Fail Fast 探测：凭据缺失时报告原因而非抛崩
            var probes = await Task.WhenAll(adapters.Select(async a =>
            {
                try
                {
                    var auth = await a.GetAuthAsync(ctx.RequestAborted);
                    return new { id = a.Id, ready = auth.HasCredential, detail = auth.HasCredential ? "ok" : "auth shape missing" };
                }
                catch (Exception e)
                {
                    return new { id = a.Id, ready = false, detail = e.Message };
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
            var resolved = registry.ResolveModel(model);
            if (resolved is null)
            {
                var ids = string.Join(", ", registry.ListModels().Select(m => m.GetType().GetProperty("id")?.GetValue(m) as string));
                return Error($"Unknown model: {model}. Available: {ids}", StatusCodes.Status404NotFound);
            }

            var (adapter, upstreamId) = resolved.Value;
            var upstreamBody = (JsonObject)body.DeepClone();
            upstreamBody["model"] = upstreamId;
            upstreamBody["stream"] = true; // 网关内部恒走流式，出口再按需聚合

            try
            {
                await adapter.GetAuthAsync(ctx.RequestAborted);
            }
            catch (Exception e)
            {
                return Error($"Adapter {adapter.Id} auth failed: {e.Message}", StatusCodes.Status401Unauthorized);
            }

            if (body["stream"] is JsonValue sv && sv.TryGetValue<bool>(out var clientStream) && clientStream && !isResponses)
            {
                // 流式：SSE 直通出口（统一加 [DONE] 收尾）
                await Sse.StreamResponseAsync(ctx.Response,
                    emit => adapter.ChatAsync(upstreamBody, emit, ctx.RequestAborted),
                    ctx.RequestAborted);
                return Results.Empty;
            }

            // 非流式：聚合 chunk 为单个 chat.completion，并记录用量
            try
            {
                var result = await Sse.CollectNonStreamingAsync(adapter, upstreamBody, ctx.RequestAborted);
                var usageNode = result["usage"] as JsonObject;
                usage.Record(
                    adapter.Id,
                    upstreamId,
                    usageNode?["prompt_tokens"]?.GetValue<long>(),
                    usageNode?["completion_tokens"]?.GetValue<long>(),
                    result["choices"]?[0]?["message"]?["content"]?.GetValue<string>().Length ?? 0);

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
                        ["usage"] = usageNode?.DeepClone()
                    };
                    return Results.Text(respObj.ToJsonString(), "application/json");
                }

                return Results.Text(result.ToJsonString(), "application/json");
            }
            catch (Exception e)
            {
                return Error($"Upstream failed: {e.Message}", StatusCodes.Status502BadGateway);
            }
        }

        app.MapPost("/v1/chat/completions", async (HttpContext ctx) => { var r = await HandleChatAsync(ctx, false); await r.ExecuteAsync(ctx); });
        app.MapPost("/chat/completions", async (HttpContext ctx) => { var r = await HandleChatAsync(ctx, false); await r.ExecuteAsync(ctx); });
        app.MapPost("/v1/responses", async (HttpContext ctx) => { var r = await HandleChatAsync(ctx, true); await r.ExecuteAsync(ctx); });
        app.MapPost("/responses", async (HttpContext ctx) => { var r = await HandleChatAsync(ctx, true); await r.ExecuteAsync(ctx); });

        return app;
    }
}
