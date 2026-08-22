# Codex Control Relay Protocol v1

状态：MVP v1 实现与互操作基线。

## 1. 协议分层

Codex Control 有两套互不混用的协议：

1. **Codex App Server JSON-RPC**：只存在于 Agent 与本地 app-server/TUI 之间，字段以执行中的 Codex CLI Schema 为准。
2. **Codex Control Relay Protocol**：Device、Relay、Controller/PWA 之间的稳定 Domain Protocol，不暴露 Codex 原始 JSON-RPC。

Relay Protocol 版本为整数 `1`。新增可选字段不提升主版本；删除字段、改变语义或改变签名串必须提升主版本。

## 2. Transport

- Device 与 Controller 都使用 WSS。
- 生产环境拒绝 `ws://`。
- 一条 WebSocket text frame 承载一个 UTF-8 JSON message。
- 不支持 binary frame。
- 单条消息默认最大 1 MiB；部署可下调，不能静默上调到无限制。
- Relay、Device 和 Controller 都必须处理 Ping/Pong、graceful close 和半开连接。

建议 endpoint：

```text
/ws/device
/ws/controller
```

## 3. Envelope

```json
{
  "version": 1,
  "type": "device.status",
  "messageId": "019d0000-0000-7000-8000-000000000001",
  "requestId": null,
  "timestamp": 1787378400000,
  "deviceId": "dev_01J...",
  "controllerId": null,
  "payload": {}
}
```

字段：

| 字段 | 必填 | 说明 |
|---|---|---|
| `version` | 是 | 当前固定为 `1` |
| `type` | 是 | 集中定义的消息类型 |
| `messageId` | 是 | 每条消息唯一 UUID；用于去重和审计关联 |
| `requestId` | 否 | 请求/响应关联 ID；事件为 `null` |
| `timestamp` | 是 | 发送方 Unix milliseconds；不能替代 Relay 接收时间 |
| `deviceId` | 按类型 | Device 范围消息的目标或来源 |
| `controllerId` | 按类型 | Controller 范围消息的目标或来源 |
| `payload` | 是 | 类型化 DTO；不允许任意魔法字段 |

Relay 必须另记自己的 `receivedAt`，安全决策不能只相信客户端 `timestamp`。

## 4. 消息类型

### 4.1 Authentication

```text
auth.hello
auth.challenge
auth.response
auth.ok
device.register
device.registered
```

### 4.2 Pairing

```text
pairing.create
pairing.created
pairing.claim
pairing.completed
pairing.revoked
```

### 4.3 Presence 与状态

```text
device.online
device.offline
device.status
device.list
device.list.result
codex.snapshot
codex.event
heartbeat
heartbeat.ack
```

### 4.4 Control

```text
control.steer
control.interrupt
control.approval
control.thread.list
control.thread.read
control.thread.start
control.thread.resume
control.result
```

### 4.5 Error

```text
error
```

所有字符串常量必须在共享协议包中生成或集中维护，Agent、Relay 和 Web 不得各自复制。

## 5. 身份与公钥格式

### 5.1 Device

Device 首次启动生成：

```text
deviceId
ECDSA P-256 public/private key pair
```

- `deviceId` 是服务端不可猜测 ID，不从机器名派生。
- private key 使用 Windows DPAPI 保护，只保存在本机。
- Relay 保存 public key、名称和必要元数据。

### 5.2 Controller

Controller/PWA 首次启动使用 Web Crypto API 生成 ECDSA P-256 key pair：

- private key 应为不可导出 `CryptoKey`；
- key 保存在 IndexedDB；
- 清除浏览器数据后重新配对。

### 5.3 Public Key 编码

v1 统一使用 P-256 未压缩 SEC1 点：

```text
0x04 || X(32 bytes) || Y(32 bytes)
```

Envelope 中使用 base64url（无 `=` padding）。Relay 持久化时可以转换成平台内部格式，但 Wire 必须保持一致。

签名使用：

```text
ECDSA P-256
SHA-256
IEEE P1363 raw signature: R(32 bytes) || S(32 bytes)
base64url without padding
```

实现前必须用 Windows CNG、.NET 和 Web Crypto 三方固定测试向量完成互操作测试。

## 6. Challenge Authentication

### 6.1 Hello

