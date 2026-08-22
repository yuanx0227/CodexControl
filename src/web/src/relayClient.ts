import {
  addPairedDevice,
  authCanonical,
  base64UrlEncode,
  getOrCreateIdentity,
  pairingCanonical,
  removePairedDevice,
  sign,
} from './controllerIdentity';
import {
  MessageType,
  createEnvelope,
  type ApprovalRequested,
  type AuthChallenge,
  type AuthOk,
  type CodexEvent,
  type CodexSnapshot,
  type ControlResult,
  type DeviceListResult,
  type DeviceSummary,
  type ErrorPayload,
  type PairingCompleted,
  type RelayEnvelope,
} from './protocol';
import { loadRelayUrl, type StoredControllerIdentity } from './storage';

export interface RelayClientState {
  connection: 'connecting' | 'connected' | 'offline';
  authenticated: boolean;
  lastError?: string;
  devices: DeviceSummary[];
  events: Record<string, CodexEvent[]>;
  approvals: Record<string, ApprovalRequested[]>;
}

type Listener = (state: RelayClientState) => void;

interface PendingRequest {
  resolve: (envelope: RelayEnvelope) => void;
  reject: (error: Error) => void;
  timer: number;
}

export class RelayClient {
  private identity?: StoredControllerIdentity;
  private socket?: WebSocket;
  private listeners = new Set<Listener>();
  private pending = new Map<string, PendingRequest>();
  private reconnectAttempt = 0;
  private reconnectTimer?: number;
  private heartbeatTimer?: number;
  private stopped = false;
  private authRequestId?: string;
  private connectionId?: string;
  private state: RelayClientState = {
    connection: 'offline',
    authenticated: false,
    devices: [],
    events: {},
    approvals: {},
  };

  subscribe(listener: Listener): () => void {
    this.listeners.add(listener);
    listener(this.snapshot());
    return () => this.listeners.delete(listener);
  }

  get currentState(): RelayClientState {
    return this.snapshot();
  }

  async start(): Promise<void> {
    this.stopped = false;
    this.identity = await getOrCreateIdentity();
    this.open();
  }

  stop(): void {
    this.stopped = true;
    if (this.reconnectTimer) window.clearTimeout(this.reconnectTimer);
    if (this.heartbeatTimer) window.clearInterval(this.heartbeatTimer);
    this.socket?.close(1000, 'client stopped');
    this.failPending(new Error('Relay client stopped'));
    this.setState({ connection: 'offline', authenticated: false });
  }

  async pair(codeInput: string): Promise<PairingCompleted> {
    const identity = this.requireIdentity();
    await this.waitForOpen();
    const code = codeInput.replaceAll(/\D/gu, '');
    if (code.length !== 6) throw new Error('请输入六位配对码');
    const proofNonce = base64UrlEncode(crypto.getRandomValues(new Uint8Array(32)));
    const proofSignature = await sign(
      identity,
      pairingCanonical(code, identity.controllerId, identity.publicKeyEncoded, proofNonce),
    );
    const envelope = await this.request(
      MessageType.pairingClaim,
      {
        code,
        controllerId: identity.controllerId,
        controllerName: navigator.userAgent.includes('Mobile') ? 'Mobile Browser' : 'Browser',
        publicKey: identity.publicKeyEncoded,
        proofNonce,
        proofSignature,
      },
      { controllerId: identity.controllerId },
    );
    const completed = envelope.payload as PairingCompleted;
    this.identity = await addPairedDevice(identity, completed.deviceId);
    this.setState({ authenticated: true, connection: 'connected', lastError: undefined });
    this.startHeartbeat();
    await this.refreshDevices();
    return completed;
  }

  async refreshDevices(): Promise<void> {
    const identity = this.requireIdentity();
    if (!this.state.authenticated) return;
    const response = await this.request(
      MessageType.deviceList,
      {},
      { controllerId: identity.controllerId },
    );
    this.applyDeviceList(response.payload as DeviceListResult);
  }

  async steer(deviceId: string, threadId: string, expectedTurnId: string, text: string): Promise<ControlResult> {
    return this.control(
      MessageType.controlSteer,
      deviceId,
      { threadId, expectedTurnId, text },
    );
  }

  async interrupt(deviceId: string, threadId: string, turnId: string): Promise<ControlResult> {
    return this.control(MessageType.controlInterrupt, deviceId, { threadId, turnId });
  }

  async approve(deviceId: string, approvalId: string, decision: unknown): Promise<ControlResult> {
    return this.control(MessageType.controlApproval, deviceId, { approvalId, decision });
  }

  async revoke(deviceId: string): Promise<void> {
    const identity = this.requireIdentity();
    await this.request(
      MessageType.pairingRevoked,
      { deviceId, controllerId: identity.controllerId },
      { deviceId, controllerId: identity.controllerId },
    );
    this.identity = await removePairedDevice(identity, deviceId);
    this.setState({ devices: this.state.devices.filter((device) => device.deviceId !== deviceId) });
  }

