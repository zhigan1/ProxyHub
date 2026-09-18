# ProxyHub 使用指南

一个将本机多个 AI 编程平台的登录凭据与额度统一封装成**单套标准 OpenAI 兼容服务**的高性能反向代理网关（.NET 10 / ASP.NET Core）。

任意 OpenAI 客户端（如 **Cherry Studio**、**Cursor**、**Cline**、**OpenRouter**、**NextChat**、**Chatbox** 等）只需把 `base_url` 指向本网关，即可无缝调用各平台模型，享受**多账号轮次调度、跨平台故障转移、自动签到与额度监控**能力。

---

## ✨ 核心特性

- 🌐 **标准 OpenAI 协议出口**：对外暴露标准的 `/v1/chat/completions`、`/v1/models`、`/v1/responses` 接口，完美支持流式（SSE）与非流式调用。
- 🔌 **主流多平台适配**：开箱即用支持 **CodeBuddy (腾讯云代码助手 / WorkBuddy)**、**Trae 国内标准版 (TraeCN)**、**TraeWork (TRAE SOLO CN)**、**Qoder (阿里通义灵码 CLI 桥接)**。
- 🔑 **无感凭据自动发现**：自动读取本机桌面端登录态（含 Trae 系本地 AES-128-CBC 加密凭据解密），无需手动填写 API Key。
- 🔄 **智能跨平台故障转移（Failover）**：
  - **特化编排分组**：请求 `deepseek-v4.1-flash` 时自动智能命中跨平台候选链；
  - **多适配器同名聚合**：自动将所有支持同名模型的平台穿插编织成多级防线，首选平台熔断毫秒级无缝漂移到下一平台同名模型，客户端无感知断流。
- 👥 **可视化多账号管理**：
  - **真实用户 ID**：告别乱码备份文件名，直观展示用户手机号 / UID；
  - **顺位调度**：展示 `#1`, `#2` 顺序标记，支持 `↑` / `↓` 按钮微调调用优先级；
  - **状态控制**：一键「启用 / 禁用」账号（禁用立即移出调度链），支持「删除」黑名单；
  - **积分额度监控与修改**：实时显示账号可用积分，支持直接点击修改自定义额度。
