# Codex Control Agent 桌面启动、配置与配对体验设计

状态：**用户已确认，三个实施阶段已落地；现场边界仍需独立验收**

版本：0.1

日期：2026-08-23（Asia/Shanghai）

## 1. 结论

本设计把当前以命令行参数、控制台输出和手工配对为主的 `CodexControlAgent.exe`，重构为一个低打扰的 Windows 常驻程序：

- 唯一 Windows 运行端点仍为 `CodexControlAgent.exe`；
- 同一 EXE 内加入 WinForms 托盘和设置窗口，不新增插件、DLL Host 或第二个桌面进程；
- 首次运行通过向导完成 Relay 和电脑名称配置，之后静默随用户登录启动；
- 设置、DPAPI 身份、日志和 Codex Runtime 缓存统一存入安装目录的 `data\`；
- Agent 自动启动并持有 `codex app-server`，设置页可一键打开真实 `codex --remote` 终端；
- 配对升级为协议 v2：二维码、六位码备用、手机摘要确认、电脑二次确认和两档权限；
- Relay、Agent、PWA 协调升级，不保留旧版“Claim 成功即授予全权限”的入口；
- 实现按桌面宿主、配对协议、安装发布三个阶段验收；
- 自动化证据、真实 Codex、真实浏览器、真实 Windows 安装和真实手机扫码证据分开陈述。

本文件是实现的产品与技术约束。用户已于 2026-08-23 明确确认开发；实现结果仍必须以源码、Migration、测试和 Release 证据为准。

## 2. 设计范围

### 2.1 目标

1. 普通用户安装后只需填写 Relay 根地址和电脑名称。
2. 首次配置成功后默认开启随当前 Windows 用户登录启动。
3. 正常开机静默进入托盘，不显示控制台、主窗口或假进度启动页。
4. 用户随时能从托盘查看真实初始化阶段、连接状态和故障原因。
5. Relay 临时不可达、Codex 运行时发现失败或配置损坏时，Agent 仍保留可操作的修复入口。
6. 消除手工复制 `codex --remote` 命令、手工配置手机 WebSocket 地址和从控制台抄配对码的步骤。
7. 配对必须由电脑本机明确授权，不能因扫描到二维码就直接取得控制权。
8. 保留现有 Headless、诊断和自动化入口，不破坏独立 Agent 的运行模型。

### 2.2 不在范围内

- 不附加、抓取或操控 Codex Desktop 窗口；
- 不使用 OCR、鼠标键盘模拟、私有 Hook 或 Desktop attach；
- 不读取、上传、保存或记录 OpenAI API Key、私有 Endpoint 凭据或 Codex 原始认证数据；
- 不把 Relay、nginx、PWA 或 Relay 数据库嵌入 Windows Agent；
- 不从设置页部署或管理远端 Relay；
- 不增加账号体系、云端设备发现或应用层 E2EE；
- 不重新设计项目和历史会话的归组逻辑；
- 不实现自动更新；
- 不升级 Agent 或 Relay 到 .NET 9/10；
- 不增加 Windows ARM64 或 Windows Server 验收范围。

### 2.3 必须保持的现有约束

- Agent 只通过公开 `codex app-server` JSON-RPC 集成 Codex；
- 本地 Codex WebSocket 只绑定 `127.0.0.1`；
- 生产 Relay 只使用 HTTPS/WSS，非 TLS 仅限 loopback 开发；
- Device 私钥继续使用 Current User DPAPI；
- Controller 私钥继续使用不可导出的 IndexedDB `CryptoKey`；
- 远程创建/恢复仍强制 `approvalPolicy=untrusted` 和 `workspace-write`；
- Approval 仍为显式 first-valid-response-wins，绝不自动批准；
- Relay 和 PWA 不解析或持久化原始 app-server Thread/JSON-RPC；
- Relay 不持久化完整 Prompt、回复、Shell Output、Diff 或源码；
- 协议消息类型仍以 `src/shared/CodexControl.Protocol/RelayMessageTypes.cs` 为唯一来源；
- 数据库模型变化必须提交 EF Core Migration，不得使用 `EnsureCreated`。

## 3. 当前实现与问题证据

| 当前行为 | 源码证据 | 用户影响 |
|---|---|---|
| Agent 配置只来自环境变量和命令行参数 | `src/agent/Configuration/AgentOptions.cs` 的 `Parse` | 普通用户必须理解路径、端口、Relay URL 和多个开关 |
| 启动入口顺序创建 Runtime、Bridge、Proxy 和 RelayClient | `src/agent/AgentProgram.cs` 的 `RunAsync` | 没有可查询的桌面生命周期 Interface，错误只能落到控制台或日志 |
| 配对码和 `codex --remote` 命令打印到控制台 | `src/agent/AgentProgram.cs` | 每次启动和本机终端连接都依赖复制命令 |
| Device 身份写入 `dataDirectory/device-identity.json` | `src/agent/Security/DeviceIdentity.cs` | DPAPI 安全已存在，但数据位置和设置体验未统一 |
| Pairing Claim 校验成功后直接写入四项全权限 | `src/relay/Pairing/PairingService.cs` 的 `ClaimLockedAsync` | 扫码或输入码后没有电脑端二次确认 |
| PWA 配对页要求运行 `--pair` 并手工输入六位码 | `src/web/src/App.tsx` 的 `PairingPanel` | 手机端仍依赖控制台操作 |
| PWA Relay URL 以完整 WebSocket 地址保存在 LocalStorage | `src/web/src/storage.ts` | 用户必须理解 `/ws/controller` 和 `ws/wss` 差异 |
| Agent 当前为 `net8.0` Console EXE、框架依赖单文件 | `src/agent/CodexControl.Agent.csproj` | 双击会暴露控制台，且不存在托盘与设置外壳 |
| 发布只输出便携 Agent 目录 | `tools/Build-Release.ps1` | 没有安装位置、开始菜单、Runtime 检测和卸载生命周期 |

这些是 v1 当前事实，不代表下文目标方案已经实现。

## 4. 体验原则

“参考微信输入法”只指低打扰常驻工具的信息架构和交互节奏，不复制微信的品牌、图标、文案或像素布局。

设计原则：

1. **静默常驻**：正常启动不抢焦点，状态通过托盘和用户主动打开的窗口查看。
2. **先给结论**：状态页先显示“可以使用 / Relay 离线 / Codex 不可用”，再提供技术细节。
3. **真实阶段**：只展示实际发生的 Runtime 发现、能力探测、app-server 启动、本地代理监听、Relay 同步等阶段，不使用假百分比。
4. **日常与高级分离**：普通用户看不到消息大小、重连退避和数据结构等实现参数。
5. **危险操作显式**：配对授权、强制退出、立即重启、重置身份和删除数据都必须二次确认。
6. **不伪造成功**：配置未落盘、Relay 未确认、撤销仍待同步时，UI 不得显示“已完成”。
7. **故障可恢复**：网络、Codex 或配置故障不应让设置入口消失。

视觉方向：

- 左侧分区导航，右侧内容页；
- 浅色留白和中性灰分组卡片，支持跟随 Windows 深浅色；
- 绿色只表示已就绪/已连接，黄色表示等待/降级，红色表示需要处理；
- 使用 Windows 系统字体和原生无障碍语义；
- 不使用大面积渐变、营销 Banner 或持续动画；
- 窗口关闭按钮只隐藏到托盘，退出必须从托盘菜单或设置页显式执行。

## 5. 目标拓扑

```text
Windows 登录
   │
   ▼
