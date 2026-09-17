# ProxyHub

一个把本机多个 AI 编程平台的登录凭据与额度统一封装成**单套 OpenAI 兼容服务**的反向代理网关（.NET 10 / ASP.NET Core）。

任意 OpenAI 客户端（Cherry Studio、Cursor、Cline、OpenRouter 等）只需把 `base_url` 指向本网关，用 `{平台}-{模型}` 形式的模型 ID，即可在各平台间透明切换，无需分别处理各平台私有的调用协议与登录态。

## 特性

- **统一 OpenAI 协议出口**：对外只暴露 `/v1/chat/completions`、`/v1/models` 等标准接口，流式（SSE）与非流式（聚合）均可。
- **多平台适配**：支持 CodeBuddy / WorkBuddy、Trae 国内版、TraeWork 桌面版、Qoder（CLI 桥接）。
- **自动凭据发现**：自动读取本机桌面端登录态（含 Trae 系加密凭据的本地解密），无需手动填 key。
- **到期自动换新**：TraeWork 在上游返回 401/403 时用本地 refreshToken 自动换取新 token 并重试。
- **动态模型列表**：启动时自动从平台拉取当前生效模型（CodeBuddy 读取本地缓存/清单/CLI，Qoder CLI，Trae 系只读本地 state.vscdb 服务端缓存），拉取失败自动回退内置基线，`/v1/models` 始终反映当前生效列表。
- **虚拟自动分组**：`auto-flash`、`auto-pro`、`auto-glm`、`auto` 等虚拟模型 ID 按通配规则展开为"跨平台 × 多账号"的有序候选链，一个 ID 背后是整个可用模型池。
- **自动故障转移与熔断**：上游 500/超时/断流自动切换下一个候选，账号级故障自动换号；节点级熔断（连续失败阈值 → 冷却期 → 半开探测）自动隔离坏节点，冷却期满放一个探测请求验证恢复。
- **多账号池**：自动扫描本机多账号登录态，叠加 config.json 手工录入（authFile / storageFile / PAT），账号粒度参与切换与熔断。
- **配置热更新**：config.json 变更即时生效（分组 / 熔断参数 / 账号），无需重启；文件监听与管理页写回双通道。
- **可视化管理页**：内置 `/admin` 单页（自包含 HTML，零构建 / 零 CDN 依赖），总览、分组、账号、熔断、配置全部可视化修改。
- **零第三方依赖**：仅使用 .NET BCL 与 ASP.NET Core 共享框架。

## 快速开始

> 先在本机安装并登录你需要的任一平台桌面端，网关会自动发现其登录态。

```bash
# 在仓库根目录运行
dotnet run --project ProxyHub
```

默认监听 `http://127.0.0.1:8265`，可视化管理页在 `http://127.0.0.1:8265/admin`。

## 客户端接入

以 Cherry Studio 为例：

| 配置项 | 值 |
| --- | --- |
| API 地址 | `http://127.0.0.1:8265/v1` |
| API Key | 不开启鉴权时任意值；开启后填代理密钥 |
| 模型 ID | `{平台}-{模型}`，见下方表 |

示例模型 ID（具体以下发的 `GET /v1/models` 清单为准）：

| 平台前缀 | 模型 ID 示例 | 说明 |
| --- | --- | --- |
| `codebuddy` | `codebuddy-glm-5.2`、`codebuddy-deepseek-v4-pro` | CodeBuddy / WorkBuddy |
| `traecn` | `traecn-glm-5.2`、`traecn-kimi-k2.6` | Trae 国内版 |
| `traework` | `traework-glm-5.2` | TraeWork 桌面版 |
| `qoder` | `qoder-qwen3.7-max`、`qoder-glm-5.2` | Qoder，需本机装有 qoderclicn CLI |
| （虚拟分组） | `auto-flash`、`auto-pro`、`auto-glm`、`auto` | 自动在全部平台的对应档位模型间切换，见下方"自动分组与故障转移" |

