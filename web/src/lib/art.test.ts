import { describe, expect, it, vi, beforeEach, afterEach } from "vitest";
import { apiFetchBlob, toSameOriginPath } from "../api/client";
import { clearCardArtCache, loadCardArt } from "./art";

vi.mock("../api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/client")>();
  return { ...actual, apiFetchBlob: vi.fn() };
});

const blobMock = vi.mocked(apiFetchBlob);

let objectUrlCounter = 0;
const revokeMock = vi.fn();
const createMock = vi.fn(() => `blob:test-${++objectUrlCounter}`);

beforeEach(() => {
  clearCardArtCache();
  objectUrlCounter = 0;
  revokeMock.mockReset();
  createMock.mockClear();
  Object.defineProperty(URL, "createObjectURL", {
    configurable: true,
    writable: true,
    value: createMock,
  });
  Object.defineProperty(URL, "revokeObjectURL", {
    configurable: true,
    writable: true,
    value: revokeMock,
  });
  blobMock.mockReset();
});

afterEach(() => {
  clearCardArtCache();
});

describe("card artwork", () => {
  it("fetches one blob per same-origin path and caches the object URL", async () => {
    blobMock.mockResolvedValue(new Blob(["<svg />"], { type: "image/svg+xml" }));

    const first = await loadCardArt("http://localhost:8082/api/v1/cards/abc/art");
    const second = await loadCardArt("/api/v1/cards/abc/art");

    expect(first).toBe("blob:test-1");
    expect(second).toBe(first);
    expect(blobMock).toHaveBeenCalledTimes(1);
    expect(blobMock).toHaveBeenCalledWith(toSameOriginPath("/api/v1/cards/abc/art"));
  });

  it("does not cache a failed load", async () => {
    blobMock
      .mockRejectedValueOnce(new Error("nope"))
      .mockResolvedValueOnce(new Blob(["<svg />"], { type: "image/svg+xml" }));

    await expect(loadCardArt("/api/v1/cards/abc/art")).rejects.toThrow("nope");
    await expect(loadCardArt("/api/v1/cards/abc/art")).resolves.toBe("blob:test-1");
    expect(blobMock).toHaveBeenCalledTimes(2);
  });

  it("revokes object URLs when the cache is cleared", async () => {
    blobMock.mockResolvedValue(new Blob(["<svg />"], { type: "image/svg+xml" }));
    await loadCardArt("/api/v1/cards/abc/art");

    clearCardArtCache();

    await vi.waitFor(() => expect(revokeMock).toHaveBeenCalledWith("blob:test-1"));
  });
});
