import { test, expect, type Page, type WebSocketRoute } from '@playwright/test';
import type { CodexEvent, CodexSnapshot, CodexThreadReadResult, RelayEnvelope } from '../src/protocol';

const deviceId = 'dev_shared_session_test';
const threadId = 'shared-thread';
const envelope = (type: string, payload: unknown, requestId?: string) => JSON.stringify({
  version: 2, type, payload, requestId, deviceId, messageId: `message-${Math.random()}`, timestamp: Date.now(),
});

async function sharedRelay(page: Page, unrelatedFailedHistory = false) {
  const sent: RelayEnvelope<Record<string, unknown>>[] = [];
  let route: WebSocketRoute;
  let sequence = 0;
  let epoch = 'epoch-one';
  let replayMode = false;
  let dropNext = false;
  let disconnectSend = false;
  let rejectWatch = false;
  const events: CodexEvent[] = [];
  let active = false;
  let status = 'completed';
  let history: CodexThreadReadResult = { threadId, entries: [], turns: [], truncated: false };
  const snapshot = (): CodexSnapshot => ({
    revision: sequence + 100, status: active ? 'Thinking' : 'Idle', lastActivityAt: Date.now(),
    changedFiles: [], pendingApprovalCount: 0, sharedSession: true, connectionState: 'online',
    capabilities: ['sharedSessionV1'], serviceInstanceId: 'service-one', streamEpoch: epoch, lastSequence: sequence,
    activeTurns: active ? [{ threadId, turnId: 'shared-turn', status: 'Thinking', startedAt: Date.now() - 2_000,
      lastActivityAt: Date.now(), changedFiles: [], pendingApprovalCount: 0 }] : [],
    threads: [{ threadId, threadState: active ? 'active' : 'idle', activeTurnId: active ? 'shared-turn' : undefined,
      lastTurnId: active ? undefined : 'previous-turn', lastTurnStatus: active ? undefined : status,
      waitingOnApproval: false, waitingOnUserInput: false, freshness: 'current' }],
  });
  await page.addInitScript(({ id }) => localStorage.setItem('codex-control-last-device', id), { id: deviceId });
  await page.routeWebSocket('**/ws/controller', (socket) => {
    route = socket;
    socket.onMessage((raw) => {
      const message = JSON.parse(String(raw)) as RelayEnvelope<Record<string, unknown>>;
      sent.push(message);
      const reply = (type: string, payload: unknown) => socket.send(envelope(type, payload, message.requestId));
      switch (message.type) {
        case 'auth.hello': reply('auth.ok', { role: 'controller', principalId: message.controllerId, connectionId: 'connection' }); break;
        case 'device.list': reply('device.list.result', { devices: [{ deviceId, name: 'Shared workstation', online: true, snapshot: snapshot() }] }); break;
        case 'control.thread.list': reply('control.result', { status: 'succeeded', result: {
          threads: [
            ...(unrelatedFailedHistory ? [{ threadId: 'unrelated-thread', name: '未同步的大历史会话', cwd: 'D:\\TestWorkspace', status: 'idle' }] : []),
            { threadId, name: '共享测试聊天', cwd: 'D:\\TestWorkspace', status: active ? 'active' : 'idle' },
          ], projects: [],
        } }); break;
        case 'control.thread.watch': {
          if (rejectWatch || message.payload.threadId === 'unrelated-thread') {
            reply('control.result', { status: 'failed', code: 'WATCH_RESPONSE_TOO_LARGE', message: '会话同步内容超过单次传输上限；当前尚未同步。' });
            break;
          }
          const replay = replayMode && message.payload.streamEpoch === epoch && typeof message.payload.afterSequence === 'number';
          reply('control.result', { status: 'succeeded', result: {
            threadId, serviceInstanceId: 'service-one', streamEpoch: epoch,
            sequence: replay ? message.payload.afterSequence : sequence,
            history: replay ? { threadId, entries: [], turns: [], truncated: false } : history,
            snapshot: snapshot(), approvals: [],
            events: replay ? events.filter((event) => event.sequence! > Number(message.payload.afterSequence)) : [],
            resyncRequired: !replay, controlAllowed: true,
          } }); break;
        }
        case 'control.thread.send':
          if (disconnectSend) socket.close({ code: 1012, reason: 'result lost after accepted input' });
          else reply('control.result', { status: 'accepted', result: { threadId, turnId: 'shared-turn' } });
          break;
        case 'control.interrupt': reply('control.result', { status: 'accepted' }); break;
        case 'control.approval': reply('control.result', { status: 'accepted' }); break;
        case 'heartbeat': reply('heartbeat.ack', {}); break;
        default: reply('error', { code: 'UNEXPECTED_REQUEST', message: message.type }); break;
      }
    });
  });
  return {
    sent,
    event(kind: string, data: Record<string, unknown> = {}, patch: Partial<CodexEvent> = {}) {
      sequence += 1;
      if (!patch.threadId || patch.threadId === threadId) {
        if (kind === 'TurnStarted') active = true;
        if (kind === 'TurnCompleted') { active = false; status = String(data.status ?? 'completed'); }
      }
      const value: CodexEvent = { eventId: `event-${sequence}`, sequence, revision: 1, streamEpoch: epoch,
        serviceInstanceId: 'service-one', occurredAt: Date.now(), threadId, turnId: 'shared-turn', itemId: 'reply', kind, data, ...patch };
      events.push(value);
      if (!dropNext) route.send(envelope('codex.event', value));
      dropNext = false;
      return value;
    },
    repeat(value: CodexEvent) { route.send(envelope('codex.event', value)); },
    disconnect() { route.close({ code: 1012, reason: 'test reconnect' }); },
    setHistory(value: CodexThreadReadResult) { history = value; },
    enableReplay() { replayMode = true; },
    missNext() { dropNext = true; },
    loseSendResult() { disconnectSend = true; },
    rejectOversizedWatch() { rejectWatch = true; },
    changeEpoch() { epoch = 'epoch-two'; sequence = 0; route.send(envelope('codex.snapshot', snapshot())); },
    gap() { sequence += 2; this.event('StreamGap'); },
  };
}

