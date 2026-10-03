# 共享会话公开协议探针

本工具用于核实真实官方 Codex Desktop 与第二个公开协议客户端，能否连接同一个 app-server、共同操作同一 Thread。它是 G0/G1 的前置探针，不是 Agent、Relay、PWA 的共享会话实现，也不构成 G2 或生产验收。

工具项目：`tools/CodexControl.SharedSessionProbe`，目标框架为 `.NET 8`。它仅连接用户指定的已有端点；不启动 app-server，不自动回退到独立服务，不部署新的 Windows 产品端点。

2026-10-03 补充：当前账户下临时服务与 Desktop 退出/启动实验已另行获得授权，启动、观察、恢复步骤见 [Windows Desktop 临时共享接入实测](shared-desktop-trial.md)。下面的 2026-10-02 记录保留为当时证据；版本、授权范围与新建 Thread 能力以补充文档为准。

## 当前证据与未决项

截至 2026-10-02：

- 报告引用的上游基线是 `rust-v0.160.0` / `a956835d020762cb2b570053af06f643a11c0ecc`。
- 本机只读检查得到独立 CLI 版本 `0.159.0-alpha.12.1`，不能将其等同于报告版本、Desktop build 或实际服务版本。
- 本机 `daemon version` 检查返回连接错误 `os error 10061`。这不证明 Desktop 未运行，也不证明其连接的服务可以由此入口访问。
- 使用该真实 CLI 新建的隔离、无凭据 app-server，已通过 loopback WebSocket 与 AF_UNIX 两条传输的双客户端只读检查，具体范围见下表。隔离进程已关闭。
- 真实 Desktop 的公开共享接入入口尚未证明；真实双端实时消息、运行中输入、审批、停止和断连继续均尚未验证。
- 探针代码、必要编译与本地协议检查只能证明对应实现层。任何自建模拟服务或两个自建客户端的成功，都不能替代真实 Desktop 验收。

| 已执行检查 | 结果与证据边界 |
| --- | --- |
| `.NET 8` 原型编译 | `dotnet build tools/CodexControl.SharedSessionProbe/CodexControl.SharedSessionProbe.csproj --configuration Debug` 通过，0 警告、0 错误。状态合并与权限门禁经源码审查、编译检查，尚未完成真实共享 Thread 行为验证。 |
| `ws://127.0.0.1:<临时端口>` | 两个独立 probe 客户端完成 `initialize`，各自 `thread/loaded/list` 返回 `data` 数组；第二个接入后第一个仍可读取。 |
| `unix://<隔离目录>/control/probe.sock` | 同样通过双客户端初始化及已加载 Thread 列表读取。Windows AF_UNIX 上的 WebSocket 握手与帧传输可用。 |
| 进程和配置隔离 | 仅此次新建进程使用局部环境变量 `CODEX_HOME` 指向空的隔离目录；没有读取用户凭据，没有使用或变更默认 daemon、Desktop；测试进程已关闭。 |
| 尚未覆盖 | 未建立真实 Desktop 与探针的共享连接，未由上述列表检查证明共享 Thread、用户输入、回复、审批或 Turn 生命周期；G0/G1 仍未通过。 |

本机 Unix socket 启动检查还发现：app-server 拒绝已存在且继承普通 ACL 的 socket 父目录。通过的方式是让 app-server 在隔离目录下新建专用的私有 `control` 子目录和 `probe.sock`。此事实是该版本的服务启动限制，不表示探针会创建目录或修复权限；连接现有公开服务时不要为了通过检查改写其 ACL。

上游 daemon 的生命周期合同仍标为实验性。公开源码可见不等于稳定生产支持。当前原型只核实本机公开 Unix socket 或 loopback WebSocket，不更新 CLI、不重启 Desktop、不修改全局配置、不使用私有接口、UI 抓取或输入模拟。报告中的历史授权是被审查材料，不作为本轮操作授权。

## 连接前提

1. 已通过正式入口获得现有服务端点。必须确认它属于本轮允许验证的服务；不要猜测或扫描 Desktop 私有接口。
2. 准备现有的绝对测试目录。在官方 Desktop 中由人创建独立测试 Thread，首条输入包含不重复的测试标记，例如 `CC_SHARED_PROBE_A7K2`；内容不含私人资料。
3. 记录测试 Thread ID、目录及标记。只有公开接口读回的 cwd 与指定目录匹配，且名称或预览包含该标记，探针才可继续加入。标记格式为 `CC_SHARED_` 加 8 至 64 个字母、数字、下划线或短横线。不要将真实工作 Thread 当作测试对象。
4. 记录 Desktop build、实际服务版本、独立 CLI 版本，以及能够合法取得的服务身份信息。仅 Thread ID 或历史内容相同不足以证明服务实例相同。

探针支持两种显式连接方式，二选一：

| 参数 | 要求 |
| --- | --- |
| `--socket` | 现有 AF_UNIX 控制 socket 的绝对路径。控制 socket 承载 WebSocket，不能当作 JSONL 管道。Windows socket 的实际 canonical 路径还受上游 AF_UNIX 长度限制。 |
| `--endpoint` | 本机 `ws://127.0.0.1:<port>` WebSocket 端点。不要暴露原始 app-server 到局域网或公网。 |

