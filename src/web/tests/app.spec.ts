import { expect, test, type Page, type WebSocketRoute } from '@playwright/test';

interface CapturedMessage {
  type: string;
  requestId?: string;
  deviceId?: string;
  controllerId?: string;
  payload: Record<string, unknown>;
}

const deviceId = 'dev_1234567890abcdef1234567890abcdef';

async function installRelayMock(page: Page) {
  const captured: CapturedMessage[] = [];
  let route: WebSocketRoute | undefined;
  await page.routeWebSocket('**/ws/controller', (socket) => {
    route = socket;
    socket.onMessage((message) => {
      const envelope = JSON.parse(String(message)) as CapturedMessage;
      captured.push(envelope);
      switch (envelope.type) {
        case 'auth.hello':
          socket.send(JSON.stringify(reply('error', envelope.requestId, {
            code: 'AUTH_FAILED',
            message: 'Controller is not paired yet.',
          })));
          break;
        case 'pairing.claim':
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
                status: 'Thinking',
                activeThreadId: 'thr-1',
                activeTurnId: 'turn-1',
                startedAt: Date.now() - 60_000,
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
        case 'pairing.revoked':
          socket.send(JSON.stringify(reply('pairing.revoked', envelope.requestId, envelope.payload, envelope.deviceId, envelope.controllerId)));
          break;
      }
    });
  });

  return {
    captured,
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
  await expect(page.getByRole('heading', { name: '开发电脑' })).toBeVisible();
  await expect(page.getByText('DEV-PC-01')).toBeVisible();
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
  await expect(page.getByText('D:\\Projects\\MES')).toBeVisible();
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

  await page.getByRole('button', { name: '停止当前任务' }).click();
  await expect.poll(() => relay.captured.some((message) => message.type === 'control.interrupt')).toBe(true);

  relay.sendApproval();
  const approval = page.getByRole('region', { name: '等待审批' });
  await expect(approval).toBeVisible();
  await expect(approval.getByText('git push origin main')).toBeVisible();
  await page.getByRole('button', { name: '允许一次' }).click();
  await expect.poll(() => relay.captured.some((message) =>
    message.type === 'control.approval' && message.payload.decision === 'accept',
  )).toBe(true);
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