test('shared chat receives Desktop messages live, sends without policy overrides and completes without a history reload', async ({ page }) => {
  const relay = await sharedRelay(page);
  await page.goto('/');
  await expect(page.getByRole('heading', { name: '共享测试聊天' }).first()).toBeVisible();
  await expect.poll(() => relay.sent.filter((item) => item.type === 'control.thread.watch').length).toBe(1);
  await page.locator('#chat-composer').fill('web input');
  await page.getByRole('button', { name: '发送消息', exact: true }).click();
  await expect.poll(() => relay.sent.filter((item) => item.type === 'control.thread.send').length).toBe(1);
  const sent = relay.sent.find((item) => item.type === 'control.thread.send')!;
  expect(sent.payload).toEqual({ threadId, text: 'web input' });
  relay.event('TurnStarted');
  relay.event('UserMessageCompleted', { text: 'Desktop live input' }, { itemId: 'desktop-user' });
  const first = relay.event('AgentMessageDelta', { delta: 'Live ' });
  relay.event('AgentMessageDelta', { delta: 'answer' });
  relay.repeat(first);
  await expect(page.getByText('Desktop live input', { exact: true })).toBeVisible();
  await expect(page.getByText('Live answer', { exact: true })).toBeVisible();
  await page.locator('#chat-composer').fill('steer this active turn');
  await page.getByRole('button', { name: '发送 Steer', exact: true }).click();
  await expect.poll(() => relay.sent.filter((item) => item.type === 'control.thread.send').length).toBe(2);
  expect(relay.sent.filter((item) => item.type === 'control.thread.send')[1].payload.expectedTurnId).toBe('shared-turn');
  relay.event('AgentMessageCompleted', { text: 'Live answer complete' });
  relay.event('TurnCompleted', { status: 'completed' });
  await expect(page.getByText('Live answer complete', { exact: true })).toBeVisible();
  await expect(page.getByText('任务已完成', { exact: true })).toBeVisible();
  expect(relay.sent.filter((item) => item.type === 'control.thread.read' || item.type === 'control.thread.resume')).toHaveLength(0);
  expect(relay.sent.filter((item) => item.type === 'control.thread.watch')).toHaveLength(1);
  await expect(page.getByRole('combobox', { name: '批准等级' })).toHaveCount(0);
});

