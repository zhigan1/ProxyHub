# ProxyHub

一个把本机多个 AI 编程平台的登录凭据与额度统一封装成**单套 OpenAI 兼容服务**的反向代理网关（.NET 10 / ASP.NET Core）。

任意 OpenAI 客户端（Cherry Studio、Cursor、Cline、OpenRouter 等）只需把 `base_url` 指向本网关，用 `{平台}-{模型}` 形式的模型 ID，即可在各平台间透明切换，无需分别处理各平台私有的调用协议与登录态。

## 特性

- **统一 OpenAI 协议出口**：对外只暴露 `/v1/chat/completions`、`/v1/models` 等标准接口，流式（SSE）与非流式（聚合）均可。
- **多平台适配**：支持 CodeBuddy / WorkBuddy、Trae 国内版、TraeWork 桌面版、Qoder（CLI 桥接）。
- **自动凭据发现**：自动读取本机桌面端登录态（含 Trae 系加密凭据的本地解密），无需手动填 key。
- **到期自动换新**：TraeWork 在上游返回 401/403 时用本地 refreshToken 自动换取新 token 并重试。
- **动态模型列表**：启动时自动从平台拉取"当前账号可用"模型（Qoder 走 `--list-models`），拉取失败自动回退内置基线，`/v1/models` 始终反映当前生效列表。
- **零第三方依赖**：仅使用 .NET BCL 与 ASP.NET Core 共享框架。

## 快速开始

> 先在本机安装并登录你需要的任一平台桌面端，网关会自动发现其登录态。

```bash
# 在仓库根目录运行
dotnet run --project ProxyHub
```

默认监听 `http://127.0.0.1:8787`。

## 客户端接入

以 Cherry Studio 为例：

| 配置项 | 值 |
| --- | --- |
| API 地址 | `http://127.0.0.1:8787/v1` |
| API Key | 不开启鉴权时任意值；开启后填代理密钥 |
| 模型 ID | `{平台}-{模型}`，见下方表 |

示例模型 ID（具体以下发的 `GET /v1/models` 清单为准）：

| 平台前缀 | 模型 ID 示例 | 说明 |
| --- | --- | --- |
| `codebuddy` | `codebuddy-glm-5.2`、`codebuddy-deepseek-v4-pro` | CodeBuddy / WorkBuddy |
| `traecn` | `traecn-glm-5.2`、`traecn-kimi-k2.6` | Trae 国内版 |
| `traework` | `traework-glm-5.2` | TraeWork 桌面版 |
| `qoder` | `qoder-qwen3.7-max`、`qoder-glm-5.2` | Qoder，需本机装有 qoderclicn CLI |

## 配置

优先级：**环境变量 > config.json > 默认值**（复写版按实际行为实现）。

### 环境变量

| 变量 | 默认值 | 说明 |
| --- | --- | --- |
| `PROXY_HUB_PORT` | `8787` | 监听端口 |
| `PROXY_HUB_KEY` | 空 | 可选，客户端 Bearer / x-api-key 鉴权密钥；为空则不做鉴权 |
| `PROXY_HUB_TIMEOUT` | `120000` | 上游请求超时（毫秒） |
| `PROXY_ADAPTER_CODEBUDDY` | `true` | 是否启用 CodeBuddy 适配器 |
| `PROXY_ADAPTER_TRAECN` | `true` | 是否启用 Trae CN 适配器 |
| `PROXY_ADAPTER_TRAEWORK` | `true` | 是否启用 TraeWork 适配器 |
| `PROXY_ADAPTER_QODER` | `true` | 是否启用 Qoder 适配器 |
| `PROXY_HUB_CONFIG` | — | 指定 config.json 路径 |
| `QODERCN_CLI` | `qoderclicn` | Qoder CLI 可执行文件 |
| `QODERCN_PERSONAL_ACCESS_TOKEN` | — | Qoder 的 PAT（无 CLI 登录态时使用） |

```powershell
$env:PROXY_HUB_PORT = "8899"
$env:PROXY_HUB_KEY  = "your-secret"
dotnet run --project ProxyHub
```

### config.json

在 `ProxyHub` 项目目录（或 `PROXY_HUB_CONFIG` 指定路径）放置：

```json
{
  "port": 8787,
  "proxyKey": "",
  "timeoutMs": 120000,
  "adapters": {
    "codebuddy": true,
    "traecn": true,
    "traework": true,
    "qoder": true
  }
}
```

## HTTP 接口

| 方法 | 路径 | 说明 | 鉴权 |
| --- | --- | --- | --- |
| GET | `/health` | 健康检查 | 否 |
| GET | `/v1/models` | 可用模型列表（OpenAI 格式） | 可选 |
| GET | `/v1/models/matrix` | 跨平台模型能力矩阵（按模型家族分组） | 可选 |
| POST | `/v1/chat/completions` | 对话（流式 / 非流式） | 可选 |
| GET | `/usage` | 各平台用量统计 | 可选 |
| GET | `/status` | 各适配器就绪状态（Fail-Fast 探测） | 可选 |

### 调用示例

非流式（标准 OpenAI 语义）：

```bash
curl -X POST http://127.0.0.1:8787/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "traework-glm-5.2",
    "messages": [{"role": "user", "content": "你好"}]
  }'
```

流式（SSE 事件流，末尾统一 `data: [DONE]`）：

```bash
curl -N -X POST http://127.0.0.1:8787/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "codebuddy-glm-5.2",
    "stream": true,
    "messages": [{"role": "user", "content": "从 1 数到 3"}]
  }'
```

