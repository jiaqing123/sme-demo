/**
 * The wallet's vertical scroll offset survives a trip into a card detail and
 * back. It is a UI preference for this tab only, so it lives in sessionStorage
 * (never localStorage) and is removed on sign-out.
 */

export const WALLET_SCROLL_KEY = "sme.wallet.scroll";

function storage(): Storage | null {
  try {
    return window.sessionStorage;
  } catch {
    return null;
  }
}

/** Stores the offset in CSS pixels; ignores non-finite values. */
export function saveWalletScroll(offset: number): void {
  if (!Number.isFinite(offset) || offset < 0) return;
  try {
    storage()?.setItem(WALLET_SCROLL_KEY, String(Math.round(offset)));
  } catch {
    // A full or disabled storage only means the position is not restored.
  }
}

/** Returns the stored offset, or null when there is nothing usable. */
export function readWalletScroll(): number | null {
  let raw: string | null = null;
  try {
    raw = storage()?.getItem(WALLET_SCROLL_KEY) ?? null;
  } catch {
    return null;
  }
  if (raw === null || raw.trim() === "") return null;

  const offset = Number(raw);
  if (!Number.isFinite(offset) || offset < 0) {
    clearWalletScroll();
    return null;
  }
  return offset;
}

export function clearWalletScroll(): void {
  try {
    storage()?.removeItem(WALLET_SCROLL_KEY);
  } catch {
    // Nothing to clear.
  }
}
