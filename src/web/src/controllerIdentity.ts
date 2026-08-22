import { loadIdentity, saveIdentity, type StoredControllerIdentity } from './storage';

const encoder = new TextEncoder();

export async function getOrCreateIdentity(): Promise<StoredControllerIdentity> {
  const existing = await loadIdentity();
  if (existing) return existing;

  const generated = await crypto.subtle.generateKey(
    { name: 'ECDSA', namedCurve: 'P-256' },
    true,
    ['sign', 'verify'],
  );
  const privateBytes = await crypto.subtle.exportKey('pkcs8', generated.privateKey);
  const publicBytes = await crypto.subtle.exportKey('raw', generated.publicKey);
  const privateKey = await crypto.subtle.importKey(
    'pkcs8',
    privateBytes,
    { name: 'ECDSA', namedCurve: 'P-256' },
    false,
    ['sign'],
  );
  new Uint8Array(privateBytes).fill(0);
  const publicKey = await crypto.subtle.importKey(
    'raw',
    publicBytes,
    { name: 'ECDSA', namedCurve: 'P-256' },
    true,
    ['verify'],
  );
  const identity: StoredControllerIdentity = {
    id: 'controller',
    controllerId: `ctl_${crypto.randomUUID().replaceAll('-', '')}`,
    privateKey,
    publicKey,
    publicKeyEncoded: base64UrlEncode(new Uint8Array(publicBytes)),
    pairedDeviceIds: [],
  };
  await saveIdentity(identity);
  return identity;
}

export async function addPairedDevice(
  identity: StoredControllerIdentity,
  deviceId: string,
): Promise<StoredControllerIdentity> {
  if (identity.pairedDeviceIds.includes(deviceId)) return identity;
  const updated = { ...identity, pairedDeviceIds: [...identity.pairedDeviceIds, deviceId] };
  await saveIdentity(updated);
  return updated;
}

export async function removePairedDevice(
  identity: StoredControllerIdentity,
  deviceId: string,
): Promise<StoredControllerIdentity> {
  const updated = {
    ...identity,
    pairedDeviceIds: identity.pairedDeviceIds.filter((value) => value !== deviceId),
  };
  await saveIdentity(updated);
  return updated;
}

export async function sign(identity: StoredControllerIdentity, canonical: string): Promise<string> {
  const signature = new Uint8Array(
    await crypto.subtle.sign(
      { name: 'ECDSA', hash: 'SHA-256' },
      identity.privateKey,
      encoder.encode(canonical),
    ),
  );
  return base64UrlEncode(normalizeSignature(signature));
}

export function authCanonical(
  controllerId: string,
  challengeId: string,
  nonce: string,
  expiresAt: number,
): string {
  return ['codex-control-auth-v1', 'controller', controllerId, challengeId, nonce, String(expiresAt)].join('\n');
}

export function pairingCanonical(
  code: string,
  controllerId: string,
  publicKey: string,
  proofNonce: string,
): string {
  return ['codex-control-pairing-proof-v1', code, controllerId, publicKey, proofNonce].join('\n');
}

export function base64UrlEncode(value: Uint8Array): string {
  let binary = '';
  value.forEach((byte) => (binary += String.fromCharCode(byte)));
  return btoa(binary).replaceAll('+', '-').replaceAll('/', '_').replace(/=+$/u, '');
}

function normalizeSignature(signature: Uint8Array): Uint8Array {
  if (signature.length === 64) return signature;
  if (signature[0] !== 0x30) throw new Error('Unsupported ECDSA signature encoding');
  let offset = 2;
  if (signature[1] & 0x80) offset = 2 + (signature[1] & 0x7f);
  if (signature[offset++] !== 0x02) throw new Error('Invalid ECDSA R component');
  const rLength = signature[offset++];
  const r = signature.slice(offset, offset + rLength);
  offset += rLength;
  if (signature[offset++] !== 0x02) throw new Error('Invalid ECDSA S component');
  const sLength = signature[offset++];
  const s = signature.slice(offset, offset + sLength);
  const result = new Uint8Array(64);
  result.set(r.slice(Math.max(0, r.length - 32)), 32 - Math.min(32, r.length));
  result.set(s.slice(Math.max(0, s.length - 32)), 64 - Math.min(32, s.length));
  return result;
}