test('accepted shared Steer remains visible while upstream delays input events and clears at the target terminal state', async ({ page }) => {
  const relay = await sharedRelay(page);
  await page.goto('/');
  await expect.poll(() => relay.sent.filter((item) => item.type === 'control.thread.watch').length).toBe(1);
  relay.event('TurnStarted');
  relay.event('AgentMessageDelta', { delta: 'The current model step is still running.' });
  await page.locator('#chat-composer').fill('steer accepted before its upstream echo');
  await page.getByRole('button', { name: '发送 Steer', exact: true }).click();
  const feedback = page.getByRole('status', { name: '已接受的干预', exact: true });
  await expect(feedback).toContainText('steer accepted before its upstream echo');
  await expect(page.getByRole('region', { name: '会话内容' }).getByText('steer accepted before its upstream echo', { exact: true })).toHaveCount(0);
  await expect(page.locator('.toast')).toHaveCount(0);
  await expect(feedback).toBeVisible();
  relay.event('TurnCompleted', { status: 'completed' }, { threadId: 'another-thread', turnId: 'another-turn' });
  await expect(feedback).toBeVisible();
  relay.event('UserMessageCompleted', { text: 'steer accepted before its upstream echo' }, { itemId: 'steer-input' });
  await expect(page.getByRole('region', { name: '会话内容' }).getByText('steer accepted before its upstream echo', { exact: true })).toHaveCount(1);
  relay.event('TurnCompleted', { status: 'interrupted' });
  await expect(feedback).toHaveCount(0);
  expect(relay.sent.filter((item) => item.type === 'control.thread.send')).toHaveLength(1);
  expect(relay.sent.filter((item) => item.type === 'control.thread.read' || item.type === 'control.thread.resume')).toHaveLength(0);
});

test('shared chat reconnects with cursor, preserves running task and converges to complete Item', async ({ page }) => {
  const relay = await sharedRelay(page);
  await page.goto('/');
  await expect.poll(() => relay.sent.filter((item) => item.type === 'control.thread.watch').length).toBe(1);
  relay.event('TurnStarted');
  relay.event('AgentMessageDelta', { delta: 'prefix' });
  await expect(page.getByText('prefix', { exact: true })).toBeVisible();
  relay.setHistory({ threadId, entries: [{ turnId: 'shared-turn', itemId: 'reply', role: 'assistant',
    text: 'prefix from snapshot', attachments: [] }], turns: [{ turnId: 'shared-turn', status: 'inProgress' }], truncated: false });
  relay.disconnect();
  await expect.poll(() => relay.sent.filter((item) => item.type === 'control.thread.watch').length).toBeGreaterThanOrEqual(2);
  await expect(page.getByText('prefix from snapshot', { exact: true })).toBeVisible();
  const recovered = relay.sent.filter((item) => item.type === 'control.thread.watch').at(-1)!;
  expect(recovered.payload.streamEpoch).toBe('epoch-one');
  expect(recovered.payload.afterSequence).toBe(2);
  relay.event('AgentMessageDelta', { delta: 'ambiguous overlap' });
  relay.event('AgentMessageCompleted', { text: 'authoritative complete reply' });
  relay.event('TurnCompleted', { status: 'completed' });
  await expect(page.getByText('authoritative complete reply', { exact: true })).toBeVisible();
  await expect(page.getByText('ambiguous overlap', { exact: true })).toHaveCount(0);
  expect(relay.sent.filter((item) => item.type === 'control.interrupt')).toHaveLength(0);
});

