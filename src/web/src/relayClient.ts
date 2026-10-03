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
  type CodexThreadWatchResult,
  type ControlResult,
  type DeviceListResult,
  type DeviceSummary,
  type ErrorPayload,
  type PairingCompleted,
  type RelayEnvelope,
} from './protocol';
import { loadRelayUrl, type StoredControllerIdentity } from './storage';
import { ThreadStore, type ThreadView } from './threadStore';

export interface RelayClientState {
  connection: 'connecting' | 'connected' | 'offline';
  authenticated: boolean;
  lastError?: string;
  devices: DeviceSummary[];
  events: Record<string, CodexEvent[]>;
  approvals: Record<string, ApprovalRequested[]>;
  threads: Record<string, Record<string, ThreadView>>;
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
  private threadStore = new ThreadStore();
  private watched = new Map<string, Set<string>>();
  private watching = new Map<string, Promise<CodexThreadWatchResult>>();
  private watchBuffers = new Map<string, CodexEvent[]>();
  private watchBufferSizes = new Map<string, number>();
  private overflowedWatches = new Set<string>();
  private streamCursors = new Map<string, { epoch: string; sequence: number }>();
  private latestSnapshots = new Map<string, CodexSnapshot>();
  private state: RelayClientState = {
    connection: 'offline',
    authenticated: false,
    devices: [],
    events: {},
    approvals: {},
    threads: {},
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

  async watchThread(deviceId: string, threadId: string): Promise<CodexThreadWatchResult> {
    const snapshot = this.state.devices.find((device) => device.deviceId === deviceId)?.snapshot;
    if (!snapshot?.sharedSession || !snapshot.capabilities?.includes('sharedSessionV1')) {
      throw new Error('当前 Agent 未启用共享会话能力，请升级并配置共享模式');
    }
    const key = `${deviceId}\0${threadId}`;
    const pending = this.watching.get(key);
    if (pending) return pending;
    const watched = this.watched.get(deviceId) ?? new Set<string>();
    watched.add(threadId);
    this.watched.set(deviceId, watched);
    const view = this.threadStore.view(deviceId, threadId);
    this.threadStore.beginWatch(deviceId, threadId);
    this.watchBuffers.set(key, []);
    this.watchBufferSizes.set(key, 0);
    this.overflowedWatches.delete(key);
    this.publishThreads(deviceId);
    const operation = this.control<CodexThreadWatchResult>(MessageType.controlThreadWatch, deviceId, {
      threadId, streamEpoch: view?.streamEpoch, afterSequence: view?.sequence,
    }, 45_000).then((response) => {
      const result = response.result;
      if (!result || result.threadId !== threadId || result.history.threadId !== threadId) {
        throw new Error('Agent 返回了不匹配的共享会话');
      }
      if (this.overflowedWatches.has(key)) throw new Error('会话更新积压超过恢复容量，已保留原内容，请重新同步');
      this.threadStore.applyWatch(deviceId, result, this.watchBuffers.get(key));
      const cursor = this.streamCursors.get(deviceId);
      const watermark = Math.max(result.sequence, ...(result.events ?? []).map((event) => event.sequence ?? 0),
        ...(this.watchBuffers.get(key) ?? []).map((event) => event.sequence ?? 0));
      this.streamCursors.set(deviceId, { epoch: result.streamEpoch,
        sequence: Math.max(cursor?.epoch === result.streamEpoch ? cursor.sequence : 0, watermark) });
      this.publishThreads(deviceId);
      return result;
    }).catch((reason: unknown) => {
      this.threadStore.failWatch(deviceId, threadId, reason instanceof Error ? reason.message : String(reason));
      this.publishThreads(deviceId);
      throw reason;
    }).finally(() => {
      this.watching.delete(key);
      this.watchBuffers.delete(key);
      this.watchBufferSizes.delete(key);
      this.overflowedWatches.delete(key);
    });
    this.watching.set(key, operation);
    return operation;
  }

  async unwatchThread(deviceId: string, threadId: string): Promise<void> {
    await this.control(MessageType.controlThreadUnwatch, deviceId, { threadId });
    this.watched.get(deviceId)?.delete(threadId);
  }

  async sendThread(deviceId: string, threadId: string, text: string, expectedTurnId?: string): Promise<CodexThreadActionResult> {
    try {
      const result = await this.control<CodexThreadActionResult>(MessageType.controlThreadSend, deviceId,
        { threadId, text, expectedTurnId }, 45_000);
      if (!result.result) throw new Error('输入结果待确认，请等待会话同步，不要重复发送');
      return result.result;
    } catch (reason) {
      if (reason instanceof Error && (isTransientReadFailure(reason) || reason.message.includes('OUTCOME_UNKNOWN'))) {
        throw new Error('输入结果待确认。连接恢复后请先查看会话；此次输入不会自动重发。');
      }
      throw reason;
    }
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
    this.threadStore.clearDevice(deviceId);
    this.watched.delete(deviceId);
    this.streamCursors.delete(deviceId);
    this.latestSnapshots.delete(deviceId);
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
      for (const device of this.state.devices) this.threadStore.markDisconnected(device.deviceId);
      this.setState({
        connection: 'offline',
        authenticated: false,
        devices: this.state.devices.map((device) => ({ ...device, online: false })),
        threads: Object.fromEntries(this.state.devices.map((device) => [device.deviceId, this.threadStore.device(device.deviceId)])),
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
        if (!envelope.requestId) this.applyDeviceList(envelope.payload as DeviceListResult);
        break;
      case MessageType.deviceOnline:
        this.updateDevice(envelope.deviceId, { online: true });
        if (envelope.deviceId) this.recoverWatched(envelope.deviceId);
        break;
      case MessageType.deviceOffline:
        if (envelope.deviceId) this.threadStore.markDisconnected(envelope.deviceId);
        this.updateDevice(envelope.deviceId, { online: false });
        break;
      case MessageType.codexSnapshot:
        if (envelope.deviceId) {
          this.applySnapshot(envelope.deviceId, envelope.payload as CodexSnapshot);
        }
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
    const devices = payload.devices.map((device) => {
      // Snapshot pushes can arrive before the initial device list. The list only
      // carries metadata, so it must not erase equally recent live fields.
      const cached = this.latestSnapshots.get(device.deviceId);
      return cached && (!device.snapshot || cached.streamEpoch === device.snapshot.streamEpoch && cached.revision >= device.snapshot.revision)
        ? { ...device, snapshot: cached } : device;
    });
    this.setState({ devices });
    for (const device of devices) {
      if (device.snapshot) this.applySnapshot(device.deviceId, device.snapshot);
      if (device.online) this.recoverWatched(device.deviceId);
    }
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
    const wasRecovering = this.pendingRecovery(deviceId);
    let streamGap = false;
    const cursor = this.streamCursors.get(deviceId);
    if (event.streamEpoch && event.sequence !== undefined) {
      if (cursor?.epoch === event.streamEpoch && event.sequence <= cursor.sequence) return;
      if (cursor && (cursor.epoch !== event.streamEpoch || event.sequence > cursor.sequence + 1)) {
        this.threadStore.markGap(deviceId);
        streamGap = true;
      }
      this.streamCursors.set(deviceId, { epoch: event.streamEpoch, sequence: event.sequence });
    }
    if (streamGap) this.recoverWatched(deviceId, this.pendingRecovery(deviceId));
    let buffered = false;
    if (event.threadId) {
      const key = `${deviceId}\0${event.threadId}`;
      const buffer = this.watchBuffers.get(key);
      if (buffer) {
        buffered = true;
        const size = (this.watchBufferSizes.get(key) ?? 0) + JSON.stringify(event).length * 2;
        if (!this.overflowedWatches.has(key) && buffer.length < 10_000 && size <= 8 * 1024 * 1024) {
          buffer.push(event);
          this.watchBufferSizes.set(key, size);
        } else {
          this.overflowedWatches.add(key);
          this.threadStore.markGap(deviceId);
        }
      }
    }
    if (event.kind === 'StreamGap' || !event.threadId && event.data.resyncRequired === true) {
      this.threadStore.markGap(deviceId);
      streamGap = true;
    }
    else if (!buffered) this.threadStore.applyEvent(deviceId, event);
    this.publishThreads(deviceId);
    if (event.kind === 'StreamGap' || streamGap) this.recoverWatched(deviceId, this.pendingRecovery(deviceId));
    else this.recoverNewlyPending(deviceId, wasRecovering);
  }

  private applySnapshot(deviceId: string, snapshot: CodexSnapshot): void {
    const old = this.latestSnapshots.get(deviceId);
    if (old?.streamEpoch === snapshot.streamEpoch && old && old.revision > snapshot.revision) return;
    this.latestSnapshots.set(deviceId, snapshot);
    const wasRecovering = this.pendingRecovery(deviceId);
    this.threadStore.applySnapshot(deviceId, snapshot);
    const cursor = this.streamCursors.get(deviceId);
    if (snapshot.streamEpoch && (!cursor || cursor.epoch !== snapshot.streamEpoch)) {
      this.streamCursors.set(deviceId, { epoch: snapshot.streamEpoch, sequence: snapshot.lastSequence ?? 0 });
    }
    this.updateDevice(deviceId, { snapshot });
    this.publishThreads(deviceId);
    this.recoverNewlyPending(deviceId, wasRecovering);
  }

  private pendingRecovery(deviceId: string): Set<string> {
    return new Set(Object.values(this.threadStore.device(deviceId))
      .filter((thread) => thread.watching && thread.needsRecovery && !thread.error)
      .map((thread) => thread.threadId));
  }

  private recoverNewlyPending(deviceId: string, previous: ReadonlySet<string>): void {
    const affected = new Set([...this.pendingRecovery(deviceId)].filter((threadId) => !previous.has(threadId)));
    if (affected.size > 0) this.recoverWatched(deviceId, affected);
  }

  private recoverWatched(deviceId: string, affected?: ReadonlySet<string>): void {
    const device = this.state.devices.find((candidate) => candidate.deviceId === deviceId);
    if (!this.state.authenticated || !device?.online || !device.snapshot?.sharedSession ||
        !device.snapshot.capabilities?.includes('sharedSessionV1')) return;
    for (const threadId of this.watched.get(deviceId) ?? []) {
      if (affected && !affected.has(threadId)) continue;
      void this.watchThread(deviceId, threadId).catch(() => { /* The thread view exposes recovery failure. */ });
    }
  }

  private publishThreads(deviceId: string): void {
    this.setState({
      threads: { ...this.state.threads, [deviceId]: this.threadStore.device(deviceId) },
      events: { ...this.state.events, [deviceId]: this.threadStore.events(deviceId) },
      approvals: { ...this.state.approvals, [deviceId]: this.threadStore.approvals(deviceId) },
    });
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
      threads: { ...this.state.threads },
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