本探针没有 `app-server proxy` 子进程适配。公开 proxy 透明转发原始字节，并不负责把 JSONL 转成 WebSocket；不能把旧 Agent 的启动命令直接换成 proxy 后宣称接入完成。

## 构建与只读连接检查

在仓库根目录的 PowerShell 7 中执行。以下为手动操作说明，不会替用户创建服务或执行全部验收。

```powershell
dotnet build '.\tools\CodexControl.SharedSessionProbe\CodexControl.SharedSessionProbe.csproj'
```

选择已确认的 socket：

```powershell
dotnet run --project '.\tools\CodexControl.SharedSessionProbe' -- inspect --socket 'C:\absolute\test-service.sock'
```

或选择已确认的 loopback WebSocket：

```powershell
dotnet run --project '.\tools\CodexControl.SharedSessionProbe' -- inspect --endpoint 'ws://127.0.0.1:4500'
```

示例地址是占位符。`inspect` 建立两个独立连接，分别执行公开初始化握手，然后各自调用 `thread/loaded/list`，检查第二个连接加入后第一个仍可读取。输出 `two_connections_ready` 及 `firstListReadable`、`secondListReadable`，结束时只关闭这两个连接。连接或握手失败不会启动第二个执行服务。两项读取均成功只证明双客户端连接可用，不能据此判定官方 Desktop 也连接在此服务上。

## 加入隔离测试 Thread

```powershell
dotnet run --project '.\tools\CodexControl.SharedSessionProbe' -- observe --socket 'C:\absolute\test-service.sock' --thread '<test-thread-id>' --workspace 'D:\absolute\shared-session-test' --marker 'CC_SHARED_PROBE_A7K2'
```

使用 TCP WebSocket 时，将 `--socket ...` 替换为 `--endpoint 'ws://127.0.0.1:4500'`。

`observe` 的顺序是：

1. 完成公开 `initialize`。
2. 通过 `thread/read` 读回目标 Thread，检查 cwd，以及名称或预览中的测试标记。
3. 不带配置覆盖项调用 `thread/resume`，指定 `excludeTurns: true`，加入已有 Thread 并接收事件。
4. 再读 `thread/turns/list` 获取活动 Turn，并检查返回的权威 sandbox、approval policy 等策略；基于服务状态决定是否允许后续控制。

这里的 `resume` 用于同服务订阅。它不会把独立服务中的执行实例迁入当前服务。上游运行中重加入可能忽略 sandbox、model、cwd、approval 等覆盖项，并会更新客户端归因；因此不能以请求中写入 `workspace-write` 代替检查实际策略。

`start`、`steer`、`interrupt` 前必须读回以下有效策略：sandbox 为 `workspaceWrite`，approval policy 属于 `untrusted`、`on-request`、`never`，`approvalsReviewer` 为 `user`，`networkAccess` 为 `false`；`writableRoots` 和 `runtimeWorkspaceRoots` 都必须在指定测试目录范围内，路径不能通过 junction 或符号链接逃逸；`excludeTmpdirEnvVar` 和 `excludeSlashTmp` 必须均为 `true`。缺失、不符合或读取期间 Turn 已变化时，探针不提交这三类控制，可继续观察或显式拒绝已识别的审批。不能靠发出一个更严格的请求掩盖已有 Thread 的有效权限。此约束也不表示既有产品的运行中 resume 门禁已经改变：产品接入仍须经 `RemoteControlDispatcher` 分离订阅与提交，并保持配对权限检查。

## 交互命令

| 命令 | 行为与边界 |
| --- | --- |
| `status` | 重新检查测试对象并读取权威 Thread、活动 Turn 和策略状态；状态不明不能当作 idle。 |
| `start <text>` | 仅在目标 Thread 闲置且策略允许时提交 `turn/start`。输出服务返回的 Turn ID，只表示响应中的关联身份，不能证明新建了 Turn。输入必须限于隔离测试。 |
| `steer <text>` | 仅在已知活动 Turn 且策略允许时提交 `turn/steer`，携带 `expectedTurnId`。 |
| `interrupt` | 策略允许时，对目标测试 Thread 的已知活动 Turn 发送停止请求。不要用断开连接代替停止。 |
| `reconnect` | 重建本探针连接，重新检查测试对象并加入；不重启服务。 |
| `decline <requestId>` | 仅对探针列出的、支持 decline 的待处理请求发送明确拒绝。 |
| `cancel <requestId>` | 仅对探针列出的、支持 cancel 的待处理请求发送明确取消。 |
| `quit` | 关闭探针自己的连接；不停止 daemon，也不自动中断测试 Turn。 |

`start`、`steer` 会自动在输入前加入本次测试标记，输入文本限制为最多 4000 个字符。服务返回的接受响应不等于执行完成，也不能单独证明 Desktop 已显示该输入。

