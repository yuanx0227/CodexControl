import { expect, test, type Page, type WebSocketRoute } from '@playwright/test';

interface CapturedMessage {
  type: string;
  requestId?: string;
  deviceId?: string;
  controllerId?: string;
  payload: Record<string, unknown>;
}

const deviceId = 'dev_1234567890abcdef1234567890abcdef';

async function installRelayMock(page: Page, options: {
  activeTurn?: boolean;
  disconnectThreadListOnce?: boolean;
  disconnectThreadReadOnce?: boolean;
  desktopOwnedActiveTurn?: boolean;
  extraSingletonProjects?: number;
  historyEntryCount?: number;
  activeThreadIds?: string[];
  newThreadReadFailures?: number;
  richHistory?: boolean;
  registeredControllerWithoutPairing?: boolean;
} = {}) {
  const activeTurn = options.activeTurn ?? true;
  let desktopTurnStartedAt = options.desktopOwnedActiveTurn
    ? Math.floor((Date.now() - 27_000) / 1_000)
    : undefined;
  const captured: CapturedMessage[] = [];
  let paired = false;
  let connectionCount = 0;
  let disconnectThreadListOnce = options.disconnectThreadListOnce ?? false;
  let disconnectThreadReadOnce = options.disconnectThreadReadOnce ?? false;
  let newThreadReadFailures = options.newThreadReadFailures ?? 0;
  const activeThreadIds = options.activeThreadIds ?? (activeTurn ? ['thr-1'] : []);
  const activeTurns = new Map(activeThreadIds.map((threadId, index) => [threadId, {
    threadId,
    turnId: `turn-${index + 1}`,
    status: 'Thinking',
    startedAt: Date.now() - (index + 1) * 60_000,
    lastActivityAt: Date.now(),
    currentProject: 'D:\\Projects\\MES',
    currentActivity: 'Running tests',
    runningCommand: 'dotnet test',
    changedFiles: ['src/LoginService.cs'],
    pendingApprovalCount: 0,
    lastAgentMessage: '正在修复失败测试',
  }]));
  const threadSummaries = [{
    threadId: 'thr-history-1',
    name: '历史测试会话',
    preview: '继续修复登录模块',
    cwd: 'D:\\Projects\\MES',
    createdAt: 1_730_831_111,
    updatedAt: 1_730_832_222,
    recencyAt: 1_730_832_500,
    status: 'notLoaded',
    sourceKind: 'appServer',
    projectId: 'project-mes',
  }, {
    threadId: 'thr-history-2',
    name: 'MES 第二个会话',
    preview: '检查 MES 接口',
    cwd: 'D:\\Projects\\MES',
    createdAt: 1_730_811_111,
    updatedAt: 1_730_812_222,
    recencyAt: 1_730_833_000,
    status: 'notLoaded',
    sourceKind: 'appServer',
    projectId: 'project-mes',
  }, {
    threadId: 'thr-vision-1',
    name: 'Vision 相机会话',
    preview: '检查相机连接',
    cwd: 'C:\\Users\\Yx\\Documents\\Codex\\2026-08-19\\vision-session',
    createdAt: 1_730_711_111,
    updatedAt: 1_730_712_222,
    recencyAt: 1_730_831_000,
    status: 'notLoaded',
    sourceKind: 'appServer',
    projectId: undefined,
  }];
  for (let index = 0; index < (options.extraSingletonProjects ?? 0); index += 1) {
    threadSummaries.push({
      threadId: `thr-singleton-${index}`,
      name: `Singleton 会话 ${index}`,
      preview: `单会话项目 ${index}`,
      cwd: `C:\\Users\\Yx\\Documents\\Codex\\2026-08-${String(10 + (index % 10)).padStart(2, '0')}\\singleton-${index}`,
      createdAt: 1_730_600_000 - index,
      updatedAt: 1_730_700_000 - index,
      recencyAt: 1_730_800_000 - index,
      status: 'notLoaded',
      sourceKind: 'appServer',
      projectId: undefined,
    });
  }
  const historyEntries = options.richHistory
    ? [{
        itemId: 'history-rich-user',
        turnId: 'history-turn-0',
        role: 'user',
        text: '# Files mentioned by the user:\n\n- sample.png\n\n## My request:\n\n请分析这张图片',
        phase: undefined,
        attachments: [{
          kind: 'image',
          name: 'sample.png',
          mimeType: 'image/png',
          dataUrl: 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=',
        }],
        changes: [],
      }, {
        itemId: 'history-rich-tool',
        turnId: 'history-turn-0',
        role: 'tool',
        text: 'dotnet test',
        phase: 'completed',
        attachments: [],
        changes: [{ path: 'src/App.tsx', kind: 'update', additions: 18, deletions: 4 }],
      }, {
        itemId: 'history-rich-assistant',
        turnId: 'history-turn-0',
        role: 'assistant',
        text: '## 结论\n\n- **Markdown 已解析**\n- `图片已显示`',
        phase: 'final_answer',
        attachments: [],
        changes: [],
      }]
    : Array.from({ length: options.historyEntryCount ?? 2 }, (_, index) => ({
        itemId: `history-item-${index}`,
        turnId: `history-turn-${Math.floor(index / 2)}`,
        role: index % 2 === 0 ? 'user' : 'assistant',
        text: index === 0
          ? '请继续修复登录模块'
          : index === 1
            ? '登录模块的历史修复已经完成'
            : `历史长消息 ${index} ${'内容'.repeat(180)}`,
        phase: index % 2 === 0 ? undefined : 'final_answer',
        attachments: [],
        changes: [],
      }));
  let reconciledHistoryText: string | undefined;
  let route: WebSocketRoute | undefined;
  await page.routeWebSocket('**/ws/controller', (socket) => {
    connectionCount += 1;
    route = socket;
    const sendActiveSnapshot = () => {
      const currentActiveTurns = [...activeTurns.values()];
      const primaryActive = currentActiveTurns[0];
      socket.send(JSON.stringify(reply('codex.snapshot', undefined, {
        revision: 10 + currentActiveTurns.length,
        status: primaryActive?.status ?? 'Idle',
        activeThreadId: primaryActive?.threadId,
        activeTurnId: primaryActive?.turnId,
        startedAt: primaryActive?.startedAt,
        lastActivityAt: Date.now(),
        currentProject: primaryActive?.currentProject ?? 'D:\\Projects\\MES',
        currentActivity: primaryActive?.currentActivity,
        runningCommand: primaryActive?.runningCommand,
        changedFiles: primaryActive?.changedFiles ?? [],
        pendingApprovalCount: 0,
        lastAgentMessage: primaryActive?.lastAgentMessage,
        agentVersion: '0.7.0-test',
        activeTurns: currentActiveTurns,
      }, deviceId)));
    };
    socket.onMessage((message) => {
      const envelope = JSON.parse(String(message)) as CapturedMessage;
      captured.push(envelope);
      switch (envelope.type) {
        case 'auth.hello':
          if (paired || options.registeredControllerWithoutPairing) {
            socket.send(JSON.stringify(reply('auth.ok', envelope.requestId, {
              role: 'controller',
              principalId: envelope.controllerId,
              connectionId: `conn-${connectionCount}`,
              serverVersion: '0.7.0-test',
            }, undefined, envelope.controllerId)));
          } else {
            socket.send(JSON.stringify(reply('error', envelope.requestId, {
              code: 'AUTH_FAILED',
              message: 'Controller is not paired yet.',
            })));
          }
          break;
        case 'pairing.claim':
          socket.send(JSON.stringify(reply('pairing.pending', undefined, {
            pairingRequestId: 'preq-browser-mock',
            deviceId,
            controllerId: envelope.controllerId,
            expiresAt: Date.now() + 60_000,
          }, deviceId, envelope.controllerId)));
          paired = true;
          socket.send(JSON.stringify(reply('pairing.completed', envelope.requestId, {
            deviceId,
            controllerId: envelope.controllerId,
            permissions: { view: true, steer: true, interrupt: true, approval: true },
            serverVersion: '0.7.0-test',
          }, deviceId, envelope.controllerId)));
          break;
        case 'device.list': {
          const currentActiveTurns = [...activeTurns.values()];
          const primaryActive = currentActiveTurns[0];
          socket.send(JSON.stringify(reply('device.list.result', envelope.requestId, {
            devices: paired ? [{
              deviceId,
              name: 'DEV-PC-01',
              online: true,
              lastSeenAt: Date.now(),
              snapshot: {
                revision: 7,
                status: primaryActive?.status ?? 'Idle',
                activeThreadId: primaryActive?.threadId,
                activeTurnId: primaryActive?.turnId,
                startedAt: primaryActive?.startedAt,
                lastActivityAt: Date.now(),
                currentProject: 'D:\\Projects\\MES',
                currentActivity: primaryActive?.currentActivity,
                runningCommand: primaryActive?.runningCommand,
                changedFiles: ['src/LoginService.cs'],
                pendingApprovalCount: 0,
                lastAgentMessage: '正在修复失败测试',
                agentVersion: '0.7.0-test',
                activeTurns: currentActiveTurns,
              },
            }] : [],
          }, undefined, envelope.controllerId)));
          break;
        }
        case 'control.steer':
        case 'control.interrupt':
        case 'control.approval':
          socket.send(JSON.stringify(reply('control.result', envelope.requestId, {
            status: envelope.type === 'control.interrupt' ? 'accepted' : 'succeeded',
          }, envelope.deviceId, envelope.controllerId)));
          break;
        case 'control.session.options':
          socket.send(JSON.stringify(reply('control.result', envelope.requestId, {
            status: 'succeeded',
            result: {
              models: [{
                id: 'gpt-5.6-sol',
                model: 'gpt-5.6-sol',
                displayName: 'GPT-5.6 Sol',
                description: 'Frontier coding model',
                isDefault: true,
              }, {
                id: 'gpt-5.6-terra',
                model: 'gpt-5.6-terra',
                displayName: 'GPT-5.6 Terra',
                description: 'Balanced coding model',
                isDefault: false,
              }],
              approvalPolicies: [{
                id: 'untrusted',
                displayName: '严格审批',
                description: 'Strict approvals',
                isDefault: true,
              }, {
                id: 'on-request',
                displayName: '按需审批',
                description: 'Ask when needed',
                isDefault: false,
              }, {
                id: 'never',
                displayName: '不发起审批',
                description: 'Fail outside the sandbox',
                isDefault: false,
              }],
            },
          }, envelope.deviceId, envelope.controllerId)));
          break;
        case 'control.thread.list':
          if (disconnectThreadListOnce) {
            disconnectThreadListOnce = false;
            void socket.close({ code: 1012, reason: 'test relay restart during thread list' });
            break;
          }
          socket.send(JSON.stringify(reply('control.result', envelope.requestId, {
            status: 'succeeded',
            result: {
              threads: threadSummaries,
              projects: [{
                projectId: 'project-vision-workspace',
                name: 'Vision Workspace',
                position: 0,
                roots: ['D:\\Projects\\VisionWorkspace'],
              }, {
                projectId: 'project-mes',
                name: 'MES',
                position: 1,
                roots: ['D:\\Projects\\MES'],
              }],
            },
          }, envelope.deviceId, envelope.controllerId)));
          break;
        case 'control.thread.read': {
          if (disconnectThreadReadOnce) {
            disconnectThreadReadOnce = false;
            void socket.close({ code: 1012, reason: 'test relay restart during thread read' });
            break;
          }
          const threadId = String(envelope.payload.threadId);
          if (threadId === 'thr-created' && newThreadReadFailures > 0) {
            newThreadReadFailures -= 1;
            socket.send(JSON.stringify(reply('control.result', envelope.requestId, {
              status: 'failed',
              code: 'THREAD_READ_FAILED',
              message: 'new thread history is not indexed yet',
            }, envelope.deviceId, envelope.controllerId)));
            break;
          }
          socket.send(JSON.stringify(reply('control.result', envelope.requestId, {
            status: 'succeeded',
            result: {
              threadId,
              name: threadId === 'thr-history-1' ? '历史测试会话' : '测试会话',
              cwd: threadId === 'thr-vision-1' ? 'D:\\Projects\\Vision' : 'D:\\Projects\\MES',
              entries: reconciledHistoryText ? [...historyEntries, {
                itemId: 'history-reconciled-live',
                turnId: 'turn-1',
                role: 'assistant',
                text: reconciledHistoryText,
                phase: 'commentary',
                attachments: [],
                changes: [],
              }] : historyEntries,
              turns: desktopTurnStartedAt !== undefined && threadId === 'thr-history-1'
                ? [{
                    turnId: 'desktop-turn-active',
                    status: 'interrupted',
                    startedAt: desktopTurnStartedAt,
                  }]
                : [{
                    turnId: 'history-turn-0',
                    status: 'completed',
                    startedAt: 1_730_831_000,
                    completedAt: 1_730_831_096,
                    durationMs: 96_000,
                  }],
              truncated: false,
            },
          }, envelope.deviceId, envelope.controllerId)));
          break;
        }
        case 'control.thread.start': {
          socket.send(JSON.stringify(reply('control.result', envelope.requestId, {
            status: 'succeeded',
            result: { threadId: 'thr-created', turnId: 'turn-created' },
          }, envelope.deviceId, envelope.controllerId)));
          activeTurns.set('thr-created', {
            threadId: 'thr-created',
            turnId: 'turn-created',
            status: 'Thinking',
            startedAt: Date.now(),
            lastActivityAt: Date.now(),
            currentProject: String(envelope.payload.cwd),
            currentActivity: 'Codex is thinking',
            runningCommand: undefined,
            changedFiles: [],
            pendingApprovalCount: 0,
            lastAgentMessage: undefined,
          });
          sendActiveSnapshot();
          break;
        }
        case 'control.thread.resume': {
          const threadId = String(envelope.payload.threadId);
          socket.send(JSON.stringify(reply('control.result', envelope.requestId, {
            status: 'succeeded',
            result: { threadId, turnId: 'turn-resumed' },
          }, envelope.deviceId, envelope.controllerId)));
          activeTurns.set(threadId, {
            threadId,
            turnId: 'turn-resumed',
            status: 'Thinking',
            startedAt: Date.now(),
            lastActivityAt: Date.now(),
            currentProject: 'D:\\Projects\\MES',
            currentActivity: 'Codex is thinking',
            runningCommand: undefined,
            changedFiles: [],
            pendingApprovalCount: 0,
            lastAgentMessage: undefined,
          });
          sendActiveSnapshot();
          break;
        }
        case 'pairing.revoke':
          socket.send(JSON.stringify(reply('pairing.revoked', envelope.requestId, envelope.payload, envelope.deviceId, envelope.controllerId)));
          break;
      }
    });
  });

  return {
    captured,
    sendDomainEvent(kind: string, data: Record<string, unknown>, threadId = 'thr-1', turnId = 'turn-1', itemId?: string) {
      if (!route) throw new Error('WebSocket mock is not connected');
      route.send(JSON.stringify(reply('codex.event', undefined, {
        eventId: `evt-sync-${crypto.randomUUID()}`, revision: 20, kind, threadId, turnId, itemId,
        occurredAt: Date.now(), data,
      }, deviceId)));
    },
    getConnectionCount: () => connectionCount,
    sendApproval() {
      if (!route) throw new Error('WebSocket mock is not connected');
      route.send(JSON.stringify(reply('codex.event', undefined, {
        eventId: 'evt-approval',
        revision: 8,
        kind: 'ApprovalRequested',
        threadId: 'thr-1',
        turnId: 'turn-1',
        itemId: 'item-1',
        occurredAt: Date.now(),
        data: {
          approvalId: 'apr-1',
          requestMethod: 'item/commandExecution/requestApproval',
          threadId: 'thr-1',
          turnId: 'turn-1',
          itemId: 'item-1',
          command: 'git push origin main',
          cwd: 'D:\\Projects\\MES',
          reason: '需要网络权限',
          availableDecisions: ['accept', 'decline'],
          requestedAt: Date.now(),
        },
      }, deviceId)));
    },
    sendAgentDelta(delta: string, itemId = 'item-live', threadId = 'thr-1') {
      if (!route) throw new Error('WebSocket mock is not connected');
      route.send(JSON.stringify(reply('codex.event', undefined, {
        eventId: `evt-delta-${crypto.randomUUID()}`,
        revision: 9,
        kind: 'AgentMessageDelta',
        threadId,
        turnId: threadId === 'thr-1' ? 'turn-1' : `turn-${threadId}`,
        itemId,
        occurredAt: Date.now(),
        data: { delta },
      }, deviceId)));
    },
    sendCommand(command: string) {
      if (!route) throw new Error('WebSocket mock is not connected');
      route.send(JSON.stringify(reply('codex.event', undefined, {
        eventId: `evt-command-${crypto.randomUUID()}`,
        revision: 9,
        kind: 'CommandCompleted',
        threadId: 'thr-1',
        turnId: 'turn-1',
        itemId: 'item-command-live',
        occurredAt: Date.now(),
        data: { command, status: 'completed' },
      }, deviceId)));
    },
    sendFileChanged() {
      if (!route) throw new Error('WebSocket mock is not connected');
      route.send(JSON.stringify(reply('codex.event', undefined, {
        eventId: `evt-file-${crypto.randomUUID()}`,
        revision: 9,
        kind: 'FileChanged',
        threadId: 'thr-1',
        turnId: 'turn-1',
        itemId: 'item-file-live',
        occurredAt: Date.now(),
        data: {
          status: 'completed',
          paths: ['src/App.tsx', 'src/relayClient.ts'],
          changes: [
            { path: 'src/App.tsx', kind: 'update', additions: 12, deletions: 3 },
            { path: 'src/relayClient.ts', kind: 'update', additions: 7, deletions: 1 },
          ],
        },
      }, deviceId)));
    },
    publishHistoryText(text: string) {
      reconciledHistoryText = text;
    },
    startDesktopTurn() {
      desktopTurnStartedAt = Math.floor(Date.now() / 1_000);
    },
    sendTurnStarted(threadId = 'thr-1') {
      if (!route) throw new Error('WebSocket mock is not connected');
      route.send(JSON.stringify(reply('codex.event', undefined, {
        eventId: `evt-turn-started-${crypto.randomUUID()}`,
        revision: 10,
        kind: 'TurnStarted',
        threadId,
        turnId: threadId === 'thr-1' ? 'turn-1' : `turn-${threadId}`,
        occurredAt: Date.now(),
        data: { startedAt: Date.now() },
      }, deviceId)));
    },
    sendAgentCompleted(text: string, itemId = 'item-live', threadId = 'thr-1') {
      if (!route) throw new Error('WebSocket mock is not connected');
      route.send(JSON.stringify(reply('codex.event', undefined, {
        eventId: `evt-completed-${crypto.randomUUID()}`,
        revision: 10,
        kind: 'AgentMessageCompleted',
        threadId,
        turnId: threadId === 'thr-1' ? 'turn-1' : `turn-${threadId}`,
        itemId,
        occurredAt: Date.now(),
        data: { text },
      }, deviceId)));
    },
    sendTurnCompleted(threadId = 'thr-1') {
      if (!route) throw new Error('WebSocket mock is not connected');
      route.send(JSON.stringify(reply('codex.event', undefined, {
        eventId: `evt-turn-completed-${crypto.randomUUID()}`,
        revision: 11,
        kind: 'TurnCompleted',
        threadId,
        turnId: threadId === 'thr-1' ? 'turn-1' : `turn-${threadId}`,
        occurredAt: Date.now(),
        data: { status: 'completed', durationMs: 96_000 },
      }, deviceId)));
    },
  };
}

