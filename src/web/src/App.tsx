import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from 'react';
import type { ApprovalRequested, CodexThreadSummary, DeviceSummary } from './protocol';
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
      <DeviceDetail
        client={client}
        device={selected}
        events={state.events[selected.deviceId] ?? []}
        approvals={state.approvals[selected.deviceId] ?? []}
        onBack={() => setSelectedId(undefined)}
        onSteer={async (text) => {
          const snapshot = selected.snapshot;
          if (!snapshot?.activeThreadId || !snapshot.activeTurnId) throw new Error('当前没有活动 Turn');
          await client.steer(selected.deviceId, snapshot.activeThreadId, snapshot.activeTurnId, text);
          showToast('干预已送入当前 Turn');
        }}
        onInterrupt={async () => {
          const snapshot = selected.snapshot;
          if (!snapshot?.activeThreadId || !snapshot.activeTurnId) throw new Error('当前没有活动 Turn');
          await client.interrupt(selected.deviceId, snapshot.activeThreadId, snapshot.activeTurnId);
          showToast('停止请求已接受，等待 Interrupted 终态');
        }}
        onApprove={async (approvalId, decision) => {
          await client.approve(selected.deviceId, approvalId, decision);
          showToast('审批结果已提交');
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
    <main className="app-shell">
      <Header connection={state.connection} />
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
        <section className="content" aria-label="设备列表">
          <div className="section-heading">
            <div>
              <p className="eyebrow">Paired devices</p>
              <h1>开发电脑</h1>
            </div>
            <button className="secondary-button" onClick={() => setShowPairing(true)}>
              添加电脑
            </button>
          </div>
          <div className="device-grid">
            {state.devices.map((device) => (
              <DeviceCard key={device.deviceId} device={device} onOpen={() => setSelectedId(device.deviceId)} />
            ))}
          </div>
          {toast && <div key={toast} className="toast">{toast}</div>}
        </section>
      )}
    </main>
  );
}

function Header({ connection }: { connection: RelayClientState['connection'] }) {
  const label = connection === 'connected' ? 'Relay 已连接' : connection === 'connecting' ? '正在连接' : 'Relay 离线';
  return (
    <header className="topbar">
      <div className="brand-mark">C</div>
      <div className="brand-copy">
        <strong>Codex Control</strong>
        <span className={`connection ${connection}`}><i />{label}</span>
      </div>
    </header>
  );
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
    <section className="pairing-card content">
      <p className="eyebrow">Secure pairing</p>
      <h1>连接你的电脑</h1>
      <p className="muted">在电脑上运行 Agent 的 <code>--pair</code>，输入三分钟内有效的六位码。</p>
      <form onSubmit={submit}>
        <label htmlFor="pairing-code">六位配对码</label>
        <input
          id="pairing-code"
          className="code-input"
          inputMode="numeric"
          autoComplete="one-time-code"
          maxLength={7}
          placeholder="583 271"
          value={formatCode(code)}
          onChange={(event) => setCode(event.target.value.replaceAll(/\D/gu, '').slice(0, 6))}
          autoFocus
        />
        <button className="primary-button" disabled={code.length !== 6 || submitting || connection === 'offline'}>
          {submitting ? '正在配对…' : '配对'}
        </button>
      </form>
      <details className="relay-settings">
        <summary>Relay 设置</summary>
        <label htmlFor="relay-url">WebSocket 地址</label>
        <div className="inline-form">
          <input id="relay-url" value={relayUrl} onChange={(event) => setRelayUrl(event.target.value)} />
          <button
            type="button"
            className="secondary-button"
            onClick={() => {
              saveRelayUrl(relayUrl);
              window.location.reload();
            }}
          >
            保存并重连
          </button>
        </div>
      </details>
      {(message || error) && <p role="alert" className="error-message">{message ?? error}</p>}
      {hasDevices && <button className="text-button" onClick={onCancel}>返回设备列表</button>}
    </section>
  );
}

function DeviceCard({ device, onOpen }: { device: DeviceSummary; onOpen: () => void }) {
  const snapshot = device.snapshot;
  return (
    <button className="device-card" onClick={onOpen} aria-label={`打开 ${device.name}`}>
      <div className="device-card-top">
        <strong>{device.name}</strong>
        <StatusPill online={device.online} status={snapshot?.status} />
      </div>
      <p className="project-path">{snapshot?.currentProject ?? '尚无活动项目'}</p>
      <p className="activity-line">{snapshot?.currentActivity ?? (device.online ? 'Idle' : 'Offline')}</p>
      <span className="open-hint">查看详情 →</span>
    </button>
  );
}

