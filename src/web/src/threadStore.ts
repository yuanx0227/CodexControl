import type {
  ApprovalRequested, CodexActiveTurn, CodexEvent, CodexSnapshot,
  CodexThreadHistoryEntry, CodexThreadReadResult, CodexThreadState, CodexThreadWatchResult,
} from './protocol';

export interface ThreadItem extends CodexThreadHistoryEntry {
  streaming?: boolean;
  syncing?: boolean;
  occurredAt?: number;
  incomplete?: boolean;
}

export interface ThreadView {
  threadId: string;
  serviceInstanceId?: string;
  streamEpoch?: string;
  sequence: number;
  history?: CodexThreadReadResult;
  items: ThreadItem[];
  events: CodexEvent[];
  approvals: ApprovalRequested[];
  state: CodexThreadState;
  activeTurn?: CodexActiveTurn;
  lastActivityAt: number;
  watching: boolean;
  syncing: boolean;
  needsRecovery: boolean;
  controlAllowed: boolean;
  policyReason?: string;
  error?: string;
}

type DeviceThreads = Record<string, ThreadView>;
const itemKey = (turnId: string, itemId: string) => JSON.stringify([turnId, itemId]);
const terminal = (status?: string) => Boolean(status && /^(completed|interrupted|failed)$/iu.test(status));
const string = (value: unknown) => typeof value === 'string' ? value : undefined;
const number = (value: unknown) => typeof value === 'number' && Number.isFinite(value) ? value : undefined;

/** Domain-only session views. State revisions never order message fragments. */
export class ThreadStore {
  private devices: Record<string, DeviceThreads> = {};
  private seen = new Map<string, Set<string>>();
  private stateRevisions = new Map<string, number>();

  device(deviceId: string): DeviceThreads { return this.devices[deviceId] ?? {}; }
  view(deviceId: string, threadId: string): ThreadView | undefined { return this.device(deviceId)[threadId]; }
  clearDevice(deviceId: string): void {
    delete this.devices[deviceId];
    for (const key of this.seen.keys()) if (key.startsWith(`${deviceId}\0`)) this.seen.delete(key);
    this.stateRevisions.delete(deviceId);
  }
  events(deviceId: string): CodexEvent[] {
    return Object.values(this.device(deviceId)).flatMap((thread) => thread.events)
      .sort((a, b) => b.occurredAt - a.occurredAt || (b.sequence ?? 0) - (a.sequence ?? 0));
  }
  approvals(deviceId: string): ApprovalRequested[] {
    return Object.values(this.device(deviceId)).flatMap((thread) => thread.approvals);
  }
  beginWatch(deviceId: string, threadId: string): void {
    const thread = this.ensure(deviceId, threadId);
    this.put(deviceId, { ...thread, watching: true, syncing: true, error: undefined });
  }
  failWatch(deviceId: string, threadId: string, error: string): void {
    const thread = this.ensure(deviceId, threadId);
    this.put(deviceId, { ...thread, syncing: false, needsRecovery: true, error });
  }
  markDisconnected(deviceId: string): void {
    for (const thread of Object.values(this.device(deviceId))) {
      this.put(deviceId, { ...thread, needsRecovery: thread.watching || thread.needsRecovery });
    }
  }
  markGap(deviceId: string): void { this.markDisconnected(deviceId); }

