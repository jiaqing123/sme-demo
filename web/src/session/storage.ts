/**
 * The session lives in sessionStorage only: one tab, no "remember me", and it
 * disappears when the tab closes. Nothing else may persist the token.
 */

export const SESSION_KEY = "sme.session";

export interface Session {
  token: string;
  /** Unix epoch milliseconds. */
  expiresAt: number;
}

export interface LoadedSession {
  session: Session | null;
  /** True when a stored session existed but had reached its expiry. */
  expired: boolean;
}

function storage(): Storage | null {
  try {
    return window.sessionStorage;
  } catch {
    return null;
  }
}

function removeStored(): void {
  try {
    storage()?.removeItem(SESSION_KEY);
  } catch {
    // Storage can be unavailable (privacy modes); there is nothing to clear.
  }
}

export function writeSession(
  token: string,
  expiresInSeconds: number,
  now: number = Date.now(),
): Session {
  const session: Session = {
    token,
    expiresAt: now + Math.max(0, expiresInSeconds) * 1000,
  };
  try {
    storage()?.setItem(SESSION_KEY, JSON.stringify(session));
  } catch {
    // A full or disabled storage must not turn a successful sign-in into an
    // error screen; the session simply does not survive a reload.
  }
  return session;
}

export function loadSession(now: number = Date.now()): LoadedSession {
  const store = storage();
  if (!store) return { session: null, expired: false };

  let raw: string | null;
  try {
    raw = store.getItem(SESSION_KEY);
  } catch {
    return { session: null, expired: false };
  }
  if (!raw) return { session: null, expired: false };

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    removeStored();
    return { session: null, expired: false };
  }

  if (typeof parsed !== "object" || parsed === null) {
    removeStored();
    return { session: null, expired: false };
  }

  const candidate = parsed as Record<string, unknown>;
  const token = candidate.token;
  const expiresAt = candidate.expiresAt;
  if (
    typeof token !== "string" ||
    token.length === 0 ||
    typeof expiresAt !== "number" ||
    !Number.isFinite(expiresAt)
  ) {
    removeStored();
    return { session: null, expired: false };
  }

  if (expiresAt <= now) {
    removeStored();
    return { session: null, expired: true };
  }

  return { session: { token, expiresAt }, expired: false };
}

export function readSession(now: number = Date.now()): Session | null {
  return loadSession(now).session;
}

export function clearSession(): void {
  removeStored();
}
