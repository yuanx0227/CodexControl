import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type FormEvent } from 'react';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import packageJson from '../package.json';
import type {
  ApprovalRequested,
  CodexActiveTurn,
  CodexApprovalPolicy,
  CodexEvent,
  CodexProjectSummary,
  CodexSnapshot,
  CodexSessionOptions,
  CodexThreadReadResult,
  CodexThreadHistoryAttachment,
  CodexThreadHistoryFileChange,
  CodexThreadSummary,
  DeviceSummary,
} from './protocol';
import { RelayClient, type RelayClientState } from './relayClient';
import { loadRelayUrl, saveRelayUrl } from './storage';
import type { ThreadView } from './threadStore';

const initialState: RelayClientState = {
  connection: 'offline',
  authenticated: false,
  devices: [],
  events: {},
  approvals: {},
  threads: {},
};

export function App() {
  const client = useMemo(() => new RelayClient(), []);
  const [pairingLink, setPairingLink] = useState<PairingLink | undefined>(() => readPairingLink());
  const [linkedCode, setLinkedCode] = useState('');
  const [state, setState] = useState(initialState);
  const [selectedId, setSelectedId] = useState<string | undefined>(() =>
    localStorage.getItem('codex-control-last-device') ?? undefined,
  );
  const [showPairing, setShowPairing] = useState(false);
  const [toast, setToast] = useState<string>();
  const toastTimer = useRef<number | undefined>(undefined);

  const showToast = useCallback((message: string) => {
    if (toastTimer.current) window.clearTimeout(toastTimer.current);
    setToast(message);
    toastTimer.current = window.setTimeout(() => {
      setToast(undefined);
      toastTimer.current = undefined;
    }, 2_500);
  }, []);

  useEffect(() => {
    const unsubscribe = client.subscribe(setState);
    return () => {
      unsubscribe();
      client.stop();
    };
  }, [client]);

  useEffect(() => {
    if (!pairingLink) void client.start();
  }, [client, pairingLink]);

  useEffect(() => () => {
    if (toastTimer.current) window.clearTimeout(toastTimer.current);
  }, []);

  const selected = state.devices.find((device) => device.deviceId === selectedId);
  if (selected) {
    return (
      <DeviceWorkspace
        key={selected.deviceId}
        client={client}
        device={selected}
        events={state.events[selected.deviceId] ?? []}
        approvals={state.approvals[selected.deviceId] ?? []}
        threadViews={state.threads[selected.deviceId] ?? {}}
        connection={state.connection}
        authenticated={state.authenticated}
        relayVersion={state.relayVersion}
        onBack={() => setSelectedId(undefined)}
        onApprove={async (approvalId, decision) => {
          await client.approve(selected.deviceId, approvalId, decision);
          showToast('审批结果已提交');
        }}
        onInterrupt={async (threadId, turnId) => {
          await client.interrupt(selected.deviceId, threadId, turnId);
          showToast('停止请求已接受，等待 Interrupted 终态');
        }}
        onRevoke={async () => {
          await client.revoke(selected.deviceId);
          setSelectedId(undefined);
          localStorage.removeItem('codex-control-last-device');
          showToast('已解除配对');
        }}
        onNotify={showToast}
        toast={toast}
      />
    );
  }

  const needsPairing = Boolean(pairingLink) || showPairing || state.devices.length === 0;
  return (
    <main className="landing-shell">
      <LandingHeader connection={state.connection} relayVersion={state.relayVersion} />
      {needsPairing ? (
        <PairingPanel
          connection={state.connection}
          error={state.lastError}
          pending={state.pairingPending}
          pairingLink={pairingLink}
          initialCode={linkedCode}
          onConfirmLink={(link) => {
            saveRelayUrl(controllerWebSocketUrl(link.relayRoot));
            window.history.replaceState(null, '', `${window.location.pathname}${window.location.search}`);
            setLinkedCode(link.code);
            setPairingLink(undefined);
          }}
          hasDevices={state.devices.length > 0}
          onCancel={() => setShowPairing(false)}
          onPair={async (code, controllerName) => {
            await client.pair(code, controllerName);
            setShowPairing(false);
            showToast('配对成功');
          }}
        />
      ) : (
        <section className="device-home" aria-label="设备列表">
          <div className="device-home-heading">
            <div>
              <p className="overline">Codex Control</p>
              <h1>开发电脑</h1>
              <p>选择一台电脑，继续历史会话或开始新任务。</p>
            </div>
            <button className="quiet-button" onClick={() => setShowPairing(true)}>添加电脑</button>
          </div>
          <div className="device-list">
            {state.devices.map((device) => (
              <DeviceRow
                key={device.deviceId}
                device={device}
                onOpen={() => {
                  localStorage.setItem('codex-control-last-device', device.deviceId);
                  setSelectedId(device.deviceId);
                }}
              />
            ))}
          </div>
          {toast && <div key={toast} className="toast">{toast}</div>}
        </section>
      )}
    </main>
  );
}

function LandingHeader({
  connection,
  relayVersion,
}: {
  connection: RelayClientState['connection'];
  relayVersion?: string;
}) {
  return (
    <header className="landing-header">
      <Brand />
      <div className="landing-meta">
        <ConnectionState connection={connection} />
        <small>Web v{packageJson.version} · Relay v{relayVersion ?? '未知'} · Protocol v2</small>
      </div>
    </header>
  );
}

function Brand() {
  return (
    <div className="brand">
      <span className="brand-glyph">C</span>
      <strong>Codex Control</strong>
    </div>
  );
}

function ConnectionState({ connection }: { connection: RelayClientState['connection'] }) {
  const label = connection === 'connected' ? 'Relay 已连接' : connection === 'connecting' ? '正在连接' : 'Relay 离线';
  return <span className={`connection-state ${connection}`}><i />{label}</span>;
}

