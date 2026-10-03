import { test, expect } from '@playwright/test';
import { ThreadStore } from '../src/threadStore';
import type { CodexEvent, CodexSnapshot, CodexThreadWatchResult } from '../src/protocol';

const snapshot = (patch: Partial<CodexSnapshot> = {}): CodexSnapshot => ({
  revision: 10, status: 'Idle', lastActivityAt: 100, changedFiles: [], pendingApprovalCount: 0,
  activeTurns: [], sharedSession: true, serviceInstanceId: 'service', streamEpoch: 'epoch',
  lastSequence: 0, capabilities: ['sharedSessionV1'], connectionState: 'online', ...patch,
});
const event = (sequence: number, kind: string, data: Record<string, unknown> = {}, patch: Partial<CodexEvent> = {}): CodexEvent => ({
  eventId: `event-${sequence}`, revision: 1, sequence, streamEpoch: 'epoch', serviceInstanceId: 'service',
  threadId: 'thread-a', turnId: 'turn-a', itemId: 'item-a', occurredAt: sequence, kind, data, ...patch,
});
const watch = (patch: Partial<CodexThreadWatchResult> = {}): CodexThreadWatchResult => ({
  threadId: 'thread-a', serviceInstanceId: 'service', streamEpoch: 'epoch', sequence: 0,
  history: { threadId: 'thread-a', entries: [], turns: [], truncated: false }, snapshot: snapshot(),
  approvals: [], events: [], resyncRequired: true, controlAllowed: true, ...patch,
});

test('ThreadStore retains independent threads and deduplicates every delta event ID after aggregation', () => {
  const store = new ThreadStore();
  store.applyWatch('device', watch());
  store.applyEvent('device', event(1, 'AgentMessageDelta', { delta: 'hello' }));
  const second = event(2, 'AgentMessageDelta', { delta: ' world' });
  store.applyEvent('device', second);
  store.applyEvent('device', { ...second, sequence: undefined });
  for (let index = 3; index < 300; index++) store.applyEvent('device', event(index, 'UserMessageCompleted',
    { text: `other-${index}` }, { threadId: 'thread-b', itemId: `item-${index}` }));
  expect(store.view('device', 'thread-a')?.items[0].text).toBe('hello world');
  expect(store.view('device', 'thread-a')?.events).toHaveLength(1);
  expect(store.view('device', 'thread-b')?.items).toHaveLength(297);
  store.applyEvent('device', event(300, 'AgentMessageCompleted', { text: 'authoritative text' }));
  store.applyEvent('device', event(301, 'AgentMessageDelta', { delta: 'late duplicate' }));
  expect(store.view('device', 'thread-a')?.items[0].text).toBe('authoritative text');
});

test('ThreadStore does not use snapshot revision to discard messages and old completion cannot stop a newer Turn', () => {
  const store = new ThreadStore();
  store.applyWatch('device', watch({ snapshot: snapshot({ revision: 99 }) }));
  store.applyEvent('device', event(1, 'TurnStarted'));
  store.applyEvent('device', event(2, 'UserMessageCompleted', { text: 'low state revision still belongs to this stream' }));
  store.applyEvent('device', event(3, 'TurnStarted', {}, { turnId: 'turn-b' }));
  store.applyEvent('device', event(4, 'TurnCompleted', { status: 'completed' }));
  expect(store.view('device', 'thread-a')?.activeTurn?.turnId).toBe('turn-b');
  expect(store.view('device', 'thread-a')?.items[0].text).toContain('low state revision');
  store.applyEvent('device', event(5, 'ThreadStatusChanged', { status: 'idle' }, { turnId: 'turn-b' }));
  store.applyEvent('device', event(6, 'TurnCompleted', { status: 'interrupted' }, { turnId: 'turn-b' }));
  expect(store.view('device', 'thread-a')?.state.lastTurnStatus).toBe('interrupted');
  expect(store.view('device', 'thread-a')?.state.threadState).toBe('idle');
});

