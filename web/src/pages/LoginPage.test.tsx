import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError, apiFetch } from "../api/client";
import { readSession } from "../session/storage";
import { SessionProvider } from "../session/SessionProvider";
import { LoginPage } from "./LoginPage";

vi.mock("../api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/client")>();
  return { ...actual, apiFetch: vi.fn() };
});

const apiFetchMock = vi.mocked(apiFetch);

const challenge = {
  challenge_id: "challenge-1",
  expires_in: 300,
  resend_in: 60,
};

function renderLogin(path = "/login", state?: unknown) {
  const entry = state === undefined ? path : { pathname: path, state };
  return render(
    <MemoryRouter initialEntries={[entry]}>
      <SessionProvider>
        <Routes>
          <Route path="/login" element={<LoginPage />} />
          <Route path="/wallet" element={<div>Wallet destination</div>} />
          <Route path="/wallet/cards/:cardId" element={<div>Card destination</div>} />
        </Routes>
      </SessionProvider>
    </MemoryRouter>,
  );
}

function setDemoConfig(enabled: boolean) {
  window.__SME_CONFIG__ = enabled
    ? {
        demoMode: true,
        demoUsername: "demo",
        demoPassword: "Demo_Passw0rd!",
        demoOtp: "123456",
      }
    : { demoMode: false };
}

beforeEach(() => {
  apiFetchMock.mockReset();
  window.sessionStorage.clear();
  setDemoConfig(false);
});

describe("LoginPage", () => {
  it("completes the two-step flow and stores the session", async () => {
    const user = userEvent.setup();
    apiFetchMock
      .mockResolvedValueOnce(challenge as never)
      .mockResolvedValueOnce({
        access_token: "token-from-verify",
        token_type: "Bearer",
        expires_in: 1800,
      } as never);

    renderLogin();

    await user.type(screen.getByLabelText("Username"), "demo");
    await user.type(screen.getByLabelText("Password"), "Demo_Passw0rd!");
    await user.click(screen.getByRole("button", { name: "Continue" }));

    const codeField = await screen.findByLabelText("Verification code");
    await user.type(codeField, "123456");
    await user.click(screen.getByRole("button", { name: "Verify and sign in" }));

    expect(await screen.findByText("Wallet destination")).toBeInTheDocument();
    expect(readSession()?.token).toBe("token-from-verify");
    expect(apiFetchMock).toHaveBeenNthCalledWith(
      1,
      "/auth/login",
      expect.objectContaining({ method: "POST" }),
      { auth: false },
    );
    expect(apiFetchMock).toHaveBeenNthCalledWith(
      2,
      "/auth/challenges/challenge-1/verify",
      expect.objectContaining({ method: "POST" }),
      { auth: false },
    );
  });

  it("shows the server message for a wrong password without navigating", async () => {
    const user = userEvent.setup();
    apiFetchMock.mockRejectedValueOnce(
      new ApiError(401, "UNAUTHENTICATED", "The username or password is incorrect."),
    );

    renderLogin();
    await user.type(screen.getByLabelText("Username"), "demo");
    await user.type(screen.getByLabelText("Password"), "wrong");
    await user.click(screen.getByRole("button", { name: "Continue" }));

    expect(
      await screen.findByText("The username or password is incorrect."),
    ).toBeInTheDocument();
    expect(screen.queryByText("Wallet destination")).not.toBeInTheDocument();
    expect(readSession()).toBeNull();
  });

  it("offers Start over after 429 and returns to the credentials step", async () => {
    const user = userEvent.setup();
    apiFetchMock
      .mockResolvedValueOnce(challenge as never)
      .mockRejectedValueOnce(
        new ApiError(
          429,
          "RATE_LIMITED",
          "Too many verification attempts. Request a new code.",
        ),
      );

    renderLogin();
    await user.type(screen.getByLabelText("Username"), "demo");
    await user.type(screen.getByLabelText("Password"), "Demo_Passw0rd!");
    await user.click(screen.getByRole("button", { name: "Continue" }));
    await user.type(await screen.findByLabelText("Verification code"), "000000");
    await user.click(screen.getByRole("button", { name: "Verify and sign in" }));

    expect(
      await screen.findByText("Too many verification attempts. Request a new code."),
    ).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Start over" }));

    expect(await screen.findByLabelText("Username")).toBeInTheDocument();
  });

  it("shows the demo hint when demoMode is enabled", () => {
    setDemoConfig(true);
    renderLogin();

    expect(screen.getByRole("region", { name: "Demo credentials" })).toBeInTheDocument();
    expect(screen.getByText("Demo_Passw0rd!")).toBeInTheDocument();
    expect(screen.getByText("123456")).toBeInTheDocument();
  });

  it("hides the demo hint when demoMode is false", () => {
    renderLogin();

    expect(screen.queryByText(/Demo credentials/i)).not.toBeInTheDocument();
    expect(screen.queryByText("Demo_Passw0rd!")).not.toBeInTheDocument();
  });

  it("shows the session-expired reason from the query string", () => {
    renderLogin("/login?reason=expired");

    expect(screen.getByText("Your session expired. Sign in again.")).toBeInTheDocument();
  });

  it("moves focus to the verification code when the OTP step opens", async () => {
    const user = userEvent.setup();
    apiFetchMock.mockResolvedValueOnce(challenge as never);

    renderLogin();
    await user.type(screen.getByLabelText("Username"), "demo");
    await user.type(screen.getByLabelText("Password"), "Demo_Passw0rd!");
    await user.click(screen.getByRole("button", { name: "Continue" }));

    expect(await screen.findByLabelText("Verification code")).toHaveFocus();
  });

  it("returns to the deep link after sign-in", async () => {
    const user = userEvent.setup();
    apiFetchMock
      .mockResolvedValueOnce(challenge as never)
      .mockResolvedValueOnce({
        access_token: "token-from-verify",
        token_type: "Bearer",
        expires_in: 1800,
      } as never);

    renderLogin("/login", { from: "/wallet/cards/c1" });
    await user.type(screen.getByLabelText("Username"), "demo");
    await user.type(screen.getByLabelText("Password"), "Demo_Passw0rd!");
    await user.click(screen.getByRole("button", { name: "Continue" }));
    await user.type(await screen.findByLabelText("Verification code"), "123456");
    await user.click(screen.getByRole("button", { name: "Verify and sign in" }));

    expect(await screen.findByText("Card destination")).toBeInTheDocument();
  });
});
