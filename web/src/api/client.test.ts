import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  ApiError,
  apiFetch,
  setUnauthorizedHandler,
  toSameOriginPath,
} from "./client";
import { readSession, writeSession } from "../session/storage";

const fetchMock = vi.fn();

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/problem+json" },
  });
}

beforeEach(() => {
  window.sessionStorage.clear();
  fetchMock.mockReset();
  vi.stubGlobal("fetch", fetchMock);
  setUnauthorizedHandler(null);
});

afterEach(() => {
  setUnauthorizedHandler(null);
  vi.unstubAllGlobals();
});

describe("apiFetch", () => {
  it("attaches the bearer token for authenticated requests", async () => {
    writeSession("token-abc", 60);
    fetchMock.mockResolvedValue(jsonResponse({ ok: true }));

    await apiFetch("/api/v1/me");

    const init = fetchMock.mock.calls[0]?.[1] as RequestInit;
    const headers = init.headers as Headers;
    expect(headers.get("Authorization")).toBe("Bearer token-abc");
    expect(fetchMock.mock.calls[0]?.[0]).toBe("/api/v1/me");
  });

  it("does not attach a token or react to a 401 when auth is false", async () => {
    writeSession("token-abc", 60);
    const onUnauthorized = vi.fn();
    setUnauthorizedHandler(onUnauthorized);
    fetchMock.mockResolvedValue(
      jsonResponse(
        { error: { code: "UNAUTHENTICATED", message: "The username or password is incorrect." } },
        401,
      ),
    );

    await expect(
      apiFetch("/auth/login", { method: "POST" }, { auth: false }),
    ).rejects.toMatchObject({
      status: 401,
      code: "UNAUTHENTICATED",
      message: "The username or password is incorrect.",
    });

    const init = fetchMock.mock.calls[0]?.[1] as RequestInit;
    const headers = new Headers(init.headers);
    expect(headers.has("Authorization")).toBe(false);
    expect(onUnauthorized).not.toHaveBeenCalled();
    expect(readSession()?.token).toBe("token-abc");
  });

  it("refuses protocol-relative paths so the token cannot leave the origin", async () => {
    writeSession("token-abc", 60);

    const error = await apiFetch("//evil.example/steal").catch((caught: unknown) => caught);

    expect(error).toBeInstanceOf(ApiError);
    expect(error).toMatchObject({ status: 0, code: "INVALID_PATH" });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("clears the session and notifies once when authenticated requests get 401", async () => {
    writeSession("token-abc", 60);
    const onUnauthorized = vi.fn();
    setUnauthorizedHandler(onUnauthorized);
    fetchMock.mockResolvedValue(new Response(null, { status: 401 }));

    const results = await Promise.allSettled([
      apiFetch("/api/v1/me"),
      apiFetch("/api/v1/me/cards"),
    ]);

    expect(results.every((result) => result.status === "rejected")).toBe(true);
    expect(onUnauthorized).toHaveBeenCalledTimes(1);
    expect(readSession()).toBeNull();
  });

  it("parses ProblemDetails including field errors", async () => {
    fetchMock.mockResolvedValue(
      jsonResponse(
        {
          error: {
            code: "VALIDATION_ERROR",
            message: "One or more validation errors occurred.",
            errors: { username: ["The username is required."] },
          },
        },
        400,
      ),
    );

    await expect(apiFetch("/auth/login", { method: "POST" }, { auth: false })).rejects.toMatchObject(
      {
        status: 400,
        code: "VALIDATION_ERROR",
        message: "One or more validation errors occurred.",
        errors: { username: ["The username is required."] },
      },
    );
  });

  it("falls back to a readable message for an empty 401 body", async () => {
    writeSession("token-abc", 60);
    setUnauthorizedHandler(() => undefined);
    fetchMock.mockResolvedValue(new Response(null, { status: 401 }));

    await expect(apiFetch("/api/v1/me")).rejects.toMatchObject({
      status: 401,
      code: "HTTP_401",
      message: "Your session is no longer valid. Sign in again.",
    });
  });

  it("maps a network failure to status 0", async () => {
    fetchMock.mockRejectedValue(new TypeError("Failed to fetch"));

    const error = await apiFetch("/api/v1/me").catch((caught: unknown) => caught);

    expect(error).toBeInstanceOf(ApiError);
    expect(error).toMatchObject({
      status: 0,
      code: "NETWORK_ERROR",
      message: "The service could not be reached. Check your connection and try again.",
    });
  });
});

describe("toSameOriginPath", () => {
  it("turns an absolute art_url into a same-origin path", () => {
    expect(
      toSameOriginPath(
        "http://localhost:8082/api/v1/cards/c1000000-0000-4000-8000-000000000001/art",
        "http://localhost:8080",
      ),
    ).toBe("/api/v1/cards/c1000000-0000-4000-8000-000000000001/art");
  });

  it("keeps a query string", () => {
    expect(toSameOriginPath("/api/v1/cards/x/art?v=2", "http://localhost:8080")).toBe(
      "/api/v1/cards/x/art?v=2",
    );
  });
});