- 🎁 **全平台自动签到与额度轮询**：
  - 借鉴开源项目 [cockpit-tools](https://github.com/jlcodes99/cockpit-tools) 理念，打通腾讯云代码助手与字节跳动 Trae 官方云端签到查额接口；
  - 网关启动与后台定时（默认 12 小时）自动完成全账号签到，同步刷新可用额度。
- ⚡ **节点级自愈熔断**：`Closed` $\to$ `Open` $\to$ `Half-Open` 状态机，自动隔离异常节点与失效账号，冷却期满放行单个请求探测恢复。
- 🎛️ **自包含 Web 管理控制台**：内置 `/admin` 控制台（以嵌入资源打包，零第三方依赖），提供健康矩阵、用量趋势、分组编排、账号管理与整齐对齐的配置面板。

---

## 🚀 快速开始

### 1. 启动网关服务

先在本机安装并登录您需要的任一平台桌面端（例如 CodeBuddy、Trae 等），网关会自动探测其登录态：

```powershell
# 方式 A：直接运行发布产物 (推荐)
cd ProxyHub\publish-8267
.\start.bat

# 方式 B：从源码运行
dotnet run --project ProxyHub
```

启动后控制台输出：
```text
ProxyHub (.NET) listening on http://127.0.0.1:8267
Admin UI:               http://127.0.0.1:8267/admin
```

- **Web 管理控制台**：[http://127.0.0.1:8267/admin](http://127.0.0.1:8267/admin)
- **API 代理地址**：`http://127.0.0.1:8267/v1/chat/completions`

---

## 📱 客户端接入指南

以 **Cherry Studio**、**Cursor** 或 **Cline** 为例：

| 配置项 | 填写值 | 说明 |
|---|---|---|
| **API 地址 (Base URL)** | `http://127.0.0.1:8267/v1` | 本地网关地址 |
| **API Key** | `sk-proxyhub`（未设代理密钥时填任意字符） | 网关鉴权密钥 |
| **模型名称 (Model)** | 详见下方模型清单 | 支持具体模型名、平台前缀名或虚拟分组名 |

### 可用模型推荐

1. **直接调用同名模型（推荐，享跨平台全自动故障转移）**：
   - `deepseek-v4.1-flash`
   - `deepseek-v4-pro`
   - `glm-5.3-flash`
   - `kimi-k2.6`
   *(网关会自动在 CodeBuddy $\to$ TraeWork $\to$ TraeCN 间按多账号顺序自动切换)*

2. **虚拟智能分组（按能力档位自动切换）**：
   - `auto-flash`：速度优先，全部平台的 flash 档位模型自动轮询与切换；
   - `auto-pro`：质量优先，pro / max 档位模型自动切换；
   - `auto-glm`：GLM 系列模型自动切换；
   - `auto`：全模型池智能兜底。

3. **指定特定平台调用**：
   - `codebuddy-deepseek-v4.1-flash`
   - `traecn-glm-5.2`
   - `traework-deepseek-v4.1-flash`
   - `qoder-qwen3.7-max`

---

## ⚙️ 配置文件 (config.json)

配置文件位于 `publish-8267/config.json`（支持管理控制台热更新或直接修改保存）：

```json
{
  "port": 8267,
  "proxyKey": "",
  "timeoutMs": 120000,
  "adapters": {
    "codebuddy": true,
    "traecn": true,
    "traework": true,
    "qoder": true
  },
  "groups": {
    "deepseek-v4.1-flash-auto": {
      "match": ["*deepseek*v4.1*flash*", "deepseek-v4.1-flash"],
      "prefer": ["codebuddy", "traecn", "traework", "qoder"],
      "description": "deepseek-v4.1-flash 全平台自动切换（所有账号轮次制）"
    },
    "auto-flash": {
      "match": ["*-flash"],
      "prefer": ["codebuddy", "traecn", "traework", "qoder"],
      "description": "速度优先：全部 flash 档模型自动切换"
    },
    "auto": {
      "match": ["*"],
      "prefer": ["codebuddy", "traecn", "traework", "qoder"],
      "description": "全部模型兜底"
    }
  },
  "circuitBreaker": {
    "failureThreshold": 2,
    "cooldownSeconds": 60,
    "halfOpenProbe": true
  },
  "admin": {
    "enabled": true,
    "failoverHeader": true,
    "autoSignin": true,
    "autoSigninIntervalHours": 12
  }
}
```

---

## 🖥️ Web 管理控制台功能

访问 `http://127.0.0.1:8267/admin`：

1. **总览面板 (Overview)**：
   - 实时监控各适配器就绪状态、今日请求数、Token 消耗统计及每日用量曲线。
2. **模型矩阵 (Models Matrix)**：
   - 查看所有平台的模型家族（GLM、DeepSeek、Kimi、Minimax 等）可用账号与健康状态。
3. **分组管理 (Groups)**：
   - 自由创建和编辑虚拟模型分组，支持通配符模式匹配（`match`）与平台调用优先级（`prefer`）。
4. **账号 / 熔断 (Accounts)**：
   - 展现账号顺位（`#1`, `#2`）、真实用户手机号/UID、积分额度；
   - 支持 `↑` / `↓` 顺位调整、一键「启用/禁用」与「删除」；
   - 点击积分数值可直接手动设定额度；实时查看与复位熔断节点。
5. **配置面板 (Settings)**：
   - 整齐卡片对齐排版的适配器开关，在线修改上游超时与熔断参数，保存即刻热更新生效。
6. **签到中心 (Signin)**：
   - 查看各平台账号的今日签到状态、连签天数与最新发放额度，支持一键「全部签到」。

---

## 🛠️ 开发者文档

关于底层架构分层、模块设计、故障转移流转图与适配器开发教程，请参阅：
👉 **[DEVELOPMENT.md](DEVELOPMENT.md)**