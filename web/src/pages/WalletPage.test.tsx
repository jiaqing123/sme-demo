import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError, apiFetch } from "../api/client";
import type { Card } from "../api/types";
import { loadCardArt } from "../lib/art";
import { WalletPage } from "./WalletPage";

vi.mock("../api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/client")>();
  return { ...actual, apiFetch: vi.fn() };
});

vi.mock("../lib/art", () => ({
  loadCardArt: vi.fn(),
  clearCardArtCache: vi.fn(),
}));

const apiFetchMock = vi.mocked(apiFetch);
const loadArtMock = vi.mocked(loadCardArt);

const cards: Card[] = [
  {
    id: "c1000000-0000-4000-8000-000000000001",
    brand: "visa",
    last4: "4242",
    expiry_month: 9,
    expiry_year: 2028,
    status: "active",
    currency: "AUD",
    display_order: 0,
    art_url: "http://localhost:8082/api/v1/cards/c1000000-0000-4000-8000-000000000001/art",
  },
  {
    id: "c1000000-0000-4000-8000-000000000002",
    brand: "mastercard",
    last4: "5454",
    expiry_month: 3,
    expiry_year: 2027,
    status: "active",
    currency: "AUD",
    display_order: 1,
    art_url: "http://localhost:8082/api/v1/cards/c1000000-0000-4000-8000-000000000002/art",
  },
  {
    id: "c1000000-0000-4000-8000-000000000003",
    brand: "visa",
    last4: "1881",
    expiry_month: 11,
    expiry_year: 2026,
    status: "active",
    currency: "AUD",
    display_order: 2,
    art_url: "http://localhost:8082/api/v1/cards/c1000000-0000-4000-8000-000000000003/art",
  },
];

function renderWallet() {
  return render(
    <MemoryRouter initialEntries={["/wallet"]}>
      <WalletPage />
    </MemoryRouter>,
  );
}

beforeEach(() => {
  apiFetchMock.mockReset();
  loadArtMock.mockReset();
  loadArtMock.mockResolvedValue("blob:card-art");
});

describe("WalletPage", () => {
  it("renders three cards with brand, last4, expiry and status accessible", async () => {
    apiFetchMock.mockResolvedValue({ items: cards } as never);

    renderWallet();

    expect(
      await screen.findByRole("img", {
        name: "Visa card ending 4242, expires 09/28, Active",
      }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("img", { name: "Mastercard card ending 5454, expires 03/27, Active" }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("img", { name: "Visa card ending 1881, expires 11/26, Active" }),
    ).toBeInTheDocument();
    expect(screen.getAllByText("Active")).toHaveLength(3);
    expect(screen.getAllByRole("link")).toHaveLength(3);
  });

  it("falls back to readable card text when artwork fails", async () => {
    apiFetchMock.mockResolvedValue({ items: cards } as never);
    loadArtMock.mockImplementation((artUrl: string) =>
      artUrl.includes("000000000001")
        ? Promise.reject(new Error("art unavailable"))
        : Promise.resolve("blob:card-art"),
    );

    renderWallet();

    expect(await screen.findByText("•••• 4242")).toBeInTheDocument();
    expect(screen.getAllByText("Active")).toHaveLength(3);
  });

  it("shows an empty state when there are no cards", async () => {
    apiFetchMock.mockResolvedValue({ items: [] } as never);

    renderWallet();

    expect(await screen.findByText("No cards yet")).toBeInTheDocument();
  });

  it("shows a retry action and refetches after a failure", async () => {
    const user = userEvent.setup();
    apiFetchMock
      .mockRejectedValueOnce(
        new ApiError(503, "UPSTREAM_UNAVAILABLE", "The service is temporarily unavailable."),
      )
      .mockResolvedValueOnce({ items: cards } as never);

    renderWallet();

    expect(
      await screen.findByText("The service is temporarily unavailable."),
    ).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Retry" }));

    expect(
      await screen.findByRole("img", {
        name: "Visa card ending 4242, expires 09/28, Active",
      }),
    ).toBeInTheDocument();
    expect(apiFetchMock).toHaveBeenCalledTimes(2);
  });
});