开启鉴权后，携带密钥：

```bash
curl -X POST http://127.0.0.1:8787/v1/chat/completions \
  -H "Authorization: Bearer your-secret" \
  -H "Content-Type: application/json" \
  -d '{"model":"traecn-glm-5.2","messages":[{"role":"user","content":"hi"}]}'
```

## 架构

```
HTTP 入口（AppFactory：鉴权 · 路由 · 错误语义）
   └─ Registry（模型前缀路由 + 能力矩阵）
        └─ Adapter（auth / models / request / stream 四职责）
             ├─ CodeBuddy —— HTTPS，CLI 身份伪装头
             ├─ TraeCn   —— HTTPS，本地加密凭据解密 + IDE 伪装头 + 端点故障转移
             ├─ TraeWork —— HTTPS，同 TraeCn，另带 401/403 自动换新重试
             └─ Qoder    —— spawn 本机 CLI 子进程，stream-json 桥接
        └─ CredentialsCache（TTL + in-flight 去重的凭据缓存）
        └─ Sse（统一出口：流式直通 / 非流式聚合）
        └─ UsageTracker（按 平台/模型/日期 统计）
```

设计要点：

- **网关内部恒走流式**：请求体一律改为 `stream=true` 与上游通信，出口再按客户端需求直通或聚合，避免两套上游处理路径。
- **协议翻译型反代**：客户端只讲 OpenAI 协议，各平台私有协议全部在适配器层吸收（请求改写、头伪装、SSE 事件归一化）。
- **真流式**：上游响应按行增量读取，首个 token 即时下发。
- **新增平台只需实现 `IAdapter`** 并在启动处注册即可。

## 测试

```bash
dotnet test
```

测试覆盖：模型路由与能力矩阵、SSE 直通与聚合、token 估算、凭据缓存（TTL / 过期 / 并发去重）、用量统计、配置加载于环境变量覆盖、各适配器协议归一化、本地加密凭据的往返与防篡改、动态模型拉取与静态回退、端到端 HTTP 路由 / 鉴权 / 错误码 / 用量闭环。

## 动态模型列表

模型来源分两级：**动态优先，静态回退**。

- 每个 `IAdapter` 可由 `RegisterModels()` 声明内置基线（兜底），并可选择性实现 `FetchModelsAsync()` 从平台实时拉取当前账号可用的模型。
- 网关启动时在后台调用所有适配器的动态源；成功且非空的适配器用动态表覆盖基线，拉取失败（未装 CLI、未登录、无稳定枚举端点等）静默保留基线，**不阻塞启动**。
- 新增平台如想支持动态模型，只需实现 `FetchModelsAsync()` 并返回 `null`（= 不支持、回退静态）或抛错（= 拉取失败、回退静态）：
  ```csharp
  public Task<IReadOnlyList<ModelRegistration>?> FetchModelsAsync(CancellationToken ct = default)
  {
      // … 调用平台枚举接口，把结果映射为 ({平台}-{模型}, {上游模型 ID})
  }
  ```
- 各平台现状：**Qoder** 已接入真实动态源（`qoderclicn --list-models`，宽容解析 JSON 或表格行）；**CodeBuddy / Trae 系**暂未提供免鉴权的稳定枚举端点，使用内置基线（可自行在 `FetchModelsAsync` 中接入厂商模型管理 API）。

## 错误码语义

| 状态码 | 触发条件 | 说明 |
| --- | --- | --- |
| 400 | 请求体非合法 JSON | `Invalid JSON body` |
| 401 | 代理密钥错误 / 适配器凭据缺失 | `Unauthorized …` / `Adapter {id} auth failed …` |
| 404 | 模型 ID 未注册 | 附全部可用模型 ID |
| 502 | 非流式路径上游调用失败 | `Upstream failed …` |

## 目录结构

```
ProxyHub/
  Program.cs                 进程入口：加载配置、组装适配器
  AppFactory.cs              全部路由 + 鉴权 + SSE 出口（依赖可注入）
  Registry.cs                模型前缀路由 + 能力矩阵
  Sse.cs                     SSE 写出 + 非流式聚合 + token 估算
  CredentialsCache.cs        凭据缓存（TTL + in-flight 去重）
  UsageTracker.cs            用量统计（线程安全）
  TcCrypto.cs                本地加密凭据解密（AES-128-CBC + SHA-512 完整性校验）
  UpstreamHttp.cs            共享传输层：连接池 + 增量 SSE 行解析
  IAdapter.cs                适配器契约
  Adapters/
    CodeBuddyAdapter.cs
    TraeAdapterBase.cs       Trae 系公共：凭据、伪装头、端点转移、SSE 归一化
    TraeCnAdapter.cs
    TraeWorkAdapter.cs
    QoderAdapter.cs
ProxyHub.Tests/              全部测试
```

## 已知限制

- 流式路径中上游中途报错会被吞为规范的 `[DONE]` 收尾（为保持客户端协议完整性，不推荐改由客户端感知；如需可在会话层记录错误）。
- Trae 系上游不返回 token 用量，网关按字符数 ÷4 估算，跨平台数字不能精确横向比较。
- 用量统计仅记录非流式请求；纯内存，重启清零。
- Qoder 依赖本机安装 qoderclicn CLI。
- 各平台采用订阅/积分计费，无公开单 token 单价，`/v1/models/matrix` 仅提供能力对比。