CodexControlAgent.exe (.NET 8 / WinForms / 单实例)
   ├─ Desktop Shell Adapter
   │    ├─ 托盘
   │    ├─ 设置窗口
   │    ├─ 系统通知
   │    └─ 单实例激活
   │
   ├─ Settings Module ──> <InstallRoot>\data\
   │    ├─ settings.json / last-good
   │    ├─ device-identity.json (DPAPI)
   │    ├─ agent-state.json
   │    ├─ logs\
   │    └─ codex-runtimes\
   │
   ├─ Runtime Coordinator Module
   │    ├─ CodexRuntimeResolver / CodexExecutableProbe
   │    ├─ AppServerBridge
   │    ├─ LocalCodexProxyServer (127.0.0.1, 自动端口)
   │    ├─ CodexStateManager / RemoteControlDispatcher
   │    └─ RelayClient
   │
   └─ Pairing Coordinator Module
        ├─ 二维码/六位码
        ├─ 本机确认
        ├─ 权限/重命名/撤销
        └─ 离线撤销队列

Relay (协议 v2 / EF Core SQLite)
   ▲                     ▲
   │ WSS device          │ WSS controller
   │                     │
Agent                 React/Vite PWA
                         ├─ Relay 摘要确认
                         ├─ Controller 名称
                         ├─ IndexedDB 身份
                         └─ 记住上次电脑
```

WinForms 是桌面 Shell 的 Adapter，不承载 Runtime、配对或配置规则。任何按钮都只调用深模块的 Interface，并根据返回结果或 Snapshot 更新 UI。

## 6. 用户流程

### 6.1 安装

1. 用户运行 Inno Setup 安装器。
2. 安装器默认安装到当前用户可写目录，例如：

   ```text
   %LOCALAPPDATA%\Programs\CodexControl\
   ```

3. 检测 x64 `.NET 8 Desktop Runtime` 和 `.NET 8 ASP.NET Core Runtime`。
4. 缺失时调用微软官方安装器；只有安装 Runtime 的过程允许出现 UAC。
5. 安装 Codex Control 本体、开始菜单和卸载入口，不在尚未配置时直接注册开机启动。
6. 首次启动 Agent，创建可写的 `data\`，进入首次向导。

安装器本体按当前用户模式运行，不要求管理员权限。Runtime 安装失败、取消或离线时，安装器必须明确指出缺失项和官方下载地址，不把 Agent 启动失败留给用户猜测。

### 6.2 首次启动

```text
加载/创建 data\
  -> 校验可写性
  -> 加载 settings.json
  -> 无配置：显示首次向导
      -> 输入 Relay 根地址
      -> 输入电脑名称
      -> 保存并测试 Relay
      -> 后台自动发现并探测 Codex
      -> 配置成功后默认启用开机启动
      -> Codex + Relay 就绪后自动生成一次配对二维码
```

首次向导只要求：

- Relay 根地址，例如 `https://control.example.com`；
- 电脑名称，默认使用 Windows 机器名，可编辑。

不会要求普通用户填写：

- `wss://.../ws/device`；
- Codex 可执行文件路径；
- 本地代理端口；
- 数据目录或日志目录；
- 单条消息大小；
- 重连策略。

如果 Relay 当前不可达，用户可以明确选择“离线保存”；Agent 保存地址、进入离线常驻并后台重连。若 Relay 可达但协议不兼容，则可以保存地址，但阻止连接和配对，并显示双方协议版本。

如果 Codex 自动发现或能力探测失败，Agent 进入降级常驻：

- 设置窗口保持可用；
- 显示真实失败步骤和错误码；
- 提供“重新检测”和“选择 codex.exe”；
- Relay 配置仍可保存；
- 修复前禁止生成配对码和执行远程任务；
- 不直接退出进程。

### 6.3 日常启动

```text
Windows 登录
  -> Agent 静默进入托盘
  -> 立即发布真实 Starting Snapshot
  -> 并行显示已知配置状态
  -> 顺序启动 Codex Bridge 和 loopback Proxy
  -> Relay 连接、同步待撤销项、声明 Ready
  -> 托盘显示“可以使用”
```

正常启动不显示窗口或启动动画。只有以下情况发出一次系统通知：

- 配置损坏且需要选择恢复方式；
- `data\` 不可写；
- Codex 持续不可用；
- Relay 从长时间离线恢复；
- 收到待电脑确认的配对申请。

网络重连失败不得连续弹窗或重复通知。

### 6.4 打开本机 Codex 终端

运行状态页提供“打开 Codex 终端”：

1. 只在 Bridge 与 loopback Proxy Ready 时启用；
2. 自动使用实际动态端口执行当前 Codex Runtime 的 `codex --remote ws://127.0.0.1:<port>/`；
3. 终端由用户明确点击后可见启动，开机时不自动弹出；
4. 本地 TUI 已连接时，按钮显示“终端已连接”并禁止重复创建；
5. 继续保持当前单 TUI 限制。

### 6.5 暂停远程访问

