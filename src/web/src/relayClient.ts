import {
  addPairedDevice,
  authCanonical,
  base64UrlEncode,
  getOrCreateIdentity,
  pairingCanonical,
  removePairedDevice,
  sign,
} from './controllerIdentity';
import packageJson from '../package.json';
import {
  MessageType,
  createEnvelope,
  type ApprovalRequested,
  type AuthChallenge,
  type AuthOk,
  type CodexApprovalPolicy,
  type CodexEvent,
  type CodexSessionOptions,
  type CodexSnapshot,
  type CodexThreadActionResult,
  type CodexThreadListResult,
  type CodexThreadReadResult,
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
  pairingPending?: string;
  relayVersion?: string;
}

type Listener = (state: RelayClientState) => void;

interface PendingRequest {
  resolve: (envelope: RelayEnvelope) => void;
  reject: (error: Error) => void;
  timer: number;
}

interface PairingClaim {
  code: string;
  controllerId: string;
  controllerName: string;
  publicKey: string;
  proofNonce: string;
  proofSignature: string;
}

export class RelayClient {
  private static readonly readRecoveryTimeoutMs = 30_000;
  private static readonly readRecoveryAttempts = 3;
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

  async pair(codeInput: string, controllerNameInput?: string): Promise<PairingCompleted> {
    const identity = this.requireIdentity();
    const code = codeInput.replaceAll(/\D/gu, '');
    if (code.length !== 6) throw new Error('请输入六位配对码');
    const proofNonce = base64UrlEncode(crypto.getRandomValues(new Uint8Array(32)));
    const controllerName = controllerNameInput?.trim() || defaultControllerName();
    if (controllerName.length > 200) throw new Error('控制端名称不能超过 200 个字符');
    const proofSignature = await sign(
      identity,
      pairingCanonical(code, identity.controllerId, controllerName, identity.publicKeyEncoded, proofNonce),
    );
    const wasAuthenticated = this.state.authenticated;
    const claim: PairingClaim = {
        code,
        controllerId: identity.controllerId,
        controllerName,
        publicKey: identity.publicKeyEncoded,
        proofNonce,
        proofSignature,
    };
    const envelope = wasAuthenticated
      ? await this.claimPairing(claim, identity.controllerId)
      : await this.request(
          MessageType.pairingClaim,
          claim,
          { controllerId: identity.controllerId },
          75_000,
        );
    const completed = envelope.payload as PairingCompleted;
    this.identity = await addPairedDevice(identity, completed.deviceId);
    this.reconnectAttempt = 0;
    this.setState({
      lastError: undefined,
      pairingPending: undefined,
      relayVersion: completed.serverVersion ?? this.state.relayVersion,
    });
    if (!wasAuthenticated) {
      this.setState({ authenticated: true, connection: 'connected' });
      this.startHeartbeat();
    }
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

  async listThreads(deviceId: string): Promise<CodexThreadListResult> {
    const threads: CodexThreadListResult['threads'] = [];
    const projects: CodexThreadListResult['projects'] = [];
    const seenThreadIds = new Set<string>();
    const seenProjectIds = new Set<string>();
    let cursor: string | undefined;
    for (let page = 0; page < 5; page += 1) {
      const result = await this.readControl<CodexThreadListResult>(
        MessageType.controlThreadList,
        deviceId,
        { limit: 100, cursor },
        45_000,
      );
      if (!result.result) throw new Error('Agent 未返回历史会话');
      for (const thread of result.result.threads) {
        if (!seenThreadIds.has(thread.threadId)) {
          seenThreadIds.add(thread.threadId);
          threads.push(thread);
        }
      }
      for (const project of result.result.projects ?? []) {
        if (!seenProjectIds.has(project.projectId)) {
          seenProjectIds.add(project.projectId);
          projects.push(project);
        }
      }

      cursor = result.result.nextCursor;
      if (!cursor) return { threads, projects };
    }

    return { threads, projects, nextCursor: cursor };
  }

  async readThread(deviceId: string, threadId: string): Promise<CodexThreadReadResult> {
    const result = await this.readControl<CodexThreadReadResult>(
      MessageType.controlThreadRead,
      deviceId,
      { threadId },
      45_000,
    );
    if (!result.result) throw new Error('Agent 未返回会话内容');
    if (result.result.threadId !== threadId) throw new Error('Agent 返回了不匹配的会话内容');
    return result.result;
  }

  async getSessionOptions(deviceId: string): Promise<CodexSessionOptions> {
    const result = await this.readControl<CodexSessionOptions>(
      MessageType.controlSessionOptions,
      deviceId,
      {},
      45_000,
    );
    if (!result.result) throw new Error('Agent 未返回会话选项');
    return result.result;
  }

  async startThread(
    deviceId: string,
    cwd: string,
    text: string,
    model: string | undefined,
    approvalPolicy: CodexApprovalPolicy,
  ): Promise<CodexThreadActionResult> {
    const result = await this.control<CodexThreadActionResult>(
      MessageType.controlThreadStart,
      deviceId,
      { cwd, text, model, approvalPolicy },
      45_000,
    );
    if (!result.result) throw new Error('Agent 未返回新会话 ID');
    return result.result;
  }

  async resumeThread(
    deviceId: string,
    threadId: string,
    text: string,
    model: string | undefined,
    approvalPolicy: CodexApprovalPolicy,
  ): Promise<CodexThreadActionResult> {
    const result = await this.control<CodexThreadActionResult>(
      MessageType.controlThreadResume,
      deviceId,
      { threadId, text, model, approvalPolicy },
      45_000,
    );
    if (!result.result) throw new Error('Agent 未返回恢复后的会话 ID');
    return result.result;
  }

  async revoke(deviceId: string): Promise<void> {
    const identity = this.requireIdentity();
    await this.request(
      MessageType.pairingRevoke,
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
      this.setState({ connection: 'connected', lastError: undefined });
      void this.beginAuthentication().catch(() => socket.close());
    };
    socket.onmessage = (event) => void this.handleMessage(String(event.data)).catch(() => socket.close());
    socket.onerror = () => socket.close();
    socket.onclose = () => {
      if (this.socket !== socket) return;
      this.socket = undefined;
      if (this.heartbeatTimer) window.clearInterval(this.heartbeatTimer);
      this.connectionId = undefined;
      this.failPending(new Error('Relay connection lost'));
      this.setState({
        connection: 'offline',
        authenticated: false,
        devices: this.state.devices.map((device) => ({ ...device, online: false })),
      });
      if (!this.stopped) this.scheduleReconnect();
    };
  }

  private async beginAuthentication(): Promise<void> {
    const identity = this.requireIdentity();
    this.authRequestId = requestId();
    this.send(
      createEnvelope(
        MessageType.authHello,
        { role: 'controller', principalId: identity.controllerId, clientVersion: packageJson.version },
        { requestId: this.authRequestId, controllerId: identity.controllerId },
      ),
    );
  }

  private async handleMessage(json: string): Promise<void> {
    let envelope: RelayEnvelope;
    try {
      envelope = JSON.parse(json) as RelayEnvelope;
      if (envelope.version !== 2 || !envelope.type) throw new Error('Invalid Relay envelope');
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
      this.reconnectAttempt = 0;
      this.setState({
        authenticated: true,
        connection: 'connected',
        lastError: undefined,
        relayVersion: auth.serverVersion,
      });
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
      case MessageType.pairingPending: {
        this.setState({ pairingPending: '已提交申请，请在电脑端确认' });
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

  private async control<TResult = unknown>(
    type: string,
    deviceId: string,
    payload: unknown,
    timeoutMs = 15_000,
  ): Promise<ControlResult<TResult>> {
    const identity = this.requireIdentity();
    const response = await this.request(type, payload, {
      deviceId,
      controllerId: identity.controllerId,
    }, timeoutMs);
    const result = response.payload as ControlResult<TResult>;
    if (result.status === 'failed') throw new Error(`${result.code ?? 'CONTROL_FAILED'}: ${result.message ?? ''}`);
    return result;
  }

  private async readControl<TResult>(
    type: string,
    deviceId: string,
    payload: unknown,
    timeoutMs: number,
  ): Promise<ControlResult<TResult>> {
    let lastError = new Error('只读请求失败');
    for (let attempt = 0; attempt < RelayClient.readRecoveryAttempts; attempt += 1) {
      try {
        return await this.control<TResult>(type, deviceId, payload, timeoutMs);
      } catch (reason) {
        lastError = reason instanceof Error ? reason : new Error(String(reason));
        if (!isTransientReadFailure(lastError) || attempt === RelayClient.readRecoveryAttempts - 1) {
          throw lastError;
        }
        await this.waitForDeviceReady(deviceId, RelayClient.readRecoveryTimeoutMs);
      }
    }
    throw lastError;
  }

  private request(
    type: string,
    payload: unknown,
    ids: { deviceId?: string; controllerId?: string },
    timeoutMs = 15_000,
  ): Promise<RelayEnvelope> {
    const id = requestId();
    return new Promise((resolve, reject) => {
      const timer = window.setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`Relay request timeout: ${type}`));
      }, timeoutMs);
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

  private claimPairing(payload: PairingClaim, controllerId: string): Promise<RelayEnvelope> {
    const relayUrl = validateRelayUrl(loadRelayUrl());
    const claimRequestId = requestId();
    return new Promise((resolve, reject) => {
      const socket = new WebSocket(relayUrl);
      let settled = false;
      const finish = (error?: Error, envelope?: RelayEnvelope) => {
        if (settled) return;
        settled = true;
        window.clearTimeout(timer);
        socket.close(1000, 'pairing claim finished');
        if (error) reject(error);
        else resolve(envelope!);
      };
      const timer = window.setTimeout(
        () => finish(new Error('配对确认超时，请重新生成配对码')),
        75_000,
      );
      socket.onopen = () => socket.send(JSON.stringify(createEnvelope(
        MessageType.pairingClaim,
        payload,
        { requestId: claimRequestId, controllerId },
      )));
      socket.onmessage = (event) => {
        try {
          const envelope = JSON.parse(String(event.data)) as RelayEnvelope;
          if (envelope.version !== 2 || !envelope.type) throw new Error('Relay 返回了无效消息');
          if (envelope.type === MessageType.pairingPending) {
            this.setState({ pairingPending: '已提交申请，请在电脑端确认' });
            return;
          }
          if (envelope.requestId !== claimRequestId) return;
          if (envelope.type === MessageType.error || envelope.type === MessageType.pairingDenied) {
            const error = envelope.payload as ErrorPayload;
            finish(new Error(`${error.code}: ${error.message}`));
            return;
          }
          if (envelope.type === MessageType.pairingCompleted) finish(undefined, envelope);
        } catch (reason) {
          finish(reason instanceof Error ? reason : new Error(String(reason)));
        }
      };
      socket.onerror = () => finish(new Error('Relay 配对连接失败'));
      socket.onclose = () => {
        if (!settled) finish(new Error('Relay 配对连接已断开'));
      };
    });
  }

  private send(envelope: RelayEnvelope): void {
    if (this.socket?.readyState !== WebSocket.OPEN) throw new Error('Relay 尚未连接');
    this.socket.send(JSON.stringify(envelope));
  }

  private async waitForDeviceReady(deviceId: string, timeoutMs: number): Promise<void> {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
      if (this.stopped) throw new Error('Relay client stopped');
      const device = this.state.devices.find((candidate) => candidate.deviceId === deviceId);
      if (this.state.authenticated && this.socket?.readyState === WebSocket.OPEN && device?.online) return;
      await new Promise((resolve) => window.setTimeout(resolve, 100));
    }
    throw new Error('Relay 或 Agent 连接恢复超时');
  }

  private startHeartbeat(): void {
    if (this.heartbeatTimer) window.clearInterval(this.heartbeatTimer);
    this.heartbeatTimer = window.setInterval(() => {
      if (!this.connectionId || this.socket?.readyState !== WebSocket.OPEN) return;
      const identity = this.requireIdentity();
      try {
        this.send(
          createEnvelope(
            MessageType.heartbeat,
            { connectionId: this.connectionId, snapshotRevision: 0 },
            { controllerId: identity.controllerId },
          ),
        );
      } catch {
        this.socket?.close();
      }
    }, 15_000);
  }

  private scheduleReconnect(): void {
    const delays = [500, 1000, 2000, 5000, 10_000];
    const base = delays[Math.min(this.reconnectAttempt++, delays.length - 1)];
    if (this.reconnectTimer) window.clearTimeout(this.reconnectTimer);
    this.reconnectTimer = window.setTimeout(() => {
      this.reconnectTimer = undefined;
      this.open();
    }, base * (1 + Math.random() * 0.2));
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
    const currentEvents = this.state.events[deviceId] ?? [];
    let events: CodexEvent[];
    if (event.kind === 'AgentMessageDelta' && event.itemId) {
      const delta = typeof event.data.delta === 'string' ? event.data.delta : '';
      const existing = currentEvents.find((candidate) =>
        candidate.kind === 'AgentMessageDelta' && candidate.itemId === event.itemId,
      );
      const previousText = existing && typeof existing.data.text === 'string' ? existing.data.text : '';
      const streamingEvent: CodexEvent = {
        ...(existing ?? event),
        revision: event.revision,
        occurredAt: event.occurredAt,
        data: { text: `${previousText}${delta}` },
      };
      events = [
        streamingEvent,
        ...currentEvents.filter((candidate) => candidate !== existing),
      ].slice(0, 100);
    } else if (event.kind === 'AgentMessageCompleted' && event.itemId) {
      events = [
        event,
        ...currentEvents.filter((candidate) =>
          !(candidate.kind === 'AgentMessageDelta' && candidate.itemId === event.itemId),
        ),
      ].slice(0, 100);
    } else {
      events = [event, ...currentEvents].slice(0, 100);
    }
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

function defaultControllerName(): string {
  const agent = navigator.userAgent;
  const platform = /iPhone/iu.test(agent)
    ? 'iPhone'
    : /iPad/iu.test(agent)
      ? 'iPad'
      : /Android/iu.test(agent)
        ? 'Android'
        : /Edg\//iu.test(agent)
          ? 'Edge'
          : /Chrome\//iu.test(agent)
            ? 'Chrome'
            : /Safari\//iu.test(agent)
              ? 'Safari'
              : 'Browser';
  return `${platform} · ${navigator.platform || 'Controller'}`.slice(0, 200);
}

function isTransientReadFailure(error: Error) {
  return error.message === 'Relay connection lost'
    || error.message === 'Relay 尚未连接'
    || error.message.startsWith('DEVICE_OFFLINE:')
    || error.message.startsWith('RELAY_OFFLINE:')
    || error.message.startsWith('Relay request timeout:');
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