```json
{
  "version": 1,
  "type": "auth.hello",
  "messageId": "uuid",
  "requestId": "auth-1",
  "timestamp": 1787378400000,
  "deviceId": "dev_01J...",
  "controllerId": null,
  "payload": {
    "role": "device",
    "clientVersion": "0.1.0"
  }
}
```

### 6.2 Challenge

```json
{
  "version": 1,
  "type": "auth.challenge",
  "messageId": "uuid",
  "requestId": "auth-1",
  "timestamp": 1787378400010,
  "deviceId": "dev_01J...",
  "controllerId": null,
  "payload": {
    "challengeId": "chl_01J...",
    "nonce": "base64url-32-random-bytes",
    "expiresAt": 1787378415010
  }
}
```

Challenge：

- nonce 至少 256 bit CSPRNG；
- 默认 TTL 15 秒；
- 一个 `challengeId` 只能成功一次；
- 成功、过期或失败次数超限后立即销毁；
- 与当前 WebSocket connection 绑定。

### 6.3 Signature Payload

签名原文是以下字段的 UTF-8，换行固定为 LF，末尾无换行：

```text
codex-control-auth-v1
<role>
<principalId>
<challengeId>
<nonce-base64url>
<expiresAt-unix-ms>
```

`principalId` 对 Device 为 `deviceId`，对 Controller 为 `controllerId`。

### 6.4 Response

```json
{
  "version": 1,
  "type": "auth.response",
  "messageId": "uuid",
  "requestId": "auth-1",
  "timestamp": 1787378400100,
  "deviceId": "dev_01J...",
  "controllerId": null,
  "payload": {
    "challengeId": "chl_01J...",
    "signature": "base64url-p1363-signature"
  }
}
```

Relay 验证成功后发送 `auth.ok`。认证前只允许 `auth.*` 和 Pairing Claim 所需的最小消息。

## 7. 六位配对

### 7.1 固定规则

```text
有效期：180 秒
最大尝试：5 次
同 Device 有效 Session：最多 1 个
成功后：立即销毁
重新生成：旧 Session 立即失效
```

Code 使用安全随机生成的 `000000` 到 `999999`，显示可格式化为 `583 271`，Wire 始终传六个 ASCII digit。

### 7.2 Pairing Create

已认证 Device 请求：

```json
{
  "type": "pairing.create",
  "requestId": "pair-1",
  "deviceId": "dev_01J...",
  "payload": {}
}
```

Relay 返回：

```json
{
  "type": "pairing.created",
  "requestId": "pair-1",
  "deviceId": "dev_01J...",
  "payload": {
    "code": "583271",
    "expiresAt": 1787378580000,
    "attemptsRemaining": 5
  }
}
```

Relay 数据库不得保存 `code` 明文。v1 使用两个分域 HMAC：

```text
codeHash = HMAC-SHA256(serverPairingSecret,
  "codex-control-pairing-v1\n" + pairingSessionId + "\n" + code)

codeLookupHash = HMAC-SHA256(serverPairingSecret,
  "codex-control-pairing-lookup-v1\n" + code)
```

`codeLookupHash` 用于定位活动 Session 和避免同时有效的六位码碰撞；`codeHash` 绑定 Session 做最终固定时间验证。数据库保存 `pairingSessionId`、两个 HMAC、`deviceId`、`expiresAt`、`attemptCount` 和 `createdAt`。

### 7.3 Pairing Claim

Controller 在本地生成身份后提交：

```json
{
  "type": "pairing.claim",
  "requestId": "claim-1",
  "controllerId": "ctl_01J...",
  "payload": {
    "code": "583271",
    "controllerName": "Yuanx Phone",
    "publicKey": "base64url-sec1-public-key",
    "proofNonce": "base64url-32-random-bytes",
    "proofSignature": "base64url-p1363-signature"
  }
}
```

`proofSignature` 的精确 canonical payload 在实现前由跨平台测试向量锁定。Claim 校验顺序必须避免泄露某个 Code 是否存在；外部统一返回 `PAIRING_INVALID`，内部审计可区分过期、尝试超限和签名失败。

成功时一个数据库事务内：

1. 锁定 Pairing Session；
2. 再次检查未过期、未销毁和尝试次数；
3. 验证 Controller key proof；
4. upsert Controller；
5. 创建 Pairing；
6. 标记 Session consumed；
7. 提交；
8. 向 Device 和 Controller 发送 `pairing.completed`。