“暂停远程访问”具有单一、清楚的语义：

- 立即取消活动配对码和待确认请求；
- 断开 Relay；
- 手机端看到电脑离线，不能查看或控制；
- 本机 app-server、Proxy 和 TUI 不停止；
- 暂停状态持久化，重启或重新登录后不能自动恢复；
- 只有用户显式点击“恢复远程访问”才重新连接 Relay。

### 6.6 退出和重启

无活动 Turn 时：

- “重启内核”只重启 Bridge、Proxy 和 RelayClient，不退出托盘 Shell；
- “退出”有界停止 Relay、Proxy、Bridge 后退出。

有活动 Turn 时：

- 需要重启内核的配置保存为“等待应用”；
- 默认在 Turn 终态后自动重启；
- “立即重启”必须再次确认会中断任务；
- “退出”默认取消；
- 可选择“任务完成后退出”或“强制退出并中断任务”。

## 7. 设置窗口信息架构

```text
┌──────────────────────────────────────────────────────────┐
│ Codex Control                                      —  □  ×│
├────────────────┬─────────────────────────────────────────┤
│ 运行状态       │  页面标题                               │
│ 连接与配对     │                                         │
│ 通用设置       │  状态卡 / 设置组 / 明确操作             │
│ 高级与诊断     │                                         │
│                │                                         │
│ v0.x.x         │                                         │
└────────────────┴─────────────────────────────────────────┘
```

### 7.1 运行状态

首屏回答三个问题：

1. Agent 是否可以使用；
2. Codex 内核是否 Ready；
3. Relay 是否 Online。

展示内容：

- 总状态：可以使用 / 正在启动 / 本机可用但远程离线 / 需要处理；
- Codex Runtime 来源与版本；
- app-server 状态；
- 本地代理实际端口和 TUI 连接状态；
- Relay 域名、连接状态和协议版本；
- 当前活动 Thread/Turn 摘要，不显示完整 Prompt 或输出；
- 待应用重启提示；
- 最近一个可操作错误和“查看详情”。

主要操作：

- 打开 Codex 终端；
- 暂停/恢复远程访问；
- 重启内核；
- 打开日志目录。

### 7.2 连接与配对

Relay 设置：

- 单一 Relay 根地址；
- “测试并应用”；
- 当前健康、协议、TLS 和最后连接时间；
- 离线保存需要二次确认；
- 生产只接受 HTTPS；
- HTTP 只在高级开发模式且目标为 loopback 时允许。

配对区域：

- “添加控制端”；
- 本地生成的二维码；
- 格式化六位码；
- 180 秒倒计时；
- 申请控制端名称、平台和短指纹；
- 电脑端 60 秒确认倒计时；
- “完整控制 / 仅查看”两档权限；
- 明确“允许”和“拒绝”。

已配对控制端列表：

- 控制端名称和本机别名；
- Controller ID 的缩略显示；
- 最近连接时间；
- 完整控制 / 仅查看；
- 在线、离线或待撤销同步；
- 重命名、修改权限和撤销。

### 7.3 通用设置

- 电脑名称；
- 随当前用户登录启动，首次配置成功后默认开启；
- 外观：跟随系统（保留未来扩展，不在首版提供皮肤系统）；
- 关闭窗口时隐藏到托盘；
- 版本和构建信息。

电脑名称修改后立即持久化，并更新 Device 元数据，不旋转 Device ID 或私钥。

开机启动修改后立即调用 Startup Adapter；如果注册失败，设置必须回滚并显示错误，不能只改变 UI 开关。

### 7.4 高级与诊断

允许修改：

- Codex 路径：自动发现 / 手动路径；
- 本地端口：自动 / 固定端口。

只读显示并提供“打开”：

