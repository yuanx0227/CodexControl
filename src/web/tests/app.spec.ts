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
  extraSingletonProjects?: number;
  historyEntryCount?: number;
} = {}) {
  const activeTurn = options.activeTurn ?? true;
  const captured: CapturedMessage[] = [];
  let paired = false;
  let connectionCount = 0;
  let disconnectThreadListOnce = options.disconnectThreadListOnce ?? false;
  let disconnectThreadReadOnce = options.disconnectThreadReadOnce ?? false;
  const threadSummaries = [{
    threadId: 'thr-history-1',
    name: '历史测试会话',
    preview: '继续修复登录模块',
    cwd: 'D:\\Projects\\MES',
    createdAt: 1_730_831_111,
    updatedAt: 1_730_832_222,
    status: 'notLoaded',
    sourceKind: 'appServer',
  }, {
    threadId: 'thr-history-2',
    name: 'MES 第二个会话',
    preview: '检查 MES 接口',
    cwd: 'D:\\Projects\\MES',
    createdAt: 1_730_811_111,
    updatedAt: 1_730_812_222,
    status: 'notLoaded',
    sourceKind: 'appServer',
  }, {
    threadId: 'thr-vision-1',
    name: 'Vision 相机会话',
    preview: '检查相机连接',
    cwd: 'D:\\Projects\\Vision',
    createdAt: 1_730_711_111,
    updatedAt: 1_730_712_222,
    status: 'notLoaded',
    sourceKind: 'appServer',
  }];
  for (let index = 0; index < (options.extraSingletonProjects ?? 0); index += 1) {
    threadSummaries.push({
      threadId: `thr-singleton-${index}`,
      name: `Singleton 会话 ${index}`,
      preview: `单会话项目 ${index}`,
      cwd: `D:\\Projects\\Singleton-${index}`,
      createdAt: 1_730_600_000 - index,
      updatedAt: 1_730_700_000 - index,
      status: 'notLoaded',
      sourceKind: 'appServer',
    });
  }
  const historyEntries = Array.from({ length: options.historyEntryCount ?? 2 }, (_, index) => ({
    itemId: `history-item-${index}`,
    turnId: `history-turn-${Math.floor(index / 2)}`,
    role: index % 2 === 0 ? 'user' : 'assistant',
    text: index === 0
      ? '请继续修复登录模块'
      : index === 1
        ? '登录模块的历史修复已经完成'
        : `历史长消息 ${index} ${'内容'.repeat(180)}`,
    phase: index % 2 === 0 ? undefined : 'final_answer',
  }));
  let route: WebSocketRoute | undefined;
  await page.routeWebSocket('**/ws/controller', (socket) => {
    connectionCount += 1;
    route = socket;
    socket.onMessage((message) => {
      const envelope = JSON.parse(String(message)) as CapturedMessage;
      captured.push(envelope);
      switch (envelope.type) {
        case 'auth.hello':
          if (paired) {
            socket.send(JSON.stringify(reply('auth.ok', envelope.requestId, {
              role: 'controller',
              principalId: envelope.controllerId,
              connectionId: `conn-${connectionCount}`,
            }, undefined, envelope.controllerId)));
          } else {
            socket.send(JSON.stringify(reply('error', envelope.requestId, {
              code: 'AUTH_FAILED',
              message: 'Controller is not paired yet.',
            })));
          }
          break;
        case 'pairing.claim':
          paired = true;
          socket.send(JSON.stringify(reply('pairing.completed', envelope.requestId, {
            deviceId,
            controllerId: envelope.controllerId,
            permissions: { view: true, steer: true, interrupt: true, approval: true },
          }, deviceId, envelope.controllerId)));
          break;
        case 'device.list':
          socket.send(JSON.stringify(reply('device.list.result', envelope.requestId, {
            devices: [{
              deviceId,
              name: 'DEV-PC-01',
              online: true,
              lastSeenAt: Date.now(),
              snapshot: {
                revision: 7,
                status: activeTurn ? 'Thinking' : 'Idle',
                activeThreadId: activeTurn ? 'thr-1' : undefined,
                activeTurnId: activeTurn ? 'turn-1' : undefined,
                startedAt: activeTurn ? Date.now() - 60_000 : undefined,
                lastActivityAt: Date.now(),
                currentProject: 'D:\\Projects\\MES',
                currentActivity: 'Running tests',
                runningCommand: 'dotnet test',
                changedFiles: ['src/LoginService.cs'],
                pendingApprovalCount: 0,
                lastAgentMessage: '正在修复失败测试',
              },
            }],
          }, undefined, envelope.controllerId)));
          break;
        case 'control.steer':
        case 'control.interrupt':
        case 'control.approval':
          socket.send(JSON.stringify(reply('control.result', envelope.requestId, {
            status: envelope.type === 'control.interrupt' ? 'accepted' : 'succeeded',
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
          socket.send(JSON.stringify(reply('control.result', envelope.requestId, {
            status: 'succeeded',
            result: {
              threadId,
              name: threadId === 'thr-history-1' ? '历史测试会话' : '测试会话',
              cwd: threadId === 'thr-vision-1' ? 'D:\\Projects\\Vision' : 'D:\\Projects\\MES',
              entries: historyEntries,
              truncated: false,
            },
          }, envelope.deviceId, envelope.controllerId)));
          break;
        }
        case 'control.thread.start':
          socket.send(JSON.stringify(reply('control.result', envelope.requestId, {
            status: 'succeeded',
            result: { threadId: 'thr-created', turnId: 'turn-created' },
          }, envelope.deviceId, envelope.controllerId)));
          break;
        case 'control.thread.resume':
          socket.send(JSON.stringify(reply('control.result', envelope.requestId, {
            status: 'succeeded',
            result: { threadId: envelope.payload.threadId, turnId: 'turn-resumed' },
          }, envelope.deviceId, envelope.controllerId)));
          break;
        case 'pairing.revoked':
          socket.send(JSON.stringify(reply('pairing.revoked', envelope.requestId, envelope.payload, envelope.deviceId, envelope.controllerId)));
          break;
      }
    });
  });

  return {
    captured,
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
    version: 1,
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

  await page.getByRole('button', { name: '打开 DEV-PC-01' }).click();
  await expect(page.getByRole('main').getByText('D:\\Projects\\MES', { exact: true })).toBeVisible();
  await expect(page.getByText('dotnet test')).toBeVisible();
  await expect(page.getByText('正在修复失败测试')).toBeVisible();
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

test('lists real history and starts or resumes Codex sessions', async ({ page }) => {
  const relay = await installRelayMock(page, { activeTurn: false });
  await pair(page);
  await page.getByRole('button', { name: '打开 DEV-PC-01' }).click();

  await page.getByRole('button', { name: '打开会话栏' }).click();
  const mesProject = page.getByRole('button', { name: 'MES，2 个会话' });
  await expect(mesProject).toHaveAttribute('aria-expanded', 'true');
  await expect(page.getByRole('button', { name: 'Vision，1 个会话' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: /^Vision 相机会话/u })).toBeVisible();
  await page.getByRole('button', { name: '在 MES 中新建会话' }).click();
  await expect(page.getByLabel('电脑上的项目目录')).toHaveValue('D:\\Projects\\MES');
  await page.getByLabel('第一条任务').fill('在 MES 项目中新建任务');
  await page.getByRole('button', { name: '创建会话并开始' }).click();
  await expect.poll(() => relay.captured.some((message) =>
    message.type === 'control.thread.start'
      && message.payload.cwd === 'D:\\Projects\\MES'
      && message.payload.text === '在 MES 项目中新建任务',
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
  await page.getByRole('button', { name: '新建任务' }).click();
  await page.getByLabel('电脑上的项目目录').fill('D:\\Projects\\NewProject');
  await page.getByLabel('第一条任务').fill('从手机创建真实会话');
  await page.getByRole('button', { name: '创建会话并开始' }).click();
  await expect.poll(() => relay.captured.some((message) =>
    message.type === 'control.thread.start' && message.payload.text === '从手机创建真实会话',
  )).toBe(true);

  await page.getByRole('button', { name: '打开会话栏' }).click();
  await historyButton.click();
  await page.getByLabel('继续历史会话的任务').fill('继续历史任务');
  await page.getByRole('button', { name: '恢复会话并发送' }).click();
  await expect.poll(() => relay.captured.some((message) =>
    message.type === 'control.thread.resume' && message.payload.threadId === 'thr-history-1',
  )).toBe(true);
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
  await installRelayMock(page, {
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
  await expect(page.getByLabel('继续历史会话的任务')).toBeVisible();

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
});

test('pairing success toast clears automatically', async ({ page }) => {
  await page.clock.install();
  await installRelayMock(page, { activeTurn: false });
  await pair(page);
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
