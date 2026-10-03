# Windows Desktop 临时共享接入实测

本轮用户已授权当前账户下的临时 loopback 服务、新测试 Thread 和一次 Desktop 退出/启动窗口，并允许仅在 Desktop 子进程设置 `CODEX_APP_SERVER_WS_URL`。这是隐藏入口的隔离兼容实验，不代表官方支持或双端聊天已经实现。SSH、私有 IPC、注入、绕过会话锁及自动审批仍不使用。

## 已核对的本机基线

2026-10-03 的只读记录：Desktop 包为 `OpenAI.Codex 26.930.3748.0`，其实际子进程后端为 `codex-cli 0.160.0`，PATH 也指向同一个镜像。现有 CodexControl Agent 的文件版本为 `0.6.0.0`，它另行启动 `0.159.0-alpha.12.1` stdio 后端。当前两端没有共享同一个执行实例。

上述是本次记录，不应硬编码为后续兼容保证。再次实测须记录新的 PID、创建时间、可执行文件和版本。Rust 可执行文件没有 PE 版本信息时，用该镜像的 `--version`，不能拿 PATH 的另一个程序代替。

最后一次准备入口时，Desktop 根 PID 为 `31396`，创建时间 UTC `2026-10-03T04:35:55.8524356Z`；其后端 PID 为 `31076`、父 PID 为 `31396`，版本仍为 `0.160.0`。早先 PID 已变化，不能复用旧 PID 作为入口参数。Electron 文件版本 `154.0.8037.98` 不是 Desktop 包版本或 Codex 后端版本。

## 已完成的必要预检

- 探针 Debug 编译通过，0 警告、0 错误；三个 PowerShell 脚本语法解析通过。未运行产品完整测试集。
- 空配置服务返回 `readOnly`，探针拒绝继续；没有为试验执行 Windows sandbox setup 或放宽权限。
- 当前账户的普通服务可返回符合要求的有效策略。服务按正常方式使用账户配置与认证，本工具不读取认证文件内容，不打印配置或服务原始日志。
- 新 Thread 显式使用 `historyMode: paginated`，检查实际策略、设置唯一名称，然后在创建连接仍存活时调用 `thread/read {includeTurns:true}`，校验仍然零 Turn。该公开调用会持久化初始记录，不发送 Prompt。
- 新增该持久化步骤后，另一探针进程的 `thread/resume` 与 `thread/turns/list` 成功，返回 `idle`、`workspaceWrite`、`untrusted`、`approvalsReviewer:user`、`controlAllowed:true`；随后 `idle-check` 返回一个已加载 Thread 且全部闲置。检查服务已关闭，无模型输入，Desktop 未由预检关闭或启动。

成功记录位于本机忽略目录 `out/probe-account-preflight-4996f2c7`，Thread ID 为 `01a10027-53ed-7c11-b705-aa83ab6edcae`。这只是公开协议预检，正式 Desktop 试验将另建目录和 Thread。失败预检留下的空 Thread 保留，没有删除历史。此前 `-32600` 原始错误未导出，因此不声称已精确确认其唯一原因。

