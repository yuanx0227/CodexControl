export { MessageType } from './messageTypes.generated';

export interface RelayEnvelope<T = unknown> {
  version: 1;
  type: string;
  messageId: string;
  requestId?: string;
  timestamp: number;
  deviceId?: string;
  controllerId?: string;
  payload: T;
}

export interface AuthChallenge {
  challengeId: string;
  nonce: string;
  expiresAt: number;
}

export interface AuthOk {
  role: 'controller';
  principalId: string;
  connectionId: string;
}

export interface PairingCompleted {
  deviceId: string;
  controllerId: string;
  permissions: PairingPermissions;
}

export interface PairingPermissions {
  view: boolean;
  steer: boolean;
  interrupt: boolean;
  approval: boolean;
}

export interface CodexSnapshot {
  revision: number;
  status: string;
  activeThreadId?: string;
  activeTurnId?: string;
  startedAt?: number;
  lastActivityAt: number;
  currentProject?: string;
  currentActivity?: string;
  runningCommand?: string;
  changedFiles: string[];
  pendingApprovalCount: number;
  lastAgentMessage?: string;
  lastError?: string;
}

export interface DeviceSummary {
  deviceId: string;
  name: string;
  online: boolean;
  snapshot?: CodexSnapshot;
  lastSeenAt?: number;
}

export interface DeviceListResult {
  devices: DeviceSummary[];
}

export interface CodexThreadSummary {
  threadId: string;
  name?: string;
  preview?: string;
  cwd?: string;
  createdAt?: number;
  updatedAt?: number;
  recencyAt?: number;
  status: string;
  sourceKind?: string;
  projectId?: string;
}

export interface CodexProjectSummary {
  projectId: string;
  name: string;
  position: number;
  roots: string[];
}

export interface CodexThreadListResult {
  threads: CodexThreadSummary[];
  projects: CodexProjectSummary[];
  nextCursor?: string;
}

export interface CodexThreadHistoryEntry {
  itemId: string;
  turnId: string;
  role: 'user' | 'assistant';
  text: string;
  phase?: string;
}

export interface CodexThreadReadResult {
  threadId: string;
  name?: string;
  cwd?: string;
  entries: CodexThreadHistoryEntry[];
  truncated: boolean;
}

export interface CodexThreadActionResult {
  threadId: string;
  turnId: string;
}

export interface CodexEvent {
  eventId: string;
  revision: number;
  kind: string;
  threadId?: string;
  turnId?: string;
  itemId?: string;
  occurredAt: number;
  data: Record<string, unknown>;
}

export interface ApprovalRequested {
  approvalId: string;
  requestMethod: string;
  threadId?: string;
  turnId?: string;
  itemId?: string;
  command?: string;
  cwd?: string;
  reason?: string;
  availableDecisions: unknown[];
  requestedAt: number;
}

export interface ControlResult<TResult = unknown> {
  status: 'accepted' | 'succeeded' | 'failed';
  code?: string;
  message?: string;
  result?: TResult;
}

export interface ErrorPayload {
  code: string;
  message: string;
}

export function createEnvelope<T>(
  type: string,
  payload: T,
  options: { requestId?: string; deviceId?: string; controllerId?: string } = {},
): RelayEnvelope<T> {
  return {
    version: 1,
    type,
    messageId: crypto.randomUUID(),
    requestId: options.requestId,
    timestamp: Date.now(),
    deviceId: options.deviceId,
    controllerId: options.controllerId,
    payload,
  };
}
