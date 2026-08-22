import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from 'react';
import type {
  ApprovalRequested,
  CodexEvent,
  CodexSnapshot,
  CodexThreadSummary,
  DeviceSummary,
} from './protocol';
import { RelayClient, type RelayClientState } from './relayClient';
import { loadRelayUrl, saveRelayUrl } from './storage';

const initialState: RelayClientState = {
  connection: 'offline',
  authenticated: false,
  devices: [],
  events: {},
  approvals: {},
};

export function App() {
  const client = useMemo(() => new RelayClient(), []);
  const [state, setState] = useState(initialState);
  const [selectedId, setSelectedId] = useState<string>();
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
    void client.start();
    return () => {
      unsubscribe();
      client.stop();
    };
  }, [client]);

  useEffect(() => () => {
    if (toastTimer.current) window.clearTimeout(toastTimer.current);
  }, []);

  const selected = state.devices.find((device) => device.deviceId === selectedId);
  if (selected) {
    return (
      <DeviceWorkspace
        client={client}
        device={selected}
        events={state.events[selected.deviceId] ?? []}
        approvals={state.approvals[selected.deviceId] ?? []}
        connection={state.connection}
        onBack={() => setSelectedId(undefined)}
        onApprove={async (approvalId, decision) => {
          await client.approve(selected.deviceId, approvalId, decision);
          showToast('审批结果已提交');
        }}
        onInterrupt={async () => {
          const snapshot = selected.snapshot;
          if (!snapshot?.activeThreadId || !snapshot.activeTurnId) throw new Error('当前没有活动 Turn');
          await client.interrupt(selected.deviceId, snapshot.activeThreadId, snapshot.activeTurnId);
          showToast('停止请求已接受，等待 Interrupted 终态');
        }}
        onRevoke={async () => {
          await client.revoke(selected.deviceId);
          setSelectedId(undefined);
          showToast('已解除配对');
        }}
        onNotify={showToast}
        toast={toast}
      />
    );
  }

  const needsPairing = showPairing || state.devices.length === 0;
  return (
    <main className="landing-shell">
      <LandingHeader connection={state.connection} />
      {needsPairing ? (
        <PairingPanel
          connection={state.connection}
          error={state.lastError}
          hasDevices={state.devices.length > 0}
          onCancel={() => setShowPairing(false)}
          onPair={async (code) => {
            await client.pair(code);
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
              <DeviceRow key={device.deviceId} device={device} onOpen={() => setSelectedId(device.deviceId)} />
            ))}
          </div>
          {toast && <div key={toast} className="toast">{toast}</div>}
        </section>
      )}
    </main>
  );
}