## 8. Pairing Permission

v1 权限结构：

```json
{
  "view": true,
  "steer": true,
  "interrupt": true,
  "approval": true
}
```

默认全部为 `true`，但每个 Control 请求仍必须逐项检查。解除配对后立即拒绝新请求；已在途命令必须在执行前再次检查授权版本。

## 9. Presence 与 Heartbeat

Device 与 Controller 每 15 秒发送：

```json
{
  "type": "heartbeat",
  "messageId": "uuid",
  "deviceId": "dev_01J...",
  "payload": {
    "connectionId": "conn_01J...",
    "snapshotRevision": 42
  }
}
```

Relay 返回 `heartbeat.ack`。120 秒没有收到任何有效消息时回收失活连接；Device 被回收后广播 Offline，Controller 页面重新可见后自行认证恢复。

重连 backoff：

```text
Agent: 1s, 2s, 3s, 5s, 10s, 10s...
Controller: 0.5s, 1s, 2s, 5s, 10s, 10s...
```

每次加入 0% 到 20% 的正向 jitter，认证成功后重置 backoff。同一 `controllerId` 可以同时注册多个 `connectionId`；新标签页不得关闭旧标签页。Device 仍保持单活动连接，避免同一电脑重复执行 Control。

## 10. Codex Snapshot

```json
{
  "type": "codex.snapshot",
  "messageId": "uuid",
  "deviceId": "dev_01J...",
  "payload": {
    "revision": 42,
    "status": "RunningCommand",
    "activeThreadId": "thr_123",
    "activeTurnId": "turn_456",
    "startedAt": 1787378000000,
    "lastActivityAt": 1787378400000,
    "currentProject": "D:\\Projects\\MES",
    "currentActivity": "Running tests",
    "runningCommand": "dotnet test",
    "changedFiles": ["src/LoginService.cs"],
    "pendingApprovalCount": 0,
    "lastAgentMessage": "正在修复失败测试",
    "relayConnected": true
  }
}
```

要求：

- `revision` 对一个 Device 单调递增；
- 重连后先发 Snapshot，再发增量 Event；
- Relay 只缓存最新 Snapshot；
- `lastAgentMessage` 和路径必须执行长度限制和日志脱敏；
- Snapshot 不包含完整对话历史。

## 11. Domain Event

`codex.event.payload`：

```json
{
  "eventId": "evt_01J...",
  "revision": 43,
  "kind": "CommandStarted",
  "threadId": "thr_123",
  "turnId": "turn_456",
  "itemId": "item_789",
  "occurredAt": 1787378401000,
  "data": {
    "summary": "dotnet test",
    "cwd": "D:\\Projects\\MES"
  }
}
```

v1 Domain Event Kind：

```text
AgentStarted
AgentIdle
TurnStarted
TurnCompleted
AgentMessageDelta
AgentMessageCompleted
CommandStarted
CommandCompleted
FileChanged
ApprovalRequested
ApprovalResolved
UserInputRequested
ErrorOccurred
```

Relay 默认不持久化完整 `data`；审计只保留 event kind、标识符、时间、结果和必要摘要。

## 12. 真实会话控制

历史列表请求：

```json
{
  "type": "control.thread.list",
  "requestId": "ctlreq_01J...",
  "deviceId": "dev_01J...",
  "controllerId": "ctl_01J...",
  "payload": { "limit": 50, "cursor": null }
}
```

Device 调用 `thread/list`，按 `recency_at desc` 查询 `cli`、`vscode`、`appServer`、`exec` 和 `unknown` 来源。返回值只规范化 `threadId/name/preview/cwd/createdAt/updatedAt/status/sourceKind`，Relay 不持久化正文。该请求要求 `view` 权限。

读取指定历史会话：

```json
{
  "type": "control.thread.read",
  "requestId": "ctlreq_01J...",
  "deviceId": "dev_01J...",
  "controllerId": "ctl_01J...",
  "payload": { "threadId": "thr_123" }
}
```