test('shared stream gap triggers a single watch and approval stays visible while resolving', async ({ page }) => {
  const relay = await sharedRelay(page);
  await page.goto('/');
  await expect.poll(() => relay.sent.filter((item) => item.type === 'control.thread.watch').length).toBe(1);
  relay.gap();
  await expect.poll(() => relay.sent.filter((item) => item.type === 'control.thread.watch').length).toBe(2);
  relay.event('TurnStarted');
  relay.event('ApprovalRequested', { approvalId: 'approval-one', threadId, turnId: 'shared-turn',
    requestMethod: 'command', availableDecisions: ['cancel'], requestedAt: Date.now() });
  await expect(page.getByRole('button', { name: '拒绝并停止', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: '允许一次', exact: true })).toHaveCount(0);
  relay.event('ApprovalResolving', { approvalId: 'approval-one' });
  await expect(page.getByText('审批已提交，等待确认', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: '拒绝并停止', exact: true })).toBeDisabled();
  relay.event('ApprovalResolved', { approvalId: 'approval-one' });
  await expect(page.getByRole('button', { name: '拒绝并停止', exact: true })).toHaveCount(0);
  expect(relay.sent.filter((item) => item.type === 'control.thread.watch')).toHaveLength(2);
});

test('shared replay fills missing sequence without dropping cached messages and epoch changes force a new view', async ({ page }) => {
  const relay = await sharedRelay(page);
  await page.goto('/');
  await expect.poll(() => relay.sent.filter((item) => item.type === 'control.thread.watch').length).toBe(1);
  relay.event('TurnStarted');
  relay.event('UserMessageCompleted', { text: 'cached message before gap' }, { itemId: 'cached' });
  await expect(page.getByText('cached message before gap', { exact: true })).toBeVisible();
  relay.enableReplay();
  relay.missNext();
  relay.event('UserMessageCompleted', { text: 'missed while disconnected' }, { itemId: 'missed' });
  relay.event('AgentMessageCompleted', { text: 'after the gap' });
  await expect(page.getByText('missed while disconnected', { exact: true })).toBeVisible();
  await expect(page.getByText('cached message before gap', { exact: true })).toBeVisible();
  await expect(page.getByText('after the gap', { exact: true })).toBeVisible();
  expect(relay.sent.filter((item) => item.type === 'control.thread.watch').at(-1)?.payload.afterSequence).toBe(2);
  relay.setHistory({ threadId, entries: [{ itemId: 'restored', turnId: 'old-turn', role: 'assistant',
    text: 'restored from a new upstream connection', attachments: [] }], turns: [], truncated: false });
  relay.changeEpoch();
  await expect(page.getByText('restored from a new upstream connection', { exact: true })).toBeVisible();
  expect(relay.sent.filter((item) => item.type === 'control.thread.watch').at(-1)?.payload.streamEpoch).toBe('epoch-one');
});

test('shared input with a lost result is shown as unknown and never retried on reconnection', async ({ page }) => {
  const relay = await sharedRelay(page);
  await page.goto('/');
  await expect.poll(() => relay.sent.filter((item) => item.type === 'control.thread.watch').length).toBe(1);
  relay.loseSendResult();
  await page.locator('#chat-composer').fill('send once even if the result is lost');
  await page.getByRole('button', { name: '发送消息', exact: true }).click();
  await expect(page.getByText('输入结果待确认。连接恢复后请先查看会话；此次输入不会自动重发。', { exact: true })).toBeVisible();
  await expect.poll(() => relay.sent.filter((item) => item.type === 'control.thread.watch').length).toBeGreaterThanOrEqual(2);
  expect(relay.sent.filter((item) => item.type === 'control.thread.send')).toHaveLength(1);
});