test('ThreadStore retains pending approval over command activity and waits for authoritative resolution', () => {
  const store = new ThreadStore();
  store.applyWatch('device', watch());
  store.applyEvent('device', event(1, 'TurnStarted'));
  store.applyEvent('device', event(2, 'ApprovalRequested', {
    approvalId: 'approval', threadId: 'thread-a', turnId: 'turn-a', availableDecisions: ['cancel'],
  }));
  store.applyEvent('device', event(3, 'CommandStarted', { command: 'benign-test' }));
  expect(store.view('device', 'thread-a')?.activeTurn?.status).toBe('WaitingApproval');
  store.applyEvent('device', event(4, 'ApprovalResolving', { approvalId: 'approval' }));
  expect(store.view('device', 'thread-a')?.approvals[0].isResolving).toBe(true);
  store.applyEvent('device', event(5, 'ApprovalResolved', { approvalId: 'approval' }));
  expect(store.view('device', 'thread-a')?.approvals).toEqual([]);
  expect(store.view('device', 'thread-a')?.activeTurn?.status).toBe('Thinking');
});

test('ThreadStore buffers live arrivals across watch and never guesses snapshot/delta overlap', () => {
  const store = new ThreadStore();
  const history = { threadId: 'thread-a', truncated: false, turns: [{ turnId: 'turn-a', status: 'inProgress' }],
    entries: [{ itemId: 'item-a', turnId: 'turn-a', role: 'assistant' as const, text: 'snapshot prefix', attachments: [] }] };
  store.applyWatch('device', watch({ history }), [event(1, 'AgentMessageDelta', { delta: 'potential overlap' })]);
  expect(store.view('device', 'thread-a')?.items[0]).toMatchObject({ text: 'snapshot prefix', syncing: true });
  store.applyEvent('device', event(2, 'AgentMessageCompleted', { text: 'complete authoritative reply' }));
  expect(store.view('device', 'thread-a')?.items[0]).toMatchObject({ text: 'complete authoritative reply', syncing: false });
});

test('ThreadStore accepts absolute delta offsets, flags gaps and never attributes unknown events to a selected thread', () => {
  const store = new ThreadStore();
  store.applyWatch('device', watch());
  store.applyEvent('device', event(1, 'AgentMessageDelta', { delta: 'abc', offset: 0 }));
  store.applyEvent('device', event(2, 'AgentMessageDelta', { delta: 'bcd', offset: 1 }));
  expect(store.view('device', 'thread-a')?.items[0].text).toBe('abcd');
  store.applyEvent('device', event(3, 'AgentMessageDelta', { delta: 'g', offset: 6 }));
  expect(store.view('device', 'thread-a')?.needsRecovery).toBe(true);
  store.applyEvent('device', event(4, 'UserMessageCompleted', { text: 'unattributed' }, { threadId: undefined }));
  expect(store.view('device', 'thread-a')?.items).toHaveLength(1);
});

test('ThreadStore displays active with missing Turn ID and requires recovery after epoch changes', () => {
  const store = new ThreadStore();
  store.applyWatch('device', watch());
  store.applyEvent('device', event(1, 'ThreadStatusChanged', { status: 'active' }, { turnId: undefined }));
  expect(store.view('device', 'thread-a')?.state.threadState).toBe('active');
  expect(store.view('device', 'thread-a')?.needsRecovery).toBe(true);
  store.applySnapshot('device', snapshot({ streamEpoch: 'epoch-2' }));
  expect(store.view('device', 'thread-a')?.controlAllowed).toBe(false);
});

test('ThreadStore preserves existing items for cursor replay and replaces truncated snapshots only with fuller content', () => {
  const store = new ThreadStore();
  store.applyWatch('device', watch());
  store.applyEvent('device', event(1, 'AgentMessageCompleted', { text: 'complete authoritative message' }));
  store.applyWatch('device', watch({ sequence: 1, resyncRequired: false, events: [
    event(2, 'UserMessageCompleted', { text: 'new input after reconnect' }, { itemId: 'user' }),
  ] }));
  expect(store.view('device', 'thread-a')?.items.map((item) => item.text)).toEqual([
    'complete authoritative message', 'new input after reconnect',
  ]);
  store.applyWatch('device', watch({ sequence: 2, history: { threadId: 'thread-a', truncated: true, turns: [],
    entries: [{ itemId: 'item-a', turnId: 'turn-a', role: 'assistant', attachments: [], text: 'complete', truncated: true, originalLength: 30 }],
  } }));
  expect(store.view('device', 'thread-a')?.items[0].text).toBe('complete authoritative message');
});