function reply(
  type: string,
  requestId: string | undefined,
  payload: unknown,
  targetDeviceId?: string,
  controllerId?: string,
) {
  return {
    version: 2,
    type,
    messageId: crypto.randomUUID(),
    requestId,
    timestamp: Date.now(),
    deviceId: targetDeviceId,
    controllerId,
    payload,
  };
}

async function pair(page: Page) {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: '连接你的电脑' })).toBeVisible();
  await page.getByLabel('六位配对码').fill('123456');
  await page.getByRole('button', { name: '配对', exact: true }).click();
  await expect(page.getByRole('button', { name: '打开 DEV-PC-01' })).toBeVisible({ timeout: 20_000 });
}

test('pairs with a signed proof and renders the recovered device snapshot', async ({ page }) => {
  const relay = await installRelayMock(page);
  await pair(page);

  const claim = relay.captured.find((message) => message.type === 'pairing.claim');
  expect(claim).toBeTruthy();
  expect(claim?.payload.code).toBe('123456');
  expect(String(claim?.payload.controllerId)).toMatch(/^ctl_[a-f0-9]{32}$/u);
  expect(String(claim?.payload.publicKey).length).toBeGreaterThan(80);
  expect(String(claim?.payload.proofSignature).length).toBeGreaterThan(80);
  await expect(page.getByText('Web v0.7.0 · Relay v0.7.0-test · Protocol v2', { exact: true })).toBeVisible();

  await page.getByRole('button', { name: '打开 DEV-PC-01' }).click();
  await expect(page.getByRole('main').getByText('D:\\Projects\\MES', { exact: true })).toBeVisible();
  await expect(page.getByText('dotnet test')).toBeVisible();
  await expect(page.getByText('正在修复失败测试')).toBeVisible();
  await expect(page.getByText('Agent v0.7.0-test', { exact: true })).toBeVisible();
});