## 配置

优先级：**环境变量 > config.json > 默认值**（复写版按实际行为实现）。

### 环境变量

| 变量 | 默认值 | 说明 |
| --- | --- | --- |
| `PROXY_HUB_PORT` | `8265` | 监听端口 |
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
  "port": 8265,
  "proxyKey": "",
  "timeoutMs": 120000,
  "adapters": {
    "codebuddy": true,
    "traecn": true,
    "traework": true,
    "qoder": true
  },
  "groups": {
    "auto-flash": {
      "match": ["*-flash"],
      "prefer": ["codebuddy", "traecn", "traework", "qoder"],
      "description": "速度优先：全部 flash 档模型自动切换"
    },
    "auto-pro": {
      "match": ["*-pro", "*-max"],
      "prefer": ["codebuddy", "traecn", "traework", "qoder"],
      "description": "质量优先：pro / max 档模型自动切换"
    }
  },
  "circuitBreaker": {
    "failureThreshold": 2,
    "cooldownSeconds": 60,
    "halfOpenProbe": true
  },
  "accounts": {
    "qoder": [
      { "label": "备用PAT", "pat": "qpat-xxxxxxxx" }
    ]
  },
  "admin": { "enabled": true, "failoverHeader": true }
}
```

| 段 | 说明 | 热更新 |
| --- | --- | --- |
| `groups` | 虚拟分组：`match` 通配模式列表（`*` 任意串，大小写不敏感）、`prefer` 平台优先级、`description` 备注 | ✅ |
| `circuitBreaker` | 熔断参数：`failureThreshold` 连续失败阈值、`cooldownSeconds` Open 冷却秒数、`halfOpenProbe` 是否半开探测 | ✅ |
| `accounts` | 手工账号：按平台解释字段（codebuddy=`authFile`、trae 系=`storageFile`、qoder=`pat`），与自动发现合并，自动发现在前 | ✅ |
| `admin` | 管理页开关：`enabled` 控制 `/admin` 页面（数据端点不受限）、`failoverHeader` 响应头开关 | ✅ |
| `port` / `adapters` | 结构性配置，仅启动时读取 | ❌ |

> 热更新通道：直接改文件（`FileSystemWatcher` + 300ms 防抖自动重载）或经管理页写回，两者共用同一条重载链路。

## HTTP 接口

| 方法 | 路径 | 说明 | 鉴权 |
| --- | --- | --- | --- |
| GET | `/health` | 健康检查 | 否 |
| GET | `/v1/models` | 可用模型列表（OpenAI 格式，含虚拟分组） | 可选 |
| GET | `/v1/models/matrix` | 跨平台模型能力矩阵（按模型家族分组） | 可选 |
| POST | `/v1/chat/completions` | 对话（流式 / 非流式，自动故障转移） | 可选 |
| POST | `/v1/responses` | Responses API 兼容出口 | 可选 |
| GET | `/usage` | 各平台用量统计 | 可选 |
| GET | `/status` | 各适配器就绪状态（Fail-Fast 探测） | 可选 |
| GET | `/admin` | 可视化管理页（单文件 HTML，程序集嵌入资源交付） | 复用代理密钥 |
| GET | `/admin/api/overview` | 总览：适配器 / 账号 / 模型数 / 用量 / 熔断 | 复用代理密钥 |
| GET | `/admin/api/groups` | 分组列表查询（含候选链展开预览，热生效） | 复用代理密钥 |
| POST | `/admin/api/groups/preview` | 分组草稿纯内存展开预览（不写 config.json） | 复用代理密钥 |
| GET | `/admin/api/models/matrix` | 模型健康矩阵：家族 × 平台的可用账号数与熔断状态 | 复用代理密钥 |
| PUT / DELETE | `/admin/api/groups/{name}` | 分组新增修改 / 删除（热生效） | 复用代理密钥 |
| GET | `/admin/api/accounts` | 账号池 + 账号级熔断状态 | 复用代理密钥 |
| POST | `/admin/api/accounts/refresh` | 重新扫描本机账号 | 复用代理密钥 |
| GET | `/admin/api/breakers` | 熔断器快照（key / 状态 / 连续失败数） | 复用代理密钥 |
| POST | `/admin/api/breakers/reset` | 复位熔断（body 可选 `key` 复位单节点，缺省复位全部） | 复用代理密钥 |
| GET / PUT | `/admin/api/config` | 读取 / 合并写入配置（顶层键覆盖，热生效） | 复用代理密钥 |

### 调用示例

非流式（标准 OpenAI 语义）：

```bash
curl -X POST http://127.0.0.1:8265/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "traework-glm-5.2",
    "messages": [{"role": "user", "content": "你好"}]
  }'