function PairingPanel({
  connection,
  error,
  pending,
  pairingLink,
  initialCode,
  onConfirmLink,
  hasDevices,
  onCancel,
  onPair,
}: {
  connection: RelayClientState['connection'];
  error?: string;
  pending?: string;
  pairingLink?: PairingLink;
  initialCode: string;
  onConfirmLink: (link: PairingLink) => void;
  hasDevices: boolean;
  onCancel: () => void;
  onPair: (code: string, controllerName: string) => Promise<void>;
}) {
  const [code, setCode] = useState(initialCode);
  const [controllerName, setControllerName] = useState(() => browserControllerName());
  const [relayUrl, setRelayUrl] = useState(loadRelayUrl());
  const [submitting, setSubmitting] = useState(false);
  const [message, setMessage] = useState<string>();

  useEffect(() => {
    if (initialCode) setCode(initialCode);
  }, [initialCode]);

  async function submit(event: FormEvent) {
    event.preventDefault();
    setSubmitting(true);
    setMessage(undefined);
    try {
      await onPair(code, controllerName);
    } catch (reason) {
      setMessage(reason instanceof Error ? reason.message : String(reason));
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <section className="pairing-view">
      <div className="pairing-panel">
        <span className="pairing-icon">↔</span>
        <h1>连接你的电脑</h1>
        {pairingLink && (
          <section className="pairing-link-summary">
            <p>将连接 Relay：<strong>{new URL(pairingLink.relayRoot).host}</strong></p>
            <p>电脑：<strong>{pairingLink.deviceName ?? '二维码中的电脑'}</strong></p>
            <button className="solid-button full-button" onClick={() => onConfirmLink(pairingLink)}>
              确认 Relay 并继续
            </button>
          </section>
        )}
        <p>扫描电脑设置页二维码，或输入三分钟内有效的六位码。</p>
        {!pairingLink && <form onSubmit={submit}>
          <label htmlFor="pairing-code">六位配对码</label>
          <input
            id="pairing-code"
            className="pairing-code"
            inputMode="numeric"
            autoComplete="one-time-code"
            maxLength={7}
            placeholder="583 271"
            value={formatCode(code)}
            onChange={(event) => setCode(event.target.value.replaceAll(/\D/gu, '').slice(0, 6))}
            autoFocus
          />
          <label htmlFor="controller-name">控制端名称</label>
          <input
            id="controller-name"
            maxLength={200}
            value={controllerName}
            onChange={(event) => setControllerName(event.target.value)}
          />
          <button className="solid-button full-button" disabled={code.length !== 6 || submitting || connection === 'offline'}>
            {submitting ? '正在配对…' : '配对'}
          </button>
        </form>}
        <details className="relay-settings">
          <summary>Relay 设置</summary>
          <label htmlFor="relay-url">WebSocket 地址</label>
          <div className="settings-row">
            <input id="relay-url" value={relayUrl} onChange={(event) => setRelayUrl(event.target.value)} />
            <button
              type="button"
              className="quiet-button"
              onClick={() => {
                saveRelayUrl(relayUrl);
                window.location.reload();
              }}
            >
              保存并重连
            </button>
          </div>
        </details>
        {pending && <p role="status" className="inline-status">{pending}</p>}
        {(message || error) && <p role="alert" className="inline-error">{message ?? error}</p>}
        {hasDevices && <button className="link-button" onClick={onCancel}>返回设备列表</button>}
      </div>
    </section>
  );
}

function DeviceRow({ device, onOpen }: { device: DeviceSummary; onOpen: () => void }) {
  const runningCount = activeTurnsForSnapshot(device.snapshot).length;
  const managedRunning = runningCount > 0;
  return (
    <button className="device-row" onClick={onOpen} aria-label={`打开 ${device.name}`}>
      <span className="device-avatar">{device.name.slice(0, 1).toUpperCase()}</span>
      <span className="device-copy">
        <strong>{device.name}</strong>
        <small>{device.online
          ? managedRunning
            ? runningCount > 1
              ? `${runningCount} 个 Agent 托管任务运行中`
              : device.snapshot?.currentProject ?? 'Agent 托管任务运行中'
            : 'Agent 当前未托管任务'
          : '设备离线'}</small>
      </span>
      <StatusPill
        online={device.online}
        status={managedRunning ? device.snapshot?.status : 'Idle'}
        label={managedRunning ? `Agent 运行中${runningCount > 1 ? ` · ${runningCount}` : ''}` : device.online ? 'Agent 空闲' : undefined}
      />
      <span className="row-chevron">›</span>
    </button>
  );
}

function ConversationLink({
  thread,
  active,
  activity,
  readAt,
  className,
  title,
  onClick,
}: {
  thread: CodexThreadSummary;
  active: boolean;
  activity?: ThreadActivityState;
  readAt?: number;
  className: string;
  title: string;
  onClick: () => void;
}) {
  const unread = Boolean(activity && activity.latestAt > (readAt ?? 0));
  return (
    <button
      className={`conversation-link ${className} ${active ? 'active' : ''}`}
      title={title}
      onClick={onClick}
    >
      <span className="conversation-link-title">{thread.name ?? thread.preview ?? '未命名会话'}</span>
      {activity?.status === 'running' ? (
        <span className="thread-indicator running" title="任务运行中" aria-label="任务运行中" />
      ) : unread && activity?.status === 'completed' ? (
        <span className="thread-indicator completed-unread" title="任务已完成，尚未阅读" aria-label="完成未读">✓</span>
      ) : unread && activity?.status === 'failed' ? (
        <span className="thread-indicator failed-unread" title="任务失败，尚未阅读" aria-label="失败未读">!</span>
      ) : unread ? (
        <span className="thread-indicator unread" title="有未读更新" aria-label="未读更新" />
      ) : null}
    </button>
  );
}

interface ChatEntry {
  id: string;
  role: 'user' | 'assistant' | 'tool' | 'system' | 'summary';
  text: string;
  meta?: string;
  streaming?: boolean;
  turnId?: string;
  occurredAt?: number;
  durationMs?: number;
  attachments?: CodexThreadHistoryAttachment[];
  processItems?: ChatEntry[];
  changes?: CodexThreadHistoryFileChange[];
}

interface ProjectNavigationGroup {
  key: string;
  name: string;
  cwd?: string;
  position: number;
  threads: CodexThreadSummary[];
}

interface ThreadNavigation {
  projects: ProjectNavigationGroup[];
  recent: CodexThreadSummary[];
}

type ThreadActivityStatus = 'running' | 'completed' | 'failed' | 'updated';

interface ThreadActivityState {
  latestAt: number;
  status: ThreadActivityStatus;
}

function sharedThreadActivity(view?: ThreadView): ThreadActivityState | undefined {
  if (!view) return undefined;
  return { latestAt: view.lastActivityAt, status: view.state.threadState === 'active' ? 'running'
    : view.state.lastTurnStatus === 'failed' || view.state.threadState === 'systemError' ? 'failed'
    : view.state.lastTurnStatus ? 'completed' : 'updated' };
}

function DeviceWorkspace({
  client,
  device,
  events,
  approvals,
  threadViews,
  connection,
  authenticated,
  relayVersion,
  onBack,
  onApprove,
  onInterrupt,
  onRevoke,
  onNotify,
  toast,
}: {
  client: RelayClient;
  device: DeviceSummary;
  events: CodexEvent[];
  approvals: ApprovalRequested[];
  threadViews: Record<string, ThreadView>;
  connection: RelayClientState['connection'];
  authenticated: boolean;
  relayVersion?: string;
  onBack: () => void;
  onApprove: (approvalId: string, decision: unknown) => Promise<void>;
  onInterrupt: (threadId: string, turnId: string) => Promise<void>;
  onRevoke: () => Promise<void>;
  onNotify: (message: string) => void;
  toast?: string;
}) {
  const [threads, setThreads] = useState<CodexThreadSummary[]>([]);
  const [projects, setProjects] = useState<CodexProjectSummary[]>([]);
  const [historyLoading, setHistoryLoading] = useState(true);
  const [legacyThreadHistory, setThreadHistory] = useState<CodexThreadReadResult>();
  const [threadHistoryLoading, setThreadHistoryLoading] = useState(false);
  const [threadHistoryError, setThreadHistoryError] = useState<string>();
  const [selectedThreadId, setSelectedThreadId] = useState<string | undefined>(() =>
    activeTurnsForSnapshot(device.snapshot)[0]?.threadId,
  );
  const [sessionOptions, setSessionOptions] = useState<CodexSessionOptions>();
  const [sessionOptionsLoading, setSessionOptionsLoading] = useState(true);
  const [selectedModel, setSelectedModel] = useState(() =>
    loadSessionPreference(device.deviceId).model ?? '',
  );
  const [approvalPolicy, setApprovalPolicy] = useState<CodexApprovalPolicy>(() =>
    loadSessionPreference(device.deviceId).approvalPolicy ?? 'untrusted',
  );
  const [expandedProjects, setExpandedProjects] = useState<Set<string>>(() => new Set());
  const [newSession, setNewSession] = useState(false);
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [composer, setComposer] = useState('');
  const [newCwd, setNewCwd] = useState(device.snapshot?.currentProject ?? '');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [interrupting, setInterrupting] = useState(false);
  const [localEntries, setLocalEntries] = useState<ChatEntry[]>([]);
  const [acceptedSharedSteers, setAcceptedSharedSteers] = useState<Record<string, { turnId: string; text: string }>>({});
  const [clockNow, setClockNow] = useState(() => Date.now());
  const [threadActivities, setThreadActivities] = useState<Record<string, ThreadActivityState>>(() =>
    loadThreadActivities(device.deviceId),
  );
  const [readReceipts, setReadReceipts] = useState<Record<string, number>>(() =>
    loadThreadReadReceipts(device.deviceId),
  );
  const [newMessageCount, setNewMessageCount] = useState(0);
  const [isAtBottom, setIsAtBottom] = useState(true);
  const threadHistoryRequest = useRef(0);
  const chatFeed = useRef<HTMLElement>(null);
  const stickChatToBottom = useRef(true);
  const initialScrollThread = useRef<string | undefined>(undefined);
  const previousTailSignature = useRef<string | undefined>(undefined);
  const previousSelectedEventCount = useRef(0);
  const preservedScrollTop = useRef(0);
  const userScrolling = useRef(false);
  const lastRefreshedCompletion = useRef<string | undefined>(undefined);
  const optimisticThreadIds = useRef(new Set<string>());
  const snapshot = device.snapshot;
  const shared = snapshot?.sharedSession === true;
  const sharedSupported = snapshot?.capabilities?.includes('sharedSessionV1') === true;
  const sharedView = shared && selectedThreadId ? threadViews[selectedThreadId] : undefined;
  const acceptedSharedSteer = !newSession && selectedThreadId ? acceptedSharedSteers[selectedThreadId] : undefined;
  const threadHistory = shared ? sharedView?.history : legacyThreadHistory;
  const sharedOffline = shared && (!authenticated || !device.online || connection !== 'connected' ||
    snapshot?.connectionState !== 'online');
  const activeTurns = useMemo(() => shared
    ? Object.values(threadViews).flatMap((thread) => thread.activeTurn ? [thread.activeTurn] : [])
    : reconcileActiveTurns(snapshot, events), [snapshot, events, shared, threadViews]);
  const activeTurnByThread = useMemo(
    () => new Map(activeTurns.map((turn) => [turn.threadId, turn])),
    [activeTurns],
  );
  const workspaceConnection = authenticated
    ? connection
    : connection === 'offline'
      ? 'offline'
      : 'connecting';

  const refreshThreads = useCallback(async (background = false) => {
    if (!background) {
      setHistoryLoading(true);
      setError(undefined);
    }
    try {
      const history = await client.listThreads(device.deviceId);
      setThreads((current) => {
        const returnedIds = new Set(history.threads.map((thread) => thread.threadId));
        returnedIds.forEach((threadId) => optimisticThreadIds.current.delete(threadId));
        const optimistic = current.filter((thread) =>
          optimisticThreadIds.current.has(thread.threadId) && !returnedIds.has(thread.threadId),
        );
        return [...optimistic, ...history.threads];
      });
      setProjects(history.projects);
      if (!background) setError(undefined);
    } catch (reason) {
      if (!background) setError(reason instanceof Error ? reason.message : String(reason));
    } finally {
      if (!background) setHistoryLoading(false);
    }
  }, [client, device.deviceId]);

  useEffect(() => {
    setAcceptedSharedSteers((current) => {
      let next = current;
      for (const [threadId, input] of Object.entries(current)) {
        const state = threadViews[threadId]?.state;
        if (state?.freshness !== 'current' || state.lastTurnId !== input.turnId ||
            !['completed', 'failed', 'interrupted'].includes(state.lastTurnStatus ?? '')) continue;
        if (next === current) next = { ...current };
        delete next[threadId];
      }
      return next;
    });
  }, [acceptedSharedSteers, threadViews]);

  const loadThread = useCallback(async (threadId: string, background = false) => {
    const request = ++threadHistoryRequest.current;
    if (!background) {
      setThreadHistoryLoading(true);
      setThreadHistoryError(undefined);
    }
    try {
      if (shared) {
        await client.watchThread(device.deviceId, threadId);
        return;
      }
      let history: CodexThreadReadResult | undefined;
      let lastReason: unknown;
      for (let attempt = 0; attempt < 4; attempt += 1) {
        try {
          history = await client.readThread(device.deviceId, threadId);
          break;
        } catch (reason) {
          lastReason = reason;
          const retryable = optimisticThreadIds.current.has(threadId) &&
            isTransientNewThreadReadFailure(reason);
          if (!retryable || attempt === 3) throw reason;
          await wait(200 * (attempt + 1));
          if (request !== threadHistoryRequest.current) return;
        }
      }
      if (!history) throw lastReason ?? new Error('Agent 未返回会话内容');
      if (request !== threadHistoryRequest.current) return;
      setThreadHistory(history);
    } catch (reason) {
      if (request !== threadHistoryRequest.current) return;
      if (background) return;
      setThreadHistory(undefined);
      setThreadHistoryError(reason instanceof Error ? reason.message : String(reason));
    } finally {
      if (request === threadHistoryRequest.current) setThreadHistoryLoading(false);
    }
  }, [client, device.deviceId, shared]);

  const markThreadRead = useCallback((threadId: string, readAt?: number) => {
    if (!readAt) return;
    setReadReceipts((current) => current[threadId] !== undefined && current[threadId] >= readAt
      ? current
      : { ...current, [threadId]: readAt });
  }, []);

  useEffect(() => {
    if (!authenticated || !device.online) {
      setHistoryLoading(false);
      return;
    }
    void refreshThreads();
  }, [authenticated, device.online, refreshThreads]);

  useEffect(() => {
    if (shared && !newSession) {
      setSessionOptionsLoading(false);
      return;
    }
    if (!authenticated || !device.online) {
      setSessionOptionsLoading(false);
      return;
    }

    let cancelled = false;
    setSessionOptionsLoading(true);
    void client.getSessionOptions(device.deviceId)
      .then((options) => {
        if (cancelled) return;
        setSessionOptions(options);
        setSelectedModel((current) => options.models.some((option) => option.model === current)
          ? current
          : options.models.find((option) => option.isDefault)?.model ?? options.models[0]?.model ?? '');
        setApprovalPolicy((current) => options.approvalPolicies.some((option) => option.id === current)
          ? current
          : options.approvalPolicies.find((option) => option.isDefault)?.id ?? 'untrusted');
      })
      .catch((reason) => {
        if (!cancelled) setError(reason instanceof Error ? reason.message : String(reason));
      })
      .finally(() => {
        if (!cancelled) setSessionOptionsLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [authenticated, client, device.deviceId, device.online, newSession, shared]);

  useEffect(() => {
    saveSessionPreference(device.deviceId, { model: selectedModel, approvalPolicy });
  }, [approvalPolicy, device.deviceId, selectedModel]);

  useEffect(() => {
    if (newSession || selectedThreadId || threads.length === 0) return;
    setSelectedThreadId(threads[0].threadId);
  }, [newSession, selectedThreadId, threads]);

  useEffect(() => {
    if (newSession || !selectedThreadId) {
      threadHistoryRequest.current += 1;
      setThreadHistory(undefined);
      setThreadHistoryLoading(false);
      setThreadHistoryError(undefined);
      return;
    }

    if (!authenticated || !device.online) {
      setThreadHistoryLoading(false);
      setThreadHistoryError(undefined);
      return;
    }

    initialScrollThread.current = selectedThreadId;
    setThreadHistory(undefined);
    void loadThread(selectedThreadId);
  }, [authenticated, device.online, loadThread, newSession, selectedThreadId]);

  useEffect(() => {
    if (!selectedThreadId || !activeTurnByThread.has(selectedThreadId)) setInterrupting(false);
  }, [activeTurnByThread, selectedThreadId, interrupting]);

  useEffect(() => {
    setThreadActivities((current) => {
      let next = current;
      for (const event of [...events].reverse()) {
        if (!event.threadId) continue;
        const existing = next[event.threadId];
        if (existing && existing.latestAt > event.occurredAt) continue;
        const status = event.kind === 'ThreadStatusChanged'
          ? readEventStatus(event) === 'active' ? 'running'
            : readEventStatus(event) === 'systemError' ? 'failed'
              : existing?.status === 'completed' || existing?.status === 'failed' ? existing.status : 'updated'
          : event.kind === 'TurnStarted'
          ? 'running'
          : event.kind === 'TurnCompleted'
            ? readEventStatus(event) === 'failed' ? 'failed' : 'completed'
            : event.kind === 'ErrorOccurred'
              ? 'failed'
              : existing?.status ?? 'updated';
        if (existing?.latestAt === event.occurredAt && existing.status === status) continue;
        if (next === current) next = { ...current };
        next[event.threadId] = { latestAt: event.occurredAt, status };
      }
      return next;
    });
  }, [events]);

  useEffect(() => {
    setThreadActivities((current) => {
      let next = current;
      for (const [threadId, activity] of Object.entries(current)) {
        if (activity.status !== 'running' || activeTurns.some((turn) => turn.threadId === threadId) ||
            activity.latestAt > (snapshot?.lastActivityAt ?? 0)) continue;
        if (next === current) next = { ...current };
        next[threadId] = { ...activity, status: 'updated' };
      }
      for (const active of activeTurns) {
        const existing = next[active.threadId];
        if (existing && existing.latestAt >= active.lastActivityAt && existing.status === 'running') continue;
        if (next === current) next = { ...current };
        next[active.threadId] = {
          latestAt: Math.max(existing?.latestAt ?? 0, active.lastActivityAt),
          status: 'running',
        };
      }
      return next;
    });
  }, [activeTurns, snapshot]);

  useEffect(() => saveThreadActivities(device.deviceId, threadActivities), [device.deviceId, threadActivities]);
  useEffect(() => saveThreadReadReceipts(device.deviceId, readReceipts), [device.deviceId, readReceipts]);

  const navigation = useMemo(() => buildThreadNavigation(threads, projects), [projects, threads]);
  const selectedThread = threads.find((thread) => thread.threadId === selectedThreadId);

  useEffect(() => {
    if (navigation.projects.length === 0) return;
    setExpandedProjects((current) => {
      const available = new Set(navigation.projects.map((group) => group.key));
      const next = new Set([...current].filter((key) => available.has(key)));
      if (next.size === 0) next.add(navigation.projects[0].key);
      return setsEqual(current, next) ? current : next;
    });
  }, [navigation.projects]);

  useEffect(() => {
    if (!selectedThread) return;
    const group = navigation.projects.find((candidate) =>
      candidate.key === selectedThread.projectId || candidate.threads.includes(selectedThread),
    );
    if (!group) return;
    const key = group.key;
    setExpandedProjects((current) => {
      if (current.has(key)) return current;
      return new Set([key]);
    });
  }, [navigation.projects, selectedThread]);

  const selectedActiveTurn = selectedThreadId ? activeTurnByThread.get(selectedThreadId) : undefined;
  const hasActiveTurn = Boolean(selectedActiveTurn);
  const taskRunning = hasActiveTurn || sharedView?.state.threadState === 'active';
  const observedSession = events.some((event) => event.threadId === selectedThreadId) ||
    (snapshot?.activeThreadId === selectedThreadId && Boolean(selectedThreadId));
  const externalSession = Boolean(
    !shared && selectedThreadId && !selectedActiveTurn && !observedSession && !newSession,
  );
  const runningStartedAt = readTimestamp(selectedActiveTurn?.startedAt);
  const runningElapsedMs = taskRunning && runningStartedAt !== undefined
    ? Math.max(0, clockNow - runningStartedAt)
    : undefined;
  const conversationId = selectedThreadId;
  const conversationSnapshot = snapshotForActiveTurn(snapshot, selectedActiveTurn);
  const displayedActivity = selectedActiveTurn?.currentActivity;
  const displayedCommand = selectedActiveTurn?.runningCommand;

  useEffect(() => {
    if (!taskRunning) return;
    setClockNow(Date.now());
    const timer = window.setInterval(() => setClockNow(Date.now()), 1_000);
    return () => window.clearInterval(timer);
  }, [taskRunning, runningStartedAt]);
  const eventEntries = useMemo(
    () => buildChatEntries(
      events,
      conversationSnapshot,
      conversationId,
    ),
    [conversationId, conversationSnapshot, events],
  );
  const historicalEntries = useMemo<ChatEntry[]>(() => {
    if (!threadHistory) return [];
    const timings = new Map(threadHistory.turns.map((turn) => [turn.turnId, turn]));
    return threadHistory.entries.map((entry) => {
      const timing = timings.get(entry.turnId);
      return {
        id: `item_${entry.turnId}_${entry.itemId}`,
        role: entry.role,
        text: entry.text,
        turnId: entry.turnId,
        durationMs: resolveDurationMs(timing),
        attachments: entry.attachments,
        changes: entry.changes,
        meta: entry.phase === 'commentary'
          ? '过程更新'
          : entry.role === 'tool'
            ? processLabel(entry.phase)
            : undefined,
      };
    });
  }, [threadHistory]);
  const chatEntries = useMemo(
    () => shared
      ? foldProcessEntries((sharedView?.items ?? []).map((item): ChatEntry => ({
          id: `item_${item.turnId}_${item.itemId}`, role: item.role, text: item.text, turnId: item.turnId,
          streaming: item.streaming, occurredAt: item.occurredAt, attachments: item.attachments, changes: item.changes,
          durationMs: resolveDurationMs(sharedView?.history?.turns.find((turn) => turn.turnId === item.turnId)),
          meta: item.incomplete ? '内容不完整，等待同步' : item.syncing ? '正在同步完整内容' : item.streaming ? '实时回复'
            : item.role === 'tool' ? processLabel(item.phase) : item.phase === 'commentary' ? '过程更新' : undefined,
        })))
      : foldProcessEntries(mergeChatEntries(historicalEntries, localEntries, eventEntries)),
    [eventEntries, historicalEntries, localEntries, shared, sharedView],
  );
  const tailEntry = chatEntries.at(-1);
  const tailSignature = tailEntry ? `${tailEntry.id}\u0000${tailEntry.text}` : undefined;
  const selectedActivity = sharedView ? {
    latestAt: sharedView.lastActivityAt,
    status: sharedView.state.threadState === 'active' ? 'running'
      : sharedView.state.lastTurnStatus === 'failed' || sharedView.state.threadState === 'systemError' ? 'failed'
      : sharedView.state.lastTurnStatus ? 'completed' : 'updated',
  } : selectedThreadId ? threadActivities[selectedThreadId] : undefined;
  const selectedEventCount = selectedThreadId
    ? events.filter((event) => event.threadId === selectedThreadId).length
    : 0;
  const completedEvent = events.find((event) =>
    event.kind === 'TurnCompleted' &&
    selectedThreadId &&
    event.threadId === selectedThreadId,
  );
  const selectedApprovals = approvals.filter((approval) =>
    !approval.threadId || approval.threadId === selectedThreadId,
  );

  useEffect(() => {
    if (!threadHistory || threadHistory.threadId !== selectedThreadId ||
        initialScrollThread.current !== selectedThreadId) return;
    initialScrollThread.current = undefined;
    const frame = window.requestAnimationFrame(() => {
      if (chatFeed.current) {
        chatFeed.current.scrollTop = chatFeed.current.scrollHeight;
        preservedScrollTop.current = chatFeed.current.scrollTop;
        stickChatToBottom.current = true;
        setIsAtBottom(true);
        setNewMessageCount(0);
        markThreadRead(selectedThreadId, selectedActivity?.latestAt);
      }
    });
    return () => window.cancelAnimationFrame(frame);
  }, [markThreadRead, selectedThreadId, threadHistory?.threadId]);

  useLayoutEffect(() => {
    if (!tailSignature) return;
    const previous = previousTailSignature.current;
    previousTailSignature.current = tailSignature;
    if (!previous || previous === tailSignature) return;
    const frame = window.requestAnimationFrame(() => {
      if (!chatFeed.current) return;
      if (stickChatToBottom.current) {
        chatFeed.current.scrollTop = chatFeed.current.scrollHeight;
        preservedScrollTop.current = chatFeed.current.scrollTop;
        setNewMessageCount(0);
        if (selectedThreadId) markThreadRead(selectedThreadId, selectedActivity?.latestAt);
      } else {
        chatFeed.current.scrollTop = preservedScrollTop.current;
      }
    });
    return () => window.cancelAnimationFrame(frame);
  }, [markThreadRead, selectedActivity?.latestAt, selectedThreadId, tailSignature]);

  useEffect(() => {
    const previous = previousSelectedEventCount.current;
    previousSelectedEventCount.current = selectedEventCount;
    if (isAtBottom) {
      setNewMessageCount(0);
      return;
    }
    if (selectedEventCount > previous) {
      setNewMessageCount((count) => count + selectedEventCount - previous);
    }
  }, [isAtBottom, selectedEventCount]);

  useLayoutEffect(() => {
    if (isAtBottom || !chatFeed.current) return;
    const feed = chatFeed.current;
    const scrollTop = preservedScrollTop.current;
    const restore = () => {
      if (!stickChatToBottom.current) feed.scrollTop = scrollTop;
    };
    restore();
    const frame = window.requestAnimationFrame(restore);
    const timer = window.setTimeout(restore, 50);
    return () => {
      window.cancelAnimationFrame(frame);
      window.clearTimeout(timer);
    };
  }, [isAtBottom, selectedEventCount]);

  useEffect(() => {
    if (!selectedThreadId || !selectedActivity || !isAtBottom) return;
    markThreadRead(selectedThreadId, selectedActivity.latestAt);
  }, [isAtBottom, markThreadRead, selectedActivity, selectedThreadId]);

  useEffect(() => {
    if (shared) return;
    if (!completedEvent || !selectedThreadId || lastRefreshedCompletion.current === completedEvent.eventId) return;
    const timer = window.setTimeout(() => {
      lastRefreshedCompletion.current = completedEvent.eventId;
      void loadThread(selectedThreadId, true);
      void refreshThreads(true);
    }, 300);
    return () => window.clearTimeout(timer);
  }, [completedEvent, loadThread, refreshThreads, selectedThreadId, shared]);

  function selectThread(threadId: string) {
    if (threadId === selectedThreadId && !newSession) {
      void loadThread(threadId);
    } else {
      threadHistoryRequest.current += 1;
      setThreadHistory(undefined);
      setThreadHistoryLoading(true);
      setThreadHistoryError(undefined);
    }
    setSelectedThreadId(threadId);
    stickChatToBottom.current = true;
    setIsAtBottom(true);
    initialScrollThread.current = threadId;
    previousTailSignature.current = undefined;
    previousSelectedEventCount.current = 0;
    preservedScrollTop.current = 0;
    setNewMessageCount(0);
    setNewSession(false);
    setSidebarOpen(false);
    setLocalEntries([]);
    setComposer('');
    setError(undefined);
  }

  function beginNewSession(cwd?: string) {
    threadHistoryRequest.current += 1;
    setSelectedThreadId(undefined);
    stickChatToBottom.current = true;
    setIsAtBottom(true);
    initialScrollThread.current = undefined;
    previousTailSignature.current = undefined;
    previousSelectedEventCount.current = 0;
    preservedScrollTop.current = 0;
    setNewMessageCount(0);
    setNewSession(true);
    setSidebarOpen(false);
    setLocalEntries([]);
    setComposer('');
    setNewCwd(cwd?.trim() || snapshot?.currentProject || '');
    setError(undefined);
    setThreadHistory(undefined);
    setThreadHistoryLoading(false);
    setThreadHistoryError(undefined);
  }

  function toggleProject(key: string) {
    setExpandedProjects((current) => {
      return current.has(key) ? new Set() : new Set([key]);
    });
  }

  async function submit(event: FormEvent) {
    event.preventDefault();
    const text = composer.trim();
    if (!text || busy) return;
    setBusy(true);
    setError(undefined);
    if (!shared) setLocalEntries((entries) => [
      ...entries,
      { id: `local_${crypto.randomUUID()}`, role: 'user', text, meta: hasActiveTurn ? 'Steer' : undefined },
    ]);
    setComposer('');
    try {
      if (shared && selectedThreadId && !newSession) {
        if (!sharedView?.controlAllowed || sharedView.needsRecovery || sharedView.syncing || sharedOffline) {
          throw new Error(sharedView?.policyReason ?? '会话尚未同步完成，暂时无法发送');
        }
        if (taskRunning && !selectedActiveTurn?.turnId) throw new Error('任务正在运行，正在同步 Turn 信息');
        const action = await client.sendThread(device.deviceId, selectedThreadId, text, selectedActiveTurn?.turnId);
        if (selectedActiveTurn) setAcceptedSharedSteers((current) => ({
          ...current, [selectedThreadId]: { turnId: action.turnId, text },
        }));
        onNotify(taskRunning ? '干预已接受，等待当前任务处理' : '输入已接受，等待任务更新');
      } else if (selectedActiveTurn) {
        await client.steer(
          device.deviceId,
          selectedActiveTurn.threadId,
          selectedActiveTurn.turnId,
          text,
        );
        onNotify('干预已送入当前 Turn');
      } else if (selectedThread) {
        const action = await client.resumeThread(
          device.deviceId,
          selectedThread.threadId,
          text,
          selectedModel || undefined,
          approvalPolicy,
        );
        setThreads((current) => current.map((thread) =>
          thread.threadId === action.threadId ? { ...thread, status: 'active' } : thread,
        ));
        setSelectedThreadId(action.threadId);
        setNewSession(false);
        setThreadHistoryError(undefined);
        onNotify('历史会话已恢复，真实 Turn 已启动');
      } else {
        const cwd = newCwd.trim();
        const action = await client.startThread(
          device.deviceId,
          cwd,
          text,
          selectedModel || undefined,
          approvalPolicy,
        );
        optimisticThreadIds.current.add(action.threadId);
        const matchingProject = projects.find((project) =>
          project.roots.some((root) => normalizeProjectPath(root) === normalizeProjectPath(cwd)),
        );
        setThreads((current) => current.some((thread) => thread.threadId === action.threadId)
          ? current.map((thread) => thread.threadId === action.threadId
            ? { ...thread, status: 'active', cwd: thread.cwd ?? cwd }
            : thread)
          : [{
              threadId: action.threadId,
              preview: text.slice(0, 1_000),
              cwd,
              createdAt: Math.floor(Date.now() / 1_000),
              updatedAt: Math.floor(Date.now() / 1_000),
              recencyAt: Math.floor(Date.now() / 1_000),
              status: 'active',
              sourceKind: 'appServer',
              projectId: matchingProject?.projectId,
            }, ...current]);
        setSelectedThreadId(action.threadId);
        setNewSession(false);
        setThreadHistory(undefined);
        setThreadHistoryError(undefined);
        onNotify('新会话已创建，真实 Turn 已启动');
      }
      if (!shared) window.setTimeout(() => void refreshThreads(true), 300);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason));
    } finally {
      setBusy(false);
    }
  }

  const title = taskRunning
    ? selectedThread?.name ?? threadHistory?.name ?? selectedThread?.preview ?? '当前任务'
    : newSession
      ? newCwd.trim()
        ? `新任务 · ${projectName(newCwd)}`
        : '新任务'
      : selectedThread?.name ?? threadHistory?.name ?? selectedThread?.preview ?? 'Codex';
  const composerLabel = hasActiveTurn
    ? 'Steer 当前任务'
    : externalSession
      ? '恢复 Desktop 外部会话'
    : selectedThread
      ? '继续历史会话的任务'
      : '第一条任务';
  const composerPlaceholder = shared && selectedThreadId && !newSession ? '发送到与 Desktop 共享的会话…' : hasActiveTurn
    ? '不要修改数据库结构，只调整业务层。'
    : externalSession
      ? '恢复后将由 Codex Control Agent 托管并实时同步…'
    : selectedThread
      ? '继续这个会话…'
      : '给电脑上的 Codex 发送任务…';
  const sendLabel = shared && selectedThreadId && !newSession ? hasActiveTurn ? '发送 Steer' : '发送消息' : hasActiveTurn
    ? '发送 Steer'
    : externalSession
      ? '恢复并由 Agent 托管'
    : selectedThread
      ? '恢复会话并发送'
      : '创建会话并开始';

  return (
    <div className={`workspace-shell ${sidebarOpen ? 'sidebar-is-open' : ''}`}>
      <aside className="conversation-sidebar">
        <div className="sidebar-top">
          <button className="icon-button mobile-close" aria-label="关闭会话栏" onClick={() => setSidebarOpen(false)}>×</button>
          <button className="sidebar-back" onClick={onBack}>‹ 所有电脑</button>
        </div>
        <button
          className="new-chat-button"
          title="新建任务"
          onClick={() => beginNewSession()}
        >
          <span>＋</span> 新建任务
        </button>
        <div className="sidebar-label">
          <span>项目</span>
          <button className="icon-button" aria-label="刷新历史" disabled={historyLoading} onClick={() => void refreshThreads()}>↻</button>
        </div>
        <nav className="conversation-nav" aria-label="历史会话">
          {historyLoading ? <p className="sidebar-empty">正在读取历史…</p> : !device.online ? (
            <p className="sidebar-empty">电脑 Agent 离线，恢复后自动加载</p>
          ) : threads.length === 0 && projects.length === 0 ? (
            <p className="sidebar-empty">暂无历史会话</p>
          ) : (
            <>
              {navigation.projects.map((group, index) => {
                const expanded = expandedProjects.has(group.key);
                const regionId = `project-threads-${index}`;
                return (
                  <section className="project-group" key={group.key}>
                    <div className="project-heading">
                      <button
                        className="project-toggle"
                        aria-expanded={expanded}
                        aria-controls={regionId}
                        aria-label={`项目 ${group.name}`}
                        onClick={() => toggleProject(group.key)}
                      >
                        <span className={`project-chevron ${expanded ? 'expanded' : ''}`}>›</span>
                        <span className="project-folder" aria-hidden="true" />
                        <span className="project-copy"><strong>{group.name}</strong></span>
                      </button>
                      {group.cwd && (
                        <button
                          className="project-new-button"
                          aria-label={`在 ${group.name} 中新建会话`}
                          title={`在 ${group.name} 中新建会话`}
                          onClick={() => beginNewSession(group.cwd)}
                        >＋</button>
                      )}
                    </div>
                    {expanded && group.threads.length > 0 && (
                      <div className="project-thread-list" id={regionId}>
                        {group.threads.map((thread) => (
                          <ConversationLink
                            key={thread.threadId}
                            thread={thread}
                            active={selectedThreadId === thread.threadId && !newSession}
                            activity={shared ? sharedThreadActivity(threadViews[thread.threadId]) : threadActivities[thread.threadId]}
                            readAt={readReceipts[thread.threadId]}
                            className="project-thread-link"
                            title={formatThreadTime(thread.recencyAt ?? thread.updatedAt ?? thread.createdAt)}
                            onClick={() => selectThread(thread.threadId)}
                          />
                        ))}
                      </div>
                    )}
                  </section>
                );
              })}
              {navigation.recent.length > 0 && (
                <div className="sidebar-label recent-label"><span>最近</span></div>
              )}
              {navigation.recent.map((thread) => (
                <ConversationLink
                  key={thread.threadId}
                  thread={thread}
                  active={selectedThreadId === thread.threadId && !newSession}
                      activity={shared ? sharedThreadActivity(threadViews[thread.threadId]) : threadActivities[thread.threadId]}
                  readAt={readReceipts[thread.threadId]}
                  className="recent-thread-link"
                  title={`${thread.cwd ?? '无项目'} · ${formatThreadTime(thread.recencyAt ?? thread.updatedAt ?? thread.createdAt)}`}
                  onClick={() => selectThread(thread.threadId)}
                />
              ))}
            </>
          )}
        </nav>
        <div className="sidebar-footer">
          <div className="sidebar-device">
            <span className="device-avatar small">{device.name.slice(0, 1).toUpperCase()}</span>
            <span>
              <strong>{device.name}</strong>
              <small><ConnectionState connection={workspaceConnection} /></small>
              <small className="version-line">Agent v{snapshot?.agentVersion ?? '未知'}</small>
            </span>
          </div>
          <div className="sidebar-version">
            Web v{packageJson.version} · Relay v{relayVersion ?? '未知'} · Protocol v2
          </div>
          <button className="sidebar-action" onClick={() => void refreshThreads()}>刷新会话</button>
          <button className="sidebar-action danger" onClick={() => void onRevoke()}>解除配对</button>
        </div>
      </aside>
      <button className="sidebar-backdrop" aria-label="关闭会话栏" onClick={() => setSidebarOpen(false)} />

      <main className="chat-main">
        <header className="chat-header">
          <button className="icon-button menu-button" aria-label="打开会话栏" onClick={() => setSidebarOpen(true)}>☰</button>
          <div className="chat-title">
            <strong>{title}</strong>
            <span>{newSession
              ? newCwd.trim() || '选择电脑上的项目目录'
              : `${selectedThread?.cwd ?? threadHistory?.cwd ?? selectedActiveTurn?.currentProject ?? device.name}` +
                (shared ? ' · 与官方 Desktop 共享' : externalSession ? ' · Desktop 外部会话（状态不可订阅）' : '')}</span>
          </div>
          <StatusPill
            online={device.online}
            status={interrupting ? 'Interrupting' : selectedActiveTurn?.status ??
              (selectedActivity?.status === 'failed' ? 'Failed' : 'Idle')}
            label={sharedOffline ? '连接中断，任务状态待同步'
              : sharedView?.syncing || sharedView?.needsRecovery ? '正在同步任务状态'
              : interrupting ? '正在停止' : sharedView?.state.waitingOnApproval || selectedActiveTurn?.status === 'WaitingApproval' ? '等待审批'
              : selectedActiveTurn?.status === 'WaitingUserInput' ? '等待输入'
              : taskRunning ? '任务正在运行' : externalSession ? 'Desktop 外部会话'
              : sharedView?.state.lastTurnStatus === 'interrupted' ||
                (!shared && completedEvent?.data.status === 'interrupted') ? '任务已停止'
              : sharedView?.state.threadState === 'notLoaded' ? '会话未加载'
              : selectedActivity?.status === 'failed' ? '任务失败'
              : selectedActivity?.status === 'completed' ? '任务已完成' : 'Agent 就绪'}
          />
          <button
            className="stop-button"
            disabled={busy || !hasActiveTurn || sharedOffline}
            onClick={() => {
              if (!selectedActiveTurn) return;
              setBusy(true);
              setError(undefined);
              void onInterrupt(selectedActiveTurn.threadId, selectedActiveTurn.turnId)
                .then(() => setInterrupting(true))
                .catch((reason) => setError(reason instanceof Error ? reason.message : String(reason)))
                .finally(() => setBusy(false));
            }}
          >
            停止当前任务
          </button>
        </header>

        {shared && (!sharedSupported || sharedOffline || sharedView?.needsRecovery || sharedView?.policyReason || sharedView?.error) && (
          <div className="history-notice" role="status">
            {!sharedSupported ? '当前 Agent 不支持共享会话协议，需要升级。'
              : sharedOffline ? '连接中断，任务状态待同步。重新连接后会自动恢复。'
              : sharedView?.error ?? sharedView?.policyReason ?? '正在恢复遗漏的会话更新…'}
            {sharedView?.error && selectedThreadId && !sharedOffline && (
              <button className="quiet-button" onClick={() => void loadThread(selectedThreadId)}>重新同步</button>
            )}
          </div>
        )}

        {(taskRunning || displayedActivity || displayedCommand) && (
          <div className="runtime-strip">
            <span className="pulse-dot" />
            <span>{taskRunning
              ? displayedActivity ?? '任务正在运行'
              : displayedActivity}</span>
            {displayedCommand && <code>{displayedCommand}</code>}
            {runningElapsedMs !== undefined && (
              <strong className="runtime-elapsed">已运行 {formatDuration(runningElapsedMs)}</strong>
            )}
          </div>
        )}

        <section
          className="chat-feed"
          aria-label="会话内容"
          ref={chatFeed}
          onWheel={(event) => {
            if (event.deltaY < 0) {
              userScrolling.current = true;
              stickChatToBottom.current = false;
              setIsAtBottom(false);
              preservedScrollTop.current = event.currentTarget.scrollTop;
              window.setTimeout(() => { userScrolling.current = false; }, 80);
            }
          }}
          onPointerDown={() => { userScrolling.current = true; }}
          onPointerUp={() => { window.setTimeout(() => { userScrolling.current = false; }, 100); }}
          onPointerCancel={() => { userScrolling.current = false; }}
          onTouchStart={() => { userScrolling.current = true; }}
          onTouchEnd={() => { window.setTimeout(() => { userScrolling.current = false; }, 100); }}
          onScroll={(event) => {
            const target = event.currentTarget;
            if (!userScrolling.current) {
              if (!stickChatToBottom.current &&
                  Math.abs(target.scrollTop - preservedScrollTop.current) > 1) {
                target.scrollTop = preservedScrollTop.current;
              }
              return;
            }
            if (userScrolling.current) preservedScrollTop.current = target.scrollTop;
            const atBottom = target.scrollHeight - target.scrollTop - target.clientHeight < 96;
            stickChatToBottom.current = atBottom;
            setIsAtBottom(atBottom);
            if (atBottom) {
              setNewMessageCount(0);
              if (selectedThreadId) markThreadRead(selectedThreadId, selectedActivity?.latestAt);
            }
          }}
        >
          {!device.online && selectedThreadId && !newSession && !sharedView?.history ? (
            <div className="history-loading" role="status">
              <span className="history-spinner" />
              <strong>正在等待电脑 Agent</strong>
              <p>Agent 恢复在线后会自动加载这个会话。</p>
            </div>
          ) : threadHistoryLoading && selectedThreadId && !newSession && !sharedView?.history ? (
            <div className="history-loading" role="status">
              <span className="history-spinner" />
              <strong>正在加载会话</strong>
              <p>从电脑上的 Codex 读取历史 Turn 和消息…</p>
            </div>
          ) : threadHistoryError && selectedThreadId && !newSession ? (
            <div className="history-loading history-failed" role="alert">
              <strong>会话加载失败</strong>
              <p>{threadHistoryError}</p>
              <button className="quiet-button" onClick={() => void loadThread(selectedThreadId)}>重新加载</button>
            </div>
          ) : newSession && chatEntries.length === 0 ? (
            <div className="empty-chat">
              <span className="empty-mark">C</span>
              <h1>今天想让电脑上的 Codex 做什么？</h1>
              <p>选择项目目录，在下方输入任务。任务会在电脑上真实执行。</p>
            </div>
          ) : chatEntries.length === 0 ? (
            <div className="empty-chat compact">
              <span className="empty-mark">C</span>
              <h1>{selectedThread?.name ?? threadHistory?.name ?? selectedThread?.preview ?? '准备就绪'}</h1>
              <p>{shared ? '在下方输入消息，继续与 Desktop 共享的会话。' : '在下方输入消息，将恢复这个历史 Thread 并开始新的 Turn。'}</p>
            </div>
          ) : (
            <div className="message-column">
              {threadHistory?.truncated && (
                <div className="history-notice">会话内容较长，当前显示最近 {threadHistory.entries.length} 条消息</div>
              )}
              {chatEntries.map((entry) => <ChatMessage key={entry.id} entry={entry} />)}
            </div>
          )}

          {selectedApprovals.map((approval) => (
            <ApprovalCard
              key={approval.approvalId}
              approval={approval}
              disabled={busy || sharedOffline || Boolean(approval.isResolving)}
              onDecision={(decision) => {
                setBusy(true);
                setError(undefined);
                void onApprove(approval.approvalId, decision)
                  .catch((reason) => setError(reason instanceof Error ? reason.message : String(reason)))
                  .finally(() => setBusy(false));
              }}
            />
          ))}
          {error && <p role="alert" className="chat-error">{error}</p>}
        </section>

        {newMessageCount > 0 && (
          <button
            className="new-messages-button"
            onClick={() => {
              if (chatFeed.current) chatFeed.current.scrollTop = chatFeed.current.scrollHeight;
              if (chatFeed.current) preservedScrollTop.current = chatFeed.current.scrollTop;
              stickChatToBottom.current = true;
              setIsAtBottom(true);
              setNewMessageCount(0);
              if (selectedThreadId) markThreadRead(selectedThreadId, selectedActivity?.latestAt);
            }}
          >
            ↓ {newMessageCount} 条新消息
          </button>
        )}

        <div className="composer-dock">
          {shared && acceptedSharedSteer && (
            <div className="shared-steer-feedback" role="status" aria-label="已接受的干预">
              <strong>干预已接受</strong>
              <p>{acceptedSharedSteer.text}</p>
              <small>此输入已送入当前任务，后续消息可能稍后出现。无需重复发送。</small>
            </div>
          )}
          {!taskRunning && !selectedThread && (
            <div className="project-picker">
              <span>⌂</span>
              <label className="sr-only" htmlFor="new-session-cwd">电脑上的项目目录</label>
              <input
                id="new-session-cwd"
                placeholder="D:\Projects\MES"
                value={newCwd}
                onChange={(event) => setNewCwd(event.target.value)}
              />
            </div>
          )}
          <form className="composer" onSubmit={submit}>
            {shared && selectedThreadId && !newSession ? (
              <div className="session-options">沿用当前会话的模型与权限设置</div>
            ) : <div className="session-options" aria-label="会话运行选项">
              <label>
                <span>模型</span>
                <select
                  aria-label="模型"
                  value={selectedModel}
                  disabled={sessionOptionsLoading}
                  title={hasActiveTurn ? '当前 Steer 不改变模型；选择将在下一次 Turn 生效' : '选择下一次 Turn 使用的模型'}
                  onChange={(event) => setSelectedModel(event.target.value)}
                >
                  <option value="">Codex 默认模型</option>
                  {selectedModel && !sessionOptions?.models.some((option) => option.model === selectedModel) && (
                    <option value={selectedModel}>{selectedModel}</option>
                  )}
                  {sessionOptions?.models.map((option) => (
                    <option key={option.id} value={option.model} title={option.description}>
                      {option.displayName}{option.isDefault ? ' · 默认' : ''}
                    </option>
                  ))}
                </select>
              </label>
              <label>
                <span>批准等级</span>
                <select
                  aria-label="批准等级"
                  value={approvalPolicy}
                  disabled={sessionOptionsLoading}
                  title={hasActiveTurn ? '当前 Turn 不变；选择将在下一次 Turn 生效' : '选择下一次 Turn 的批准等级'}
                  onChange={(event) => setApprovalPolicy(event.target.value as CodexApprovalPolicy)}
                >
                  {approvalPolicyOptions(sessionOptions).map((option) => (
                    <option key={option.id} value={option.id} title={option.description}>
                      {option.displayName}
                    </option>
                  ))}
                </select>
              </label>
            </div>}
            <label className="sr-only" htmlFor="chat-composer">{composerLabel}</label>
            <textarea
              id="chat-composer"
              aria-label={composerLabel}
              placeholder={composerPlaceholder}
              value={composer}
              onChange={(event) => setComposer(event.target.value)}
              onKeyDown={(event) => {
                if (event.key === 'Enter' && !event.shiftKey) {
                  event.preventDefault();
                  event.currentTarget.form?.requestSubmit();
                }
              }}
              rows={1}
            />
            <div className="composer-footer">
              <span>{shared && selectedThreadId && !newSession ? hasActiveTurn ? '干预当前任务' : '共享会话'
                : hasActiveTurn ? 'Steer 当前 Turn' : externalSession ? '恢复后切换为 Agent 托管' : selectedThread ? '恢复历史会话' : '创建新会话'}</span>
              <button
                className="send-button"
                aria-label={sendLabel}
                title={sendLabel}
                disabled={busy || !device.online || !composer.trim() || (!hasActiveTurn && !selectedThread && !newCwd.trim()) ||
                  (shared && !newSession && Boolean(selectedThreadId) && (!sharedSupported || sharedOffline || !sharedView?.controlAllowed ||
                    sharedView.needsRecovery || sharedView.syncing || taskRunning && !selectedActiveTurn?.turnId))}
              >
                ↑
              </button>
            </div>
          </form>
          <p className="composer-hint">Codex 会在你的电脑上运行。重要操作仍需审批。</p>
        </div>
        {toast && <div key={toast} className="toast">{toast}</div>}
      </main>
    </div>
  );
}

function ChatMessage({ entry }: { entry: ChatEntry }) {
  if (entry.role === 'summary') {
    const impact = summarizeFileImpact(entry.changes ?? []);
    return (
      <details className="process-summary">
        <summary>
          <span>过程摘要</span>
          {impact && <small className="file-impact-total">{impact}</small>}
          {entry.durationMs !== undefined && <small>耗时 {formatDuration(entry.durationMs)}</small>}
        </summary>
        <div className="process-summary-items">
          {(entry.processItems ?? []).map((item) => (
            <div className="process-summary-item" key={item.id}>
              <strong>{item.meta ?? '电脑操作'}</strong>
              <code>{item.text}</code>
            </div>
          ))}
        </div>
      </details>
    );
  }
  if (entry.role === 'system') {
    return <div className="system-message"><span>{entry.text}</span></div>;
  }
  if (entry.role === 'tool') {
    return <div className="tool-message"><span>›_</span><div><strong>{entry.meta ?? '电脑操作'}</strong><p>{entry.text}</p></div></div>;
  }
  const prompt = entry.role === 'user' ? splitUserPrompt(entry.text) : undefined;
  return (
    <article className={`chat-message ${entry.role} ${entry.streaming ? 'streaming' : ''}`}>
      {entry.role === 'assistant' && <span className="assistant-avatar">C</span>}
      <div>
        {entry.meta && <small>{entry.meta}</small>}
        <MarkdownBody text={prompt?.request ?? entry.text} />
        {entry.streaming && <span className="typing-caret" aria-hidden="true" />}
        <ImageAttachments attachments={entry.attachments ?? []} />
        {prompt?.context && (
          <details className="prompt-context">
            <summary>附件与上下文</summary>
            <MarkdownBody text={prompt.context} />
          </details>
        )}
        {entry.durationMs !== undefined && (
          <small className="message-duration">耗时 {formatDuration(entry.durationMs)}</small>
        )}
      </div>
    </article>
  );
}

function MarkdownBody({ text }: { text: string }) {
  return (
    <div className="markdown-body">
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        skipHtml
        components={{
          a: ({ href, children }) => (
            <a href={href} target="_blank" rel="noreferrer">{children}</a>
          ),
          img: ({ src, alt }) => typeof src === 'string' &&
            (src.startsWith('data:image/') || src.startsWith('/'))
            ? <img src={src} alt={alt ?? '图片'} loading="lazy" />
            : <span className="blocked-markdown-image">[图片：{alt ?? '外部地址'}]</span>,
        }}
      >
        {text}
      </ReactMarkdown>
    </div>
  );
}

function ImageAttachments({ attachments }: { attachments: CodexThreadHistoryAttachment[] }) {
  const images = attachments.filter((attachment) =>
    attachment.kind === 'image' &&
    attachment.dataUrl.startsWith(`data:${attachment.mimeType};base64,`) &&
    attachment.mimeType.startsWith('image/'),
  );
  if (images.length === 0) return null;
  return (
    <div className="message-attachments">
      {images.map((attachment, index) => (
        <a
          href={attachment.dataUrl}
          target="_blank"
          rel="noreferrer"
          className="message-image-link"
          key={`${attachment.name}-${index}`}
        >
          <img src={attachment.dataUrl} alt={attachment.name} loading="lazy" />
          <small>{attachment.name}</small>
        </a>
      ))}
    </div>
  );
}

function ApprovalCard({
  approval,
  disabled,
  onDecision,
}: {
  approval: ApprovalRequested;
  disabled: boolean;
  onDecision: (decision: unknown) => void;
}) {
  const decisions = approval.availableDecisions;
  return (
    <section className="approval-message" aria-label="等待审批">
      <div className="approval-heading"><span>!</span><div><strong>{approval.isResolving ? '审批已提交，等待确认' : '等待审批'}</strong><small>{approval.requestMethod}</small></div></div>
      {approval.command && <pre>{approval.command}</pre>}
      {approval.cwd && <p>目录：{approval.cwd}</p>}
      {approval.reason && <p>{approval.reason}</p>}
      <div className="approval-actions">
        {decisions.map((decision) => (
          <button
            key={JSON.stringify(decision)}
            disabled={disabled || approval.isResolving}
            className={decisionKind(decision) === 'allow' ? 'solid-button' : 'quiet-button'}
            onClick={() => onDecision(decision)}
          >
            {decisionLabel(decision)}
          </button>
        ))}
      </div>
    </section>
  );
}

function StatusPill({ online, status, label }: { online: boolean; status?: string; label?: string }) {
  const value = online ? status ?? 'Idle' : 'Offline';
  return <span className={`status-pill ${statusClass(value)}`}><i />{online ? label ?? value : 'Offline'}</span>;
}

function statusClass(status: string) {
  if (status === 'WaitingApproval') return 'warning';
  if (status === 'Failed' || status === 'Offline') return 'offline';
  if (status === 'Idle' || status === 'Completed' || status === 'Interrupted') return 'idle';
  return 'running';
}

function decisionKind(decision: unknown) {
  const value = typeof decision === 'string' ? decision : Object.keys(decision as object)[0];
  return value?.startsWith('accept') || value?.startsWith('apply') ? 'allow' : 'deny';
}

function decisionLabel(decision: unknown) {
  const value = typeof decision === 'string' ? decision : Object.keys(decision as object)[0];
  return ({ accept: '允许一次', acceptForSession: '本次会话允许', decline: '拒绝', cancel: '拒绝并停止' } as Record<string, string>)[value] ?? value;
}

function buildThreadNavigation(
  threads: CodexThreadSummary[],
  projects: CodexProjectSummary[],
): ThreadNavigation {
  const groups = projects
    .map<ProjectNavigationGroup>((project) => ({
      key: project.projectId,
      name: project.name,
      cwd: project.roots[0],
      position: project.position,
      threads: [],
    }))
    .sort((left, right) => left.position - right.position || left.name.localeCompare(right.name));
  const projectsById = new Map(groups.map((group) => [group.key, group]));
  const recent: CodexThreadSummary[] = [];
  const cwdProjects = new Map<string, ProjectNavigationGroup>();
  for (const thread of [...threads].sort(compareThreadRecency)) {
    const project = thread.projectId ? projectsById.get(thread.projectId) : undefined;
    if (project) {
      project.threads.push(thread);
      continue;
    }

    if (thread.cwd && !isCodexGeneratedSessionDirectory(thread.cwd)) {
      const key = `cwd:${normalizeProjectPath(thread.cwd)}`;
      let cwdProject = cwdProjects.get(key);
      if (!cwdProject) {
        cwdProject = {
          key,
          name: projectName(thread.cwd),
          cwd: thread.cwd,
          position: 1_000_000 + cwdProjects.size,
          threads: [],
        };
        cwdProjects.set(key, cwdProject);
        groups.push(cwdProject);
      }
      cwdProject.threads.push(thread);
      continue;
    }

    recent.push(thread);
  }

  for (const project of groups) project.threads.sort(compareThreadRecency);
  recent.sort(compareThreadRecency);
  return { projects: groups, recent };
}

function compareThreadRecency(left: CodexThreadSummary, right: CodexThreadSummary) {
  return threadRecency(right) - threadRecency(left)
    || (left.name ?? left.preview ?? '').localeCompare(right.name ?? right.preview ?? '');
}

function threadRecency(thread: CodexThreadSummary) {
  return thread.recencyAt ?? thread.updatedAt ?? thread.createdAt ?? 0;
}

function isCodexGeneratedSessionDirectory(cwd: string) {
  return /[\\/]Documents[\\/]Codex[\\/]20\d{2}-\d{2}-\d{2}[\\/]/iu.test(cwd);
}

function normalizeProjectPath(cwd: string) {
  const normalized = cwd.trim().replaceAll('/', '\\').replace(/\\+$/u, '');
  return /^[a-z]:\\/iu.test(normalized) || normalized.startsWith('\\\\')
    ? normalized.toLocaleLowerCase()
    : normalized;
}

function projectName(cwd?: string) {
  if (!cwd?.trim()) return '未识别项目';
  const normalized = cwd.trim().replace(/[\\/]+$/u, '');
  return normalized.split(/[\\/]/u).filter(Boolean).at(-1) ?? normalized;
}

function setsEqual(left: Set<string>, right: Set<string>) {
  return left.size === right.size && [...left].every((value) => right.has(value));
}

function mergeChatEntries(...groups: ChatEntry[][]): ChatEntry[] {
  const merged: ChatEntry[] = [];
  for (const group of groups) {
    for (const entry of group) {
      const index = merged.findIndex((candidate) => candidate.id === entry.id);
      if (index >= 0) {
        // History can contain a prefix from before this browser subscribed to the stream.
        const previous = merged[index];
        let text = entry.text;
        if (entry.streaming && !text.startsWith(previous.text)) {
          let overlap = Math.min(previous.text.length, text.length);
          while (overlap > 0 && !previous.text.endsWith(text.slice(0, overlap))) overlap -= 1;
          text = previous.text + text.slice(overlap);
        }
        merged[index] = { ...previous, ...entry, text };
        continue;
      }
      const provisional = (value: ChatEntry) => value.id.startsWith('local_') || value.id === 'snapshot-last-message';
      const echo = merged.findIndex((candidate) => candidate.role === entry.role && candidate.text === entry.text &&
        (!candidate.turnId || !entry.turnId || candidate.turnId === entry.turnId) &&
        (provisional(candidate) || provisional(entry)));
      if (echo >= 0) {
        if (provisional(merged[echo]) && !provisional(entry)) merged[echo] = entry;
        continue;
      }
      merged.push(entry);
    }
  }
  return merged.slice(-240);
}

function buildChatEntries(events: CodexEvent[], snapshot: CodexSnapshot | undefined, threadId?: string): ChatEntry[] {
  const chronological = [...events].reverse().filter((event) =>
    !threadId || !event.threadId || event.threadId === threadId,
  );
  const timings = new Map<string, { startedAt?: number; completedAt?: number; durationMs?: number }>();
  for (const event of chronological) {
    if (!event.turnId) continue;
    const timing = timings.get(event.turnId) ?? {};
    if (event.kind === 'TurnStarted') {
      timing.startedAt = readTimestamp(event.data.startedAt) ?? event.occurredAt;
    } else if (event.kind === 'TurnCompleted') {
      timing.completedAt = readTimestamp(event.data.completedAt) ?? event.occurredAt;
      timing.startedAt ??= readTimestamp(event.data.startedAt);
      timing.durationMs = readNumber(event.data.durationMs) ?? resolveDurationMs(timing);
    }
    timings.set(event.turnId, timing);
  }

  const entries: ChatEntry[] = [];
  for (const event of chronological) {
    const text = eventText(event.data);
    const durationMs = event.turnId ? resolveDurationMs(timings.get(event.turnId)) : undefined;
    switch (event.kind) {
      case 'UserMessageCompleted':
        if (text || Array.isArray(event.data.attachments)) entries.push({
          id: event.itemId ? `item_${event.turnId}_${event.itemId}` : event.eventId,
          role: 'user',
          text,
          turnId: event.turnId,
          occurredAt: event.occurredAt,
          attachments: Array.isArray(event.data.attachments)
            ? event.data.attachments as CodexThreadHistoryAttachment[] : undefined,
        });
        break;
      case 'AgentMessageCompleted':
        if (text) entries.push({
          id: event.itemId ? `item_${event.turnId}_${event.itemId}` : event.eventId,
          role: 'assistant',
          text,
          turnId: event.turnId,
          occurredAt: event.occurredAt,
          durationMs,
        });
        break;
      case 'AgentMessageDelta':
        if (text) entries.push({
          id: event.itemId ? `item_${event.turnId}_${event.itemId}` : event.eventId,
          role: 'assistant',
          text,
          meta: '实时回复',
          streaming: true,
          turnId: event.turnId,
          occurredAt: event.occurredAt,
        });
        break;
      case 'CommandStarted':
      case 'CommandCompleted':
        if (text) entries.push({
          id: event.eventId,
          role: 'tool',
          text,
          meta: event.kind === 'CommandStarted' ? '运行命令' : '命令完成',
          turnId: event.turnId,
          occurredAt: event.occurredAt,
          durationMs,
        });
        break;
      case 'FileChanged': {
        const changes = readFileChanges(event.data.changes);
        const changedFiles = changes.length > 0
          ? formatFileChanges(changes)
          : Array.isArray(event.data.paths)
            ? event.data.paths.filter((value): value is string => typeof value === 'string').join('\n')
            : text;
        if (changedFiles) entries.push({
          id: event.eventId,
          role: 'tool',
          text: changedFiles,
          meta: '修改文件',
          turnId: event.turnId,
          occurredAt: event.occurredAt,
          durationMs,
          changes,
        });
        break;
      }
      case 'ErrorOccurred':
        entries.push({
          id: event.eventId,
          role: 'system',
          text: text || '任务发生错误',
          turnId: event.turnId,
          occurredAt: event.occurredAt,
        });
        break;
      case 'ContentIncomplete':
        entries.push({ id: event.eventId, role: 'system', text: '此条消息内容未完整，正在等待同步。',
          turnId: event.turnId, occurredAt: event.occurredAt });
        break;
    }
  }

  if (snapshot?.lastAgentMessage && !entries.some((entry) =>
    entry.role === 'assistant' && entry.text === snapshot.lastAgentMessage,
  )) {
    entries.push({ id: 'snapshot-last-message', role: 'assistant', text: snapshot.lastAgentMessage,
      turnId: snapshot.activeTurnId });
  }
  return entries.slice(-80);
}

function foldProcessEntries(entries: ChatEntry[]): ChatEntry[] {
  const folded: ChatEntry[] = [];
  let buffer: ChatEntry[] = [];
  const summarizedTurns = new Set<string>();

  const flush = () => {
    if (buffer.length === 0) return;
    const deduplicated = new Map<string, ChatEntry>();
    for (const item of buffer) {
      deduplicated.set(`${item.turnId ?? ''}\u0000${item.text}`, item);
    }
    const processItems = [...deduplicated.values()];
    const turnId = processItems[0]?.turnId;
    if (turnId) summarizedTurns.add(turnId);
    folded.push({
      id: `summary_${processItems[0]?.id ?? crypto.randomUUID()}`,
      role: 'summary',
      text: '过程摘要',
      turnId,
      durationMs: processItems.find((item) => item.durationMs !== undefined)?.durationMs,
      processItems,
      changes: mergeFileChanges(processItems.flatMap((item) => item.changes ?? [])),
    });
    buffer = [];
  };

  for (const entry of entries) {
    if (entry.role === 'tool') {
      if (buffer.length > 0 && buffer[0].turnId !== entry.turnId) flush();
      buffer.push(entry);
      continue;
    }
    flush();
    folded.push(entry);
  }
  flush();

  return folded.map((entry) => entry.role === 'assistant' && entry.turnId && summarizedTurns.has(entry.turnId)
    ? { ...entry, durationMs: undefined }
    : entry);
}

function eventText(data: Record<string, unknown>) {
  const value = data.text ?? data.command ?? data.path ?? data.message ?? data.status;
  return typeof value === 'string' ? value : '';
}

function readEventStatus(event: CodexEvent) {
  return typeof event.data.status === 'string' ? event.data.status : undefined;
}

function activeTurnsForSnapshot(snapshot?: CodexSnapshot): CodexActiveTurn[] {
  const advertised = snapshot?.activeTurns?.filter((turn) =>
    Boolean(turn.threadId && turn.turnId),
  ) ?? [];
  if (snapshot?.activeTurns !== undefined) return advertised;
  if (!snapshot?.activeThreadId || !snapshot.activeTurnId) return [];
  return [{
    threadId: snapshot.activeThreadId,
    turnId: snapshot.activeTurnId,
    status: snapshot.status,
    startedAt: snapshot.startedAt ?? snapshot.lastActivityAt,
    lastActivityAt: snapshot.lastActivityAt,
    currentProject: snapshot.currentProject,
    currentActivity: snapshot.currentActivity,
    runningCommand: snapshot.runningCommand,
    changedFiles: snapshot.changedFiles,
    pendingApprovalCount: snapshot.pendingApprovalCount,
    lastAgentMessage: snapshot.lastAgentMessage,
    lastError: snapshot.lastError,
  }];
}

function reconcileActiveTurns(snapshot: CodexSnapshot | undefined, events: CodexEvent[]): CodexActiveTurn[] {
  const turns = new Map(activeTurnsForSnapshot(snapshot).map((turn) => [turn.threadId, turn]));
  for (const event of [...events].reverse()) {
    if (!event.threadId || event.revision < (snapshot?.revision ?? 0)) continue;
    const active = turns.get(event.threadId);
    if (event.kind === 'TurnStarted' && event.turnId) {
      if (active?.turnId === event.turnId) continue;
      turns.set(event.threadId, {
        threadId: event.threadId, turnId: event.turnId, status: 'Thinking',
        startedAt: readTimestamp(event.data.startedAt) ?? event.occurredAt,
        lastActivityAt: event.occurredAt, changedFiles: [], pendingApprovalCount: 0,
      });
    } else if (event.kind === 'TurnCompleted' && active?.turnId === event.turnId) {
      turns.delete(event.threadId);
    } else if (event.kind === 'ThreadStatusChanged') {
      if (event.data.status === 'active' && active) {
        const flags = Array.isArray(event.data.activeFlags) ? event.data.activeFlags : [];
        turns.set(event.threadId, { ...active,
          status: flags.includes('waitingOnApproval') ? 'WaitingApproval'
            : flags.includes('waitingOnUserInput') ? 'WaitingUserInput'
            : active.status === 'WaitingApproval' || active.status === 'WaitingUserInput' ? 'Thinking' : active.status,
        });
      } else if (event.data.status === 'idle' || event.data.status === 'notLoaded' || event.data.status === 'systemError') {
        turns.delete(event.threadId);
      }
    }
  }
  return [...turns.values()];
}

function snapshotForActiveTurn(
  snapshot: CodexSnapshot | undefined,
  active: CodexActiveTurn | undefined,
): CodexSnapshot | undefined {
  if (!snapshot || !active) return undefined;
  return {
    ...snapshot,
    status: active.status,
    activeThreadId: active.threadId,
    activeTurnId: active.turnId,
    startedAt: active.startedAt,
    lastActivityAt: active.lastActivityAt,
    currentProject: active.currentProject,
    currentActivity: active.currentActivity,
    runningCommand: active.runningCommand,
    changedFiles: active.changedFiles,
    pendingApprovalCount: active.pendingApprovalCount,
    lastAgentMessage: active.lastAgentMessage,
    lastError: active.lastError,
  };
}

function approvalPolicyOptions(options?: CodexSessionOptions) {
  return options?.approvalPolicies.length ? options.approvalPolicies : [
    {
      id: 'untrusted' as const,
      displayName: '严格审批',
      description: '仅可信操作直接执行，其他操作必须明确批准。',
      isDefault: true,
    },
    {
      id: 'on-request' as const,
      displayName: '按需审批',
      description: 'Codex 可在需要时发起审批请求。',
      isDefault: false,
    },
    {
      id: 'never' as const,
      displayName: '不发起审批',
      description: '超出 workspace-write 边界的操作会直接失败。',
      isDefault: false,
    },
  ];
}

interface SessionPreference {
  model?: string;
  approvalPolicy?: CodexApprovalPolicy;
}

function loadSessionPreference(deviceId: string): SessionPreference {
  const stored = readStoredRecord(`codex-control-session-options:${deviceId}`);
  const approval = stored.approvalPolicy;
  return {
    model: typeof stored.model === 'string' ? stored.model : undefined,
    approvalPolicy: approval === 'untrusted' || approval === 'on-request' || approval === 'never'
      ? approval
      : undefined,
  };
}

function saveSessionPreference(deviceId: string, preference: SessionPreference) {
  localStorage.setItem(`codex-control-session-options:${deviceId}`, JSON.stringify(preference));
}

function isTransientNewThreadReadFailure(reason: unknown) {
  const message = reason instanceof Error ? reason.message : String(reason);
  return message.startsWith('THREAD_NOT_FOUND:') ||
    message.startsWith('THREAD_READ_FAILED:') ||
    message.startsWith('APP_SERVER_TIMEOUT:');
}

function wait(milliseconds: number) {
  return new Promise<void>((resolve) => window.setTimeout(resolve, milliseconds));
}

function loadThreadActivities(deviceId: string): Record<string, ThreadActivityState> {
  const stored = readStoredRecord(`codex-control-thread-activity:${deviceId}`);
  const activities: Record<string, ThreadActivityState> = {};
  for (const [threadId, value] of Object.entries(stored).slice(0, 200)) {
    if (!value || typeof value !== 'object') continue;
    const candidate = value as Record<string, unknown>;
    if (typeof candidate.latestAt !== 'number' || !Number.isFinite(candidate.latestAt) ||
        typeof candidate.status !== 'string' ||
        !['running', 'completed', 'failed', 'updated'].includes(candidate.status)) continue;
    activities[threadId] = {
      latestAt: candidate.latestAt,
      status: candidate.status as ThreadActivityStatus,
    };
  }
  return activities;
}

function saveThreadActivities(deviceId: string, activities: Record<string, ThreadActivityState>) {
  localStorage.setItem(`codex-control-thread-activity:${deviceId}`, JSON.stringify(activities));
}

function loadThreadReadReceipts(deviceId: string): Record<string, number> {
  const stored = readStoredRecord(`codex-control-thread-read:${deviceId}`);
  const receipts: Record<string, number> = {};
  for (const [threadId, value] of Object.entries(stored).slice(0, 200)) {
    if (typeof value === 'number' && Number.isFinite(value) && value >= 0) receipts[threadId] = value;
  }
  return receipts;
}

function saveThreadReadReceipts(deviceId: string, receipts: Record<string, number>) {
  localStorage.setItem(`codex-control-thread-read:${deviceId}`, JSON.stringify(receipts));
}

function readStoredRecord(key: string): Record<string, unknown> {
  try {
    const parsed = JSON.parse(localStorage.getItem(key) ?? '{}') as unknown;
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed)
      ? parsed as Record<string, unknown>
      : {};
  } catch {
    return {};
  }
}

function readFileChanges(value: unknown): CodexThreadHistoryFileChange[] {
  if (!Array.isArray(value)) return [];
  return value.flatMap((candidate) => {
    if (!candidate || typeof candidate !== 'object') return [];
    const change = candidate as Record<string, unknown>;
    if (typeof change.path !== 'string' || !change.path.trim()) return [];
    return [{
      path: change.path,
      kind: typeof change.kind === 'string' ? change.kind : undefined,
      additions: readNonNegativeInteger(change.additions),
      deletions: readNonNegativeInteger(change.deletions),
    }];
  }).slice(0, 30);
}

function readNonNegativeInteger(value: unknown) {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0 ? value : undefined;
}

function formatFileChanges(changes: CodexThreadHistoryFileChange[]) {
  return changes.map((change) => {
    const statistics = change.additions === undefined && change.deletions === undefined
      ? ''
      : `  +${change.additions ?? 0} -${change.deletions ?? 0}`;
    return `${change.path}${statistics}`;
  }).join('\n');
}

function mergeFileChanges(changes: CodexThreadHistoryFileChange[]) {
  const merged = new Map<string, CodexThreadHistoryFileChange>();
  for (const change of changes) {
    const existing = merged.get(change.path);
    if (!existing) {
      merged.set(change.path, { ...change });
      continue;
    }
    merged.set(change.path, {
      path: change.path,
      kind: change.kind ?? existing.kind,
      additions: sumOptional(existing.additions, change.additions),
      deletions: sumOptional(existing.deletions, change.deletions),
    });
  }
  return [...merged.values()];
}

function sumOptional(left?: number, right?: number) {
  return left === undefined && right === undefined ? undefined : (left ?? 0) + (right ?? 0);
}

function summarizeFileImpact(changes: CodexThreadHistoryFileChange[]) {
  if (changes.length === 0) return undefined;
  const additions = changes.reduce((sum, change) => sum + (change.additions ?? 0), 0);
  const deletions = changes.reduce((sum, change) => sum + (change.deletions ?? 0), 0);
  const hasStatistics = changes.some((change) => change.additions !== undefined || change.deletions !== undefined);
  return hasStatistics
    ? `影响 ${changes.length} 个文件 · +${additions} -${deletions}`
    : `影响 ${changes.length} 个文件`;
}

function splitUserPrompt(text: string): { request: string; context?: string } {
  const marker = /^#{1,3}\s+My request:\s*$/imu;
  const match = marker.exec(text);
  if (!match) return { request: text };
  const context = text.slice(0, match.index).trim();
  const request = text.slice(match.index + match[0].length).trim();
  return request ? { request, context: context || undefined } : { request: text };
}

function resolveDurationMs(timing?: { startedAt?: number; completedAt?: number; durationMs?: number }) {
  if (!timing) return undefined;
  if (typeof timing.durationMs === 'number' && timing.durationMs >= 0) return timing.durationMs;
  const startedAt = readTimestamp(timing.startedAt);
  const completedAt = readTimestamp(timing.completedAt);
  return startedAt !== undefined && completedAt !== undefined && completedAt >= startedAt
    ? completedAt - startedAt
    : undefined;
}

function readTimestamp(value: unknown) {
  if (typeof value !== 'number' || !Number.isFinite(value) || value <= 0) return undefined;
  return value < 1_000_000_000_000 ? value * 1_000 : value;
}

function readNumber(value: unknown) {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

function formatDuration(milliseconds: number) {
  const totalSeconds = Math.max(0, Math.round(milliseconds / 1_000));
  if (totalSeconds < 60) return `${totalSeconds}秒`;
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  if (minutes < 60) return seconds > 0 ? `${minutes}分${seconds}秒` : `${minutes}分钟`;
  const hours = Math.floor(minutes / 60);
  const remainingMinutes = minutes % 60;
  return remainingMinutes > 0 ? `${hours}小时${remainingMinutes}分钟` : `${hours}小时`;
}

function processLabel(phase?: string) {
  if (!phase) return '电脑操作';
  if (/file/iu.test(phase)) return '修改文件';
  if (/completed|succeeded|failed|declined/iu.test(phase)) return '命令完成';
  if (/command|running|inProgress/iu.test(phase)) return '运行命令';
  return phase;
}

function formatCode(value: string) {
  return value.length > 3 ? `${value.slice(0, 3)} ${value.slice(3)}` : value;
}

function formatThreadTime(value?: number) {
  if (!value) return '未知时间';
  const milliseconds = value < 1_000_000_000_000 ? value * 1_000 : value;
  return new Date(milliseconds).toLocaleString(undefined, {
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  });
}

interface PairingLink {
  relayRoot: string;
  code: string;
  deviceName?: string;
}

function readPairingLink(): PairingLink | undefined {
  if (!window.location.hash.startsWith('#/pair?')) return undefined;
  try {
    const parameters = new URLSearchParams(window.location.hash.slice('#/pair?'.length));
    const encodedRelay = parameters.get('relay');
    const code = parameters.get('code')?.replaceAll(/\D/gu, '') ?? '';
    if (!encodedRelay || code.length !== 6) return undefined;
    const padded = encodedRelay.replaceAll('-', '+').replaceAll('_', '/')
      .padEnd(Math.ceil(encodedRelay.length / 4) * 4, '=');
    const relayRoot = new TextDecoder().decode(
      Uint8Array.from(atob(padded), (character) => character.charCodeAt(0)),
    );
    const root = new URL(relayRoot);
    if (root.protocol !== 'https:' &&
        !(root.protocol === 'http:' && (root.hostname === '127.0.0.1' || root.hostname === 'localhost'))) {
      return undefined;
    }
    return { relayRoot: root.toString().replace(/\/$/u, ''), code, deviceName: parameters.get('device') ?? undefined };
  } catch {
    return undefined;
  }
}

function controllerWebSocketUrl(relayRoot: string) {
  const url = new URL(relayRoot);
  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
  url.pathname = '/ws/controller';
  url.search = '';
  url.hash = '';
  return url.toString();
}

function browserControllerName() {
  const agent = navigator.userAgent;
  const kind = /iPhone/iu.test(agent) ? 'iPhone'
    : /iPad/iu.test(agent) ? 'iPad'
      : /Android/iu.test(agent) ? 'Android'
        : /Edg\//iu.test(agent) ? 'Edge'
          : /Chrome\//iu.test(agent) ? 'Chrome'
            : /Safari\//iu.test(agent) ? 'Safari'
              : 'Browser';
  return `${kind} · ${navigator.platform || 'Controller'}`.slice(0, 200);
}