test('re-pairs an authenticated Controller whose old Pairing was removed', async ({ page }) => {
  const relay = await installRelayMock(page, { registeredControllerWithoutPairing: true });
  await page.goto('/');
  await page.getByLabel('六位配对码').fill('123456');
  await page.getByRole('button', { name: '配对', exact: true }).click();
  await expect(page.getByRole('button', { name: '打开 DEV-PC-01' })).toBeVisible({ timeout: 20_000 });
  expect(relay.getConnectionCount()).toBeGreaterThanOrEqual(2);
});

test('confirms QR Relay summary before applying the one-time code', async ({ page }) => {
  await installRelayMock(page);
  await page.goto('/#/pair?relay=aHR0cDovLzEyNy4wLjAuMTo0MTcz&code=123456&device=DEV-QR');
  await expect(page.getByText('127.0.0.1:4173', { exact: true })).toBeVisible();
  await expect(page.getByText('DEV-QR', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: '确认 Relay 并继续' }).click();
  await expect(page.getByLabel('六位配对码')).toHaveValue('123 456');
  await expect(page).not.toHaveURL(/code=123456/u);
  await expect(page.getByLabel('控制端名称')).not.toHaveValue('');
});

test('restores the last selected computer after reload', async ({ page }) => {
  await installRelayMock(page);
  await page.goto('/');
  await pair(page);
  await page.getByRole('button', { name: '打开 DEV-PC-01' }).click();
  await expect(page.getByRole('button', { name: '停止当前任务' })).toBeVisible();
  await page.reload();
  await expect(page.getByRole('button', { name: '停止当前任务' })).toBeVisible({ timeout: 20_000 });
});