  private open(): void {
    if (this.stopped || this.socket?.readyState === WebSocket.OPEN || this.socket?.readyState === WebSocket.CONNECTING) {
      return;
    }

    let relayUrl: URL;
    try {
      relayUrl = validateRelayUrl(loadRelayUrl());
    } catch (error) {
      this.setState({ connection: 'offline', lastError: error instanceof Error ? error.message : String(error) });
      return;
    }

    this.setState({ connection: 'connecting' });
    const socket = new WebSocket(relayUrl);
    this.socket = socket;
    socket.onopen = () => {
      this.reconnectAttempt = 0;
      this.setState({ connection: 'connected', lastError: undefined });
      void this.beginAuthentication();
    };
    socket.onmessage = (event) => void this.handleMessage(String(event.data));
    socket.onerror = () => socket.close();
    socket.onclose = () => {
      if (this.heartbeatTimer) window.clearInterval(this.heartbeatTimer);
      this.connectionId = undefined;
      this.failPending(new Error('Relay connection lost'));
      this.setState({ connection: 'offline', authenticated: false });
      if (!this.stopped) this.scheduleReconnect();
    };
  }

  private async beginAuthentication(): Promise<void> {
    const identity = this.requireIdentity();
    this.authRequestId = requestId();
    this.send(
      createEnvelope(
        MessageType.authHello,
        { role: 'controller', principalId: identity.controllerId, clientVersion: '0.1.0' },
        { requestId: this.authRequestId, controllerId: identity.controllerId },
      ),
    );
  }

  private async handleMessage(json: string): Promise<void> {
    let envelope: RelayEnvelope;
    try {
      envelope = JSON.parse(json) as RelayEnvelope;
      if (envelope.version !== 1 || !envelope.type) throw new Error('Invalid Relay envelope');
    } catch {
      this.setState({ lastError: 'Relay 返回了无效消息' });
      return;
    }

    if (envelope.type === MessageType.authChallenge) {
      await this.answerChallenge(envelope as RelayEnvelope<AuthChallenge>);
      return;
    }

    if (envelope.type === MessageType.authOk) {
      const auth = envelope.payload as AuthOk;
      this.connectionId = auth.connectionId;
      this.setState({ authenticated: true, connection: 'connected', lastError: undefined });
      this.startHeartbeat();
      await this.refreshDevices();
      return;
    }

    if (envelope.type === MessageType.error && envelope.requestId === this.authRequestId) {
      const error = envelope.payload as ErrorPayload;
      this.setState({ authenticated: false, lastError: error.code === 'AUTH_FAILED' ? undefined : error.message });
      return;
    }

    if (envelope.requestId && this.pending.has(envelope.requestId)) {
      const pending = this.pending.get(envelope.requestId)!;
      this.pending.delete(envelope.requestId);
      window.clearTimeout(pending.timer);
      if (envelope.type === MessageType.error) {
        const error = envelope.payload as ErrorPayload;
        pending.reject(new Error(`${error.code}: ${error.message}`));
      } else {
        pending.resolve(envelope);
      }
    }

    switch (envelope.type) {
      case MessageType.pairingCompleted: {
        const pairing = envelope.payload as PairingCompleted;
        const identity = this.requireIdentity();
        this.identity = await addPairedDevice(identity, pairing.deviceId);
        break;
      }
      case MessageType.deviceListResult:
        this.applyDeviceList(envelope.payload as DeviceListResult);
        break;
      case MessageType.deviceOnline:
        this.updateDevice(envelope.deviceId, { online: true });
        break;
      case MessageType.deviceOffline:
        this.updateDevice(envelope.deviceId, { online: false });
        break;
      case MessageType.codexSnapshot:
        this.updateDevice(envelope.deviceId, { snapshot: envelope.payload as CodexSnapshot });
        break;
      case MessageType.codexEvent:
        this.applyEvent(envelope.deviceId, envelope.payload as CodexEvent);
        break;
    }
  }

  private async answerChallenge(envelope: RelayEnvelope<AuthChallenge>): Promise<void> {
    const identity = this.requireIdentity();
    const challenge = envelope.payload;
    const signature = await sign(
      identity,
      authCanonical(
        identity.controllerId,
        challenge.challengeId,
        challenge.nonce,
        challenge.expiresAt,
      ),
    );
    this.send(
      createEnvelope(
        MessageType.authResponse,
        { challengeId: challenge.challengeId, signature },
        { requestId: envelope.requestId, controllerId: identity.controllerId },
      ),
    );
  }

