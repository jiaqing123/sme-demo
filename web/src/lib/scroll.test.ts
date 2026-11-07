import { beforeEach, describe, expect, it } from "vitest";
import {
  WALLET_SCROLL_KEY,
  clearWalletScroll,
  readWalletScroll,
  saveWalletScroll,
} from "./scroll";

describe("wallet scroll storage", () => {
  beforeEach(() => {
    window.sessionStorage.clear();
  });

  it("stores and reads a rounded offset in sessionStorage", () => {
    saveWalletScroll(123.6);

    expect(window.sessionStorage.getItem(WALLET_SCROLL_KEY)).toBe("124");
    expect(readWalletScroll()).toBe(124);
    expect(window.localStorage.getItem(WALLET_SCROLL_KEY)).toBeNull();
  });

  it("returns null when nothing was saved", () => {
    expect(readWalletScroll()).toBeNull();
  });

  it("rejects and removes invalid stored values", () => {
    window.sessionStorage.setItem(WALLET_SCROLL_KEY, "not-a-number");

    expect(readWalletScroll()).toBeNull();
    expect(window.sessionStorage.getItem(WALLET_SCROLL_KEY)).toBeNull();

    window.sessionStorage.setItem(WALLET_SCROLL_KEY, "-20");
    expect(readWalletScroll()).toBeNull();
  });

  it("ignores non-finite offsets instead of storing them", () => {
    saveWalletScroll(Number.NaN);
    saveWalletScroll(Number.POSITIVE_INFINITY);
    saveWalletScroll(-1);

    expect(readWalletScroll()).toBeNull();
  });

  it("clears the stored offset", () => {
    saveWalletScroll(480);
    clearWalletScroll();

    expect(readWalletScroll()).toBeNull();
    expect(window.sessionStorage.getItem(WALLET_SCROLL_KEY)).toBeNull();
  });
});