  applySnapshot(deviceId: string, snapshot: CodexSnapshot): void {
    const prior = this.stateRevisions.get(deviceId);
    const sameEpoch = Object.values(this.device(deviceId)).every((thread) =>
      !thread.streamEpoch || !snapshot.streamEpoch || thread.streamEpoch === snapshot.streamEpoch);
    if (sameEpoch && prior !== undefined && snapshot.revision < prior) return;
    this.stateRevisions.set(deviceId, snapshot.revision);
    const advertised = snapshot.activeTurns ?? (snapshot.activeThreadId && snapshot.activeTurnId ? [{
      threadId: snapshot.activeThreadId, turnId: snapshot.activeTurnId, status: snapshot.status,
      startedAt: snapshot.startedAt ?? snapshot.lastActivityAt, lastActivityAt: snapshot.lastActivityAt,
      currentProject: snapshot.currentProject, currentActivity: snapshot.currentActivity,
      runningCommand: snapshot.runningCommand, changedFiles: snapshot.changedFiles,
      pendingApprovalCount: snapshot.pendingApprovalCount,
    }] : []);
    const states = new Map(snapshot.threads?.map((state) => [state.threadId, state]) ?? []);
    const active = new Map(advertised.map((turn) => [turn.threadId, turn]));
    for (const threadId of new Set([...Object.keys(this.device(deviceId)), ...states.keys(), ...active.keys()])) {
      const thread = this.ensure(deviceId, threadId);
      const changedEpoch = Boolean(thread.streamEpoch && snapshot.streamEpoch && thread.streamEpoch !== snapshot.streamEpoch);
      const changedService = Boolean(thread.serviceInstanceId && snapshot.serviceInstanceId &&
        thread.serviceInstanceId !== snapshot.serviceInstanceId);
      const nextState = states.get(threadId);
      let nextActive = active.get(threadId);
      // A late snapshot cannot revive an already completed turn or overwrite newer stream state.
      const olderStream = !changedEpoch && snapshot.lastSequence !== undefined && snapshot.lastSequence < thread.sequence;
      if (nextActive && nextActive.turnId === thread.state.lastTurnId && terminal(thread.state.lastTurnStatus)) nextActive = undefined;
      this.put(deviceId, {
        ...thread,
        // Keep the old cursor until watch installs a view for the new instance.
        // Relabeling old content would incorrectly turn recovery into a replay.
        serviceInstanceId: changedService ? thread.serviceInstanceId : snapshot.serviceInstanceId ?? thread.serviceInstanceId,
        streamEpoch: changedEpoch ? thread.streamEpoch : snapshot.streamEpoch ?? thread.streamEpoch,
        needsRecovery: thread.needsRecovery || changedEpoch || changedService,
        controlAllowed: changedEpoch || changedService ? false : thread.controlAllowed,
        activeTurn: olderStream ? thread.activeTurn : nextActive,
        state: olderStream ? thread.state : nextState ?? {
          ...thread.state,
          threadState: nextActive ? 'active' : 'idle', activeTurnId: nextActive?.turnId,
          waitingOnApproval: nextActive?.status === 'WaitingApproval',
          waitingOnUserInput: nextActive?.status === 'WaitingUserInput',
          activity: nextActive?.currentActivity,
        },
      });
    }
  }

  applyWatch(deviceId: string, result: CodexThreadWatchResult, buffered: CodexEvent[] = []): void {
    const previous = this.ensure(deviceId, result.threadId);
    const replayOnly = !result.resyncRequired && Boolean(previous.history) &&
      previous.streamEpoch === result.streamEpoch && previous.serviceInstanceId === result.serviceInstanceId;
    const activeTurns = new Set(result.history.turns.filter((turn) => !terminal(turn.status)).map((turn) => turn.turnId));
    if (result.snapshot.activeTurns) {
      for (const turn of result.snapshot.activeTurns) if (turn.threadId === result.threadId) activeTurns.add(turn.turnId);
    }
    const next: ThreadView = {
      ...previous, serviceInstanceId: result.serviceInstanceId, streamEpoch: result.streamEpoch,
      sequence: replayOnly ? previous.sequence : result.sequence,
      history: replayOnly ? previous.history : result.history, events: replayOnly ? previous.events : [],
      items: replayOnly ? previous.items : result.history.entries.map((entry) => {
        const existing = previous.items.find((item) => item.itemId === entry.itemId && item.turnId === entry.turnId);
        if (entry.truncated && existing && !existing.incomplete && !existing.streaming && existing.text.length > entry.text.length) return existing;
        return { ...entry, incomplete: entry.truncated,
        // The upstream snapshot has no atomic delta cursor. Never guess an overlap.
        streaming: entry.role === 'assistant' && activeTurns.has(entry.turnId),
        syncing: entry.role === 'assistant' && activeTurns.has(entry.turnId),
      }; }),
      approvals: result.approvals, watching: true, syncing: false, needsRecovery: false,
      controlAllowed: result.controlAllowed, policyReason: result.policyReason, error: undefined,
    };
    if (!replayOnly) this.seen.delete(this.key(deviceId, result.threadId));
    this.put(deviceId, next);
    this.applySnapshot(deviceId, result.snapshot);
    const replay = [...result.events, ...buffered].filter((event) => event.threadId === result.threadId &&
      (!event.streamEpoch || event.streamEpoch === result.streamEpoch) &&
      (event.sequence === undefined || event.sequence > result.sequence))
      .sort((a, b) => (a.sequence ?? 0) - (b.sequence ?? 0));
    for (const event of replay) this.applyEvent(deviceId, event);
  }