  private async control(type: string, deviceId: string, payload: unknown): Promise<ControlResult> {
    const identity = this.requireIdentity();
    const response = await this.request(type, payload, {
      deviceId,
      controllerId: identity.controllerId,
    });
    const result = response.payload as ControlResult;
    if (result.status === 'failed') throw new Error(`${result.code ?? 'CONTROL_FAILED'}: ${result.message ?? ''}`);
    return result;
  }

  private request(type: string, payload: unknown, ids: { deviceId?: string; controllerId?: string }): Promise<RelayEnvelope> {
    const id = requestId();
    return new Promise((resolve, reject) => {
      const timer = window.setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`Relay request timeout: ${type}`));
      }, 15_000);
      this.pending.set(id, { resolve, reject, timer });
      try {
        this.send(createEnvelope(type, payload, { requestId: id, ...ids }));
      } catch (error) {
        window.clearTimeout(timer);
        this.pending.delete(id);
        reject(error instanceof Error ? error : new Error(String(error)));
      }
    });
  }

  private send(envelope: RelayEnvelope): void {
    if (this.socket?.readyState !== WebSocket.OPEN) throw new Error('Relay 尚未连接');
    this.socket.send(JSON.stringify(envelope));
  }

  private async waitForOpen(): Promise<void> {
    const deadline = Date.now() + 10_000;
    while (Date.now() < deadline) {
      if (this.socket?.readyState === WebSocket.OPEN) return;
      await new Promise((resolve) => window.setTimeout(resolve, 50));
    }
    throw new Error('Relay 连接超时');
  }

  private startHeartbeat(): void {
    if (this.heartbeatTimer) window.clearInterval(this.heartbeatTimer);
    this.heartbeatTimer = window.setInterval(() => {
      if (!this.connectionId) return;
      const identity = this.requireIdentity();
      this.send(
        createEnvelope(
          MessageType.heartbeat,
          { connectionId: this.connectionId, snapshotRevision: 0 },
          { controllerId: identity.controllerId },
        ),
      );
    }, 15_000);
  }

  private scheduleReconnect(): void {
    const delays = [1000, 2000, 5000, 10_000, 30_000];
    const base = delays[Math.min(this.reconnectAttempt++, delays.length - 1)];
    this.reconnectTimer = window.setTimeout(() => this.open(), base * (1 + Math.random() * 0.2));
  }

  private applyDeviceList(payload: DeviceListResult): void {
    this.setState({ devices: payload.devices });
  }

  private updateDevice(deviceId: string | undefined, patch: Partial<DeviceSummary>): void {
    if (!deviceId) return;
    this.setState({
      devices: this.state.devices.map((device) =>
        device.deviceId === deviceId ? { ...device, ...patch } : device,
      ),
    });
  }

  private applyEvent(deviceId: string | undefined, event: CodexEvent): void {
    if (!deviceId) return;
    const events = [event, ...(this.state.events[deviceId] ?? [])].slice(0, 100);
    const approvals = { ...this.state.approvals };
    if (event.kind === 'ApprovalRequested') {
      const approval = event.data as unknown as ApprovalRequested;
      approvals[deviceId] = [approval, ...(approvals[deviceId] ?? [])];
    } else if (event.kind === 'ApprovalResolved') {
      const resolved = event.data as { approvalId: string };
      approvals[deviceId] = (approvals[deviceId] ?? []).filter(
        (approval) => approval.approvalId !== resolved.approvalId,
      );
    }
    this.setState({ events: { ...this.state.events, [deviceId]: events }, approvals });
  }

  private setState(patch: Partial<RelayClientState>): void {
    this.state = { ...this.state, ...patch };
    const snapshot = this.snapshot();
    this.listeners.forEach((listener) => listener(snapshot));
  }

  private snapshot(): RelayClientState {
    return {
      ...this.state,
      devices: [...this.state.devices],
      events: { ...this.state.events },
      approvals: { ...this.state.approvals },
    };
  }

  private requireIdentity(): StoredControllerIdentity {
    if (!this.identity) throw new Error('Controller identity is not initialized');
    return this.identity;
  }

  private failPending(error: Error): void {
    this.pending.forEach((pending) => {
      window.clearTimeout(pending.timer);
      pending.reject(error);
    });
    this.pending.clear();
  }
}

function requestId(): string {
  return `req_${crypto.randomUUID().replaceAll('-', '')}`;
}

function validateRelayUrl(value: string): URL {
  const url = new URL(value);
  if (url.protocol !== 'wss:' && url.protocol !== 'ws:') throw new Error('Relay URL 必须使用 wss://');
  const local = url.hostname === '127.0.0.1' || url.hostname === 'localhost';
  if (url.protocol === 'ws:' && !local) throw new Error('非本机 Relay 必须使用 wss://');
  url.pathname = url.pathname.endsWith('/ws/controller')
    ? url.pathname
    : `${url.pathname.replace(/\/$/u, '')}/ws/controller`;
  return url;
}