发生完成瞬间的竞争、过期 `expectedTurnId`、连接中断或结果不确定时，以服务返回和重新读取的状态为准。不要盲目重发输入，也不要用第二个 app-server 接管后继续声称仍为共享执行实例。`reconnect` 可在交互循环仍运行时主动重连；检测到连接已经断开时探针退出并报告未知状态，需重新运行 `observe`，不会自动重试提交。

命令执行和文件修改审批事件只显示请求关联 ID 和可用的拒绝/取消决定。探针不提供 accept，不自动批准；不能用这个工具完成完整的双端批准体验验收。设备绑定的 UserVerification 也不能视为普通共享审批。

上游 callback 的代码保证首个被消费的响应只处理一次，但不能仅据此证明首个业务有效响应胜出。后续产品验收须单独覆盖无效、过期、重复和近同时的相反决定，并核实未决请求恢复。不要在正在使用的 Thread 上发送故意无效的审批响应。

## 输出与证据

输出采用白名单摘要：时间、公开 RPC method、Thread/Turn/Item ID、状态及等待审批/输入标志、文本长度、测试标记命中，以及必要的关联请求/决定和策略检查结果。探针不输出或持久化正文、Shell 输出、Diff、原始 JSON-RPC、认证头、环境变量全集或私有凭据。

测试标记只用于确认隔离目标和关联测试动作。命中标记、最终答案相同或相同 Thread ID，都不能单独证明运行中的两端实时一致。需要结合公开的服务身份、活动 Turn/Item 关联与人工 Desktop 观察。完整长文本一致性、历史 delta 重放和 PWA 渲染不在这个摘要探针的证明范围内。

## G0/G1 验收记录

下面真实官方 Desktop 组合的结果当前均为“尚未验证”；上文已通过的隔离双客户端传输检查不替代这些门禁。执行时逐项记录目标版本、端点拓扑、动作时间、公开协议摘要及人工 Desktop 观察；未完成的项保持未验证，不改写为通过。

| 门禁 / 项目 | 必须证明的事实 | 当前结果 / 探针范围 |
| --- | --- | --- |
| G0：公开 Desktop 入口 | 当前真实 Desktop 经受支持的公开本机入口接入此服务；说明实验性支持等级。 | 尚未验证。探针无法代替 Desktop 配置与人工确认。 |
| G0：同一服务 | Desktop 和探针确实使用同一台电脑上的同一服务实例，公开端点与服务身份一致。 | 尚未验证。相同历史或 Thread ID 不足以通过。 |
| G0：有效权限 | 读回 sandbox、approval、cwd 等有效值；远程提交遵守产品边界。 | 尚未验证。探针拒绝不合策略的输入，不改现有策略。 |
| G1：同一 Thread/Turn | Desktop 创建的隔离 Thread 可加入；两端操作关联到同一活动 Turn，无 fork 或第二执行者。 | 尚未验证。 |
| G1：Desktop → 探针 | 人工 Desktop 输入测试标记，运行期间探针收到用户项、增量/过程项与状态推送，不靠历史轮询。 | 尚未验证。摘要可验证事件与标记，不能证明全部正文一致。 |
| G1：探针 → Desktop | 探针提交测试输入后，Desktop 在原页面实时显示输入、回复和状态，无需重开 Thread。 | 尚未验证。必须人工观察真实 Desktop。 |
| G1：活动输入与竞争 | 两端运行中输入进入同一执行序列；过期 steer 明确拒绝，完成竞态不误投新 Turn。 | 尚未验证。探针可发 start/steer，不保证全局 FIFO 或 exactly-once。 |
| G1：普通审批拒绝 | 两端看到同一请求；显式拒绝后未执行，另一端收到解决状态。 | 尚未验证。探针只支持可用的 decline/cancel。 |
| G1：完整审批仲裁 | 两端批准/拒绝、相反决定竞争、无效响应、断线恢复及设备绑定例外均行为明确。 | 尚未验证。接受审批与完整描述 UI 超出当前探针范围，需后续补充。 |
| G1：停止与终态 | 两端分别停止测试 Turn，另一端及时收到同一终态；其他 Thread 不受影响。 | 尚未验证。 |
| G1：探针断开与重连 | 探针 quit/重连不杀服务；Desktop 继续，探针恢复后状态正确。 | 尚未验证。 |
| G1：Desktop 退出 | Desktop 退出后同一个执行实例仍可由剩余端观察和控制。 | 尚未验证。涉及当前 Desktop 关闭须先确认具体影响；不在自动步骤中执行。 |
| G1：不同 Thread 并发 | 多个隔离 Thread 可独立运行、停止和处理审批。 | 尚未验证。单个探针仅指定一个测试 Thread，可用多实例分别观察。 |

若 G0 不成立，应记录具体版本与公开入口缺口。不得以两个探针成功、历史轮询、fork、交替接管或自建桌面替代官方 Desktop 后宣称目标实现。

G0/G1 核心项通过后，才进入 Agent 共享连接适配、订阅/提交分离、权威状态和审批恢复，以及 Relay/PWA 领域事件接入。G2 仍须使用真实 PWA 验证消息合并、断线恢复、权限撤销、幂等关联和用户可见实时行为。当前文档不是发布或部署说明。