  applyEvent(deviceId: string, event: CodexEvent): boolean {
    // Unattributed events must never leak into the selected conversation.
    if (!event.threadId) return false;
    let thread = this.ensure(deviceId, event.threadId);
    const key = this.key(deviceId, event.threadId);
    const seen = this.seen.get(key) ?? new Set<string>();
    const id = `${event.streamEpoch ?? ''}\0${event.eventId}`;
    if (seen.has(id)) return false;
    if (thread.streamEpoch && event.streamEpoch && thread.streamEpoch !== event.streamEpoch) {
      this.put(deviceId, { ...thread, needsRecovery: true, controlAllowed: false });
      return false;
    }
    if (event.sequence !== undefined && event.sequence <= thread.sequence) return false;
    seen.add(id);
    if (seen.size > 10_000) seen.delete(seen.values().next().value!);
    this.seen.set(key, seen);
    thread = { ...thread,
      serviceInstanceId: event.serviceInstanceId ?? thread.serviceInstanceId,
      streamEpoch: event.streamEpoch ?? thread.streamEpoch,
      sequence: event.sequence ?? thread.sequence,
      lastActivityAt: Math.max(thread.lastActivityAt, event.occurredAt),
      items: [...thread.items], approvals: [...thread.approvals], state: { ...thread.state },
    };
    const sameItem = (candidate: CodexEvent) => candidate.itemId === event.itemId && candidate.turnId === event.turnId;
    const events = thread.events;
    let rendered = event;
    if (event.kind === 'AgentMessageDelta' && event.itemId) {
      const previous = events.find((candidate) => candidate.kind === 'AgentMessageDelta' && sameItem(candidate));
      if (events.some((candidate) => candidate.kind === 'AgentMessageCompleted' && sameItem(candidate))) return false;
      rendered = { ...event, data: { ...event.data, text: `${string(previous?.data.text) ?? ''}${string(event.data.delta) ?? ''}` } };
      thread.events = [rendered, ...events.filter((candidate) => candidate !== previous)];
    } else if (event.itemId && (event.kind === 'AgentMessageCompleted' || event.kind === 'UserMessageCompleted')) {
      thread.events = [event, ...events.filter((candidate) => !(sameItem(candidate) &&
        (candidate.kind === event.kind || candidate.kind === 'ContentIncomplete' ||
          event.kind === 'AgentMessageCompleted' && candidate.kind === 'AgentMessageDelta')))];
    } else {
      thread.events = [event, ...events];
    }
    if (thread.events.length > 1_000) thread.events = thread.events.slice(0, 1_000);
    this.applyItem(thread, event);
    this.applyLifecycle(thread, event);
    if (event.data.truncated === true || event.data.resyncRequired === true) thread.needsRecovery = true;
    if (event.kind === 'ApprovalRequested') {
      const approval = event.data as unknown as ApprovalRequested;
      thread.approvals = [approval, ...thread.approvals.filter((value) => value.approvalId !== approval.approvalId)];
      thread.state.waitingOnApproval = true;
    } else if (event.kind === 'ApprovalResolving') {
      thread.approvals = thread.approvals.map((value) => value.approvalId === event.data.approvalId
        ? { ...value, isResolving: true } : value);
    } else if (event.kind === 'ApprovalResolved') {
      thread.approvals = thread.approvals.filter((value) => value.approvalId !== event.data.approvalId);
      thread.state.waitingOnApproval = thread.approvals.length > 0;
    } else if (event.kind === 'ThreadSettingsChanged') {
      thread.needsRecovery = true;
    }
    if (thread.activeTurn) {
      thread.activeTurn = { ...thread.activeTurn, pendingApprovalCount: thread.approvals.length,
        status: thread.state.waitingOnApproval ? 'WaitingApproval' : thread.state.waitingOnUserInput ? 'WaitingUserInput'
          : thread.activeTurn.status === 'WaitingApproval' || thread.activeTurn.status === 'WaitingUserInput' ? 'Thinking' : thread.activeTurn.status,
      };
    }
    this.put(deviceId, thread);
    return true;
  }