test('sends steer, interrupt and the exact available approval decision', async ({ page }) => {
  const relay = await installRelayMock(page);
  await pair(page);
  await page.getByRole('button', { name: '打开 DEV-PC-01' }).click();

  await page.getByPlaceholder('不要修改数据库结构，只调整业务层。').fill('先修复失败测试');
  await page.getByRole('button', { name: '发送 Steer' }).click();
  await expect.poll(() => relay.captured.some((message) => message.type === 'control.steer')).toBe(true);

  const interruptButton = page.getByRole('button', { name: '停止当前任务' });
  await expect(interruptButton).toBeEnabled();
  await interruptButton.click({ force: true });
  await expect.poll(() => relay.captured.some((message) => message.type === 'control.interrupt')).toBe(true);

  relay.sendApproval();
  const approval = page.getByRole('region', { name: '等待审批' });
  await expect(approval).toBeVisible();
  await expect(approval.getByText('git push origin main')).toBeVisible();
  const allowButton = page.getByRole('button', { name: '允许一次' });
  await expect(allowButton).toBeEnabled();
  await allowButton.click({ force: true });
  await expect.poll(() => relay.captured.some((message) =>
    message.type === 'control.approval' && message.payload.decision === 'accept',
  )).toBe(true);
});

test('streams the active Codex reply with a typewriter and finalizes it', async ({ page }) => {
  const relay = await installRelayMock(page);
  await pair(page);
  await page.getByRole('button', { name: '打开 DEV-PC-01' }).click();

  const initialReadCount = relay.captured.filter((message) => message.type === 'control.thread.read').length;
  const initialListCount = relay.captured.filter((message) => message.type === 'control.thread.list').length;
  relay.sendCommand('dotnet test');
  relay.sendFileChanged();
  relay.sendAgentDelta('第一段');
  relay.sendAgentDelta('第二段');

  await expect(page.locator('.typing-caret')).toBeVisible();
  await expect(page.getByText('第一段第二段', { exact: true })).toBeVisible({ timeout: 5_000 });
  relay.sendAgentCompleted('第一段第二段，回复完成');
  await expect(page.getByText('第一段第二段，回复完成', { exact: true })).toBeVisible();
  await expect(page.locator('.typing-caret')).toHaveCount(0);

  relay.sendTurnCompleted();
  const liveSummary = page.locator('.process-summary').filter({ hasText: 'dotnet test' });
  await expect(liveSummary).not.toHaveAttribute('open', '');
  await expect(liveSummary.getByText('耗时 1分36秒', { exact: true })).toBeVisible();
  await expect(liveSummary.getByText('影响 2 个文件 · +19 -4', { exact: true })).toBeVisible();
  await expect.poll(() => relay.captured.filter((message) => message.type === 'control.thread.read').length)
    .toBeGreaterThan(initialReadCount);
  await expect.poll(() => relay.captured.filter((message) => message.type === 'control.thread.list').length)
    .toBeGreaterThan(initialListCount);
});