Device 先调用 `thread/read(includeTurns=false)` 读取元数据，再尝试按 `sortDirection=desc` 分页调用 `thread/items/list`，直到得到最近 200 条用户/助手消息或达到分页上限。当前 Codex Desktop 若返回方法未支持，则兼容回退 `thread/read(includeTurns=true)`；Agent 允许最多 128MB 的本机 app-server 单行响应，但对外仍执行同样的最近 200 条、单条 20000 字符和总正文限制。命令输出、Diff、源码及原始 JSON-RPC 不进入 Domain Payload。该请求要求 `view` 权限，Relay 只转发并按既有短时幂等机制处理，不写入业务数据库。

新建会话并立即启动真实 Turn：

```json
{
  "type": "control.thread.start",
  "requestId": "ctlreq_01J...",
  "deviceId": "dev_01J...",
  "controllerId": "ctl_01J...",
  "payload": {
    "cwd": "D:\\Projects\\MES",
    "text": "修复登录模块并运行测试"
  }
}
```

Device 串行执行 `thread/start -> turn/start`。`cwd` 必须是 Device 上存在的绝对目录，任务文本非空且不超过 20000 字符。

恢复历史并启动后续 Turn：

```json
{
  "type": "control.thread.resume",
  "requestId": "ctlreq_01J...",
  "deviceId": "dev_01J...",
  "controllerId": "ctl_01J...",
  "payload": {
    "threadId": "thr_123",
    "text": "继续处理剩余失败测试"
  }
}
```

Device 串行执行 `thread/resume -> turn/start`。新建/恢复都要求 `steer` 权限，强制 `approvalPolicy=untrusted` 与 `sandbox=workspace-write`；若已有活动 Turn，返回 `TURN_ALREADY_ACTIVE`。

成功 `result`：

```json
{ "threadId": "thr_123", "turnId": "turn_456" }
```

## 13. Steer

Controller 请求：

```json
{
  "type": "control.steer",
  "requestId": "ctlreq_01J...",
  "deviceId": "dev_01J...",
  "controllerId": "ctl_01J...",
  "payload": {
    "threadId": "thr_123",
    "expectedTurnId": "turn_456",
    "text": "不要修改数据库结构，只调整业务层。"
  }
}
```

Device 必须在调用 app-server 前重新核对本地活动 Thread/Turn。成功只在 app-server `turn/steer` 返回相同 `turnId` 后报告。

不能在 Turn 已结束时隐式调用 `turn/start`。

## 14. Interrupt

```json
{
  "type": "control.interrupt",
  "requestId": "ctlreq_01J...",
  "deviceId": "dev_01J...",
  "controllerId": "ctl_01J...",
  "payload": {
    "threadId": "thr_123",
    "turnId": "turn_456"
  }
}
```

响应分两阶段：

1. app-server 接受 RPC：`control.result.status = "accepted"`；PWA 显示 `Interrupting`。
2. 收到 `turn/completed` 且状态 `interrupted`：发 Domain Event，PWA 显示 `Interrupted`。

若只完成第一阶段，不能显示任务已经停止。

## 15. Approval

Approval Requested Event 必须携带 app-server 实际字段的规范化子集：

```json
{
  "approvalId": "apr_01J...",
  "requestMethod": "item/commandExecution/requestApproval",
  "threadId": "thr_123",
  "turnId": "turn_456",
  "itemId": "item_789",
  "command": "git push origin main",
  "cwd": "D:\\Projects\\MES",
  "reason": "Codex needs network access",
  "availableDecisions": ["accept", "decline", "cancel"],
  "requestedAt": 1787378400000
}
```

Controller Response：

```json
{
  "type": "control.approval",
  "requestId": "ctlreq_01J...",
  "deviceId": "dev_01J...",
  "controllerId": "ctl_01J...",
  "payload": {
    "approvalId": "apr_01J...",
    "decision": "accept"
  }
}
```

Device 将 Relay `approvalId` 映射回内存中的 app-server Server Request ID。原始 Request ID 不发送给未授权 Controller，也不长期持久化。

first-valid-response-wins 必须使用原子状态转换：

```text
Pending -> Resolving -> Resolved
```

失败发送时可从 `Resolving` 回到 `Pending`，但同一时刻只能有一个发送者。

## 16. Control Result

```json
{
  "type": "control.result",
  "requestId": "ctlreq_01J...",
  "deviceId": "dev_01J...",
  "controllerId": "ctl_01J...",
  "payload": {
    "status": "succeeded",
    "code": null,
    "message": null,
    "result": {
      "turnId": "turn_456"
    }
  }
}
```