  private applyItem(thread: ThreadView, event: CodexEvent): void {
    if (!event.turnId) return;
    const eventItemId = event.itemId ?? (event.kind === 'PlanUpdated' ? 'plan' :
      event.kind === 'ErrorOccurred' || event.kind === 'UnsupportedItem' ? event.eventId : undefined);
    if (!eventItemId) return;
    const key = itemKey(event.turnId, eventItemId);
    const index = thread.items.findIndex((item) => itemKey(item.turnId, item.itemId) === key);
    const previous = thread.items[index];
    let item: ThreadItem | undefined;
    const base = { itemId: eventItemId, turnId: event.turnId, attachments: [], occurredAt: event.occurredAt,
      incomplete: event.data.truncated === true || event.data.resyncRequired === true };
    if (event.kind === 'ContentIncomplete') {
      const originalKind = string(event.data.originalKind) ?? '';
      item = { ...base, ...previous,
        role: previous?.role ?? (originalKind.startsWith('UserMessage') ? 'user'
          : originalKind.startsWith('AgentMessage') ? 'assistant' : 'tool'),
        text: previous?.text || '此条消息内容未完整，正在等待同步。',
        incomplete: true, syncing: true,
      };
    } else if (event.kind === 'AgentMessageDelta') {
      if (previous?.role === 'assistant' && !previous.streaming) return;
      const delta = string(event.data.delta) ?? '';
      const offset = number(event.data.offset);
      let text = previous?.role === 'assistant' ? previous.text : '';
      let syncing = previous?.syncing ?? false;
      if (offset !== undefined) {
        if (offset > text.length) { syncing = true; thread.needsRecovery = true; }
        else if (text.slice(offset, Math.min(text.length, offset + delta.length)) === delta.slice(0, Math.max(0, text.length - offset))) {
          text += delta.slice(Math.max(0, text.length - offset));
          syncing = false;
        } else { syncing = true; thread.needsRecovery = true; }
      } else if (!syncing) { text += delta; }
      item = { ...base, ...previous, role: 'assistant', text, streaming: true, syncing };
    } else if (event.kind === 'AgentMessageCompleted' || event.kind === 'UserMessageCompleted') {
      if (base.incomplete && previous && !previous.incomplete && !previous.streaming &&
          previous.text.length > (string(event.data.text)?.length ?? 0)) return;
      item = { ...base, ...previous, role: event.kind === 'UserMessageCompleted' ? 'user' : 'assistant',
        text: string(event.data.text) ?? '', streaming: false, syncing: false, incomplete: base.incomplete,
        attachments: Array.isArray(event.data.attachments) ? event.data.attachments as ThreadItem['attachments'] : [],
      };
    } else if (event.kind === 'CommandStarted' || event.kind === 'CommandCompleted' || event.kind === 'CommandOutputDelta') {
      const output = string(event.data.output) ?? string(event.data.delta);
      item = { ...base, ...previous, role: 'tool', phase: event.kind === 'CommandStarted' ? 'inProgress' : 'completed',
        text: event.kind === 'CommandOutputDelta' ? `${previous?.text ?? ''}${output ?? ''}`
          : [string(event.data.command), output].filter(Boolean).join('\n') || previous?.text || '',
      };
    } else if (event.kind === 'FileChanged') {
      const changes = Array.isArray(event.data.changes) ? event.data.changes as ThreadItem['changes'] : undefined;
      item = { ...base, role: 'tool', phase: 'completed', changes,
        text: changes?.map((change) => change.path).join('\n') ?? string(event.data.text) ?? '',
      };
    } else if (event.kind === 'ReasoningSummaryDelta') {
      item = { ...base, role: 'tool', phase: 'commentary', text: `${previous?.text ?? ''}${string(event.data.delta) ?? ''}` };
    } else if (event.kind === 'PlanUpdated') {
      const plan = Array.isArray(event.data.plan) ? event.data.plan as { step?: string; status?: string }[] : [];
      item = { ...base, role: 'tool', phase: 'commentary',
        text: [string(event.data.explanation), ...plan.map((step) => `${step.status ?? ''} ${step.step ?? ''}`)].filter(Boolean).join('\n'),
      };
    } else if (event.kind === 'ErrorOccurred' || event.kind === 'UnsupportedItem') {
      if (event.kind === 'UnsupportedItem' && previous) return;
      item = { ...base, role: 'tool', phase: event.kind === 'ErrorOccurred' ? 'failed' : 'unsupported',
        text: string(event.data.message) ?? '此过程事件暂不支持完整显示',
      };
    }
    if (!item) return;
    if (index < 0) thread.items.push(item); else thread.items[index] = item;
  }