源码依据固定为 OpenAI Codex `a956835d020762cb2b570053af06f643a11c0ecc`：[paginated 读取时持久化](https://github.com/openai/codex/blob/a956835d020762cb2b570053af06f643a11c0ecc/codex-rs/app-server/src/request_processors/thread_processor.rs#L3048-L3061)、[零 Turn 的 Persist 语义](https://github.com/openai/codex/blob/a956835d020762cb2b570053af06f643a11c0ecc/codex-rs/thread-store/src/local/live_writer.rs#L291-L299)。这些证据不证明隐藏 Desktop 入口可用。

## 运行与恢复

### 2026-10-03 首次 Desktop 试验失败与修复

用户运行 `20261003-c78854db` 后，临时服务与空 Thread 创建成功，但入口报告 `desktop_launch_failed`，清理被阻止；02、03 均报告 `SERVICE_IDENTITY_CHANGED`。这次未通过 Desktop 接入验收，不应继续使用已消耗的 01 入口重试。

已复现脚本缺陷：`ConvertFrom-Json` 将 `startedUtc` 解析为 `DateTime`，原代码再转 `[string]` 时丢失小数秒。manifest 原值为 `2026-10-03T05:13:51.1062449Z`，旧转换仅保留到 `05:13:51`，因此精确身份检查可能误报。02、03 已改为直接转换到 `DateTimeOffset`，保留完整 ticks，没有降低 PID、创建时间、镜像路径或监听归属门槛。用原 manifest 和当前诊断进程的 JSON 往返检查均通过。

本次后续只读检查时，原服务 PID `26556` 已不存在，端口 `13255` 无监听；没有为此执行停止或 Desktop 重启。旧 manifest 是失败时快照，不代表服务现在仍运行。Desktop 启动异常未保存，不能把时间转换缺陷当作启动失败的已证实原因。

启动器现记录失败步骤、异常类型、HRESULT、Win32 错误码和代码行号，不输出异常正文；并为启动瞬间暂不可读的进程镜像提供最多 3 秒的元数据重读。后者只是启动时序防护，尚未证明它解决了本次 Desktop 失败。

如果原 01 PowerShell 窗口仍在，可在该窗口直接执行下面脚本，提取保留的启动器错误元数据，无需重启或启动任何服务：

```powershell
& 'D:\Yuanx\Code\Ai\CodexControl\tools\Get-SharedDesktopTrialFailure.ps1'
```

脚本只筛选启动器的错误记录并输出类型、数字代码和行号，不输出原始错误文本、日志、命令行或环境。若原窗口已关闭或错误已过期，会明确报告无法取得；不得据此猜测失败原因。

用户随后确认原窗口已关闭。再次只读核实发现：当前 Desktop 已更新为 `26.930.3930.0`，根 PID 为 `19592`，创建时间 UTC `2026-10-03T05:18:07.9532937Z`；其后端 PID 为 `9656`，实际镜像仍报告 `codex-cli 0.160.0`。原 manifest 指向的旧 Desktop 与旧后端镜像均已不存在。这证明旧入口不可复用，但没有当时异常，不能断言镜像更新就是首次启动失败原因。

第二次入口已准备在 `out/shared-desktop-trial-launchers/20261003-retry-8625cb6b`，绑定上述当前实例；正式试验目录 `out/shared-desktop-trial/20261003-retry-8625cb6b` 尚未创建。未启动服务或重启 Desktop，等待用户确认第二次退出/启动窗口。入口新增退出后的镜像存在检查，以及启动 Desktop 前的再次检查；文件被更新移除时明确停止，不悄悄替换实验版本。只有 01 输出 `awaiting_manual_same_thread_check` 且在真实 Desktop 找到同一测试 ID 后才进入 02；失败时先保留 manifest 并诊断。脚本语法与快捷方式参数读回已检查，新 Desktop 版本的实际 WS 接入仍未验证。

1. 先完成当前 Desktop 的其他任务，保存工作。准备好的试验快捷方式应从资源管理器打开；它使用普通权限 PowerShell 7，独立于 Desktop 的进程树。也可在从开始菜单打开的 PowerShell 7 内直接运行试验脚本。不要从 Desktop 内置终端执行。
2. 控制器等待记录中的 Desktop 自然退出。阅读其窗口提示后，使用 Desktop 的正常退出操作；关一个窗口不一定代表应用退出。脚本不强制关闭 Desktop。
3. 控制器启动自己拥有的 `127.0.0.1` 临时 app-server，创建独立目录和无输入的新 Thread，并仅在新 Desktop 子进程设置隐藏 WS URL。原有配置、历史、认证和快捷方式不被改写。
4. 在 Desktop 内人工找到唯一测试名称，核对同一 Thread ID。先只发送固定标记聊天，不调用工具。新 ID 只由试验 Desktop 和探针操作，不在现有 Agent/PWA 中继续它。
5. 从资源管理器打开 `02-observe.lnk`，它调用 `Watch-SharedDesktopTrial.ps1` 并核对 manifest 中的服务身份、监听归属与目录。窗口内输入 `start 只回复 WEB_A，不调用工具`，检查 Desktop 活跃页面能否实时收到；再从 Desktop 发送含完整唯一名称和 `DESKTOP_B` 的消息，检查探针的 `userMessage`、`agentMessage`、Turn 事件及 `markerSeen`。不要在其他历史会话发送输入。再按验收表检查 Steer、拒绝/取消审批和停止。所有批准必须由能展示具体请求内容的界面人工完成；现有探针不提供 accept。
6. 完成后让测试 Turn 结束，再正常退出试验 Desktop。服务清理仅针对本次拥有的进程；Desktop 仍运行时不因超时结束服务。关闭本次探针后，通过公开接口检查所有已加载 Thread；状态不明或仍活动时保留服务并标记 `cleanup_blocked`，不能强行结束未知任务。列表与状态检查不是原子锁，因此不能在 Desktop 仍可发送消息时据此结束服务。
7. 探针输入 `quit`，从资源管理器打开 `03-restore.lnk`，再正常退出试验 Desktop。它调用 `Complete-SharedDesktopTrial.ps1` 等待 Desktop 退出，确认无活动任务或其他连接后清理本次服务，并在新 Desktop 子进程环境中移除隐藏变量后启动原版可执行文件。此操作不修改用户级或系统级环境。未改过的正常快捷方式继续保留；不迁移、删除或重命名历史。

`01-start.lnk` 调用 `Invoke-SharedDesktopTrial.ps1`；只运行一次，保留其控制器窗口直至恢复完成。快捷方式目录与尚未创建的正式试验目录分离，启动前不预建后者。若出现 `desktop_connection_uncertain`、找不到测试 Thread 或探针权限门禁拒绝，记录固定失败状态并走恢复入口，不回退到原 Agent、不覆盖权限、不在其他 Thread 尝试。

本次入口已生成但尚未运行：`out/shared-desktop-trial-launchers/20261003-c78854db`；正式试验将使用 `out/shared-desktop-trial/20261003-c78854db`。入口绑定上面记录的 Desktop 实例；若它再次重启，应重新生成入口。恢复脚本通过 `runner.claim` 等待启动脚本完成清理，避免双方同时检查或关闭服务。等待超过 45 秒返回 `RUNNER_CLEANUP_IN_PROGRESS` 并保留服务。由于子进程输出仍由控制器丢弃，提前关闭 01 可能使输出管道断开，不能当作安全清理方式。

临时 WS 服务无鉴权且仅绑定 loopback，其他本机进程也可能连接。默认测试窗口有上限；若活动任务阻止安全清理，必须先人工结束测试再完成服务清理，不能把控制器退出当作端点已经关闭。

## 证据与验收

### 后续只读澄清与设计结论（2026-10-03）

用户已明确确认人工批准后“审批卡片已经消失，运行状态已经结束”，补齐该次 Desktop UI 收敛证据。

进一步只读核实此前额外的 writableRoot：`C:\Users\Yx\.codex\visualizations\2026\10\03\01a10081-78c0-7851-a412-58e2b1febd88`，恰为当前 Thread 的 Desktop 可视化产物目录。目录及祖先无 reparse point，网络仍关闭、sandbox 仍为 workspaceWrite。当前包内静态 Turn 准备逻辑明确计算并加入这一专属目录，与公开接口通知和策略读回一致。它不在项目源码树内，但属于已查明的 Desktop 会话辅助范围，未发现此次新增其他项目目录或开放整个 `.codex` 的证据。

因此撤回“此变化阻断整个共享会话方案”的判断：原严格探针仍按其单目录规则拒绝控制，原结果不改写；产品设计应精确识别当前 Thread 的辅助目录，不把实验隔离门槛当成完整产品权限模型。已完成的改造设计和证据链见 [shared-session-redesign.md](shared-session-redesign.md)。本轮未改变探针策略、产品代码、权限或运行配置。

### Desktop 人工批准与探针收敛（2026-10-03）

后续反向输入：用户在该真实 Desktop 会话发送指定无工具标记消息，探针在原连接上实时收到 Turn `01a10084-f0c1-7ae2-b616-fdb94d6ce213` 的 userMessage（markerSeen）、agentMessage/delta、agentMessage 完成（41 字符、markerSeen）、turn/completed(completed) 与 idle；未以历史轮询代替事件。用户尚未明确确认上一审批卡片消失及界面结束状态。

本次 Desktop 输入前先收到 thread/settings/updated。完成后的有效策略检查显示 controlAllowed:false；进一步无权限覆盖的公开接口诊断确认 sandbox 仍为 workspaceWrite、networkAccess:false、cwd 仍为隔离目录，但 additional writableRoots 中 1 个根位于隔离目录之外，runtimeWorkspaceRoots 共 2 个、其中 1 个位于隔离目录之外。Desktop 发起 Turn 前后存在可写范围扩大，违反当前严格隔离门槛；未输出额外目录原始路径，未改变权限。证据位于同一测试目录的 `desktop-input-result.json`。该结果证明反向实时输入链路，但阻断继续在此 Thread 上执行探针控制测试，不能将双向消息通过升级为完整共享控制验收通过。

新隔离 Thread `01a10081-78c0-7851-a412-58e2b1febd88`（`CC_SHARED_UI_REMAINING_1003`）在真实 Desktop 打开后，探针再次读回严格 workspaceWrite/untrusted/user 策略及临时目录限制，控制门禁通过。探针发起 Turn `01a10082-9733-7b52-a7da-6c224619a87e`，请求只在隔离工作目录执行一次 `Set-Content -LiteralPath ./approval-check.txt -Value APPROVAL_TEST`；收到审批请求 `2` 和 waitingOnApproval，批准前文件不存在。

用户明确反馈“已批准，看到 APPROVAL_DONE”。探针实时收到该请求的 serverRequest/resolved、对应命令 item/completed、最终 turn/completed(completed) 和 idle；文件存在且内容与测试常量匹配。该证据支持真实 Desktop 人工批准后探针状态收敛，不涉及自动批准。Desktop 卡片是否消失、全量事件完整性和不同审批决定竞争仍未验证；不能仅凭单个文件存在断言命令绝无重复执行。结果保存于 `out/shared-desktop-trial/20261003-retry-8625cb6b/desktop-ui-bdec23ae0ae148da885b2251d41d4e86/manual-approval-result.json`。

### 生命周期、语义与审批补测（2026-10-03）

用户明确要求继续代测后，执行 `tools/Test-SharedSessionLifecycle.ps1`，全程只操作新建隔离 Thread。服务身份始终为 PID `12156`、创建时间 UTC `2026-10-03T05:34:52.6917820Z`。不停止服务或 Desktop、不自动批准请求、不持久化回复正文。两个独立探针进程使用公开接口参与同一测试 Thread。

| 检查 | 实测结果与边界 |
| --- | --- |
| Steer 实际效果 | 同一 Turn 中接受 Steer，最终完成回复包含新的唯一指令标记；通过。Steer 不等于立刻中止当前流式正文，立即停止应使用 interrupt。 |
| 进程真正退出 | 原探针进程 `24880` 完全退出后，另一探针仍收到同一 Turn 的后续流式事件，且可继续 Steer 到正常完成；服务身份不变。 |
| 新进程重连 | 新探针进程 `26000` 重新加入同一 Thread，严格策略与权威状态有效。此项不是仅执行 reconnect 命令。 |
| 不同 Thread 并发 | A 正在等待真实命令审批时，B 完成独立文字 Turn；通过。 |
| 未决审批重放 | 请求同时出现在两个探针；其中一个进程退出后，新进程重连仍收到原请求；通过。 |
| 取消竞争与收敛 | 两个独立客户端均实际发送同一请求的 cancel，观察客户端收到一次 resolved、一次 interrupted 终态，测试文件未写入；通过。未验证 accept，也未证明不同决定之间的完整 first-valid-response-wins 行为。 |
| 停止隔离 | A 被取消并结束后，B 仍有后续流式输出；B 直到收到自身 interrupt 才终止；通过。 |

证据目录：`lifecycle-0eeef1a2c9ed4c7dae1abce898af5c35` 保存 Steer、进程退出与重连结果；`lifecycle-a44cac107c464e41b53a3721b83175a3` 保存审批补测、并发与停止隔离结果。均在 `out/shared-desktop-trial/20261003-retry-8625cb6b/` 下，包含 `result.json` 与脱敏 `events.jsonl`。

保留失败证据：首轮 `lifecycle-cb79180b58ee4472aea394e39d15a695` 等待语义完成超时，已清理测试 Turn。随后限制每批读取量以避免输出处理延迟控制，缩短输入，相关检查通过。第二轮末尾报告 `DECLINE_NOT_SUPPORTED`：实际请求提供的负向选项只有 cancel，是测试对必须提供 decline 的错误假设，不是服务审批失败或探针漏解析；已只重跑相关审批检查并通过。

上述多探针结果不替代 Desktop 审批界面验收。真实 Desktop 退出与重连已在用户确认后通过 `tools/Test-SharedDesktopExit.ps1` 完成，结果如下；恢复原生模式仍经已有 03 流程。真实 Desktop 的人工审批、接受后的执行、竞争决定及完整界面收敛仍待验收。

### 真实 Desktop 退出与重连（2026-10-03）

用户正常退出原 Desktop PID `7036` 后，独立探针在同一 Thread `01a1007a-2cb5-73d3-a9c6-6483b11d3c92`、同一 Turn `01a1007a-31dc-77c1-938f-16123d053d5c` 继续操作；退出后的 Steer 生效并完成，服务身份未改变。控制器重新启动官方 Desktop，结果 `passed:true`、`desktopExited:true`、`continuedSameTurnAfterDesktopExit:true`、`serverIdentityUnchanged:true`、`desktopReconnected:true`，未自动审批。

用户返回后再次现场核对：服务仍为 PID `12156`、创建时间 UTC `2026-10-03T05:34:52.6917820Z`，持有 `127.0.0.1:9023`；新 Desktop PID `12364`、创建时间 UTC `2026-10-03T06:37:38.3137510Z`，其已建立的 TCP 连接指向该监听。服务创建时间与原 manifest 精确匹配；原 manifest 中旧 Desktop PID 不代表重连后的进程。此前识别的 Desktop 包版本为 `26.930.3930.0`、服务 CLI 版本为 `0.160.0`；此次读取的 ChatGPT.exe 文件版本 `154.0.8037.98` 不替代包版本，codex.exe 文件版本字段为空。

证据：`out/shared-desktop-trial/20261003-retry-8625cb6b/desktop-exit-6fe39e6afdb149ac938056d36528e61e/result.json` 与同目录脱敏事件。`desktopSameThreadUiVerified:false`：此项证明真实 Desktop 生命周期与同一服务重连，不证明重新打开同一 Thread 后的 UI 状态、全部正文/过程事件完整性或人工批准流程。测试控制台 01、04 仍需保留，避免关闭重定向输出管道；未经恢复流程不直接关闭服务。

### 自动 Steer / 停止检查（2026-10-03）

用户反馈输出时不便在控制台操作，并明确授权代测。现场读取原测试 Thread `01a10051-979f-7cc1-8e1d-a972ea6c6a81`：当时已闲置，但有效策略变为 `dangerFullAccess / never`，`controlAllowed:false`。这说明当时的控制门槛不满足；没有覆盖其权限，也不能凭这一快照断言此前每次操作失败都由此导致。

通过 `tools/Test-SharedSessionControls.ps1` 在同一服务 PID `12156` 下新建严格策略的隔离 Thread `01a10059-c9ba-73e0-ad4c-053a93c80790`，自动执行一次无工具文字生成检查：收到 `item/agentMessage/delta` 后发送 Steer，确认目标与响应均为 Turn `01a10059-ce7b-7f50-a203-b98a951e3b2b`；随后发送 interrupt，收到同一 Turn 的权威 `turn/completed`，状态为 `interrupted`；再次读回为 `idle`、无活动 Turn。`passed:true` 仅表示这些协议断言通过。

结果与脱敏事件位于 `out/shared-desktop-trial/20261003-retry-8625cb6b/controls-5716a2749fea4e549a6148a2fcb6c783/` 下的 `result.json`、`events.jsonl`。没有保存消息正文或原始服务日志，没有自动审批，没有重启/停止服务或 Desktop。自动探针已退出，测试 Turn 已停止。使用新输出目录编译，保留用户仍在运行的交互探针。

本次紧接 Steer 发送了 interrupt，未验证改写后的完整回复语义；没有 UI 自动操作，Desktop 画面一致性仍需实际观察。审批、整个客户端断开后继续、Desktop 真正退出/重连及不同 Thread 并发仍未完成验收。不得将上述成功升级为全部共享会话目标已完成。

2026-10-03 第二次试验已到达 `awaiting_manual_same_thread_check`。再次只读核对：服务 PID `12156`、创建时间 UTC `2026-10-03T05:34:52.6917820Z` 持有 `127.0.0.1:9023`，真实 Desktop PID `7036` 与探针 PID `19924` 各自的 TCP 连接均对应此服务。此项证明当前接入同一服务实例；仍不证明完整消息与生命周期验收。

测试 Thread `01a10042-157f-7a13-bc39-e3ea76eef4f7` 在创建时策略检查通过，但稍后无覆盖参数的公开重加入响应为：`workspaceWrite`、`on-request`、`user`、`networkAccess:false`、两个临时目录排除项均为 `false`，额外写目录为空且 runtime roots 匹配隔离目录。阻断原因是两项临时目录排除设置，不是 `on-request`。没有通过 resume 覆盖或放宽权限。安全摘要见试验目录下 `policy-diagnosis.json`。

探针补充了排除项诊断、`help` 命令和空行忽略；未知命令现在明确返回 `UNKNOWN_COMMAND_USE_HELP`。编译输出到 `out/shared-session-probe-policy-diagnostics`，保留仍运行的旧探针二进制。编译通过，0 警告、0 错误。

下一项候选是 `create-observe`：创建一个全新隔离 Thread 后保持创建连接，同时启动交互观察连接；两者均属于同一个探针进程，退出时一起关闭。它不修改已有测试 Thread，不发 Prompt，不重启服务。公开 0.160 源码在已有订阅者时保留加载中的 Thread，忽略不匹配的 resume 覆盖；无订阅者的闲置 Thread 则可能被重新加载。因此该顺序值得实测，但尚未证明本次策略变化一定由 Desktop resume 引发，也不保证 Desktop 后续 turn/start 不会再改变策略。

入口为第二次快捷方式目录的 `02b-new-subscribed-test.lnk`。原 02 输入 `quit` 后运行它，保留 01 和新窗口，再由真实 Desktop 打开新输出的唯一名称与 ID，输入 `status` 检查。只有 `controlAllowed:true` 才继续显式短消息测试；再次失败时保留证据，不迁就权限。这个入口已编译并做脚本语法检查，尚未执行或创建新 Thread。正式客户端退出验收必须退出整个探针进程，不能仅用 `reconnect` 代表整个客户端离线。

服务身份使用 PID、创建时间、镜像版本和监听归属联合确认。Desktop 的网络子进程也可能持有 WS 连接，应关联其父子关系。客户端 `_epoch`、Thread ID、历史目录、`loaded/list` 和 TCP 建连均不能单独证明全部目标成立。

进程输出和原始日志不落盘。结果目录只保存本次实验标识、已知路径、进程/连接元数据、探针的白名单事件摘要和失败类别。不得添加环境变量全集、认证内容、消息正文、Shell 输出或原始 JSON-RPC。

| 项目 | 通过标准 |
| --- | --- |
| 接入 | Desktop 与探针的连接对应同一 app-server 进程身份；未静默回到第二个 stdio 执行者。 |
| 双向消息 | 活跃页面实时显示另一端输入及响应，不需要重开；按 Thread/Turn/Item 关联，无重复和缺失。 |
| 执行中输入 | 同一活动 Turn 上的 Steer 生效；过期 expectedTurnId 明确失败，不盲目改 API 或重发。 |
| 审批 | 同一请求在两端出现；人工决定后 resolved 收敛。完整批准界面和竞争验收仍需后续补齐。 |
| 停止及完成 | 对应 Turn 的权威终态一致，RPC 接受停止不等于已经停止。 |
| 退出与重连 | 真正退出任一客户端后，服务身份不变，另一端任务继续；恢复后状态及未决审批正确。 |
| 不同 Thread | A 执行/等审批时 B 可以推进，停止 A 不影响 B。 |

当前探针输出摘要，不证明全部正文或全部过程事件完整。上述内容保留探针阶段证据；后续用户确认启动产品改造后的实现与新增测试见 [共享会话实施](shared-session-implementation.md)。探针结果不直接替代新产品的真实Desktop/网页交叉验收。测试失败不得禁用 `codex_app`、改全局权限或加入兼容性补丁以隐藏失败。