```

流式（SSE 事件流，末尾统一 `data: [DONE]`）：

```bash
curl -N -X POST http://127.0.0.1:8265/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "codebuddy-glm-5.2",
    "stream": true,
    "messages": [{"role": "user", "content": "从 1 数到 3"}]
  }'
```

开启鉴权后，携带密钥：

```bash
curl -X POST http://127.0.0.1:8265/v1/chat/completions \
  -H "Authorization: Bearer your-secret" \
  -H "Content-Type: application/json" \
  -d '{"model":"traecn-glm-5.2","messages":[{"role":"user","content":"hi"}]}'
```

## 自动分组与故障转移

### 虚拟分组

分组是叠加在真实模型之上的虚拟模型 ID（`/v1/models` 中 `owned_by=proxyhub`），请求 `"model": "auto-flash"` 时按以下顺序展开候选链：

```
账号轮次（先跨平台把各平台第 1 账号全部试完，全部失败后才进入第 2 轮备用账号）
  → 轮内按 prefer 平台序（未列出的平台按注册顺序殿后，Qoder 注册最后、每轮殿后）
    → 同平台同账号内匹配模型按名字典序（稳定、可预测）
```

- `match` 通配作用于各平台**当前生效模型表**（动态拉取优先），平台新增模型自动被分组收录，无需改配置。
- 分组支持任意多个，`auto`（全部模型兜底）与 `auto-glm`（某家族全版本）这类写法均可。

### 熔断与切换

候选链节点 = 平台 × 账号 × 上游模型，熔断键为三元组 `平台|账号|模型`：

| 场景 | 行为 |
| --- | --- |
| 上游 5xx / 超时 / 断流 / 401/403 换新后仍失败 | 记一次失败，切下一候选（换模型或换号） |
| 上游 4xx（非 401/403） | 视为请求本身问题，直接透传给客户端，不消耗切换 |
| 连续失败达 `failureThreshold` | 节点 Open：冷却 `cooldownSeconds` 内的请求直接跳过该节点 |
| 冷却期满 | 放行一个半开探测请求：成功回 Closed，失败重新计时 |
| 流式请求 | 首个内容块**写出前**可自由换源；已提交后只能补错误块 + `[DONE]` 收尾（保证客户端协议完整） |
| 全链失败 | 返回 502，错误信息附全部已尝试节点 |

账号级故障（凭据失效、账号额度耗尽）通过"同模型多账号节点"自然换号，无需专门逻辑。

### 多账号管理

账号来源两路合并，`/admin/api/accounts` 可查看全量与各账号的熔断状态：

| 平台 | 自动发现 | 手工录入字段 |
| --- | --- | --- |
| codebuddy | `%LOCALAPPDATA%\CodeBuddyExtension\Data\Public\auth\*.info`，每个有效文件一个账号（workbuddy 优先） | `authFile` |
| traecn | `%APPDATA%\Trae CN\User\globalStorage\storage.json` + `Trae CN*` 同家族 profile 目录 | `storageFile` |
| traework | `%APPDATA%\TRAE SOLO CN\…\storage.json` + 同家族 profile 目录 | `storageFile` |
| qoder | `~/.qoderworkcn/.auth-cn/user`（CLI 登录落盘）+ `QODERCN_PERSONAL_ACCESS_TOKEN` | `pat` |

- 手工账号在 config.json `accounts` 段录入，可配多把（如多个 PAT），参与切换与熔断。
- `POST /admin/api/accounts/refresh` 随时重新扫描本机（新登录了桌面端后无需重启）。

> **风控与安全提示**：同平台多账号高频并发可能触发平台风控，默认每个适配器仍只用第一个健康账号，仅在熔断/切换时动用后备账号；负载均衡模式启用前请自行评估。

## 可视化管理页

浏览器打开 `http://127.0.0.1:8265/admin`（鉴权复用代理密钥：`Authorization: Bearer …` 或 `x-api-key …`）：

