import { beforeEach, describe, expect, it } from "vitest";
import {
  SESSION_KEY,
  clearSession,
  loadSession,
  readSession,
  writeSession,
} from "./storage";

beforeEach(() => {
  window.sessionStorage.clear();
});

describe("session storage", () => {
  it("writes token and an absolute expiry", () => {
    const now = 1_700_000_000_000;
    const session = writeSession("token-123", 1800, now);
    expect(session).toEqual({ token: "token-123", expiresAt: now + 1_800_000 });
    expect(JSON.parse(window.sessionStorage.getItem(SESSION_KEY) ?? "{}")).toEqual(
      session,
    );
  });

  it("reads a live session", () => {
    const now = 1_700_000_000_000;
    writeSession("token-123", 60, now);
    expect(readSession(now + 1_000)).toEqual({
      token: "token-123",
      expiresAt: now + 60_000,
    });
  });

  it("treats the expiry instant as expired and removes it", () => {
    const now = 1_700_000_000_000;
    writeSession("token-123", 60, now);
    expect(loadSession(now + 60_000)).toEqual({ session: null, expired: true });
    expect(window.sessionStorage.getItem(SESSION_KEY)).toBeNull();
  });

  it("rejects malformed JSON and clears it", () => {
    window.sessionStorage.setItem(SESSION_KEY, "{not json");
    expect(loadSession()).toEqual({ session: null, expired: false });
    expect(window.sessionStorage.getItem(SESSION_KEY)).toBeNull();
  });

  it("rejects a stored value with the wrong shape", () => {
    window.sessionStorage.setItem(
      SESSION_KEY,
      JSON.stringify({ token: 42, expiresAt: "tomorrow" }),
    );
    expect(readSession()).toBeNull();
    expect(window.sessionStorage.getItem(SESSION_KEY)).toBeNull();
  });

  it("clears the session explicitly", () => {
    writeSession("token-123", 60);
    clearSession();
    expect(readSession()).toBeNull();
  });
});