test('oversized content preserves existing text, shows missing content and never loops failed recovery', async ({ page }) => {
  const relay = await sharedRelay(page);
  await page.goto('/');
  await expect.poll(() => relay.sent.filter((item) => item.type === 'control.thread.watch').length).toBe(1);
  relay.event('TurnStarted');
  relay.event('AgentMessageDelta', { delta: 'already received text' });
  await expect(page.getByText('already received text', { exact: true })).toBeVisible();
  relay.rejectOversizedWatch();
  relay.event('ContentIncomplete', { originalKind: 'AgentMessageCompleted', truncated: true,
    resyncRequired: true, reason: 'EVENT_TOO_LARGE' });
  await expect(page.getByText('already received text', { exact: true })).toBeVisible();
  await expect(page.getByText('内容不完整，等待同步', { exact: true })).toBeVisible();
  await expect(page.getByText(/WATCH_RESPONSE_TOO_LARGE/u)).toBeVisible();
  expect(relay.sent.filter((item) => item.type === 'control.thread.watch')).toHaveLength(2);
  relay.event('ContentIncomplete', { originalKind: 'UserMessageCompleted', truncated: true,
    resyncRequired: true, reason: 'EVENT_TOO_LARGE' }, { itemId: 'new-oversized-user' });
  await expect(page.getByText('此条消息内容未完整，正在等待同步。', { exact: true })).toBeVisible();
  relay.event('UserMessageCompleted', { text: 'later event remains live' }, { itemId: 'later-user' });
  await expect(page.getByText('later event remains live', { exact: true })).toBeVisible();
  expect(relay.sent.filter((item) => item.type === 'control.thread.watch')).toHaveLength(2);
  relay.event('AgentMessageCompleted', { text: 'complete replacement content' });
  await expect(page.getByText('complete replacement content', { exact: true })).toBeVisible();
  await expect(page.getByText('already received text', { exact: true })).toHaveCount(0);
});

async function selectHealthyThreadAfterFailedHistory(page: Page) {
  const relay = await sharedRelay(page, true);
  await page.goto('/');
  await page.getByRole('button', { name: '打开会话栏', exact: true }).click();
  await page.getByRole('button', { name: /未同步的大历史会话/u }).click();
  await expect(page.getByRole('status').filter({ hasText: 'WATCH_RESPONSE_TOO_LARGE' })).toBeVisible();
  await page.getByRole('button', { name: '打开会话栏', exact: true }).click();
  await page.getByRole('button', { name: /共享测试聊天/u }).click();
  await page.locator('#chat-composer').fill('continue the healthy thread');
  await expect(page.getByRole('button', { name: '发送消息', exact: true })).toBeEnabled();
  const failedWatches = relay.sent.filter((item) => item.type === 'control.thread.watch' && item.payload.threadId === 'unrelated-thread').length;
  return { relay, failedWatches };
}

test('unrelated failed history cannot block live settings recovery or cause retries on a stream gap', async ({ page }) => {
  const { relay, failedWatches } = await selectHealthyThreadAfterFailedHistory(page);
  const healthyWatches = () => relay.sent.filter((item) => item.type === 'control.thread.watch' && item.payload.threadId === threadId).length;
  expect(healthyWatches()).toBe(1);
  relay.event('ThreadSettingsChanged', { resyncRequired: true });
  await expect.poll(healthyWatches).toBe(2);
  await expect(page.getByRole('button', { name: '发送消息', exact: true })).toBeEnabled();
  relay.event('TurnStarted');
  relay.event('UserMessageCompleted', { text: 'Desktop message after settings recovery' }, { itemId: 'desktop-after-settings' });
  relay.event('TurnCompleted', { status: 'completed' });
  await expect(page.getByText('Desktop message after settings recovery', { exact: true })).toBeVisible();
  await expect(page.getByText('任务已完成', { exact: true })).toBeVisible();
  relay.gap();
  await expect.poll(healthyWatches).toBe(3);
  await expect(page.getByRole('button', { name: '发送消息', exact: true })).toBeEnabled();
  expect(relay.sent.filter((item) => item.type === 'control.thread.watch' && item.payload.threadId === 'unrelated-thread')).toHaveLength(failedWatches);
});

test('unrelated failed history cannot block recovery after the upstream epoch changes', async ({ page }) => {
  const { relay, failedWatches } = await selectHealthyThreadAfterFailedHistory(page);
  relay.setHistory({ threadId, entries: [{ itemId: 'restored-with-failed-history', turnId: 'old-turn', role: 'assistant',
    text: 'restored while another thread has a sync error', attachments: [] }], turns: [], truncated: false });
  relay.changeEpoch();
  await expect(page.getByText('restored while another thread has a sync error', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: '发送消息', exact: true })).toBeEnabled();
  expect(relay.sent.filter((item) => item.type === 'control.thread.watch' && item.payload.threadId === threadId)).toHaveLength(2);
  expect(relay.sent.filter((item) => item.type === 'control.thread.watch' && item.payload.threadId === 'unrelated-thread')).toHaveLength(failedWatches);
});
