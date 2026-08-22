export interface StoredControllerIdentity {
  id: 'controller';
  controllerId: string;
  privateKey: CryptoKey;
  publicKey: CryptoKey;
  publicKeyEncoded: string;
  pairedDeviceIds: string[];
}

const databaseName = 'codex-control';
const storeName = 'identity';

function openDatabase(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(databaseName, 1);
    request.onupgradeneeded = () => {
      if (!request.result.objectStoreNames.contains(storeName)) {
        request.result.createObjectStore(storeName, { keyPath: 'id' });
      }
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error ?? new Error('IndexedDB open failed'));
  });
}

export async function loadIdentity(): Promise<StoredControllerIdentity | undefined> {
  const database = await openDatabase();
  try {
    return await new Promise((resolve, reject) => {
      const request = database.transaction(storeName, 'readonly').objectStore(storeName).get('controller');
      request.onsuccess = () => resolve(request.result as StoredControllerIdentity | undefined);
      request.onerror = () => reject(request.error ?? new Error('Identity read failed'));
    });
  } finally {
    database.close();
  }
}

export async function saveIdentity(identity: StoredControllerIdentity): Promise<void> {
  const database = await openDatabase();
  try {
    await new Promise<void>((resolve, reject) => {
      const transaction = database.transaction(storeName, 'readwrite');
      transaction.objectStore(storeName).put(identity);
      transaction.oncomplete = () => resolve();
      transaction.onerror = () => reject(transaction.error ?? new Error('Identity write failed'));
      transaction.onabort = () => reject(transaction.error ?? new Error('Identity write aborted'));
    });
  } finally {
    database.close();
  }
}

export function loadRelayUrl(): string {
  const configured = localStorage.getItem('codex-control-relay-url');
  if (configured) return configured;
  const current = new URL(window.location.href);
  current.protocol = current.protocol === 'https:' ? 'wss:' : 'ws:';
  current.pathname = '/ws/controller';
  current.search = '';
  current.hash = '';
  return current.toString();
}

export function saveRelayUrl(value: string): void {
  localStorage.setItem('codex-control-relay-url', value);
}