function DeviceDetail({
  client,
  device,
  events,
  approvals,
  onBack,
  onSteer,
  onInterrupt,
  onApprove,
  onRevoke,
  onNotify,
  toast,
}: {
  client: RelayClient;
  device: DeviceSummary;
  events: Array<{ eventId: string; kind: string; occurredAt: number; data: Record<string, unknown> }>;
  approvals: ApprovalRequested[];
  onBack: () => void;
  onSteer: (text: string) => Promise<void>;
  onInterrupt: () => Promise<void>;
  onApprove: (approvalId: string, decision: unknown) => Promise<void>;
  onRevoke: () => Promise<void>;
  onNotify: (message: string) => void;
  toast?: string;
}) {
  const [steer, setSteer] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [interrupting, setInterrupting] = useState(false);
  const [threads, setThreads] = useState<CodexThreadSummary[]>([]);
  const [historyLoading, setHistoryLoading] = useState(true);
  const [selectedThreadId, setSelectedThreadId] = useState<string>();
  const [resumeText, setResumeText] = useState('');
  const [showNewSession, setShowNewSession] = useState(false);
  const [newCwd, setNewCwd] = useState(device.snapshot?.currentProject ?? '');
  const [newText, setNewText] = useState('');
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
    if (snapshot?.status === 'Interrupted') setInterrupting(false);
  }, [snapshot?.status]);

  async function run(action: () => Promise<void>) {
    setBusy(true);
    setError(undefined);
    try {
      await action();
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason));
    } finally {
      setBusy(false);
    }
  }

  const selectedThread = threads.find((thread) => thread.threadId === selectedThreadId);
  const hasActiveTurn = Boolean(snapshot?.activeTurnId);

  return (
    <main className="app-shell">
      <header className="detail-header">
        <button className="back-button" onClick={onBack}>← 返回</button>
        <div>
          <h1>{device.name}</h1>
          <StatusPill online={device.online} status={interrupting ? 'Interrupting' : snapshot?.status} />
        </div>
      </header>
      <section className="content detail-content">
        <div className="metric-grid">
          <Metric label="项目" value={snapshot?.currentProject ?? '—'} />
          <Metric label="当前活动" value={snapshot?.currentActivity ?? '—'} />
          <Metric label="运行命令" value={snapshot?.runningCommand ?? '—'} />
          <Metric label="最近消息" value={snapshot?.lastAgentMessage ?? '—'} />
        </div>

        <section className="session-card" aria-label="Codex 会话控制">
          <div className="session-heading">
            <div>
              <p className="eyebrow">Real app-server sessions</p>
              <h2>Codex 会话</h2>
              <p className="muted">历史来自电脑上的 <code>thread/list</code>；创建和恢复会直接启动真实 Turn。</p>
            </div>
            <div className="button-row compact-actions">
              <button
                className="secondary-button"
                disabled={historyLoading || !device.online}
                onClick={() => void refreshThreads()}
              >
                {historyLoading ? '加载中…' : '刷新历史'}
              </button>
              <button
                className="primary-button"
                disabled={!device.online || hasActiveTurn}
                onClick={() => setShowNewSession((value) => !value)}
              >
                新建会话
              </button>
            </div>
          </div>

          {hasActiveTurn && (
            <p className="session-warning">当前 Turn 正在运行；可在下方 Steer 或停止，结束后再创建/恢复会话。</p>
          )}

          {showNewSession && (
            <form
              className="session-form"
              onSubmit={(event) => {
                event.preventDefault();
                void run(async () => {
                  await client.startThread(device.deviceId, newCwd.trim(), newText.trim());
                  setNewText('');
                  setShowNewSession(false);
                  onNotify('新会话已创建，真实 Turn 已启动');
                  await refreshThreads();
                });
              }}
            >
              <label htmlFor="new-session-cwd">电脑上的项目目录</label>
              <input
                id="new-session-cwd"
                placeholder="D:\Projects\MES"
                value={newCwd}
                onChange={(event) => setNewCwd(event.target.value)}
              />
              <label htmlFor="new-session-text">第一条任务</label>
              <textarea
                id="new-session-text"
                placeholder="描述要让电脑上的 Codex 真正执行的任务"
                value={newText}
                onChange={(event) => setNewText(event.target.value)}
                rows={4}
              />
              <button
                className="primary-button"
                disabled={busy || hasActiveTurn || !newCwd.trim() || !newText.trim()}
              >
                创建会话并开始
              </button>
            </form>
          )}

          <div className="thread-list" aria-label="历史会话">
            {historyLoading ? <p className="muted">正在读取电脑历史会话…</p> : threads.length === 0 ? (
              <p className="muted">电脑的 Codex 存储中没有可恢复的会话。</p>
            ) : threads.map((thread) => (
              <button
                key={thread.threadId}
                className={`thread-item ${selectedThreadId === thread.threadId ? 'selected' : ''}`}
                onClick={() => setSelectedThreadId(thread.threadId)}
              >
                <span className="thread-title">{thread.name ?? thread.preview ?? '未命名会话'}</span>
                {thread.preview && thread.name && <span className="thread-preview">{thread.preview}</span>}
                <span className="thread-meta">
                  {thread.cwd ?? '未知目录'} · {formatThreadTime(thread.updatedAt ?? thread.createdAt)} · {thread.status}
                </span>
              </button>
            ))}
          </div>

          {selectedThread && (
            <form
              className="session-form resume-form"
              onSubmit={(event) => {
                event.preventDefault();
                void run(async () => {
                  await client.resumeThread(device.deviceId, selectedThread.threadId, resumeText.trim());
                  setResumeText('');
                  onNotify('历史会话已恢复，真实 Turn 已启动');
                  await refreshThreads();
                });
              }}
            >
              <strong>继续：{selectedThread.name ?? selectedThread.preview ?? selectedThread.threadId}</strong>
              <textarea
                aria-label="继续历史会话的任务"
                placeholder="输入后续任务，将恢复该会话并开始新的 Turn"
                value={resumeText}
                onChange={(event) => setResumeText(event.target.value)}
                rows={3}
              />
              <button
                className="primary-button"
                disabled={busy || hasActiveTurn || !resumeText.trim()}
              >
                恢复会话并发送
              </button>
            </form>
          )}
        </section>

        {approvals.map((approval) => (
          <ApprovalCard
            key={approval.approvalId}
            approval={approval}
            disabled={busy}
            onDecision={(decision) => run(() => onApprove(approval.approvalId, decision))}
          />
        ))}

        <section className="control-card">
          <h2>立即干预</h2>
          <textarea
            placeholder="不要修改数据库结构，只调整业务层。"
            value={steer}
            onChange={(event) => setSteer(event.target.value)}
            rows={4}
          />
          <div className="button-row">
            <button
              className="primary-button"
              disabled={busy || !device.online || !snapshot?.activeTurnId || !steer.trim()}
              onClick={() => run(async () => {
                await onSteer(steer.trim());
                setSteer('');
              })}
            >
              发送 Steer
            </button>
            <button
              className="danger-button"
              disabled={busy || !device.online || !snapshot?.activeTurnId}
              onClick={() => run(async () => {
                await onInterrupt();
                setInterrupting(true);
              })}
            >
              停止当前任务
            </button>
          </div>
        </section>

        <section className="timeline-card">
          <h2>最近活动</h2>
          {events.length === 0 ? <p className="muted">暂无事件</p> : (
            <ol className="timeline">
              {events.map((event) => (
                <li key={event.eventId}>
                  <time>{new Date(event.occurredAt).toLocaleTimeString()}</time>
                  <strong>{event.kind}</strong>
                  <span>{eventSummary(event.data)}</span>
                </li>
              ))}
            </ol>
          )}
        </section>

        {error && <p role="alert" className="error-message">{error}</p>}
        {toast && <div key={toast} className="toast">{toast}</div>}
        <button className="text-button danger-text" onClick={() => run(onRevoke)}>解除此电脑配对</button>
      </section>
    </main>
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
    <section className="approval-card" aria-label="等待审批">
      <div className="approval-title"><span>!</span><div><p className="eyebrow">等待审批</p><h2>{approval.requestMethod}</h2></div></div>
      {approval.command && <pre>{approval.command}</pre>}
      {approval.cwd && <p className="muted">目录：{approval.cwd}</p>}
      {approval.reason && <p>{approval.reason}</p>}
      <div className="button-row">
        {decisions.map((decision) => (
          <button
            key={JSON.stringify(decision)}
            disabled={disabled}
            className={decisionKind(decision) === 'allow' ? 'primary-button' : 'secondary-button'}
            onClick={() => onDecision(decision)}
          >
            {decisionLabel(decision)}
          </button>
        ))}
      </div>
    </section>
  );
}

function Metric({ label, value }: { label: string; value: string }) {
  return <div className="metric"><span>{label}</span><strong>{value}</strong></div>;
}

function StatusPill({ online, status }: { online: boolean; status?: string }) {
  const value = online ? status ?? 'Idle' : 'Offline';
  return <span className={`status-pill ${statusClass(value)}`}><i />{value}</span>;
}

function statusClass(status: string) {
  if (status === 'WaitingApproval') return 'warning';
  if (status === 'Failed' || status === 'Offline') return 'offline';
  if (status === 'Idle' || status === 'Completed') return 'idle';
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

function eventSummary(data: Record<string, unknown>) {
  const value = data.command ?? data.text ?? data.status ?? data.delta;
  return typeof value === 'string' ? value : '';
}
