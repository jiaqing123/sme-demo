import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { App } from "./App";
import { readSession, writeSession } from "./session/storage";
import { SessionProvider } from "./session/SessionProvider";

const fetchMock = vi.fn();

beforeEach(() => {
  window.sessionStorage.clear();
  fetchMock.mockReset();
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function renderApp(entry: string) {
  return render(
    <MemoryRouter initialEntries={[entry]}>
      <SessionProvider>
        <App />
      </SessionProvider>
    </MemoryRouter>,
  );
}

describe("session guarding", () => {
  it("redirects to sign-in with reason=required when there is no session", async () => {
    renderApp("/wallet");

    expect(await screen.findByText("Sign in to continue.")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "SME Demo" })).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("clears the session and redirects with reason=expired on a protected 401", async () => {
    writeSession("expired-token", 60);
    fetchMock.mockResolvedValue(new Response(null, { status: 401 }));

    renderApp("/wallet");

    expect(await screen.findByText("Your session expired. Sign in again.")).toBeInTheDocument();
    expect(readSession()).toBeNull();
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});