test('uses pushed events without polling the full active history', async ({ page }) => {
  const relay = await installRelayMock(page);
  await pair(page);
  await page.getByRole('button', { name: '打开 DEV-PC-01' }).click();
  await expect.poll(() => relay.captured.filter((message) => message.type === 'control.thread.read').length)
    .toBeGreaterThan(0);
  const initialReadCount = relay.captured.filter((message) => message.type === 'control.thread.read').length;
  await page.waitForTimeout(2_400);
  expect(relay.captured.filter((message) => message.type === 'control.thread.read')).toHaveLength(initialReadCount);
  relay.sendAgentDelta('事件推送的过程更新');
  await expect(page.getByText('事件推送的过程更新', { exact: true })).toBeVisible();
});

test('session sync shows live user input and keeps repeated messages in different turns', async ({ page }) => {
  const relay = await installRelayMock(page);
  await pair(page);
  await page.getByRole('button', { name: '打开 DEV-PC-01' }).click();
  relay.sendDomainEvent('UserMessageCompleted', { text: '来自另一端的消息' }, 'thr-1', 'turn-1', 'user-sync');
  relay.sendDomainEvent('UserMessageCompleted', { text: '来自另一端的消息' }, 'thr-1', 'turn-1', 'user-sync');
  await expect(page.getByText('来自另一端的消息', { exact: true })).toHaveCount(1);
  relay.sendDomainEvent('UserMessageCompleted', { text: '来自另一端的消息' }, 'thr-1', 'turn-2', 'user-sync');
  await expect(page.getByText('来自另一端的消息', { exact: true })).toHaveCount(2);
  relay.sendAgentDelta('第一段', 'shared-item');
  relay.sendAgentDelta('其他会话', 'shared-item', 'thr-history-1');
  relay.sendAgentDelta('第二段', 'shared-item');
  await expect(page.getByText('第一段第二段', { exact: true })).toBeVisible();
  await expect(page.getByText('第一段其他会话第二段', { exact: true })).toHaveCount(0);
});