function LandingHeader({ connection }: { connection: RelayClientState['connection'] }) {
  return (
    <header className="landing-header">
      <Brand />
      <ConnectionState connection={connection} />
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
  hasDevices,
  onCancel,
  onPair,
}: {
  connection: RelayClientState['connection'];
  error?: string;
  hasDevices: boolean;
  onCancel: () => void;
  onPair: (code: string) => Promise<void>;
}) {
  const [code, setCode] = useState('');
  const [relayUrl, setRelayUrl] = useState(loadRelayUrl());
  const [submitting, setSubmitting] = useState(false);
  const [message, setMessage] = useState<string>();

  async function submit(event: FormEvent) {
    event.preventDefault();
    setSubmitting(true);
    setMessage(undefined);
    try {
      await onPair(code);
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
        <p>在电脑上运行 Agent 的 <code>--pair</code>，输入三分钟内有效的六位码。</p>
        <form onSubmit={submit}>
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
          <button className="solid-button full-button" disabled={code.length !== 6 || submitting || connection === 'offline'}>
            {submitting ? '正在配对…' : '配对'}
          </button>
        </form>
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
        {(message || error) && <p role="alert" className="inline-error">{message ?? error}</p>}
        {hasDevices && <button className="link-button" onClick={onCancel}>返回设备列表</button>}
      </div>
    </section>
  );
}

function DeviceRow({ device, onOpen }: { device: DeviceSummary; onOpen: () => void }) {
  return (
    <button className="device-row" onClick={onOpen} aria-label={`打开 ${device.name}`}>
      <span className="device-avatar">{device.name.slice(0, 1).toUpperCase()}</span>
      <span className="device-copy">
        <strong>{device.name}</strong>
        <small>{device.snapshot?.currentProject ?? (device.online ? '尚无活动项目' : '设备离线')}</small>
      </span>
      <StatusPill online={device.online} status={device.snapshot?.status} />
      <span className="row-chevron">›</span>
    </button>
  );
}

interface ChatEntry {
  id: string;
  role: 'user' | 'assistant' | 'tool' | 'system';
  text: string;
  meta?: string;
}

function DeviceWorkspace({
  client,
  device,
  events,
  approvals,
  connection,
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
  connection: RelayClientState['connection'];
  onBack: () => void;
  onApprove: (approvalId: string, decision: unknown) => Promise<void>;
  onInterrupt: () => Promise<void>;
  onRevoke: () => Promise<void>;
  onNotify: (message: string) => void;
  toast?: string;
}) {
  const [threads, setThreads] = useState<CodexThreadSummary[]>([]);
  const [historyLoading, setHistoryLoading] = useState(true);
  const [selectedThreadId, setSelectedThreadId] = useState<string>();
  const [newSession, setNewSession] = useState(false);
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [composer, setComposer] = useState('');
  const [newCwd, setNewCwd] = useState(device.snapshot?.currentProject ?? '');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [interrupting, setInterrupting] = useState(false);
  const [localEntries, setLocalEntries] = useState<ChatEntry[]>([]);
  const snapshot = device.snapshot;

  const refreshThreads = useCallback(async () => {
    setHistoryLoading(true);
    try {
      const history = await client.listThreads(device.deviceId);
      setThreads(history.threads);
      setError(undefined);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason));
    } finally {
      setHistoryLoading(false);
    }
  }, [client, device.deviceId]);

  useEffect(() => {
    void refreshThreads();
  }, [refreshThreads]);

  useEffect(() => {
    if (!snapshot?.activeThreadId) return;
    setSelectedThreadId(snapshot.activeThreadId);
    setNewSession(false);
  }, [snapshot?.activeThreadId]);

  useEffect(() => {
    if (newSession || selectedThreadId || threads.length === 0) return;
    setSelectedThreadId(threads[0].threadId);
  }, [newSession, selectedThreadId, threads]);

  useEffect(() => {
    if (snapshot?.status === 'Interrupted') setInterrupting(false);
  }, [snapshot?.status]);

  const selectedThread = threads.find((thread) => thread.threadId === selectedThreadId);
  const hasActiveTurn = Boolean(snapshot?.activeThreadId && snapshot.activeTurnId);
  const conversationId = snapshot?.activeThreadId ?? selectedThreadId;
  const eventEntries = useMemo(
    () => buildChatEntries(events, snapshot, conversationId),
    [conversationId, events, snapshot],
  );
  const chatEntries = [...localEntries, ...eventEntries];

  function selectThread(threadId: string) {
    setSelectedThreadId(threadId);
    setNewSession(false);
    setSidebarOpen(false);
    setLocalEntries([]);
    setComposer('');
    setError(undefined);
  }

  function beginNewSession() {
    setSelectedThreadId(undefined);
    setNewSession(true);
    setSidebarOpen(false);
    setLocalEntries([]);
    setComposer('');
    setError(undefined);
  }

  async function submit(event: FormEvent) {
    event.preventDefault();
    const text = composer.trim();
    if (!text || busy) return;
    setBusy(true);
    setError(undefined);
    setLocalEntries((entries) => [
      ...entries,
      { id: `local_${crypto.randomUUID()}`, role: 'user', text, meta: hasActiveTurn ? 'Steer' : undefined },
    ]);
    setComposer('');
    try {
      if (hasActiveTurn) {
        await client.steer(device.deviceId, snapshot!.activeThreadId!, snapshot!.activeTurnId!, text);
        onNotify('干预已送入当前 Turn');
      } else if (selectedThread) {
        const action = await client.resumeThread(device.deviceId, selectedThread.threadId, text);
        setSelectedThreadId(action.threadId);
        setNewSession(false);
        onNotify('历史会话已恢复，真实 Turn 已启动');
      } else {
        const action = await client.startThread(device.deviceId, newCwd.trim(), text);
        setSelectedThreadId(action.threadId);
        setNewSession(false);
        onNotify('新会话已创建，真实 Turn 已启动');
      }
      await refreshThreads();
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason));
    } finally {
      setBusy(false);
    }
  }

  const title = hasActiveTurn
    ? selectedThread?.name ?? selectedThread?.preview ?? '当前任务'
    : newSession
      ? '新任务'
      : selectedThread?.name ?? selectedThread?.preview ?? 'Codex';
  const composerLabel = hasActiveTurn
    ? 'Steer 当前任务'
    : selectedThread
      ? '继续历史会话的任务'
      : '第一条任务';
  const composerPlaceholder = hasActiveTurn
    ? '不要修改数据库结构，只调整业务层。'
    : selectedThread
      ? '继续这个会话…'
      : '给电脑上的 Codex 发送任务…';
  const sendLabel = hasActiveTurn
    ? '发送 Steer'
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
        <button className="new-chat-button" onClick={beginNewSession}>
          <span>＋</span> 新建任务
        </button>
        <div className="sidebar-label">
          <span>会话</span>
          <button className="icon-button" aria-label="刷新历史" disabled={historyLoading} onClick={() => void refreshThreads()}>↻</button>
        </div>
        <nav className="conversation-nav" aria-label="历史会话">
          {historyLoading ? <p className="sidebar-empty">正在读取历史…</p> : threads.length === 0 ? (
            <p className="sidebar-empty">暂无历史会话</p>
          ) : threads.map((thread) => (
            <button
              key={thread.threadId}
              className={`conversation-link ${selectedThreadId === thread.threadId && !newSession ? 'active' : ''}`}
              onClick={() => selectThread(thread.threadId)}
            >
              <span>{thread.name ?? thread.preview ?? '未命名会话'}</span>
              <small>{formatThreadTime(thread.updatedAt ?? thread.createdAt)}</small>
            </button>
          ))}
        </nav>
        <div className="sidebar-footer">
          <div className="sidebar-device">
            <span className="device-avatar small">{device.name.slice(0, 1).toUpperCase()}</span>
            <span><strong>{device.name}</strong><small><ConnectionState connection={connection} /></small></span>
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
            <span>{selectedThread?.cwd ?? snapshot?.currentProject ?? device.name}</span>
          </div>
          <StatusPill online={device.online} status={interrupting ? 'Interrupting' : snapshot?.status} />
          <button
            className="stop-button"
            disabled={busy || !hasActiveTurn}
            onClick={() => {
              setBusy(true);
              setError(undefined);
              void onInterrupt()
                .then(() => setInterrupting(true))
                .catch((reason) => setError(reason instanceof Error ? reason.message : String(reason)))
                .finally(() => setBusy(false));
            }}
          >
            停止当前任务
          </button>
        </header>

        {(snapshot?.currentActivity || snapshot?.runningCommand) && (
          <div className="runtime-strip">
            <span className="pulse-dot" />
            <span>{snapshot.currentActivity ?? '运行中'}</span>
            {snapshot.runningCommand && <code>{snapshot.runningCommand}</code>}
          </div>
        )}

        <section className="chat-feed" aria-label="会话内容">
          {newSession && chatEntries.length === 0 ? (
            <div className="empty-chat">
              <span className="empty-mark">C</span>
              <h1>今天想让电脑上的 Codex 做什么？</h1>
              <p>选择项目目录，在下方输入任务。任务会在电脑上真实执行。</p>
            </div>
          ) : chatEntries.length === 0 ? (
            <div className="empty-chat compact">
              <span className="empty-mark">C</span>
              <h1>{selectedThread?.name ?? selectedThread?.preview ?? '准备就绪'}</h1>
              <p>在下方输入消息，将恢复这个历史 Thread 并开始新的 Turn。</p>
            </div>
          ) : (
            <div className="message-column">
              {chatEntries.map((entry) => <ChatMessage key={entry.id} entry={entry} />)}
            </div>
          )}

          {approvals.map((approval) => (
            <ApprovalCard
              key={approval.approvalId}
              approval={approval}
              disabled={busy}
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

        <div className="composer-dock">
          {!hasActiveTurn && !selectedThread && (
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
              <span>{hasActiveTurn ? 'Steer 当前 Turn' : selectedThread ? '恢复历史会话' : '创建新会话'}</span>
              <button
                className="send-button"
                aria-label={sendLabel}
                title={sendLabel}
                disabled={busy || !device.online || !composer.trim() || (!hasActiveTurn && !selectedThread && !newCwd.trim())}
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
  if (entry.role === 'system') {
    return <div className="system-message"><span>{entry.text}</span></div>;
  }
  if (entry.role === 'tool') {
    return <div className="tool-message"><span>›_</span><div><strong>{entry.meta ?? '电脑操作'}</strong><p>{entry.text}</p></div></div>;
  }
  return (
    <article className={`chat-message ${entry.role}`}>
      {entry.role === 'assistant' && <span className="assistant-avatar">C</span>}
      <div>
        {entry.meta && <small>{entry.meta}</small>}
        <p>{entry.text}</p>
      </div>
    </article>
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
  const decisions = approval.availableDecisions.length > 0
    ? approval.availableDecisions
    : ['accept', 'decline', 'cancel'];
  return (
    <section className="approval-message" aria-label="等待审批">
      <div className="approval-heading"><span>!</span><div><strong>等待审批</strong><small>{approval.requestMethod}</small></div></div>
      {approval.command && <pre>{approval.command}</pre>}
      {approval.cwd && <p>目录：{approval.cwd}</p>}
      {approval.reason && <p>{approval.reason}</p>}
      <div className="approval-actions">
        {decisions.map((decision) => (
          <button
            key={JSON.stringify(decision)}
            disabled={disabled}
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

function StatusPill({ online, status }: { online: boolean; status?: string }) {
  const value = online ? status ?? 'Idle' : 'Offline';
  return <span className={`status-pill ${statusClass(value)}`}><i />{value}</span>;
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

function buildChatEntries(events: CodexEvent[], snapshot: CodexSnapshot | undefined, threadId?: string): ChatEntry[] {
  const entries: ChatEntry[] = [];
  for (const event of [...events].reverse()) {
    if (threadId && event.threadId && event.threadId !== threadId) continue;
    const text = eventText(event.data);
    switch (event.kind) {
      case 'AgentMessageCompleted':
        if (text) entries.push({ id: event.eventId, role: 'assistant', text });
        break;
      case 'CommandStarted':
      case 'CommandCompleted':
        if (text) entries.push({ id: event.eventId, role: 'tool', text, meta: event.kind === 'CommandStarted' ? '运行命令' : '命令完成' });
        break;
      case 'FileChanged':
        if (text) entries.push({ id: event.eventId, role: 'tool', text, meta: '修改文件' });
        break;
      case 'ErrorOccurred':
        entries.push({ id: event.eventId, role: 'system', text: text || '任务发生错误' });
        break;
      case 'TurnStarted':
        entries.push({ id: event.eventId, role: 'system', text: 'Turn 已开始' });
        break;
      case 'TurnCompleted':
        entries.push({ id: event.eventId, role: 'system', text: `Turn ${text || '已完成'}` });
        break;
    }
  }

  if (snapshot?.lastAgentMessage && !entries.some((entry) =>
    entry.role === 'assistant' && entry.text === snapshot.lastAgentMessage,
  )) {
    entries.push({ id: 'snapshot-last-message', role: 'assistant', text: snapshot.lastAgentMessage });
  }
  return entries.slice(-80);
}

function eventText(data: Record<string, unknown>) {
  const value = data.text ?? data.command ?? data.path ?? data.message ?? data.status;
  return typeof value === 'string' ? value : '';
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