- 安装目录；
- `data\`；
- 日志目录；
- Runtime 缓存目录；
- 设备身份状态和 Device ID 缩略值。

诊断操作：

- 重新检测 Codex；
- 复制脱敏状态；
- 导出脱敏诊断包；
- 打开日志目录；
- 恢复默认设置；
- 重置设备身份；
- 退出 Agent。

不进入设置页的参数：

- `max-message-bytes`；
- Writer/Reader Queue 容量；
- 重连退避数组；
- 心跳间隔；
- Relay 速率限制；
- OpenAI/Codex 身份、模型或 Endpoint。

## 8. 深模块、Interface 与 Seam

### 8.1 设计要求

桌面重构不能把当前 `AgentProgram.RunAsync` 拆成大量 WinForms 事件处理器。复杂度应集中到少量深模块：调用者只学习小 Interface，模块内部负责顺序、超时、重试、回滚和状态转换。

| Module | 外部 Interface 负责什么 | 隐藏的 Implementation 复杂度 |
|---|---|---|
| Desktop Application | 选择 GUI/Headless、运行、激活已有实例、退出码 | WinForms 消息循环、Console attach、单实例 IPC、异常收口 |
| Settings | 加载 Snapshot、应用 Change、恢复/重置 | 路径、Schema、原子写、备份、损坏隔离、CLI/环境覆盖 |
| Runtime Coordinator | Start、Stop、Pause、Resume、RequestRestart、Snapshot | Runtime 发现、Probe、Bridge/Proxy/Relay 顺序、重启退避、活动 Turn 延迟 |
| Pairing Coordinator | Create、Approve、Deny、Update、Revoke、Snapshot | v2 协议、倒计时、二维码、权限、离线撤销、Device Ready Gate |
| Diagnostics | Capture、Export | 脱敏、大小限制、日志摘要、失败证据 |

### 8.2 Desktop Application Module

概念 Interface：

```text
Run(LaunchRequest) -> ExitCode
ActivateExistingInstance(ActivationRequest) -> ActivationResult
```

`LaunchRequest` 区分：

- GUI：无参数双击或 `--background`；
- Headless：`--headless`；
- 一次性：`--help`、`--probe-only`。

GUI 使用 `WinExe`，避免双击时出现控制台；Headless/一次性模式必须附加父控制台并保留可脚本化的 stdout、stderr 和退出码。不得为此新增第二个 Console EXE。

单实例规则：

- 每个 Windows 用户只有一个 GUI/常驻实例；
- 第二次双击通过用户隔离的 named pipe 激活已有设置窗口；
- 第二个 Headless 常驻实例明确报错；
- `--help` 和 `--probe-only` 可独立运行；
- named mutex/pipe 名称包含用户 SID，不跨用户争用。

### 8.3 Settings Module

概念 Interface：

```text
Load() -> SettingsLoadResult
Apply(SettingsChange) -> SettingsApplyResult
RestoreLastGood() -> SettingsSnapshot
ResetDefaults() -> SettingsSnapshot
ResetDeviceIdentity(Confirmation) -> IdentityResetResult
```

UI 不直接读写 JSON、注册表或文件路径。测试和 Headless 调用同一 Interface。

Settings Module 在内部使用两个真实 Adapter：

- Filesystem Adapter：生产读写安装目录 `data\`；
- In-memory Adapter：测试原子保存、损坏恢复和迁移语义。

### 8.4 Runtime Coordinator Module

概念 Interface：

```text
Start(RuntimeSettings) -> RuntimeSnapshot
PauseRemoteAccess() -> RuntimeSnapshot
ResumeRemoteAccess() -> RuntimeSnapshot
RequestRestart(RestartMode) -> RestartResult
RequestExit(ExitMode) -> ExitResult
ObserveSnapshot(callback) -> Subscription
```

Runtime Coordinator 拥有以下对象的创建和销毁顺序：

1. `AgentLog`；
2. `CodexRuntimeResolver`；
3. `CodexExecutableProbe`；
4. `CodexStateManager`；
5. `AppServerBridge`；
6. `LocalCodexProxyServer`；
7. `RemoteControlDispatcher`；
8. `RelayClient`；
9. `PairingCoordinator`。

WinForms 不得保存或直接 Dispose 这些对象。Runtime Coordinator 负责有界停止、故障重启和 Snapshot 聚合。

### 8.5 Pairing Coordinator Module

Pairing Coordinator 的 Relay 是“远端但自有”的依赖。Interface 位于 Agent 业务规则一侧：

```text
CreatePairing() -> PairingSnapshot
ResolvePending(requestId, decision, permissionProfile) -> PairingResult
ListControllers() -> ControllerPairingList
Rename(pairingId, alias) -> PairingResult
SetPermission(pairingId, profile) -> PairingResult
Revoke(pairingId) -> RevokeResult
SynchronizeBeforeReady() -> SyncResult
```

Adapter：

- Relay WebSocket Adapter：生产；
- In-memory Relay Adapter：状态机、并发和离线测试。

二维码生成是本地 Implementation，不调用第三方二维码网站或远端图片接口。

### 8.6 Shell Adapter

WinForms Shell Adapter 只承担：

- 把 Snapshot 映射为控件状态；
- 把用户操作转换为模块命令；
- 显示错误、倒计时和确认；
- 托盘、窗口、系统通知和无障碍。

配对通知只能打开本机确认页，不能在 Windows 通知中直接“允许”。

## 9. 生命周期与状态模型

### 9.1 分离状态轴

不得用一个红/绿灯混合所有故障。Shell、Codex Core 和 Relay 是三个独立状态轴。

Shell：

```text
FirstRun / Ready / Faulted / Exiting
```

Codex Core：

```text
Stopped
  -> Discovering
  -> Probing
  -> StartingBridge
  -> StartingProxy
  -> Ready
  -> RestartPending
  -> Stopping
  -> Failed
```

Relay：

```text
Paused / Offline / Connecting / Authenticating / Synchronizing / Online
                                      └──────────────> Incompatible / Failed
```

聚合状态示例：

| Codex Core | Relay | 用户结论 |
|---|---|---|
| Ready | Online | 可以使用 |
| Ready | Offline | 本机可用，远程离线 |
| Ready | Paused | 本机可用，远程访问已暂停 |
| Failed | Online | Codex 不可用，需要修复 |
| Starting | Connecting | 正在启动 |
| Ready | Incompatible | 本机可用，Relay 版本不兼容 |

### 9.2 自动重启

保留现有有界退避思想，但由 Runtime Coordinator 统一拥有：

- app-server 意外退出后按退避重启；
- 稳定运行超过阈值后重置退避；
- Relay 断线只重连 Relay，不重启 app-server；
- 配置引起的重启不计入故障退避；
- 暂停远程访问不视为故障；
- UI 只展示当前阶段和下一次重试时间，不弹窗轰炸。

### 9.3 动态端口

- 默认端口模式为 `auto`，传递 `0` 让 loopback Listener 选择可用端口；
- Runtime Snapshot 返回实际端口；
- 一键终端使用实际端口；
- 高级设置可以选择 `fixed` 并输入 1–65535；
- 固定端口冲突时进入可修复故障，不静默改端口；
- 无论何种模式都只监听 IPv4 loopback。

## 10. 配置与数据持久化

### 10.1 数据根目录

安装版：

```text
<InstallRoot>\
  CodexControlAgent.exe
  data\
    settings.json
    settings.last-good.json
    agent-state.json
    device-identity.json
    logs\
    codex-runtimes\
    diagnostics\
```

便携版同样使用 EXE 旁的 `data\`。

规则：

- `data\` 必须可写；
- 不可写时进入故障页并停止 Runtime 启动；
- 不得静默回退到 `%LOCALAPPDATA%`；
- 不提供易丢失的纯内存模式；
- 覆盖升级不得覆盖、清空或重建 `data\`；
- Release 包本身不得携带开发机的 `data\`；
- 复制到另一电脑或 Windows 用户后，DPAPI 解密失败必须进入身份修复流程，不能静默生成新身份并假装原配对仍有效。

### 10.2 `settings.json` 建议 Schema

```json
{
  "schemaVersion": 1,
  "device": {
    "name": "DEV-PC-01"
  },
  "relay": {
    "rootUrl": "https://control.example.com",
    "remoteAccessPaused": false
  },
  "startup": {
    "runAtLogin": true,
    "startCoreAutomatically": true
  },
  "codex": {
    "pathMode": "auto",
    "path": null
  },
  "localProxy": {
    "portMode": "auto",
    "port": null
  },
  "appearance": {
    "theme": "system"
  }
}
```

设置文件不得包含：

- OpenAI API Key、auth token 或私有 Endpoint 凭据；
- Device 私钥明文；
- Pairing Code；
- Prompt、回复、Shell Output、Diff 或源码；
- Relay Pairing Secret。

### 10.3 运行状态文件

`agent-state.json` 只保存必须跨重启保持的本机状态：

- 待同步撤销的 Pairing ID 和幂等 `requestId`；
- 最后一次成功启动的 Runtime 元数据摘要；
- 设置损坏恢复标志；
- 安装/升级迁移版本。

它不保存 Codex 原始状态、会话内容或 JSON-RPC 历史。

### 10.4 原子写和损坏恢复

保存顺序：

1. 验证新 Snapshot；
2. 写入同目录临时文件；
3. Flush 并重新解析临时文件；
4. 保留上一个有效文件为 `settings.last-good.json`；
5. 原子替换 `settings.json`；
6. 更新 UI 为已保存。

加载失败时：

- 原始损坏文件改名隔离，保留证据；
- 尝试 `settings.last-good.json`；
- 显示“配置已损坏，已加载上次有效配置”；
- 用户可接受恢复或重新配置；
- 不静默覆盖原文件；
- 不直接退出 Agent Shell。

### 10.5 配置优先级

从低到高：

```text
安全默认值
  < settings.json
  < CODEX_CONTROL_* 环境变量
  < 当前进程命令行参数