test('session sync reconciles status notifications without reentering the conversation', async ({ page }) => {
  const relay = await installRelayMock(page);
  await pair(page);
  await page.getByRole('button', { name: '打开 DEV-PC-01' }).click();
  relay.sendDomainEvent('ThreadStatusChanged', { status: 'active', activeFlags: ['waitingOnUserInput'] });
  await expect(page.locator('.chat-header .status-pill')).toHaveText('等待输入');
  relay.sendDomainEvent('ThreadStatusChanged', { status: 'active', activeFlags: ['waitingOnApproval'] });
  await expect(page.locator('.chat-header .status-pill')).toHaveText('等待审批');
  relay.sendDomainEvent('ThreadStatusChanged', { status: 'idle', activeFlags: [] });
  await expect(page.getByRole('button', { name: '停止当前任务' })).toBeDisabled();
  await expect(page.locator('.chat-header .status-pill')).toHaveText('Agent 就绪');
});

test('marks Desktop-owned history as external and never polls it', async ({ page }) => {
  const relay = await installRelayMock(page, { activeTurn: false, desktopOwnedActiveTurn: true });
  await pair(page);
  await page.getByRole('button', { name: '打开 DEV-PC-01' }).click();
  await page.getByRole('button', { name: '打开会话栏' }).click();
  await page.getByRole('button', { name: /^历史测试会话/u }).click();

  await expect(page.locator('.chat-header .status-pill')).toHaveText('Desktop 外部会话');
  await expect(page.getByRole('textbox', { name: '恢复 Desktop 外部会话' })).toBeEnabled();
  const initialReadCount = relay.captured.filter((message) => message.type === 'control.thread.read').length;
  relay.publishHistoryText('Desktop 当前过程输出已同步');
  await page.waitForTimeout(2_400);
  expect(relay.captured.filter((message) => message.type === 'control.thread.read')).toHaveLength(initialReadCount);
  await expect(page.getByText('Desktop 当前过程输出已同步', { exact: true })).toHaveCount(0);
});

test('shows running unread and completed-unread indicators for managed events', async ({ page }) => {
  const relay = await installRelayMock(page, { activeTurn: false });
  await pair(page);
  await page.getByRole('button', { name: '打开 DEV-PC-01' }).click();
  await page.getByRole('button', { name: '打开会话栏' }).click();
  await page.getByRole('button', { name: /^MES 第二个会话/u }).click();
  await page.getByRole('button', { name: '打开会话栏' }).click();
  const historyButton = page.getByRole('button', { name: /^历史测试会话/u });
  relay.sendTurnStarted('thr-history-1');
  await expect(historyButton.locator('.thread-indicator.running')).toBeVisible();
  relay.sendAgentCompleted('托管任务已完成', 'managed-completed', 'thr-history-1');
  relay.sendTurnCompleted('thr-history-1');
  await expect(historyButton.locator('.thread-indicator.completed-unread')).toBeVisible();
  await historyButton.click();
  await page.getByRole('button', { name: '打开会话栏' }).click();
  await expect(page.getByRole('button', { name: /^历史测试会话/u }).locator('.thread-indicator')).toHaveCount(0);
});

test('lists real history and starts or resumes Codex sessions', async ({ page }) => {
  const relay = await installRelayMock(page, { activeTurn: false });
  await pair(page);
  await page.getByRole('button', { name: '打开 DEV-PC-01' }).click();

  await page.getByRole('button', { name: '打开会话栏' }).click();
  const projectNames = await page.locator('.project-copy strong').allTextContents();
  expect(projectNames.slice(0, 2)).toEqual(['Vision Workspace', 'MES']);
  const mesProject = page.getByRole('button', { name: '项目 MES' });
  await expect(mesProject).toHaveAttribute('aria-expanded', 'true');
  const mesRegionId = await mesProject.getAttribute('aria-controls');
  const mesThreadNames = await page.locator(`#${mesRegionId} .conversation-link-title`).allTextContents();
  expect(mesThreadNames).toEqual(['MES 第二个会话', '历史测试会话']);
  await expect(page.getByRole('button', { name: /^Vision 相机会话/u })).toBeVisible();
  await expect(page.getByText('最近', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: '在 Vision 中新建会话' })).toHaveCount(0);
  await page.getByRole('button', { name: '在 MES 中新建会话' }).click();
  await expect(page.getByLabel('模型')).toHaveValue('gpt-5.6-sol');
  await page.getByLabel('模型').selectOption('gpt-5.6-terra');
  await page.getByLabel('批准等级').selectOption('on-request');
  await expect(page.getByLabel('电脑上的项目目录')).toHaveValue('D:\\Projects\\MES');
  await page.getByLabel('第一条任务').fill('在 MES 项目中新建任务');
  await page.getByRole('button', { name: '创建会话并开始' }).click();
  await expect.poll(() => relay.captured.some((message) =>
    message.type === 'control.thread.start'
      && message.payload.cwd === 'D:\\Projects\\MES'
      && message.payload.text === '在 MES 项目中新建任务'
      && message.payload.model === 'gpt-5.6-terra'
      && message.payload.approvalPolicy === 'on-request',
  )).toBe(true);

  await page.getByRole('button', { name: '打开会话栏' }).click();
  const historyButton = page.getByRole('button', { name: /^历史测试会话/u });
  await expect(historyButton).toBeVisible();
  await historyButton.click();
  await expect(page.getByText('请继续修复登录模块', { exact: true })).toBeVisible();
  await expect(page.getByText('登录模块的历史修复已经完成', { exact: true })).toBeVisible();
  await expect.poll(() => relay.captured.some((message) =>
    message.type === 'control.thread.read' && message.payload.threadId === 'thr-history-1',
  )).toBe(true);
  await page.getByRole('button', { name: '打开会话栏' }).click();
  await page.locator('.new-chat-button').click();
  await page.getByLabel('电脑上的项目目录').fill('D:\\Projects\\NewProject');
  await page.getByLabel('第一条任务').fill('从手机创建真实会话');
  await page.getByRole('button', { name: '创建会话并开始' }).click();
  await expect.poll(() => relay.captured.some((message) =>
    message.type === 'control.thread.start' && message.payload.text === '从手机创建真实会话',
  )).toBe(true);

  await page.getByRole('button', { name: '打开会话栏' }).click();
  await historyButton.click();
  await page.getByLabel('恢复 Desktop 外部会话').fill('继续历史任务');
  await page.getByRole('button', { name: '恢复并由 Agent 托管' }).click();
  await expect.poll(() => relay.captured.some((message) =>
    message.type === 'control.thread.resume' && message.payload.threadId === 'thr-history-1',
  )).toBe(true);
});

