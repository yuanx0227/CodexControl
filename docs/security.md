# Security

## 1. 信任模型

- Windows Device 与浏览器 Controller 是长期身份主体；
- Relay 是公司自托管可信节点；
- 第一版依赖 TLS，不实现应用层 E2EE；
- OpenAI/Codex 身份始终留在 Device；
- 本地 TUI 只通过 loopback 连接 Agent。

### Codex Desktop 运行时

- 不附加、读取或控制 Desktop 已打开的会话；
- PATH 不可用时只从当前用户 AppX Package Repository 读取 `OpenAI.Codex_*` 的 `PackageRootFolder`；
- 严格校验目标仍位于 `WindowsApps\OpenAI.Codex_*`，只接受固定四个运行时文件；
- 运行时只复制到当前用户数据目录，不修改 WindowsApps；
- 以 Desktop 包版本隔离目录，并通过 staging directory + atomic move 发布；
- 每次启动仍执行 `--version`、`--remote` 与 app-server stdio 能力检查；
- 不复制或上传 Codex 配置、OpenAI API Key、Endpoint 或用户数据。

## 2. 密钥

### Device

- ECDSA P-256；
- private key 使用 Current User DPAPI；
- public/private 一致性在加载时固定时间验证；
- 首次并发创建使用原子 move，失败方重新加载获胜身份；
- 日志不输出 private key 或 DPAPI blob。

### Controller

- Web Crypto ECDSA P-256；
- 生成时只为导入短暂导出 PKCS#8，随后覆盖 JS Buffer；
- IndexedDB 保存不可导出的 private `CryptoKey`；
- public key 使用 65-byte uncompressed SEC1；
- Signature 使用 IEEE P1363 `R || S`。

## 3. Authentication

签名原文：

```text
codex-control-auth-v1
<role>
<principalId>
<challengeId>
<nonce-base64url>
<expiresAt-unix-ms>
```

Challenge：

- 256-bit CSPRNG；
- 15 秒 TTL；
- 绑定 WebSocket connection、role 与 principal；
- consume 时原子删除；
- replay 失败。

## 4. Pairing

- Code 使用 CSPRNG `000000–999999`；
- TTL 180 秒；
- 同 Device 最多一个活动 Session；
- 新 Session 使旧 Session 失效；
- Controller proof 失败五次后 consume；
- Claim IP/Code、Device Registration 和 Auth 都有限速；
- 活动 Code Lookup 与最终验证使用不同 HMAC 域；
- 数据库不保存明文 Code；
- Migration 无法重建旧 Lookup HMAC 时安全失效旧 Pairing Session。

## 5. Authorization

每次 Control 都重新读取有效 Pairing：

```text
view
steer
interrupt
approval
```

解除配对后新 Control 立即 `PERMISSION_DENIED`。Control Result 必须匹配 Relay 已路由的 `requestId + deviceId + controllerId`，Device 不能向任意 Controller 伪造 Result。

- `control.thread.list` 与 `control.thread.read` 必须具有 `view`；
- `control.thread.start` 与 `control.thread.resume` 必须具有 `steer`；
- 新会话目录必须是 Device 上真实存在的绝对路径；
- 创建/恢复强制 `approvalPolicy=untrusted` 与 `workspace-write` sandbox；
- 活动 Turn 存在时拒绝创建或恢复另一会话；
- Relay 只实时转发历史摘要和任务文本，不持久化其正文。

## 6. Approval

- 不自动批准；
- TUI/PWA first-valid-response-wins；
- 优先严格匹配 `availableDecisions`；
- 非法 Decision 不发送给 app-server；
- 第二响应明确拒绝；
- `serverRequest/resolved` 是最终清理信号；
- 日志/数据库只保留类型、ID、时间和结果元数据。

## 7. Transport

### Agent Local Proxy

- 只绑定 `127.0.0.1`；
- 带浏览器 `Origin` 的连接拒绝；
- 单 TUI；
- Text Frame、严格 UTF-8、消息大小和有界队列；
- 不暴露公网。

### Relay

- 生产只允许 nginx HTTPS/WSS；
- Relay 容器仅 `expose 8080`，Compose 不发布；
- nginx 设置 Upgrade/Connection 与 75 秒 read timeout；
- 浏览器 Origin 必须在白名单；
- 开发 `ws://` 必须显式开启且 PWA 只允许 localhost。

## 8. 数据最小化

Relay 允许持久化：

- public identity；
- Pairing/permission/revocation；
- HMAC pairing session；
- last seen；
- 最新 Snapshot 摘要；
- Control/Pairing audit metadata。

默认禁止持久化：

- OpenAI API Key；
- private key；
- 完整 Prompt/Agent Response；
- 完整 Shell Output；
- 完整 Diff/源码；
- 完整 Codex JSON-RPC 历史。

`thread/list` 的名称、预览和 cwd 仅作为配对 Controller 的实时 `control.result` 返回，Relay 数据库和结构化日志都不保存这些字段。

## 9. 日志

Agent rolling log：

- API Key/Bearer Redaction；
- 单字段长度限制；
- app-server stderr 只记录行数；
- 不记录协议正文。

Relay 使用结构化框架日志；EF 参数默认不输出值。生产应设置 `Logging:LogLevel:Default=Information` 或更高，不启用 Sensitive Data Logging。

## 10. Web

- private key 不可导出；
- Relay URL 生产必须 WSS；
- CSP 禁止第三方脚本、对象和 frame；
- Service Worker 只缓存 GET；
- UI 不接受 HTML 注入，React 默认转义；
- Approval Decision 作为 JSON 数据发送，不执行内容。

## 11. 已测试攻击面

- Challenge replay；
- 错误 ECDSA Signature；
- 错误/过期 Pairing Code；
- 五次错误 Pairing proof；
- 未配对 Controller 隔离；
- 撤销后权限拒绝；
- 重复/冲突 Control Request ID；
- Device 伪造 Control Result；
- 未授权历史读取/Thread 创建/Thread 恢复；
- 活动 Turn 并发创建拒绝；
- 本机项目路径不存在时失败关闭；
- WebSocket Origin 拒绝；
- 第二 TUI 拒绝；
- 无效 capability；
- 明文 Pairing Code 不落库；
- npm/NuGet 已知漏洞扫描。

## 12. 生产清单

- 使用受信任 CA 证书，不使用开发自签名证书；
- Pairing Secret 至少 32 个随机字符，通过 Secret Manager 注入；
- Relay 8080 不直接暴露；
- 配置准确的 `CODEX_CONTROL_ALLOWED_ORIGINS`；
- 备份/限制 SQLite volume 权限；
- 定期轮换 Pairing Secret（轮换会使未完成 Session 失效）；
- 监控 Auth/Pairing rate-limit 与异常 Control audit；
- 在真实公网、Android/iOS 和企业代理环境执行渗透与恢复测试。