- **总览**：适配器就绪状态 / 账号数 / 模型数 / 今日用量 / 熔断快照与趋势图
- **模型矩阵**：跨平台模型家族能力矩阵，附行级可用账号数与熔断状态（Open / Half-Open）统计（`/admin/api/models/matrix`）
- **分组管理**：支持虚拟分组 CRUD 与编辑弹窗内草稿实时预览（约 200ms 防抖提交 `/admin/api/groups/preview`，纯内存展开不落盘，校验错误内联展示）
- **账号 / 熔断**：账号池查看、表单化手工添加账号（CodeBuddy=`authFile`、Trae 系=`storageFile`、Qoder=`pat`）、重新扫描本机账号、节点级/全量熔断复位
- **配置**：读取与顶层键合并写回 config.json（热生效），支持结构化 groups / accounts 编辑；原始 JSON 仅作诊断输出，PAT 自动脱敏（`******`）且不回填输入框

页面以嵌入资源形式随程序集交付（`EmbeddedResource`，LogicalName `ProxyHub.wwwroot.admin.html`；自包含单文件 HTML，无构建、无 CDN 依赖），运行时不再依赖磁盘 `wwwroot` 目录，`admin.enabled=false` 可整体关闭页面访问（数据端点不受影响）。

> **已交付的 P3 能力**：
> - **嵌入式单页交付**：`admin.html` 打包进程序集清单资源，发布产物无需磁盘 `wwwroot` 目录即可访问 `/admin`。
> - **模型健康矩阵**：`GET /admin/api/models/matrix` 提供家族 / 平台维度的可用账号数与熔断（Open / Half-Open）状态统计。
> - **分组草稿预览**：`POST /admin/api/groups/preview` 在内存中展开未保存的 match / prefer 草稿，所见即所得后再保存。
> - **结构化配置与 PAT 脱敏**：分组 / 账号结构化表单编辑，PAT 仅用于保存，展示时自动脱敏且不回填输入框。

## 多人使用与负载均衡（方案）

当前为单机单用户设计。多用户场景的演进方案见 `docs/设计方案-v2.md`（请求级加权轮询、账号池并发上限、按用户的配额与审计），代码暂未实现。

## 架构

```
HTTP 入口（AppFactory：鉴权 · 路由 · 错误语义 · SSE 出口）
   ├─ ModelGroups（虚拟分组展开：通配匹配 × 平台优先级 × 账号序）
   └─ FailoverExecutor（候选链遍历 · 首块提交前可换源 · 成败记入熔断）
        └─ CircuitBreaker（节点级熔断：Closed → Open → Half-Open 状态机）
   └─ Registry（模型前缀路由 + 动态/静态两级模型表 + 能力矩阵）
   └─ AccountRegistry（账号池：自动发现 + 手工录入合并）
        └─ Adapter（auth / models / chat / 账号发现 四职责）
             ├─ CodeBuddy —— HTTPS，CLI 身份伪装头，auth 目录多账号
             ├─ TraeCn   —— HTTPS，本地加密凭据解密 + IDE 伪装头 + 端点故障转移
             ├─ TraeWork —— HTTPS，同 TraeCn，另带 401/403 自动换新重试
             └─ Qoder    —— spawn 本机 CLI 子进程，stream-json 桥接
        └─ CredentialsCache（TTL + in-flight 去重的凭据缓存）
        └─ Sse（统一出口：流式直通 / 非流式聚合 + 首块缓冲）
        └─ UsageTracker（按 平台/模型/日期 统计）
   └─ 热更新链：config.json 变更 / 管理页写回 → ConfigStore → RuntimeConfig.Apply
        → 分组 / 熔断参数 / 账号 即时重建（端口等结构性配置仅启动生效）
```

