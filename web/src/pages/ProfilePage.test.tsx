import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError, apiFetch } from "../api/client";
import type { MeResponse, UserInfo } from "../api/types";
import { readSession, writeSession } from "../session/storage";
import { SessionProvider } from "../session/SessionProvider";
import { ProfilePage } from "./ProfilePage";

vi.mock("../api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/client")>();
  return { ...actual, apiFetch: vi.fn() };
});

const apiFetchMock = vi.mocked(apiFetch);

const me: MeResponse = { id: "user-1", org: "sme", name: "Alex Morgan" };
const userInfo: UserInfo = {
  id: "user-1",
  name: "Alex Morgan",
  phone: "+61400000000",
  org: "sme",
};

function renderProfile() {
  return render(
    <MemoryRouter initialEntries={["/profile"]}>
      <SessionProvider>
        <Routes>
          <Route path="/profile" element={<ProfilePage />} />
          <Route path="/login" element={<div>Login destination</div>} />
        </Routes>
      </SessionProvider>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  apiFetchMock.mockReset();
  window.sessionStorage.clear();
  writeSession("token-profile", 60);
});

describe("ProfilePage", () => {
  it("fetches /me and /auth/userinfo and shows phone from userinfo", async () => {
    apiFetchMock.mockImplementation(((path: string) => {
      if (path === "/api/v1/me") return Promise.resolve(me);
      if (path === "/auth/userinfo") return Promise.resolve(userInfo);
      return Promise.reject(new Error(`unexpected path ${path}`));
    }) as never);

    renderProfile();

    expect(await screen.findByText("Alex Morgan")).toBeInTheDocument();
    expect(screen.getByText("sme")).toBeInTheDocument();
    expect(screen.getByText("+61400000000")).toBeInTheDocument();
    expect(apiFetchMock).toHaveBeenCalledWith("/api/v1/me");
    expect(apiFetchMock).toHaveBeenCalledWith("/auth/userinfo");
  });

  it("signs out, clearing the session and returning to sign in", async () => {
    const user = userEvent.setup();
    apiFetchMock.mockImplementation(((path: string) => {
      if (path === "/api/v1/me") return Promise.resolve(me);
      if (path === "/auth/userinfo") return Promise.resolve(userInfo);
      return Promise.reject(new Error(`unexpected path ${path}`));
    }) as never);

    renderProfile();
    await screen.findByText("+61400000000");
    await user.click(screen.getByRole("button", { name: "Sign out" }));

    expect(await screen.findByText("Login destination")).toBeInTheDocument();
    expect(readSession()).toBeNull();
  });

  it("shows an error and retries both requests", async () => {
    const user = userEvent.setup();
    let fail = true;
    apiFetchMock.mockImplementation(((path: string) => {
      if (fail) {
        return Promise.reject(
          new ApiError(503, "UPSTREAM_UNAVAILABLE", "The profile service is unavailable."),
        );
      }
      if (path === "/api/v1/me") return Promise.resolve(me);
      if (path === "/auth/userinfo") return Promise.resolve(userInfo);
      return Promise.reject(new Error(`unexpected path ${path}`));
    }) as never);

    renderProfile();
    expect(
      await screen.findByText("The profile service is unavailable."),
    ).toBeInTheDocument();

    fail = false;
    await user.click(screen.getByRole("button", { name: "Retry" }));

    expect(await screen.findByText("Alex Morgan")).toBeInTheDocument();
  });
});