```

环境变量和命令行只覆盖当前运行，不回写 `settings.json`。

模式规则：

- 无参数：GUI/托盘；
- `--background`：GUI Shell 静默启动；
- `--headless`：使用同一 Settings/Runtime Interface，无 WinForms 窗口；
- `--help`、`--probe-only`：一次性运行并退出；
- `--pair`：只允许有交互式本机控制台时创建配对，并在控制台执行同等的显式 Allow/View-only/Deny 确认；stdin 被重定向或无交互终端时返回 `PAIRING_CONFIRMATION_UI_REQUIRED`；
- 任何 Headless 模式都不得自动批准配对或 Codex Approval。

## 11. Relay 地址模型

设置只保存服务根地址：

```text
https://control.example.com
```

由一个 URL Module 派生：

```text
Health:          https://control.example.com/healthz
Device WSS:      wss://control.example.com/ws/device
Controller WSS:  wss://control.example.com/ws/controller
PWA:             https://control.example.com/
```

约束：

- 生产只接受 `https`；
- `http` 只允许 `127.0.0.1`、`localhost` 或 IPv6 loopback 的显式开发模式；
- 拒绝用户名、密码、query 和 fragment；
- 首版根路径必须为 `/`，不支持任意反向代理子路径；
- 保存前访问 `/healthz` 并检查 `protocolVersion`；
- TLS 证书错误不能绕过；
- Relay 不可达允许用户明确离线保存；
- 协议不兼容保存地址但阻止 WS 连接与配对。

## 12. 配对协议 v2

### 12.1 安全语义

v2 必须同时满足：

- 六位码 TTL 仍为 180 秒；
- Proof 失败上限仍为 5；
- 同 Device 同时只有一个活动码；
- 新码使旧码失效；
- 第一个通过 Code 和签名校验的 Claim 立即占用配对码；
- 占用后不再接受第二个 Claim；
- 电脑端有独立 60 秒确认期；
- 拒绝或超时后必须重新生成配对码；
- Relay 只有在电脑明确 Allow 后才创建/恢复 Pairing；
- 默认 UI 选择“完整控制”，但必须明确列出四项能力；
- 通知不能直接批准；
- Relay 断开、暂停或 Device Connection 被替换时，活动码和待确认请求立即失效。

### 12.2 二维码

二维码由 Agent 本地生成，内容使用 PWA URL fragment，例如：

```text
https://control.example.com/#/pair?relay=<base64url-root>&code=583271
```

Fragment 不发送给 HTTP Server，也不进入 Referrer。PWA 读取后必须立即使用 `history.replaceState` 清除地址栏中的码。

手机扫码后的页面先显示：

- Relay 域名；
- 电脑名称；
- 自动生成但可编辑的 Controller 名称，例如“Yx 的 iPhone”；
- “确认配对”按钮。

手机确认只表示提交申请，不表示电脑已经授权。

六位码继续作为无法扫码时的备用入口。

### 12.3 v2 流程

```text
Device -> Relay: pairing.create
Relay  -> Device: pairing.created(code, expiresAt)

Controller -> Relay: pairing.claim(v2 proof)
Relay:
  - 原子校验 Code、TTL、次数和 Proof
  - 消耗 PairingSession
  - 创建 60 秒 PairingRequest
Relay -> Controller: pairing.pending(requestId, expiresAt)
Relay -> Device: pairing.confirmation.requested

Device 用户选择 Full / ViewOnly / Deny
Device -> Relay: pairing.confirmation.resolve(requestId, decision, profile)

Allow:
  Relay 原子创建 Controller/Pairing/权限
  Relay -> Device + Controller: pairing.completed

Deny/Timeout/Disconnect:
  Relay 终结 PairingRequest
  Relay -> 可达双方: pairing.denied / pairing.expired
