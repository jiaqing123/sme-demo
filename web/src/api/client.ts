import { clearSession, readSession } from "../session/storage";
import type { ProblemDetails } from "./types";

/**
 * The single HTTP entry point. Paths are always same-origin relative paths:
 * nginx proxies /auth and /api in the container, and Vite proxies them in
 * development. The bearer token never appears in a URL.
 */

export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly errors?: Record<string, string[]>;

  constructor(
    status: number,
    code: string,
    message: string,
    errors?: Record<string, string[]>,
  ) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.code = code;
    this.errors = errors;
  }
}

type UnauthorizedHandler = () => void;

let unauthorizedHandler: UnauthorizedHandler | null = null;
let unauthorizedNotified = false;

/** SessionProvider registers the redirect-and-clear behaviour here. */
export function setUnauthorizedHandler(handler: UnauthorizedHandler | null): void {
  unauthorizedHandler = handler;
  unauthorizedNotified = false;
}

/** A fresh sign-in allows the next authenticated 401 to redirect again. */
export function resetUnauthorizedNotification(): void {
  unauthorizedNotified = false;
}

const STATUS_MESSAGES: Record<number, string> = {
  400: "The request could not be completed.",
  401: "Your session is no longer valid. Sign in again.",
  403: "You do not have access to that resource.",
  404: "The requested resource was not found.",
  429: "Too many attempts. Try again shortly.",
  500: "Something went wrong. Try again.",
  503: "The service is temporarily unavailable. Try again shortly.",
};

const DEFAULT_MESSAGE = "The request could not be completed.";

export interface RequestOptions {
  /** Send the bearer token and treat a 401 as session expiry. Default true. */
  auth?: boolean;
  timeoutMs?: number;
}

function defaultTimeoutMs(): number {
  return 15_000;
}

/**
 * art_url is absolute (it points at the API host). The browser must still call
 * it same-origin through nginx, so keep only pathname + query.
 */
export function toSameOriginPath(url: string, origin?: string): string {
  const base =
    origin ?? (typeof window !== "undefined" ? window.location.origin : "http://localhost");
  const parsed = new URL(url, base);
  const path = parsed.pathname.startsWith("/") ? parsed.pathname : `/${parsed.pathname}`;
  return `${path}${parsed.search}`;
}

async function parseProblem(response: Response): Promise<ProblemDetails | null> {
  let text: string;
  try {
    text = await response.text();
  } catch {
    return null;
  }
  if (!text) return null;
  try {
    const parsed: unknown = JSON.parse(text);
    return typeof parsed === "object" && parsed !== null ? (parsed as ProblemDetails) : null;
  } catch {
    return null;
  }
}

function notifyUnauthorized(): void {
  if (!unauthorizedHandler || unauthorizedNotified) return;
  // Parallel requests can all receive 401; the redirect happens once.
  unauthorizedNotified = true;
  unauthorizedHandler();
}

async function toApiError(response: Response, auth: boolean): Promise<ApiError> {
  const problem = await parseProblem(response);
  const code = problem?.error?.code ?? `HTTP_${response.status}`;
  const message =
    problem?.error?.message ??
    problem?.detail ??
    STATUS_MESSAGES[response.status] ??
    DEFAULT_MESSAGE;
  const errors = problem?.error?.errors;

  if (response.status === 401 && auth) {
    clearSession();
    notifyUnauthorized();
  }

  return new ApiError(response.status, code, message, errors);
}

/**
 * Rejects anything that would leave this origin before the bearer token is
 * attached. `path.startsWith("/")` alone is not enough: `//host/x` is a
 * protocol-relative URL and would send the token to another origin.
 */
function sameOriginPath(path: string): string {
  const origin = window.location.origin;
  if (!path.startsWith("/")) {
    throw new ApiError(0, "INVALID_PATH", "Request paths must be same-origin relative paths.");
  }
  const parsed = new URL(path, origin);
  if (parsed.origin !== origin) {
    throw new ApiError(0, "INVALID_PATH", "Request paths must stay on this origin.");
  }
  return `${parsed.pathname}${parsed.search}`;
}

async function request(
  path: string,
  init: RequestInit = {},
  options: RequestOptions = {},
): Promise<Response> {
  const auth = options.auth ?? true;
  const target = sameOriginPath(path);

  const controller = new AbortController();
  let timedOut = false;
  const timer = setTimeout(() => {
    timedOut = true;
    controller.abort();
  }, options.timeoutMs ?? defaultTimeoutMs());

  const externalSignal = init.signal;
  const forwardAbort = () => controller.abort();
  if (externalSignal) {
    if (externalSignal.aborted) controller.abort();
    else externalSignal.addEventListener("abort", forwardAbort, { once: true });
  }

  const headers = new Headers(init.headers);
  if (!headers.has("Accept")) headers.set("Accept", "application/json");
  if (auth) {
    const session = readSession();
    if (session) headers.set("Authorization", `Bearer ${session.token}`);
  }

  try {
    const response = await fetch(target, { ...init, headers, signal: controller.signal });
    if (!response.ok) {
      throw await toApiError(response, auth);
    }
    return response;
  } catch (error) {
    if (error instanceof ApiError) throw error;
    if (timedOut) {
      throw new ApiError(
        0,
        "NETWORK_ERROR",
        "The request timed out. Check your connection and try again.",
      );
    }
    throw new ApiError(
      0,
      "NETWORK_ERROR",
      "The service could not be reached. Check your connection and try again.",
    );
  } finally {
    clearTimeout(timer);
    externalSignal?.removeEventListener("abort", forwardAbort);
  }
}

export async function apiFetch<T>(
  path: string,
  init: RequestInit = {},
  options: RequestOptions = {},
): Promise<T> {
  const response = await request(path, init, options);
  if (response.status === 204) return undefined as T;
  let text: string;
  try {
    text = await response.text();
  } catch {
    throw new ApiError(0, "NETWORK_ERROR", "The response could not be read. Try again.");
  }
  if (!text) return undefined as T;
  try {
    return JSON.parse(text) as T;
  } catch {
    throw new ApiError(
      response.status,
      "INVALID_RESPONSE",
      "The service returned an unexpected response.",
    );
  }
}

export async function apiFetchBlob(
  path: string,
  options: RequestOptions = {},
): Promise<Blob> {
  const response = await request(path, {}, options);
  try {
    return await response.blob();
  } catch {
    throw new ApiError(0, "NETWORK_ERROR", "The response could not be read. Try again.");
  }
}