设计要点：

- **网关内部恒走流式**：请求体一律改为 `stream=true` 与上游通信，出口再按客户端需求直通或聚合，避免两套上游处理路径。
- **协议翻译型反代**：客户端只讲 OpenAI 协议，各平台私有协议全部在适配器层吸收（请求改写、头伪装、SSE 事件归一化）。
- **真流式**：上游响应按行增量读取，首个 token 即时下发；故障转移发生在首块提交前时对客户端完全透明。
- **配置单一事实来源**：一切可配置项（分组 / 熔断 / 账号 / 管理开关）都落盘 config.json，热更新链路广播生效。
- **新增平台只需实现 `IAdapter`** 并在启动处注册即可，自动获得分组、切换、熔断、管理页能力。

## 测试

```bash
dotnet test
```

测试覆盖：模型路由与能力矩阵、SSE 直通与聚合、token 估算、凭据缓存（TTL / 过期 / 并发去重）、用量统计、配置加载于环境变量覆盖、各适配器协议归一化、本地加密凭据的往返与防篡改、动态模型拉取与静态回退、**虚拟分组展开与通配匹配、熔断状态机（阈值 / 冷却 / 半开 / 参数热更）、故障转移（首块前换源 / 提交后收尾 / 非重试透传 / 熔断跳过）、配置落盘与热更新传播、管理 API 读写闭环（分组增删改 / 账号 / 熔断复位 / 配置合并写回）**、端到端 HTTP 路由 / 鉴权 / 错误码 / 用量 / 分组切换闭环。

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
- 各平台现状：
  - **CodeBuddy**：已接入多层真实本地动态源，按优先级依次读取本地服务端下发缓存（`~/.codebuddy/local_storage/entry_*.info`）、安装配置清单（`product.json` / `product.internal.json`）及 CLI 探测（`codebuddy --help`）。
  - **Qoder**：已接入 CLI 真实动态源（`qoderclicn --list-models`），宽容解析 JSON 或表格输出。
  - **TraeCN 与 TraeWork**：只读各自 `StorageFile` 同目录 `state.vscdb`（VSCode fork 的 SQLite 键值缓存库）的 `ItemTable` 单表，查询以 `AI.agent.modeListMap` 与 `AI.agent.model.model_list_map` 结尾的两个服务端模型缓存键，解析服务端下发 JSON 提取聊天模型列表。
    - **零网络、零凭据**：纯本地只读解析，不联网发起上游请求，不读取或解密任何用户凭据。
    - **零第三方依赖**：通过 Windows 内置 `winsqlite3.dll` P/Invoke 只读打开（带 300ms busy timeout），共享模式容忍桌面端占用，零 NuGet 依赖。
    - **安全回退**：缓存缺失、被锁、损坏或格式变动时返回 `null` 并自动回退静态基线。
    - **多账号限制**：多账号多 profile 场景下，当前动态模型发现仅读取默认 profile（即默认 `storage.json` 所在目录）的 `state.vscdb`。

## 错误码语义

