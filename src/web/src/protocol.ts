export { MessageType } from './messageTypes.generated';

export interface RelayEnvelope<T = unknown> {
  version: 2;
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
  serverVersion?: string;
}

export interface PairingCompleted {
  deviceId: string;
  controllerId: string;
  permissions: PairingPermissions;
  serverVersion?: string;
}

export interface PairingPermissions {
  view: boolean;
  steer: boolean;
  interrupt: boolean;
  approval: boolean;
}

export interface CodexSnapshot {
  serviceInstanceId?: string;
  streamEpoch?: string;
  lastSequence?: number;
  sharedSession?: boolean;
  connectionState?: 'connecting' | 'online' | 'reconnecting' | 'offline';
  capabilities?: string[];
  threads?: CodexThreadState[];
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
  agentVersion?: string;
  activeTurns?: CodexActiveTurn[];
}

export interface CodexThreadState {
  threadId: string;
  threadState: string;
  activeTurnId?: string;
  lastTurnId?: string;
  lastTurnStatus?: string;
  activity?: string;
  waitingOnApproval: boolean;
  waitingOnUserInput: boolean;
  freshness?: string;
}

export interface CodexActiveTurn {
  threadId: string;
  turnId: string;
  status: string;
  startedAt: number;
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
  role: 'user' | 'assistant' | 'tool';
  text: string;
  phase?: string;
  attachments: CodexThreadHistoryAttachment[];
  changes?: CodexThreadHistoryFileChange[];
  truncated?: boolean;
  originalLength?: number;
}

export interface CodexThreadHistoryAttachment {
  kind: 'image';
  name: string;
  mimeType: string;
  dataUrl: string;
}

export interface CodexThreadHistoryFileChange {
  path: string;
  kind?: string;
  additions?: number;
  deletions?: number;
}

export interface CodexTurnTiming {
  turnId: string;
  status?: string;
  startedAt?: number;
  completedAt?: number;
  durationMs?: number;
}

export interface CodexThreadReadResult {
  threadId: string;
  name?: string;
  cwd?: string;
  entries: CodexThreadHistoryEntry[];
  turns: CodexTurnTiming[];
  truncated: boolean;
}

export interface CodexThreadActionResult {
  threadId: string;
  turnId: string;
}

export interface CodexThreadWatchResult {
  threadId: string;
  serviceInstanceId: string;
  streamEpoch: string;
  sequence: number;
  history: CodexThreadReadResult;
  snapshot: CodexSnapshot;
  approvals: ApprovalRequested[];
  events: CodexEvent[];
  resyncRequired: boolean;
  controlAllowed: boolean;
  policyReason?: string;
}

export type CodexApprovalPolicy = 'untrusted' | 'on-request' | 'never';

export interface CodexModelOption {
  id: string;
  model: string;
  displayName: string;
  description: string;
  isDefault: boolean;
}

export interface CodexApprovalPolicyOption {
  id: CodexApprovalPolicy;
  displayName: string;
  description: string;
  isDefault: boolean;
}

export interface CodexSessionOptions {
  models: CodexModelOption[];
  approvalPolicies: CodexApprovalPolicyOption[];
}

export interface CodexEvent {
  serviceInstanceId?: string;
  streamEpoch?: string;
  sequence?: number;
  eventId: string;
  revision: number;
  // Includes ThreadStatusChanged (status, activeFlags) and UserMessageCompleted (text, attachments).
  kind: string;
  threadId?: string;
  turnId?: string;
  itemId?: string;
  occurredAt: number;
  data: Record<string, unknown>;
}

export interface ApprovalRequested {
  isResolving?: boolean;
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
    version: 2,
    type,
    messageId: crypto.randomUUID(),
    requestId: options.requestId,
    timestamp: Date.now(),
    deviceId: options.deviceId,
    controllerId: options.controllerId,
    payload,
  };
}