```

### 12.4 v2 Proof

Controller 名称必须加入签名原文，避免中途篡改：

```text
codex-control-pairing-proof-v2
<code>
<controllerId>
<controllerName>
<publicKey>
<proofNonce>
```

继续使用 ECDSA P-256、SHA-256 和 P1363 `R || S`。

电脑确认页显示 Controller 名称、平台摘要和公钥短指纹。短指纹只用于帮助识别，不替代签名校验。

### 12.5 权限模型

| Profile | view | steer | interrupt | approval |
|---|---:|---:|---:|---:|
| ViewOnly | true | false | false | false |
| Full | true | true | true | true |

后续修改权限必须：

- 由 Device 本机设置页发起；
- 在 Relay 当前 Pairing 上原子更新；
- 立即影响所有新 Control；
- 离线修改进入同步队列；
- UI 在 Relay 确认前显示“待同步”，不能显示完成。

Controller 自报名称保存在 `ControllerEntity.Name`。电脑端自定义名称应保存在 Pairing 级 Alias，不能修改同一 Controller 在其他 Device 上的显示名称。

### 12.6 Device Ready Gate 与离线撤销

为消除重连瞬间的控制窗口，v2 Device Connection 分两阶段：

```text
Authenticated -> Synchronizing -> Ready/Online
```

在 `Ready` 前：

- Relay 不向该 Device 路由 Control；
- Controller 不收到 Device Online；
- Agent 逐项发送持久化的待撤销/降权操作；
- 所有操作使用幂等 `requestId`；
- Relay 确认后 Agent 才删除本机队列项；
- 同步全部完成后 Device 显式发送 Ready。

若同步失败，Device 保持 Offline/Synchronizing，不得先上线再补撤销。

### 12.7 协议消息面

具体命名在实现前更新 `docs/protocol.md`，但 v2 至少需要以下语义：

```text
pairing.create / pairing.created
pairing.claim / pairing.pending
pairing.confirmation.requested
pairing.confirmation.resolve
pairing.completed / pairing.denied / pairing.expired
pairing.list / pairing.list.result
pairing.update / pairing.updated
pairing.revoke / pairing.revoked
device.sync / device.sync.completed
device.ready
```

所有改变状态的请求必须携带幂等 `requestId`，响应必须匹配 Relay 跟踪。

### 12.8 Relay 数据模型

需要 EF Core Migration：

- 新增 `PairingRequests`，保存已验证 Claim 的短期确认状态；
- `Pairings` 新增 Pairing 级 Alias 或等价字段；
- 明确 Permission Profile 与四个现有布尔权限的映射；
- Pending Request 保存 Controller 公钥和必要公开元数据，不保存 Pairing Code；
- Audit 记录 Created/Claimed/Allowed/Denied/Expired/Revoked/PermissionChanged 元数据；
- 不保存二维码、私钥、完整请求正文或签名原文。

当前所有 Pairing 均为测试数据，协调升级时：

1. 备份 Relay SQLite 和记录校验值；
2. Migration 清理旧 `Pairings` 与未完成 `PairingSessions`；
3. 保留 Device 和 Controller 公共身份记录；
4. PWA 认证后以 Relay 返回的真实 Device List 清理本地陈旧 `pairedDeviceIds`；
5. 所有 Controller 按 v2 重新配对；
6. 不提供 v1 直接配对兼容期。

这是一次明确的数据清理，实施前仍需在迁移计划中列出实际数据库路径、备份和回滚步骤。

## 13. PWA 设计

### 13.1 Relay 配置

- 内部持久化 Relay 根地址，不再要求用户输入完整 WebSocket 路径；
- 从当前 HTTPS Origin 或二维码派生 Controller WSS；
- 二维码切换 Relay 前显示域名摘要并由用户确认；
- 生产不允许 `ws`；本机开发只允许 loopback；
- Relay 切换失败时保留原可用配置，不能把用户锁在离线页。

### 13.2 Controller 名称

- 基于浏览器和平台生成默认名称；
- 扫码摘要页可编辑；
- 名称进入 v2 Proof；
- 配对后 Device 可设置 Pairing 级 Alias。

### 13.3 日常进入

- 只有一台已配对电脑时直接进入；
- 多台时恢复上次使用的电脑；
- 保留明显“切换电脑”入口；
- 上次电脑离线时仍进入其工作区并显示等待重连；
- 不改变现有项目/最近分区和 Thread 读取语义。

### 13.4 浏览器持久化

继续保持：

- Controller 私钥为不可导出 IndexedDB `CryptoKey`；
- Relay 根地址和 last-selected-device 属于非敏感 UI 状态；
- 清除浏览器数据后必须重新配对；
- 不把 Pairing Code 长期写入 LocalStorage、IndexedDB、Service Worker Cache 或日志。

## 14. 安装、升级与卸载

### 14.1 安装器

选择 Inno Setup，原因：

- 支持当前用户安装；
- 适合单个 Setup EXE；
- 可检测并启动官方 Runtime 安装器；
- 可创建当前用户开始菜单和卸载入口；
- 可精确控制 `data\` 在升级和卸载时的处理；
- MSIX 安装目录只读，与本设计冲突。

### 14.2 Framework 与发布形态

- Agent：`.NET 8`、`net8.0-windows`、win-x64、framework-dependent；
- Relay/shared 保持 `.NET 8`，不因 WinForms 改为 Windows TFM；
- Agent 发布仍尽量保持单文件；
- 主交付为 Inno 安装包；
- 保留 framework-dependent 便携版供开发和排障；
- 便携版不负责自动补齐 Runtime，只给出明确检测结果；
- 安装版负责检测 Desktop Runtime 和 ASP.NET Core Runtime。

### 14.3 开机启动

- 首次配置成功后默认启用；
- 使用当前用户 `HKCU` Run 注册，命令指向稳定安装路径并携带 `--background`；
- 不创建 Windows Service；
- 不使用计划任务提升权限；
- 便携版允许注册，但移动 EXE 后设置页必须检测并修复失效项。

### 14.4 覆盖升级

覆盖升级必须：

- 停止 Agent 或请求其有界退出；
- 替换应用文件；
- 完整保留 `data\`；
- 迁移 `settings.json` Schema；
- 保留 DPAPI Device 身份；
- 保留开机启动开关并修复路径；
- 重新检测 Runtime；
- 启动后显示一次升级结果，不弹营销窗口。

禁止对安装根目录做不区分目标的递归清理。任何发布/安装清理都必须解析并验证绝对路径，并显式排除 `data\`。

### 14.5 卸载

- 正常覆盖升级不进入数据删除流程；
- 用户主动卸载时默认勾选删除 `data\`；
- 可以选择“保留配置以便重新安装”；
- 删除前请求运行中的 Agent 尽力撤销 Pairing 并有界退出；
- Relay 不可达时提示旧 Device 记录可能作为离线记录残留，但旧私钥删除后不能恢复控制新身份；
- 删除开机启动、开始菜单和应用文件；
- 删除 `data\` 后不可恢复，卸载页必须明确说明。

### 14.6 签名

- 内部/测试构建允许未签名；
- 未签名安装器、Agent 和验收报告必须醒目标记 `UNSIGNED`；
- 签名能力作为可选 Release 输入；
- 若提供证书，Agent 与最终安装器使用一致 Authenticode 身份、SHA-256 和 RFC 3161 时间戳；
- 证书、PFX、密码或签名服务凭据不得进入仓库、日志或发布包；
- 未签名证据不能宣称已通过公共 SmartScreen/企业策略验收。

## 15. 重置语义

“恢复默认设置”：

- 只重置 `settings.json` 中普通配置；
- 不删除 Device 身份；
- 不删除日志；
- 不删除 Runtime 缓存；
- 不撤销 Pairing。

“重置设备身份”：

- 位于危险区域；
- 二次确认；
- 在线时先尽力撤销全部 Pairing；
- 停止 RelayClient；
- 删除 DPAPI Device 身份和待撤销队列；
- 创建新 Device 身份；
- 需要所有 Controller 重新配对；
- 离线时明确提示 Relay 可能保留旧的离线 Device 元数据。

## 16. 诊断与隐私

脱敏诊断包允许包含：

- Agent/Relay/PWA 版本和协议版本；
- Settings Schema 版本；
- Windows 版本和架构；
- Runtime 发现来源、Codex 版本和能力探测结论；
- Core/Relay 状态转换和时间；
- loopback 实际端口；
- 最近错误码和有界堆栈；
- 安装、升级和 Runtime 检测结果；
- 日志文件清单及大小。

默认省略或脱敏：

- 完整 Relay 内网 URL；
- Device/Controller 完整 ID；
- DPAPI Blob；
- 公私钥；
- Pairing Code、nonce、signature；
- Prompt、回复、Shell Output、Diff、源码；
- app-server 原始 JSON-RPC；
- OpenAI/Codex 身份、模型和 Endpoint 配置。

诊断包只保存到用户选择的位置，不自动上传。

## 17. 分阶段实施计划

本节只是设计顺序，不构成实施授权。

### 阶段 1：桌面宿主与配置

范围：

- `net8.0-windows` WinForms Shell；
- GUI/Headless 双模式；
- 单实例与激活；
- Settings Module 和 `data\` 布局；
- 首次向导；
- Runtime Coordinator 和真实状态轴；
- 托盘、设置四页、系统通知；
- 动态端口和一键 Codex 终端；
- 暂停、延迟重启和安全退出；
- 开机启动 Adapter；
- 配置损坏、目录不可写和 Codex 不可用恢复。

阶段出口：不改变 v1 Relay 协议，也能通过现有真实 Codex 和 Agent 测试；桌面 Shell 不包含假状态。

### 阶段 2：配对协议 v2

范围：

- 共享协议常量和 Contracts；
- Agent Pairing Coordinator；
- Relay v2 Claim/Confirm/Permission/Revoke；
- Device Ready Gate 和离线撤销；
- EF Core Migration 和测试 Pairing 清理；
- PWA 二维码摘要、Controller 名称、pending UI 和上次电脑；
- 协议、安全、架构和开发文档同步；
- Agent/Relay/PWA 跨端测试同步。

阶段出口：v1 配对入口关闭；并发 Claim、拒绝、超时、断线、重放、五次失败、权限和离线撤销全部验证。

### 阶段 3：安装与发布

范围：

- Inno Setup；
- Runtime 检测和按需 UAC；
- 开始菜单、卸载和升级；
- `data\` 保留/删除；
- 便携版；
- 可选签名和 `UNSIGNED` 标识；
- Release 脚本与 SHA-256 清单；
- Windows 10/11 干净环境验收。

阶段出口：安装、首次配置、开机启动、覆盖升级、卸载和便携故障路径都有可复验记录。

## 18. 预计变更面

确认后实施预计涉及：

- `src/agent/`：桌面 Shell、Settings、Runtime Coordinator、Pairing Coordinator、诊断和启动入口；
- `src/agent/CodexControl.Agent.csproj`：WinForms/Windows TFM/GUI 输出设置；
- `src/shared/CodexControl.Protocol/`：协议 v2 常量和 Contracts；
- `src/relay/Pairing/`、`Routing/`、`WebSockets/`：Claim/确认/Ready Gate/管理；
- `src/relay/Persistence/`：Entities、DbContext、Migration；
- `src/web/src/`：配对入口、Relay 根地址、Controller 名称、pending 状态和 last device；
- `tests/` 与 `src/web/tests/`：跨模块和系统验证；
- `tools/Build-Release.ps1`：安装包、签名输入和 Release 验证；
- 新增 Inno Setup 源文件；
- `docs/architecture.md`、`protocol.md`、`security.md`、`development.md`、`feasibility.md`：实现完成后按真实证据更新。

预计变更面不是预先批准的文件清单。实施时仍须重新读取当前磁盘、检查工作区和逐项验证。

## 19. 验证矩阵

### 19.1 Agent 自动化

- GUI/Headless 参数和退出码；
- 每用户单实例、第二次激活和第二个 Headless 拒绝；
- `settings.json` 原子写、last-good、损坏隔离和 Schema Migration；
- `data\` 不可写失败关闭；
- DPAPI 身份原用户加载、跨用户/跨机器失败路径；
- 动态端口和固定端口冲突；
- Runtime 各阶段 Snapshot；
- Relay 离线不重启 Core；
- 暂停状态跨重启保持；
- 活动 Turn 延迟重启、完成后退出和强制退出；
- 一键 TUI 和第二 TUI 拒绝；
- 脱敏诊断包无受限内容。

### 19.2 Relay 自动化

- v2 Pairing Code 180 秒 TTL 和五次失败；
- 第一个有效 Claim 占用码；
- 并发第二 Claim 拒绝；
- ControllerName 纳入 Proof；
- 电脑 Allow/ViewOnly/Deny；
- 60 秒确认超时；
- Device 断线/暂停使请求失效；
- Pairing 只在 Allow 后落库；
- 权限修改立即影响 Control；
- 撤销、重复撤销和幂等 requestId；
- Synchronizing Device 不可路由；
- 待撤销同步完成后才 Online；
- 无 Pairing Code 明文；
- Migration 后旧测试 Pairing 清空；
- Audit 只保存允许元数据。

### 19.3 PWA / Playwright

- QR fragment 解析后立即清理 URL；
- Relay 域名和电脑名称摘要；
- Controller 默认名称和编辑；
- pending/allow/deny/expired UI；
- ViewOnly 隐藏或禁用变更操作；
- 只有一台电脑直达；
- 多台恢复 last device 和切换；
- Relay 切换失败保留原配置；
- 浏览器 reload 后身份认证恢复；
- Chromium/Android profile 和 WebKit/iPhone profile；
- 现有项目/最近分区、历史、Steer、Interrupt 和 Approval 无回归。

### 19.4 安装器

- Windows 10 x64 干净环境；
- Windows 11 x64 干净环境；
- 两套 Runtime 都存在；
- 只缺 Desktop Runtime；
- 只缺 ASP.NET Core Runtime；
- Runtime 安装取消、失败和无网络；
- 当前用户安装无 UAC；
- Runtime 安装按需 UAC；
- 首次配置后开机启动；
- 覆盖升级保留完整 `data\`；
- 移动便携 EXE 后启动项故障提示；
- 只读目录拒绝启动；
- 卸载默认删除和选择保留两条路径；
- `UNSIGNED` 产物标识；
- SHA-256 清单校验。

### 19.5 真实闭环

- 真实 `codex app-server` 能力探测；
- 真实 `codex --remote` 一键启动；
- 真实 Thread/Turn、Steer、Interrupt 和 Approval；
- 真实 Relay + 真实浏览器 Web Crypto 配对；
- 真实手机扫码、电脑确认和权限切换；
- Agent/Relay 断线重连；
- Windows 登录后静默启动；
- 活动 Turn 下升级/退出不会被自动化结果替代。

## 20. 验收证据边界

| 证据 | 能证明 | 不能替代 |
|---|---|---|
| Build | 代码可编译 | Runtime、UI、协议闭环 |
| Fake/In-memory Adapter | 模块状态机和失败语义 | 真实 Relay/Codex/Windows |
| Agent/Relay 自动化 | 本机协议、权限和持久化 | 真实手机、安装和企业网络 |
| Playwright | 真实浏览器和 Relay 路径 | iOS/Android 实机后台行为 |
| Real Codex | 当前 Codex 版本的公开 app-server 闭环 | 其他电脑和未来 Codex 版本 |
| Inno/VM | 安装、Runtime、升级和卸载 | 生产分发信誉和企业策略 |
| 真实手机 | 扫码、确认和移动交互 | 生产域名、证书、弱网和规模 |
| Docker | 本机容器部署 | 生产 DNS/CA/防火墙/FAT/SAT |

任何阶段不得用绿色 UI、Schema、新表、构建成功或单一 happy-path 测试代替真实调用链证据。

## 21. 明确拒绝的替代方案

- WPF：本场景不需要复杂动画，生命周期与交付成本更高；
- 本机网页设置：不能提供目标中的输入法式原生常驻体验；
- 多 Relay Profile：增加误连环境和切换状态，本期只保留一个当前连接；
- 固定完整 WebSocket URL：普通用户不应理解 `/ws/device` 和 `/ws/controller`；
- 持续轮换配对码：长期扩大配对入口；
- 扫码即完整授权：缺少电脑本机确认；
- 通知直接批准：容易误触；
- Relay 离线时禁止撤销：安全操作不能依赖在线；
- Relay 内嵌 Agent：破坏部署和信任模型；
- MSIX：安装目录只读，与统一 `data\` 位置冲突；
- Self-contained Agent：已确认使用 framework-dependent + 安装器补齐 Runtime；
- 强制生产签名门槛：当前为内部/测试分发，改为可选签名并标记 `UNSIGNED`；
- v1/v2 双协议兼容期：现有 Pairing 均为测试数据，直接协调升级；
- 自动更新：需要独立的可信源、签名、回滚和供应链设计，不纳入本期。

## 22. 主要风险与缓解

| 风险 | 后果 | 缓解 |
|---|---|---|
| WinForms 改为 WinExe 后 CLI 输出丢失 | 自动化和排障受损 | 单 EXE 按模式 AttachConsole，保留退出码 |
| 所有数据位于安装目录 | 升级/卸载误删 | 当前用户可写安装路径、显式保护 `data\`、真实升级测试 |
| DPAPI 文件被复制 | 用户误以为身份可迁移 | 解密失败进入显式身份修复，不静默轮换 |
| Framework 依赖缺失 | Agent 在 UI 出现前无法运行 | 安装器前置检测两套 Runtime，缺失时官方安装器补齐 |
| 协议 v2 协调发布 | 混合版本离线 | 提升协议版本、health 明示、不保留 v1 配对入口 |
| 离线撤销重连竞态 | 被撤销 Controller 短暂控制 | Authenticated 后先 Synchronizing，完成撤销才 Ready |
| QR 泄漏 | 未授权配对申请 | Fragment、立即清 URL、180 秒、首 Claim 占用、电脑确认 |
| 活动 Turn 被设置修改打断 | 任务丢失 | RestartPending、终态后应用、强制操作二次确认 |
| 测试 Pairing 清理 | 数据不可恢复 | 迁移前 SQLite 备份和校验，明确只清 Pairing 关系 |
| 未签名内部包 | SmartScreen/企业策略阻断 | `UNSIGNED` 标识、单独报告，不宣称公共分发通过 |

## 23. 外部参考

- [.NET on Windows 安装方式](https://learn.microsoft.com/en-us/dotnet/core/install/windows)
- [Microsoft SmartScreen reputation](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation)
- [Authenticode 时间戳](https://learn.microsoft.com/en-us/windows/win32/seccrypto/time-stamping-authenticode-signatures)
- [Inno Setup 非管理员安装](https://jrsoftware.org/ishelp/topic_setup_privilegesrequired.htm)
- [MSIX 安装目录只读模型](https://learn.microsoft.com/en-us/windows/msix/msix-containerization-overview)

## 24. 用户确认 Gate

本设计没有待定的产品分支。用户已于 2026-08-23 明确确认开发，Gate 已满足。

确认前曾禁止：

- 修改 `src/`、`tests/`、`deploy/`、`tools/`；
- 修改 `.csproj`、`package.json`、协议常量或 Contracts；
- 新增 EF Migration；
- 清理任何现有 Pairing 数据；
- 生成或发布安装包；
- 改动当前 Release 产物；
- 以本文件作为“功能已完成”的证据。

实现已按阶段 1、阶段 2、阶段 3 顺序推进。自动化、真实 Codex、本机安装/升级/卸载和 Release
证据已执行；Windows 10/11 两台干净机、真实手机、正式域名/证书和生产网络仍不能由本机证据替代。