test('manages multiple active sessions and keeps a new session stable while history is indexing', async ({ page }) => {
  const relay = await installRelayMock(page, {
    activeTurn: false,
    activeThreadIds: ['thr-history-1', 'thr-history-2'],
    newThreadReadFailures: 2,
  });
  await pair(page);
  await page.getByRole('button', { name: '打开 DEV-PC-01' }).click();

  await expect(page.getByLabel('模型')).toBeEnabled();
  await expect(page.getByLabel('批准等级')).toBeEnabled();
  await page.getByLabel('模型').selectOption('gpt-5.6-terra');
  await page.getByLabel('批准等级').selectOption('on-request');
  await page.getByLabel('Steer 当前任务').fill('干预第一个会话');
  await page.getByRole('button', { name: '发送 Steer' }).click();
  await expect.poll(() => relay.captured.some((message) =>
    message.type === 'control.steer' &&
    message.payload.threadId === 'thr-history-1' &&
    message.payload.expectedTurnId === 'turn-1',
  )).toBe(true);

  await page.getByRole('button', { name: '打开会话栏' }).click();
  await page.getByRole('button', { name: /^MES 第二个会话/u }).click();
  await page.getByLabel('Steer 当前任务').fill('干预第二个会话');
  await page.getByRole('button', { name: '发送 Steer' }).click();
  await expect.poll(() => relay.captured.some((message) =>
    message.type === 'control.steer' &&
    message.payload.threadId === 'thr-history-2' &&
    message.payload.expectedTurnId === 'turn-2',
  )).toBe(true);

  await page.getByRole('button', { name: '打开会话栏' }).click();
  await page.locator('.new-chat-button').click();
  await page.getByLabel('电脑上的项目目录').fill('D:\\Projects\\Concurrent');
  await page.getByLabel('模型').selectOption('gpt-5.6-terra');
  await page.getByLabel('批准等级').selectOption('on-request');
  await page.getByLabel('第一条任务').fill('并发新会话任务');
  await page.getByRole('button', { name: '创建会话并开始' }).click();

  await expect(page.locator('.chat-message.user').getByText('并发新会话任务', { exact: true })).toBeVisible();
  await expect(page.getByText('new thread history is not indexed yet')).toHaveCount(0);
  await expect.poll(() => relay.captured.some((message) =>
    message.type === 'control.thread.start' &&
    message.payload.model === 'gpt-5.6-terra' &&
    message.payload.approvalPolicy === 'on-request',
  )).toBe(true);

  await page.getByRole('button', { name: '打开会话栏' }).click();
  await expect(page.getByRole('button', { name: /^并发新会话任务/u })).toBeVisible();
  const mesProject = page.getByRole('button', { name: '项目 MES' });
  await mesProject.click();
  const mesRegionId = await mesProject.getAttribute('aria-controls');
  await expect(page.locator(`#${mesRegionId} .thread-indicator.running`)).toHaveCount(2);
});

test('renders markdown images elapsed time and a folded process summary', async ({ page }) => {
  await installRelayMock(page, { activeTurn: false, richHistory: true });
  await pair(page);
  await page.getByRole('button', { name: '打开 DEV-PC-01' }).click();
  await page.getByRole('button', { name: '打开会话栏' }).click();
  await page.getByRole('button', { name: /^历史测试会话/u }).click();

  await expect(page.getByRole('heading', { name: '结论' })).toBeVisible();
  await expect(page.getByText('Markdown 已解析', { exact: true })).toBeVisible();
  await expect(page.getByText('请分析这张图片', { exact: true })).toBeVisible();
  await expect(page.getByRole('img', { name: 'sample.png' })).toBeVisible();

  const context = page.locator('.prompt-context');
  await expect(context).not.toHaveAttribute('open', '');
  const summary = page.locator('.process-summary');
  await expect(summary).not.toHaveAttribute('open', '');
  await expect(summary.getByText('耗时 1分36秒', { exact: true })).toBeVisible();
  await expect(summary.getByText('影响 1 个文件 · +18 -4', { exact: true })).toBeVisible();
  await summary.locator('summary').click();
  await expect(summary.getByText('dotnet test', { exact: true })).toBeVisible();
});