  private applyLifecycle(thread: ThreadView, event: CodexEvent): void {
    const status = string(event.data.status);
    if (event.kind === 'TurnStarted' && event.turnId) {
      if (thread.state.lastTurnId === event.turnId && terminal(thread.state.lastTurnStatus)) return;
      thread.state = { ...thread.state, threadState: 'active', activeTurnId: event.turnId,
        waitingOnApproval: false, waitingOnUserInput: false };
      thread.activeTurn = { threadId: thread.threadId, turnId: event.turnId, status: 'Thinking',
        startedAt: number(event.data.startedAt) ?? event.occurredAt, lastActivityAt: event.occurredAt,
        changedFiles: [], pendingApprovalCount: 0 };
    } else if (event.kind === 'TurnCompleted' && event.turnId) {
      const isCurrent = !thread.state.activeTurnId || thread.state.activeTurnId === event.turnId;
      if (isCurrent) {
        thread.state = { ...thread.state, threadState: 'idle', activeTurnId: undefined,
          lastTurnId: event.turnId, lastTurnStatus: status ?? 'completed', waitingOnApproval: false, waitingOnUserInput: false };
        thread.activeTurn = undefined;
      }
      thread.approvals = thread.approvals.filter((approval) => approval.turnId !== event.turnId);
      if (thread.items.some((item) => item.turnId === event.turnId && item.syncing)) thread.needsRecovery = true;
      if (thread.history) thread.history = { ...thread.history, turns: [
        ...thread.history.turns.filter((turn) => turn.turnId !== event.turnId),
        { turnId: event.turnId, status: status ?? 'completed', startedAt: number(event.data.startedAt),
          completedAt: number(event.data.completedAt) ?? event.occurredAt, durationMs: number(event.data.durationMs) },
      ] };
    } else if (event.kind === 'ThreadStatusChanged') {
      const flags = Array.isArray(event.data.activeFlags) ? event.data.activeFlags : [];
      if (event.turnId && thread.state.activeTurnId && event.turnId !== thread.state.activeTurnId) return;
      thread.state = { ...thread.state, threadState: status ?? 'unknown',
        waitingOnApproval: flags.includes('waitingOnApproval') || thread.approvals.length > 0,
        waitingOnUserInput: flags.includes('waitingOnUserInput') };
      if (status === 'idle') { thread.state.activeTurnId = undefined; thread.activeTurn = undefined; }
      if (status === 'active' && !thread.activeTurn) thread.needsRecovery = true;
    } else if (thread.activeTurn && (!event.turnId || event.turnId === thread.activeTurn.turnId)) {
      let activity: string | undefined;
      if (event.kind === 'AgentMessageDelta') activity = 'Streaming';
      else if (event.kind === 'CommandStarted') activity = 'RunningCommand';
      else if (event.kind === 'FileChanged') activity = 'Editing';
      if (activity) {
        thread.state.activity = activity;
        thread.activeTurn = { ...thread.activeTurn, status: activity, lastActivityAt: event.occurredAt };
      }
    }
  }

  private key(deviceId: string, threadId: string): string { return `${deviceId}\0${threadId}`; }
  private put(deviceId: string, thread: ThreadView): void {
    this.devices[deviceId] = { ...this.device(deviceId), [thread.threadId]: thread };
  }
  private ensure(deviceId: string, threadId: string): ThreadView {
    return this.view(deviceId, threadId) ?? {
      threadId, sequence: 0, items: [], events: [], approvals: [], watching: false, syncing: false,
      needsRecovery: false, controlAllowed: false, lastActivityAt: 0,
      state: { threadId, threadState: 'unknown', waitingOnApproval: false, waitingOnUserInput: false },
    };
  }
}
