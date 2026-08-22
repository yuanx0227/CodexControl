import { useEffect, useMemo, useState, type FormEvent } from 'react';
import type { ApprovalRequested, DeviceSummary } from './protocol';
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

  useEffect(() => {
    const unsubscribe = client.subscribe(setState);
    void client.start();
    return () => {
      unsubscribe();
      client.stop();
    };
  }, [client]);

  const selected = state.devices.find((device) => device.deviceId === selectedId);
  if (selected) {
    return (
      <DeviceDetail
        device={selected}
        events={state.events[selected.deviceId] ?? []}
        approvals={state.approvals[selected.deviceId] ?? []}
        onBack={() => setSelectedId(undefined)}
        onSteer={async (text) => {
          const snapshot = selected.snapshot;
          if (!snapshot?.activeThreadId || !snapshot.activeTurnId) throw new Error('当前没有活动 Turn');
          await client.steer(selected.deviceId, snapshot.activeThreadId, snapshot.activeTurnId, text);
          setToast('干预已送入当前 Turn');
        }}
        onInterrupt={async () => {
          const snapshot = selected.snapshot;
          if (!snapshot?.activeThreadId || !snapshot.activeTurnId) throw new Error('当前没有活动 Turn');
          await client.interrupt(selected.deviceId, snapshot.activeThreadId, snapshot.activeTurnId);
          setToast('停止请求已接受，等待 Interrupted 终态');
        }}
        onApprove={async (approvalId, decision) => {
          await client.approve(selected.deviceId, approvalId, decision);
          setToast('审批结果已提交');
        }}
        onRevoke={async () => {
          await client.revoke(selected.deviceId);
          setSelectedId(undefined);
          setToast('已解除配对');
        }}
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
            setToast('配对成功');
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
          {toast && <div className="toast">{toast}</div>}
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
  device,
  events,
  approvals,
  onBack,
  onSteer,
  onInterrupt,
  onApprove,
  onRevoke,
  toast,
}: {
  device: DeviceSummary;
  events: Array<{ eventId: string; kind: string; occurredAt: number; data: Record<string, unknown> }>;
  approvals: ApprovalRequested[];
  onBack: () => void;
  onSteer: (text: string) => Promise<void>;
  onInterrupt: () => Promise<void>;
  onApprove: (approvalId: string, decision: unknown) => Promise<void>;
  onRevoke: () => Promise<void>;
  toast?: string;
}) {
  const [steer, setSteer] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [interrupting, setInterrupting] = useState(false);
  const snapshot = device.snapshot;

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
        {toast && <div className="toast">{toast}</div>}
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

function eventSummary(data: Record<string, unknown>) {
  const value = data.command ?? data.text ?? data.status ?? data.delta;
  return typeof value === 'string' ? value : '';
}