`status`：

```text
accepted
succeeded
failed
```

Relay 以 `requestId + deviceId + controllerId + control type` 建立五分钟幂等记录。相同请求完成后返回缓存 Result；相同 `requestId` 改用于另一 Control 时返回 `REQUEST_ID_CONFLICT`。Device 返回的 `control.result` 必须匹配 Relay 已路由记录，否则返回 `CONTROL_RESULT_UNEXPECTED` 且不转发给 Controller。

## 17. Error Codes

```text
CODEX_NOT_FOUND
CODEX_NOT_EXECUTABLE
CODEX_VERSION_UNSUPPORTED
APP_SERVER_START_FAILED
APP_SERVER_PROTOCOL_ERROR
APP_SERVER_DISCONNECTED
BRIDGE_OVERLOADED
REQUEST_ID_NAMESPACE_COLLISION
REQUEST_ID_REQUIRED
REQUEST_ID_CONFLICT
CONTROL_RESULT_UNEXPECTED
TURN_NOT_ACTIVE
TURN_MISMATCH
TURN_ALREADY_ACTIVE
THREAD_NOT_FOUND
THREAD_CONTROL_FAILED
PROJECT_PATH_INVALID
PROJECT_NOT_FOUND
RELAY_OFFLINE
AUTH_FAILED
AUTH_CHALLENGE_EXPIRED
AUTH_CHALLENGE_REPLAYED
PAIRING_EXPIRED
PAIRING_INVALID
PAIRING_RATE_LIMITED
CONTROLLER_NOT_PAIRED
PERMISSION_DENIED
APPROVAL_ALREADY_RESOLVED
APPROVAL_DECISION_UNAVAILABLE
APPROVAL_NOT_FOUND
PROTOCOL_VERSION_UNSUPPORTED
MESSAGE_TOO_LARGE
RATE_LIMITED
INTERNAL_ERROR
```

外部 `message` 是用户可理解文本，不能包含内部异常栈、Key、Token、完整命令输出或数据库细节。Relay 日志使用独立 correlation ID。

## 18. 去重与顺序

- Relay 对每个已认证 Principal 缓存滑动窗口内的 `messageId`，重复消息不重复执行。
- Control 命令以 `requestId` 幂等；同一 Controller + Device + requestId 必须返回第一次的最终结果。
- `revision` 只用于一个 Device 的 Snapshot/Event 顺序，不作为全局序号。
- PWA 发现 revision gap 时请求最新 Snapshot，不猜测缺失状态。
- 重连后旧连接的 `connectionId` 失效，旧连接消息不得覆盖新连接 Presence。

## 19. Rate Limit 基线

最低要求：

- Pairing Claim：按 IP、Code 和 ControllerId 限速；
- Device registration/enrollment：按 IP 和 installation ID 限速；
- Authentication failure：按 IP 和 Principal 限速；
- Control command：按 Pairing 和 Device 限速；
- 消息大小和每秒消息数限速。

具体数值在 Relay 压测后写入配置，不能硬编码在三个客户端中。

## 20. 数据保留

允许持久化：

- Device/Controller public identity；
- Pairing、权限、撤销状态；
- Pairing Session 安全摘要；
- last seen / presence metadata；
- 控制与审批审计元数据；
- 最新 Snapshot 的受限字段。

默认禁止持久化：

- OpenAI API Key 或完整 auth token；
- private key；
- 完整 prompt/agent response；
- 完整 shell output；
- 完整 diff 或项目代码；
- 完整 Codex 原始 JSON-RPC 历史。

## 21. v1 验证状态

已自动化验证：

- Windows/.NET 与 Browser Web Crypto ECDSA P1363 互操作；
- Pairing 并发 Claim 只有一个成功；
- Challenge replay、错误 Signature、TTL 和五次 Proof 失败；
- Control Request ID 幂等、冲突与 Result 关联；
- Device 断线重连和旧连接 fencing；
- 未配对 Controller 数据隔离；
- 撤销后权限拒绝；
- Snapshot revision 恢复；
- Agent/PWA reload authentication；
- Agent log Redaction 与协议正文抑制。

生产部署仍需补充真实弱网乱序、大量并发连接、正式证书/域名、Android/iOS 实机后台挂起和渗透测试。