| 状态码 | 触发条件 | 说明 |
| --- | --- | --- |
| 400 | 请求体非合法 JSON / 管理端点非法参数 | `Invalid JSON body` / `invalid group name` 等 |
| 401 | 代理密钥缺失或错误 | `Unauthorized: missing or bad proxy key` |
| 404 | 模型 ID 未注册 / 管理端点资源不存在 | 附全部可用模型 ID 或 `group not found` |
| 4xx | 上游返回不可重试错误（非 401/403） | 原样透传上游状态码与信息，不消耗切换 |
| 502 | 候选链全部失败（含熔断全开） | `Failover exhausted …` 附全部已尝试节点 |

## 目录结构

```
ProxyHub/
  Program.cs                 进程入口：加载配置、组装适配器、启动热更新与账号扫描
  AppFactory.cs              全部路由 + 鉴权 + SSE 出口 + 候选链构建（依赖可注入）
  AdminApi.cs                管理 API（/admin 页面与 /admin/api/* 数据端点）
  Registry.cs                模型前缀路由 + 动态/静态两级模型表 + 能力矩阵
  ModelGroups.cs             虚拟分组：通配匹配 + 平台优先级 + 候选链展开
  FailoverExecutor.cs        故障转移执行器：候选链遍历 + 首块缓冲 + 熔断联动
  CircuitBreaker.cs          节点级熔断状态机（Closed → Open → Half-Open）
  AccountRegistry.cs         账号池：自动发现 + 手工录入合并
  AdapterAccount.cs          账号模型（平台 / 账号 ID / 凭据来源）
  RuntimeConfig.cs           运行时配置持有者 + 变更广播 + API 鉴权
  ConfigStore.cs             config.json 存取（原子写 + 热重载）+ 文件监听
  ProxyHubConfig.cs          配置 v2 schema（分组 / 熔断 / 账号 / 管理段）
  Sse.cs                     SSE 写出 + 非流式聚合 + token 估算
  UpstreamException.cs       上游错误分类（可重试 / 不可重试）
  CredentialsCache.cs        凭据缓存（TTL + in-flight 去重）
  UsageTracker.cs            用量统计（线程安全）
  TcCrypto.cs                本地加密凭据解密（AES-128-CBC + SHA-512 完整性校验）
  UpstreamHttp.cs            共享传输层：连接池 + 增量 SSE 行解析
  IAdapter.cs                适配器契约
  wwwroot/admin.html         可视化管理页（自包含单文件，以嵌入资源随程序集交付）
  Adapters/
    CodeBuddyAdapter.cs
    TraeAdapterBase.cs       Trae 系公共：凭据、伪装头、端点转移、SSE 归一化、多账号扫描
    TraeCnAdapter.cs
    TraeWorkAdapter.cs
    QoderAdapter.cs
  docs/设计方案-v2.md        多用户负载均衡演进方案
ProxyHub.Tests/              全部测试
```

## 已知限制

- 流式路径中上游中途报错会被吞为规范的错误块 + `[DONE]` 收尾（为保持客户端协议完整性；首块提交前的失败则完全透明换源）。
- Trae 系上游不返回 token 用量，网关按字符数 ÷4 估算，跨平台数字不能精确横向比较。
- 用量统计纯内存，重启清零。
- 熔断状态纯内存，重启全部回到 Closed。
- Qoder 依赖本机安装 qoderclicn CLI。
- 各平台采用订阅/积分计费，无公开单 token 单价，`/v1/models/matrix` 仅提供能力对比。
- 多用户负载均衡（请求级轮询、并发上限、按用户配额）仅为方案，未实现，见 `docs/设计方案-v2.md`。
- `/admin` 管理页由程序集嵌入资源交付（源码 `wwwroot/admin.html` 仅作为嵌入资源来源），运行时无磁盘 `wwwroot` 依赖。
- TraeCN / TraeWork 动态模型发现当前只读取默认 profile 目录下的 `state.vscdb`，多账号 profile 场景下非默认 profile 的模型缓存暂不参与动态拉取。