test('recovers history after controller disconnects during list and read', async ({ page }) => {
  const relay = await installRelayMock(page, {
    activeTurn: false,
    disconnectThreadListOnce: true,
    disconnectThreadReadOnce: true,
  });
  await pair(page);
  await page.getByRole('button', { name: '打开 DEV-PC-01' }).click();

  await page.getByRole('button', { name: '打开会话栏' }).click();
  const historyButton = page.getByRole('button', { name: /^历史测试会话/u });
  await expect(historyButton).toBeVisible({ timeout: 20_000 });
  await historyButton.click();
  await expect(page.getByText('请继续修复登录模块', { exact: true })).toBeVisible({ timeout: 20_000 });
  await expect(page.getByText('登录模块的历史修复已经完成', { exact: true })).toBeVisible();
  await expect(page.getByText('Relay connection lost', { exact: true })).toHaveCount(0);
  await expect.poll(relay.getConnectionCount).toBeGreaterThanOrEqual(3);
  await expect.poll(() => relay.captured.filter((message) => message.type === 'control.thread.list').length)
    .toBeGreaterThanOrEqual(2);
  await expect.poll(() => relay.captured.filter((message) => message.type === 'control.thread.read').length)
    .toBeGreaterThanOrEqual(2);
});

test('keeps sidebar scroll and composer visible with long history', async ({ page }) => {
  const relay = await installRelayMock(page, {
    activeTurn: false,
    extraSingletonProjects: 30,
    historyEntryCount: 160,
  });
  await pair(page);
  await page.getByRole('button', { name: '打开 DEV-PC-01' }).click();
  await page.getByRole('button', { name: '打开会话栏' }).click();

  const navigation = page.getByRole('navigation', { name: '历史会话' });
  const sidebarMetrics = await navigation.evaluate((element) => ({
    clientHeight: element.clientHeight,
    scrollHeight: element.scrollHeight,
  }));
  expect(sidebarMetrics.scrollHeight).toBeGreaterThan(sidebarMetrics.clientHeight);
  await navigation.evaluate((element) => { element.scrollTop = element.scrollHeight; });
  await expect(page.getByRole('button', { name: /^Singleton 会话 29/u })).toBeVisible();

  await page.getByRole('button', { name: /^历史测试会话/u }).click();
  await expect(page.getByText(/历史长消息 159/u)).toBeVisible({ timeout: 20_000 });
  await expect(page.getByLabel('恢复 Desktop 外部会话')).toBeVisible();

  const layout = await page.evaluate(() => {
    const root = document.documentElement;
    const feed = document.querySelector<HTMLElement>('.chat-feed');
    const dock = document.querySelector<HTMLElement>('.composer-dock');
    return {
      horizontalOverflow: root.scrollWidth - root.clientWidth,
      verticalPageOverflow: root.scrollHeight - root.clientHeight,
      feedScrollable: Boolean(feed && feed.scrollHeight > feed.clientHeight),
      composerBottom: dock?.getBoundingClientRect().bottom ?? Number.POSITIVE_INFINITY,
      viewportHeight: window.innerHeight,
    };
  });
  expect(layout.horizontalOverflow).toBeLessThanOrEqual(0);
  expect(layout.verticalPageOverflow).toBeLessThanOrEqual(0);
  expect(layout.feedScrollable).toBe(true);
  expect(layout.composerBottom).toBeLessThanOrEqual(layout.viewportHeight + 1);

  const feed = page.locator('.chat-feed');
  await page.waitForTimeout(250);
  await feed.evaluate((element) => {
    element.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, pointerType: 'touch' }));
    element.scrollTop = 0;
    element.dispatchEvent(new Event('scroll', { bubbles: true }));
    element.dispatchEvent(new PointerEvent('pointerup', { bubbles: true, pointerType: 'touch' }));
  });
  await page.waitForTimeout(200);
  await expect.poll(async () => feed.evaluate((element) => element.scrollTop)).toBeLessThan(80);
  const scrollTopBeforeMessage = await feed.evaluate((element) => element.scrollTop);
  relay.sendAgentDelta('向上滚动后的新消息', 'scroll-message', 'thr-history-1');
  await page.waitForTimeout(100);
  const scrollTopAfterMessage = await feed.evaluate((element) => element.scrollTop);
  expect(Math.abs(scrollTopAfterMessage - scrollTopBeforeMessage)).toBeLessThan(20);
  await expect(page.getByText('向上滚动后的新消息', { exact: true })).toHaveCount(1);
  await expect(page.getByRole('button', { name: /1 条新消息/u })).toBeVisible();
  await page.getByRole('button', { name: /1 条新消息/u }).click();
  await expect(page.getByText('向上滚动后的新消息', { exact: true })).toBeVisible();
  await expect.poll(async () => feed.evaluate((element) =>
    element.scrollHeight - element.scrollTop - element.clientHeight,
  )).toBeLessThan(96);
});

test('pairing success toast clears automatically', async ({ page }) => {
  await page.clock.install();
  await installRelayMock(page, { activeTurn: false });
  await page.goto('/');
  await page.getByLabel('六位配对码').fill('123456');
  await page.getByRole('button', { name: '配对', exact: true }).click();
  const toast = page.getByText('配对成功', { exact: true });
  await expect(toast).toBeVisible();
  await page.clock.fastForward(3_000);
  await expect(toast).toHaveCount(0);
});

test('is mobile-safe and exposes installable PWA metadata', async ({ page, request }) => {
  await installRelayMock(page);
  await page.goto('/');
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow).toBeLessThanOrEqual(0);
  const manifest = await request.get('/manifest.webmanifest');
  expect(manifest.ok()).toBeTruthy();
  expect((await manifest.json()).display).toBe('standalone');
  const worker = await request.get('/sw.js');
  expect(worker.ok()).toBeTruthy();
});
